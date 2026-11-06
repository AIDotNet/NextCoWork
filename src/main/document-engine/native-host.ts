/**
 * 原生文档引擎 helper 的**进程与协议**适配 —— 把一个跑在插件包里的原生进程
 * 包装成 `DocumentEngineProvider`,交给 `manager.ts`。
 *
 * ## 为了什么需求建的
 *
 * 办公插件自带的 LibreOffice 引擎不能进 Electron 主进程(崩溃会带走整个应用、
 * 原生内存问题无从隔离),也不能进 iframe(没有 Node)。所以每份打开的文档由
 * 一个独立 helper 进程承载,主进程经 stdio 分帧协议(`native-frame.ts`)和它说话。
 * 这个文件负责:起进程、握手、请求/回执配对、崩溃检测、收尾。
 *
 * ## 协议 v1(控制帧都是 JSON)
 *
 * ```
 * helper → 主进程(启动后第一帧)  { v:1, event:'hello', data:{ protocol:1, engineVersion } }
 * 主进程 → helper                  { v:1, id, method, params }
 * helper → 主进程                  { v:1, id, ok:true, result } | { v:1, id, ok:false, error:{ code, message } }
 * helper → 主进程(主动事件)      { v:1, event, data }
 * ```
 *
 * 方法:`document.open { path, format }` → `{ capabilities }`;
 * `document.apply { operations }` → `{ warnings, undoable }`;
 * `document.saveAs { path, format }` → `{}`;`shutdown {}` → `{}`;
 * `document.input { events }` → `{ modified, invalidations, cursor?, … }`(形状见 `interaction.ts`)。
 * `document.command { command, args? }` → 与 input 同形的回执(功能区命令,见 `commands.ts`)。
 *
 * ## 不变式
 *
 * - **每次 spawn 前核对入口摘要**(`resolveEntry` 由调用方传入 `native-installer.ts`
 *   的 `resolveVerifiedEntry`),不信安装时那一次。
 * - **不走 shell、不继承凭据环境。** 只给 helper 一份白名单环境,HOME / TMP 指向
 *   私有工作目录:引擎 profile 与临时文件不落到用户家目录,也拿不到 API key 之类的变量。
 * - **helper 的回执是不可信输入。** capability 只保留宿主认得的操作;错误码只认
 *   协议里定义过的,其余一律 `io`。
 * - 进程意外退出 → 所有在途请求以 `engine_crashed` 失败,并通知会话管理器;
 *   管理器据此把在途修改记为「结果未知」,绝不重放。
 * - **二进制附件只靠位置配对,同一时刻只允许一个请求在等它。** 附件帧没有 id:
 *   一条 binary frame 必须先于、且紧邻它那条 `attachment:true` 的 JSON 回执。
 *   多一帧、少一帧、尺寸不符、没有在途请求、或插进别的 id 的控制帧,都按协议故障
 *   杀掉 helper(`settleAttachment` / `protocolFailure`)—— 上一次的 tile 绝不能被
 *   当成下一次请求的附件交付。
 */
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { mkdir, rm } from 'node:fs/promises'
import { join } from 'node:path'
import {
  DocumentEngineError,
  isDocumentFormat,
  isDocumentOperationKind,
  parseOperationResults,
  type DocumentCapabilities,
  type DocumentErrorCode,
  type DocumentFormat,
  type DocumentOperation,
  type DocumentQuery,
  type DocumentRenderRequest,
  type DocumentRenderResult
} from '../../shared/document-engine/protocol'
import { parseInputResult, parseInteraction, type DocumentInputEvent, type DocumentInputResult } from '../../shared/document-engine/interaction'
import { parseCommandIds, type CommandArgs, type DocumentCommandId } from '../../shared/document-engine/commands'
import { NATIVE_PROTOCOL_VERSION } from '../../shared/plugin/native-component'
import { killTree } from '../kernel/node-spawn'
import type { DocumentEngineHandle, DocumentEngineProvider } from './manager'
import { FrameDecoder, encodeJsonFrame } from './native-frame'

const DEFAULT_STARTUP_TIMEOUT_MS = 30_000
const SHUTDOWN_GRACE_MS = 2_000
/** stderr 只留尾巴做诊断。★ 不设上限的话一个疯狂打日志的 helper 能吃光主进程内存 */
const STDERR_TAIL_BYTES = 16 * 1024

const KNOWN_ERROR_CODES: ReadonlySet<string> = new Set<DocumentErrorCode>([
  'unsupported_format', 'unsupported_operation', 'invalid_operation', 'stale_revision', 'stale_generation',
  'disk_conflict', 'timeout', 'macro_denied', 'io', 'engine_crashed', 'busy'
])

export type SpawnHelper = (command: string, args: string[], options: { cwd: string; env: NodeJS.ProcessEnv }) => ChildProcessWithoutNullStreams

const defaultSpawn: SpawnHelper = (command, args, options) =>
  spawn(command, args, {
    cwd: options.cwd,
    env: options.env,
    shell: false,
    windowsHide: true,
    // POSIX:自成进程组,收尾时 killTree 能带走引擎派生的子进程
    detached: process.platform !== 'win32',
    stdio: ['pipe', 'pipe', 'pipe']
  })

/**
 * helper 的环境变量白名单。
 *
 * ★ 显式列举而不是「复制 process.env 再删几个」:删除式清单漏一个就是把一枚
 * token 交给第三方原生代码,而新增的敏感变量永远不会有人记得回来加进删除表。
 */
export function helperEnv(workDir: string, source: NodeJS.ProcessEnv = process.env): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = {
    HOME: join(workDir, 'home'),
    USERPROFILE: join(workDir, 'home'),
    TMPDIR: join(workDir, 'tmp'),
    TMP: join(workDir, 'tmp'),
    TEMP: join(workDir, 'tmp'),
    /*
      需求:随包安装的 Python helper 是**被打包进 app.asar / 签名清单的文件**,它每次
      启动都会在自己旁边落 `__pycache__/*.pyc`。
      不满足会怎样:升级后那些 .pyc 仍在旧位置,索引重建与 macOS 签名校验都依赖安装
      目录逐字节不变 —— 表现为「改了代码却校验不过」,而错误信息完全不提 Python。
      显式写死成 '1',不从 source 透传:调用方万一在 process.env 里留了 '0',这里就
      静默失效,而且失效时零报错。
    */
    PYTHONDONTWRITEBYTECODE: '1'
  }
  // 动态库装载与本地化需要的少数几项,原样透传
  for (const key of ['PATH', 'SystemRoot', 'WINDIR', 'LANG', 'LC_ALL', 'LC_CTYPE', 'FONTCONFIG_PATH']) {
    const value = source[key]
    if (value !== undefined) env[key] = value
  }
  return env
}

interface Pending {
  resolve: (value: unknown) => void
  reject: (error: Error) => void
  attachment?: {
    expectedBytes: number
    /** 附件帧已收到时填上。没有它 = 还一个字都没来 */
    bytes?: Uint8Array
    resolve: (value: { result: unknown; bytes: Uint8Array }) => void
  }
}

export class NativeHelperConnection {
  private readonly pending = new Map<number, Pending>()
  /**
   * 唯一一个「正在等附件」的请求 id。
   *
   * ★ 最多一个,不是排队也不是缓冲。附件帧没有 id,只能靠「紧跟在它那条
   * `attachment:true` 回执之前」这个位置关系配对;允许第二个在途请求,等于让同一条
   * 无 id 的字节流有两种解释,而错配的症状是「预览里出现上一份文档 / 上一次渲染的
   * tile」——像素是合法的,所以从界面上根本看不出哪里错了。
   *
   * 只有它非 null 时通道才接 binary 帧;它被清掉(交付 / 取消 / 故障)之后到来的
   * 附件一律判协议故障,见 `settleAttachment`。
   */
  private attachmentId: number | null = null
  /**
   * 协议故障之后的原因串。置上之后新请求一律立刻拒绝 —— 字节流已经失步,
   * 再往上写请求只会得到另一个错配的回执。
   */
  private broken: string | null = null
  private readonly exitListeners = new Set<(reason: string) => void>()
  private nextId = 1
  private stderrTail = ''
  private exited = false
  private closing = false
  private closePromise: Promise<void> | null = null
  /** `exit` 与协议故障都可能通知退出监听者,★ 只通知一次:否则会话会被标记两次崩溃 */
  private exitNotified = false
  engineVersion = ''

  private constructor(private readonly child: ChildProcessWithoutNullStreams, readonly workDir: string) {}

  /**
   * 起 helper 并等 `hello` 握手。握手超时 / 协议版本不符 / 先退出 → 抛 `engine_unavailable`。
   */
  static async start(options: {
    command: string
    args?: string[]
    workDir: string
    startupTimeoutMs?: number
    spawnImpl?: SpawnHelper
  }): Promise<NativeHelperConnection> {
    await mkdir(join(options.workDir, 'home'), { recursive: true, mode: 0o700 })
    await mkdir(join(options.workDir, 'tmp'), { recursive: true, mode: 0o700 })
    const child = (options.spawnImpl ?? defaultSpawn)(options.command, options.args ?? [], {
      cwd: options.workDir,
      env: helperEnv(options.workDir)
    })
    const connection = new NativeHelperConnection(child, options.workDir)
    try {
      await connection.handshake(options.startupTimeoutMs ?? DEFAULT_STARTUP_TIMEOUT_MS)
      return connection
    } catch (error) {
      // 需求：握手失败也拥有 HOME/TMP 和子进程，不能等尚未创建的 session 来收尾。
      await connection.close()
      throw error
    }
  }

  private handshake(timeoutMs: number): Promise<void> {
    return new Promise((resolve, reject) => {
      let greeted = false
      const fail = (message: string): void => {
        if (greeted) return
        greeted = true
        clearTimeout(timer)
        this.kill()
        reject(new DocumentEngineError('engine_unavailable', message))
      }
      const timer = setTimeout(() => { fail(`document engine did not start within ${timeoutMs}ms${this.stderrSuffix()}`) }, timeoutMs)
      const decoder = new FrameDecoder()
      this.child.stderr.on('data', (chunk: Buffer) => {
        this.stderrTail = (this.stderrTail + chunk.toString('utf8')).slice(-STDERR_TAIL_BYTES)
      })
      this.child.on('error', (error) => {
        fail(`document engine failed to start: ${error.message}`)
        this.protocolFailure(error.message)
      })
      // 需求：helper 提前退出后写管道会触发 EPIPE，必须拒绝请求而不是让主进程崩溃。
      this.child.stdin.on('error', (error) => { this.protocolFailure(error.message) })
      this.child.on('exit', (code, signal) => {
        this.exited = true
        const reason = `document engine exited (${signal ?? String(code)})${this.stderrSuffix()}`
        fail(reason)
        this.failAll(reason)
        this.notifyExit(reason)
      })
      this.child.stdout.on('data', (chunk: Buffer) => {
        let frames
        try {
          frames = decoder.push(chunk)
        } catch (error) {
          // 字节流失步没有恢复办法,只能收掉这个 helper(见 native-frame.ts)
          const reason = `document engine protocol error: ${(error as Error).message}`
          fail(reason)
          this.protocolFailure(reason)
          return
        }
        for (const frame of frames) {
          if (this.broken !== null) return
          if (frame.kind === 'binary') {
            this.settleAttachment(frame.bytes)
            continue
          }
          const message = frame.value as Record<string, unknown> | null
          if (message === null || typeof message !== 'object' || message.v !== 1) continue
          if (!greeted && message.event === 'hello') {
            const data = (message.data ?? {}) as Record<string, unknown>
            if (data.protocol !== NATIVE_PROTOCOL_VERSION) {
              fail(`document engine speaks protocol ${String(data.protocol)}, host expects ${NATIVE_PROTOCOL_VERSION}`)
              return
            }
            this.engineVersion = typeof data.engineVersion === 'string' ? data.engineVersion.slice(0, 128) : ''
            greeted = true
            clearTimeout(timer)
            resolve()
            continue
          }
          if (typeof message.id === 'number') this.settle(message)
          else if (this.attachmentId !== null && this.pending.get(this.attachmentId)?.attachment?.bytes !== undefined) {
            this.protocolFailure('an event interrupted the attachment and its response')
          }
        }
      })
    })
  }

  onExit(listener: (reason: string) => void): () => void {
    this.exitListeners.add(listener)
    return () => { this.exitListeners.delete(listener) }
  }

  request(method: string, params: unknown, signal?: AbortSignal): Promise<unknown> {
    const blocked = this.refuseRequest()
    if (blocked !== null) return Promise.reject(blocked)
    const id = this.nextId++
    return new Promise((resolve, reject) => {
      const onAbort = (): void => {
        if (this.removePending(id) === undefined) return
        reject(new DocumentEngineError('timeout', `${method} was cancelled`))
      }
      this.pending.set(id, {
        resolve: (value) => { signal?.removeEventListener('abort', onAbort); resolve(value) },
        reject: (error) => { signal?.removeEventListener('abort', onAbort); reject(error) }
      })
      if (signal !== undefined) {
        if (signal.aborted) { onAbort(); return }
        signal.addEventListener('abort', onAbort, { once: true })
      }
      this.child.stdin.write(encodeJsonFrame({ v: 1, id, method, params }))
    })
  }

  /**
   * 请求一条带二进制附件的回执。附件帧没有 id,只能绑定到**紧跟在它后面**的那条
   * 声明了 `attachment: true` 的 JSON 回执;所以同一时刻只允许一个这样的请求在途
   * (见 `attachmentId`),多一条、少一条、尺寸不符或插进别的 id 都杀掉 helper。
   */
  requestAttachment(
    method: string,
    params: unknown,
    expectedBytes: number,
    signal?: AbortSignal
  ): Promise<{ result: unknown; bytes: Uint8Array }> {
    const blocked = this.refuseRequest()
    if (blocked !== null) return Promise.reject(blocked)
    // 需求：普通请求也必须先完成，否则它迟到的回执会插入无 id 的附件配对。
    if (this.pending.size > 0) return Promise.reject(new DocumentEngineError('invalid_operation', 'another document request is in flight'))
    const id = this.nextId++
    /*
      ★ 占位在 write 之前:helper 可以在同一次 pipe 读里就把 binary 和回执一起送回来,
      而 stdout 的 data 回调是同步逐帧处理的 —— 晚一步占位,那条附件撞上的就是
      「没有在途请求」,好端端的一次渲染被自己的调度顺序判成协议故障。
    */
    this.attachmentId = id
    return new Promise((resolve, reject) => {
      const onAbort = (): void => {
        if (this.removePending(id) === undefined) return
        /*
          需求:带附件的请求一旦取消,这条通道必须作废,而不是只把 pending 删掉。
          原因:①写进 stdin 的 render 撤不回来,helper 仍会把 tile 发出来,而附件帧
          没有 id —— 通道留着,它就会被当成**下一次** render 的附件交付;②取消意味着
          调用方已经不等这次结果了,通道里剩下的字节全是它不再认识的状态。
          不满足会怎样:表现为「取消一次预览后,下一次预览显示的是上一次的 tile」,
          像素合法、尺寸也可能恰好对上,所以从界面上看不出错,零报错。
        */
        this.protocolFailure(`${method} was cancelled while its attachment was in flight`)
        reject(new DocumentEngineError('timeout', `${method} was cancelled`))
      }
      const attachment: NonNullable<Pending['attachment']> = {
        expectedBytes,
        resolve: (value) => { signal?.removeEventListener('abort', onAbort); resolve(value) }
      }
      this.pending.set(id, {
        /*
          附件请求的 JSON 回执**不单独交付**:上面的 resolve 只是兜底,真正的交付在
          `settle` 里(必须拿到附件字节)。走到这里有附件声明却没有字节,那是协议故障。
        */
        resolve: (value) => { signal?.removeEventListener('abort', onAbort); resolve({ result: value, bytes: new Uint8Array() }) },
        reject: (error) => { signal?.removeEventListener('abort', onAbort); reject(error) },
        attachment
      })
      if (signal !== undefined) {
        if (signal.aborted) { onAbort(); return }
        signal.addEventListener('abort', onAbort, { once: true })
      }
      this.child.stdin.write(encodeJsonFrame({ v: 1, id, method, params }))
    })
  }

  /**
   * 收尾:先礼后兵。发 `shutdown` 等一小会儿,不走就杀整棵进程树,最后删私有目录。
   *
   * ★ 幂等且共享同一个 Promise:收尾的触发点有两个 —— 崩溃后 `markCrashed` 顺手收,
   * 和退出时 `closeAll` 收。不共享的话第二次调用会在第一次还没删完目录时就返回,
   * 于是 `closeAll` 之后 helper 的私有目录还留在盘上。
   */
  close(): Promise<void> {
    this.closePromise ??= this.shutdownHelper()
    return this.closePromise
  }

  private async shutdownHelper(): Promise<void> {
    this.closing = true
    if (!this.exited) {
      const exited = new Promise<void>((resolve) => { this.child.once('exit', () => { resolve() }) })
      /*
        broken 之后不再请求 shutdown:协议已经失步,helper 的回执本来就不可信,
        再发一条只是把一个不该被信任的请求交给一个正在被杀的进程。直接杀,只等 exit。
      */
      let graceful = false
      if (this.broken === null) {
        this.request('shutdown', {}).catch(() => undefined)
        graceful = await Promise.race([exited.then(() => true), delay(SHUTDOWN_GRACE_MS).then(() => false)])
      }
      if (!graceful) {
        this.kill()
        await Promise.race([exited, delay(SHUTDOWN_GRACE_MS)])
      }
    }
    this.failAll('document engine closed')
    await rm(this.workDir, { recursive: true, force: true })
  }

  private settle(message: Record<string, unknown>): void {
    const id = message.id as number
    /*
      ★ 在途附件期间出现**别的 id** 的控制帧就是失步:协议的配对规则是「binary 紧接它
      自己的那条回执」,中间插进任何一条别的回执,后面那条 binary 究竟属于谁就无从判断。
      症状和错配一样(下一次渲染显示上一张 tile),但成因难查,所以在这里就断掉。
    */
    if (this.attachmentId !== null && id !== this.attachmentId) {
      this.protocolFailure(`response ${id} arrived while response ${this.attachmentId} was still waiting for its attachment`)
      return
    }
    const pending = this.pending.get(id)
    if (pending === undefined) return // 已取消 / 迟到的回执
    if (message.ok === true) {
      if (pending.attachment !== undefined) {
        const result = message.result
        if (result === null || typeof result !== 'object' || (result as Record<string, unknown>).attachment !== true) {
          this.rejectAttachment(id, 'render response did not declare its attachment')
          return
        }
        const bytes = pending.attachment.bytes
        if (bytes === undefined) {
          /*
            需求:v1 协议里 binary 帧必须先于且紧邻它那条 `attachment:true` 回执。
            原因:回执先到意味着字节还在后面;此刻若继续等,那条迟到的 binary 会落在
            附件占位已经被清掉(甚至已被下一个请求占用)的时刻,只能被当成下一次请求的
            附件 —— 或者被当成孤儿帧。
            不满足会怎样:表现为下一次预览拿到上一次的 tile,而且中间任何一步都不报错。
          */
          this.rejectAttachment(id, 'render response declared an attachment that no binary frame carried')
          return
        }
        this.removePending(id)
        pending.attachment.resolve({ result, bytes })
        return
      }
      this.removePending(id)
      pending.resolve(message.result)
      return
    }
    const error = (message.error ?? {}) as Record<string, unknown>
    const code = typeof error.code === 'string' && KNOWN_ERROR_CODES.has(error.code) ? (error.code as DocumentErrorCode) : 'io'
    const text = typeof error.message === 'string' ? error.message.slice(0, 2000) : 'document engine error'
    this.removePending(id)
    pending.reject(new DocumentEngineError(code, text))
  }

  /**
   * 收一条二进制帧。只有唯一那个在等附件的请求能接,且只接一次、尺寸必须完全对上。
   *
   * ★ 这里**只记录**字节,不交付:交付发生在它那条 JSON 回执到达时(`settle`)。
   * 立刻 resolve 的话,同一块 pipe 数据里紧随其后的第二条附件就来不及被判成故障,
   * 上层会把一次错配当成成功渲染。
   */
  private settleAttachment(bytes: Uint8Array): void {
    const id = this.attachmentId
    const attachment = id === null ? undefined : this.pending.get(id)?.attachment
    if (id === null || attachment === undefined) {
      this.protocolFailure('binary attachment arrived with no request waiting for it')
      return
    }
    if (attachment.bytes !== undefined) {
      this.protocolFailure(`response ${id} already received one attachment`)
      return
    }
    if (bytes.byteLength !== attachment.expectedBytes) {
      // 多传 / 少传同样是失步:剩下的字节会顶进下一帧的帧头
      this.rejectAttachment(id, `render attachment has ${bytes.byteLength} bytes; expected ${attachment.expectedBytes}`)
      return
    }
    attachment.bytes = bytes
  }

  /**
   * 附件缺失 / 尺寸不符 / 没有声明附件:先把 `io` 交回调用方,再把通道按协议故障收掉。
   *
   * ★ 顺序是刻意的:先 `protocolFailure`(它会把崩溃同步通知给会话管理器)再 reject,
   * 于是调用方拿到失败时,会话状态已经是 crashed,而不是「刚失败但还是 ready」。
   */
  private rejectAttachment(id: number, reason: string): void {
    const pending = this.removePending(id)
    this.protocolFailure(reason)
    pending?.reject(new DocumentEngineError('io', reason))
  }

  /**
   * 协议故障 = 这条通道的字节流已经不可信(失步、多一帧、少一帧、附件对不上人)。
   *
   * ★ 顺序是刻意的:置 broken(此后新请求一律拒绝)→ 清掉在途附件占位 → killTree →
   * **同步**通知 onExit(会话管理器据此把在途修改记为「结果未知」并标记 crashed)→
   * 最后 failAll 把失败交回调用方。
   * 等真实 `exit` 事件再通知是不行的:killTree 之后进程退出是异步的,那段窗口里
   * 调用方已经拿到 reject 并去读了会话状态,看到的还是 ready。
   */
  private protocolFailure(reason: string): void {
    if (this.broken !== null) return
    this.broken = `document engine protocol error: ${reason}`
    this.attachmentId = null
    this.kill()
    this.notifyExit(this.broken)
    this.failAll(this.broken)
  }

  /**
   * 通知退出监听者。★ 用一次性开关:协议故障已经同步通知过一次,之后真实 `exit`
   * 还会再来一次 —— 不挡的话管理器会把同一个会话标记两次崩溃。
   */
  private notifyExit(reason: string): void {
    if (this.exitNotified || this.closing) return
    this.exitNotified = true
    for (const listener of this.exitListeners) listener(reason)
  }

  /**
   * 新请求的准入检查。三种情况必须当场拒绝,而不是把请求写进管道:
   * 进程已经退出、通道已经因协议故障作废、或者有附件在途 —— 最后一条见 `attachmentId`,
   * 此时任何多出来的请求都会让那条无 id 的附件字节流变得无法解释。
   */
  private refuseRequest(): DocumentEngineError | null {
    if (this.exited) return new DocumentEngineError('engine_crashed', 'document engine is not running')
    if (this.broken !== null) return new DocumentEngineError('engine_crashed', this.broken)
    if (this.attachmentId !== null) {
      return new DocumentEngineError('invalid_operation', 'the document engine is waiting for a render attachment; another request would make that id-less attachment ambiguous')
    }
    return null
  }

  private removePending(id: number): Pending | undefined {
    const pending = this.pending.get(id)
    if (pending === undefined) return undefined
    this.pending.delete(id)
    if (this.attachmentId === id) this.attachmentId = null
    return pending
  }

  private failAll(reason: string): void {
    for (const id of [...this.pending.keys()]) {
      const pending = this.removePending(id)
      pending?.reject(new DocumentEngineError('engine_crashed', reason))
    }
    this.attachmentId = null
  }

  private kill(): void {
    const pid = this.child.pid
    if (pid === undefined || this.exited) return
    killTree(pid, 'SIGKILL')
  }

  private stderrSuffix(): string {
    const tail = this.stderrTail.trim()
    return tail === '' ? '' : `: ${tail.slice(-500)}`
  }
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => { setTimeout(resolve, ms) })
}

/**
 * helper 回报的 capability → 宿主认得的形状。
 *
 * ★ 格式必须与打开的文件一致、操作只保留协议里有的:helper 声称支持一个宿主不认识的
 * 操作,UI 和 Agent 就会画出 / 下发一个没有校验器的操作。
 */
export function parseCapabilities(raw: unknown, format: DocumentFormat, engineVersion: string): DocumentCapabilities {
  const record = (raw ?? {}) as Record<string, unknown>
  const caps = (record.capabilities ?? {}) as Record<string, unknown>
  const operations = Array.isArray(caps.operations) ? [...new Set(caps.operations.filter(isDocumentOperationKind))] : []
  const canExport = Array.isArray(caps.canExport) ? [...new Set(caps.canExport.filter(isDocumentFormat))] : []
  const macros = (caps.macros ?? {}) as Record<string, unknown>
  const interaction = parseInteraction(caps.interaction)
  const commands = parseCommandIds(caps.commands)
  return {
    format,
    engineVersion,
    operations,
    canSave: caps.canSave === true,
    canExport,
    canUndo: caps.canUndo === true,
    macros: { list: macros.list === true, run: macros.run === true },
    ...(interaction === undefined ? {} : { interaction }),
    ...(commands === undefined ? {} : { commands })
  }
}

/**
 * 把一个插件携带的原生引擎包装成 `DocumentEngineProvider`。**一份文档一个 helper 进程。**
 *
 * ★ 一文档一进程是刻意的:某份损坏文档把引擎拖崩,只影响它自己的会话,
 * 其它打开着的文档里未保存的输入不会被一起带走。代价是多份文档多份内存。
 */
export class NativeDocumentEngineProvider implements DocumentEngineProvider {
  constructor(
    private readonly options: {
      id: string
      formats: readonly DocumentFormat[]
      /** 每次 spawn 前核对并返回入口绝对路径(`resolveVerifiedEntry`) */
      resolveEntry: () => Promise<string>
      /** 账户私有目录下 helper 的工作根 */
      workRoot: string
      startupTimeoutMs?: number
      spawnImpl?: SpawnHelper
      args?: string[]
    }
  ) {}

  get id(): string { return this.options.id }
  get formats(): readonly DocumentFormat[] { return this.options.formats }

  async open(
    input: { workingPath: string; format: DocumentFormat; onCrash: () => void },
    signal: AbortSignal
  ): Promise<DocumentEngineHandle> {
    const command = await this.options.resolveEntry()
    const connection = await NativeHelperConnection.start({
      command,
      ...(this.options.args === undefined ? {} : { args: this.options.args }),
      workDir: join(this.options.workRoot, randomUUID()),
      ...(this.options.startupTimeoutMs === undefined ? {} : { startupTimeoutMs: this.options.startupTimeoutMs }),
      ...(this.options.spawnImpl === undefined ? {} : { spawnImpl: this.options.spawnImpl })
    })
    const unsubscribe = connection.onExit(() => { input.onCrash() })
    let capabilities: DocumentCapabilities
    try {
      const opened = await connection.request('document.open', { path: input.workingPath, format: input.format }, signal)
      capabilities = parseCapabilities(opened, input.format, connection.engineVersion)
    } catch (error) {
      unsubscribe()
      await connection.close()
      throw error
    }
    return {
      capabilities,
      apply: async (operations: DocumentOperation[], applySignal: AbortSignal) => {
        const raw = (await connection.request('document.apply', { operations }, applySignal) ?? {}) as Record<string, unknown>
        const warnings = Array.isArray(raw.warnings) ? raw.warnings.filter((w): w is string => typeof w === 'string').slice(0, 50).map((w) => w.slice(0, 500)) : []
        const results = parseOperationResults(raw.results, operations.length)
        return { warnings, undoable: raw.undoable === true, ...(results === undefined ? {} : { results }) }
      },
      saveTo: async (outputPath: string, saveSignal: AbortSignal) => {
        await connection.request('document.saveAs', { path: outputPath, format: input.format }, saveSignal)
      },
      render: async (request: DocumentRenderRequest, renderSignal: AbortSignal): Promise<DocumentRenderResult> => {
        const expectedBytes = request.width * request.height * 4
        const response = await connection.requestAttachment('document.render', request, expectedBytes, renderSignal)
        const raw = response.result
        if (raw === null || typeof raw !== 'object') throw new DocumentEngineError('io', 'render response is invalid')
        const result = raw as Record<string, unknown>
        if (result.width !== request.width || result.height !== request.height || result.format !== 'rgba') {
          throw new DocumentEngineError('io', 'render response dimensions or format are invalid')
        }
        return { width: request.width, height: request.height, format: 'rgba', bytes: response.bytes }
      },
      exportTo: async (outputPath: string, format: DocumentFormat, exportSignal: AbortSignal) => {
        /*
          需求:导出复用协议里已有的 `document.saveAs { path, format }`,不发
          `document.exportAs`。原因:v1 协议里没有 exportAs 这个方法,发一条 helper
          不认识的请求等于永远等不到回执;而 saveAs 已经带 format,目标格式由 helper
          按它自己的转换能力写出。
          不满足会怎样:导出一直挂到会话超时,用户看到的是「导出中」永远不结束。
        */
        await connection.request('document.saveAs', { path: outputPath, format }, exportSignal)
      },
      // 查询请求已由管理器 `validateQuery` 收窄过;结果是 helper 的回执,原样交回调用方
      query: async (request: DocumentQuery, querySignal: AbortSignal) => connection.request('document.query', request, querySignal),
      input: async (events: DocumentInputEvent[], inputSignal: AbortSignal): Promise<DocumentInputResult> => {
        const parsed = parseInputResult(await connection.request('document.input', { events }, inputSignal))
        // ★ 回执形状不对 = 不知道这批按键有没有改模型;按引擎故障上抛,由管理器标崩溃,不猜
        if (parsed === null) throw new DocumentEngineError('io', 'input response is invalid')
        return parsed
      },
      command: async (command: DocumentCommandId, args: CommandArgs | undefined, commandSignal: AbortSignal): Promise<DocumentInputResult> => {
        const parsed = parseInputResult(await connection.request('document.command', args === undefined ? { command } : { command, args }, commandSignal))
        // 同 input:回执说不清改没改模型,就按引擎故障处理
        if (parsed === null) throw new DocumentEngineError('io', 'command response is invalid')
        return parsed
      },
      close: async () => {
        unsubscribe()
        await connection.close()
      }
    }
  }
}
