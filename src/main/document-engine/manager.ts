/**
 * 文档会话管理器 —— 「同一个文档只有一个活动模型」这条不变式的唯一持有者。
 *
 * ## 为了什么需求建的
 *
 * 办公插件要求编辑器画布与 Agent 工具操作**同一个**活动文档:用户正在打字的
 * 未保存内容,Agent 的修改必须叠在它上面,而不是另开一份、改完覆盖回磁盘。
 * 做到这一点只能让所有修改(UI 输入、Agent 批次、宏、保存)进入**同一个会话的
 * 同一条串行队列**,并由一个地方记账修订号。这个类就是那个地方。
 *
 * ## 它拥有 / 不拥有什么
 *
 * - 拥有:会话身份与去重、调用方作用域、串行队列、修订号与保存状态、操作回执
 *   (含「结果未知」)、私有工作副本的生命周期。
 * - 不拥有:文档语义。排版、计算、对象模型全在引擎 provider 里(插件携带的
 *   LibreOffice helper);这里只认 `DocumentEngineProvider` 接口,于是能用
 *   fake provider 在无头 Node 里把全部账目测到(计划 §12 B)。
 * - 不做远程:传进来的是**本机绝对路径**。SSH 工作区的回写 / 租约没有实现,
 *   调用方必须在进来之前拒绝(返回 `unsupported_environment`),不能静默退回本地。
 * - 不做崩溃快照恢复:`reloadFromDisk` 只能回到上次**已保存**的内容,未保存改动会丢,
 *   这一点在返回值里如实体现(dirty=false、generation+1)。恢复快照(计划 §5
 *   `recovery.ts`)接上之前,不许把这条路叫做「恢复」。
 *
 * ## 不依赖 electron
 *
 * 只用 node:fs 与纯函数,和 `kernel/**` 同一立场:无头测试能直接构造它。
 */
import { randomUUID } from 'node:crypto'
import type { Stats } from 'node:fs'
import { lstat, realpath, rm, stat } from 'node:fs/promises'
import { dirname, join } from 'node:path'
import {
  DocumentEngineError,
  documentFormatOf,
  validateOperations,
  validateQuery,
  type DocumentApplyResult,
  type DocumentCapabilities,
  type DocumentFormat,
  type DocumentOperation,
  type DocumentOperationResult,
  type DocumentQuery,
  type DocumentRenderRequest,
  type DocumentRenderResult
} from '../../shared/document-engine/protocol'
import { validateInputEvents, type DocumentInputEvent, type DocumentInputResult } from '../../shared/document-engine/interaction'
import { validateCommand, type CommandArgs, type DocumentCommandId } from '../../shared/document-engine/commands'
import {
  applyPrecondition,
  initialSession,
  isSessionDirty,
  reduceSession,
  type DocumentSessionEvent,
  type DocumentSessionSnapshot
} from '../../shared/document-engine/session'
import { commitExport, commitSave, createWorkingCopy, digestFile } from './file-store'

/** 引擎打开的一份文档。实现方是原生 helper 的适配层(或测试里的 fake)。 */
export interface DocumentEngineHandle {
  readonly capabilities: DocumentCapabilities
  apply(operations: DocumentOperation[], signal: AbortSignal): Promise<{ warnings: string[]; undoable: boolean; results?: DocumentOperationResult[] }>
  /** 把当前模型写到核心给出的路径。★ 路径由核心决定,引擎不能自选保存位置 */
  saveTo(outputPath: string, signal: AbortSignal): Promise<void>
  /** 只读查询。可选:不支持查询的引擎不实现它,管理器如实报 `unsupported_operation` */
  query?(request: DocumentQuery, signal: AbortSignal): Promise<unknown>
  /** 预览 tile。可选:不支持画布的引擎不能伪造一张空图。 */
  render?(request: DocumentRenderRequest, signal: AbortSignal): Promise<DocumentRenderResult>
  /** 导出到核心指定的工作区外部产物路径,不改变当前 session 的保存状态。 */
  exportTo?(outputPath: string, format: DocumentFormat, signal: AbortSignal): Promise<void>
  /** 画布交互输入。可选:只读引擎不实现;事件已由管理器按 `capabilities.interaction` 收窄 */
  input?(events: DocumentInputEvent[], signal: AbortSignal): Promise<DocumentInputResult>
  /** 功能区命令。可选:没有命令表的引擎不实现;命令已由管理器按 `capabilities.commands` 收窄 */
  command?(command: DocumentCommandId, args: CommandArgs | undefined, signal: AbortSignal): Promise<DocumentInputResult>
  close(): Promise<void>
}

export interface DocumentEngineProvider {
  /** `<pluginId>/<engineId>`,与清单里 `customEditors[].documentEngine` 的写法一致 */
  readonly id: string
  readonly formats: readonly DocumentFormat[]
  open(
    input: { workingPath: string; format: DocumentFormat; onCrash: () => void },
    signal: AbortSignal
  ): Promise<DocumentEngineHandle>
}

export interface DocumentSessionManagerOptions {
  /** 账户私有目录。工作副本与保存产物只落在这里 */
  privateDir: string
  /** 单次引擎调用的超时。超时的修改记为「结果未知」 */
  timeoutMs?: number
  resolveRealPath?: (path: string) => Promise<string>
  newId?: () => string
  onChange?: (snapshot: DocumentSessionSnapshot) => void
}

/** 调用方作用域。★ 由核心从受信上下文派生(Tab 绑定 / 工具调用 token),不是模型传的 */
export interface DocumentCallerScope {
  accountScope: string
  workspaceId: string
}

export type OperationRecord =
  | { status: 'applied'; sessionId: string; result: DocumentApplyResult }
  | { status: 'rejected'; sessionId: string; code: string }
  | { status: 'unknown'; sessionId: string }

interface Session {
  snapshot: DocumentSessionSnapshot
  key: string
  accountScope: string
  workspaceIds: Set<string>
  absolutePath: string
  providerId: string
  handle: DocumentEngineHandle | null
  workingDir: string
  views: Set<string>
  queue: Promise<unknown>
  /** 首次打开完成(成功或失败)时 settle。并发打开同一文件的第二个调用方等它 */
  loading: Promise<void>
  /**
   * 正在进行的 `handle.close()`。★ 崩溃时 `markCrashed` 会 fire-and-forget 地起一次
   * 关闭;之后 `closeAll` 再进来时 `handle` 已经是 null,若不复用这个 promise,收尾会
   * 在 helper 真正退出**之前**就完成,私有目录被删而子进程还在跑。
   */
  closing: Promise<void> | null
}

const DEFAULT_TIMEOUT_MS = 60_000
/**
 * 单次 tile 的**像素**边长上限。★ 画布请求来自插件视图,不能因为一个错误的缩放值就让
 * helper 分配超大 RGBA 缓冲;`width * height * 4` 到 `2048 * 2048 * 4` 就是 16 MiB ——
 * 这个预算是我们**推的,不是量出来的**(没有跑过真实 helper 的峰值内存),所以它只
 * 约束要分配的像素缓冲,不约束文档单位。超过上限会在进入会话队列前被拒绝,表现为
 * 明确的 `invalid_operation`,而不是把编辑器和其它文档一起拖慢。
 */
const MAX_RENDER_DIMENSION = 2048
/**
 * 文档单位坐标 / tile 尺寸的上限。
 *
 * 需求:`x` / `y` / `tileWidth` / `tileHeight` 是**文档坐标**,不是像素。A1 表格最后一列
 * (16384)或 CAD 图纸的坐标轻松超过 2048,用像素上限去卡它们会把正常文档判成非法。
 * 这里只保证它们是**有界的正 safe integer** —— 有界是为了让 helper 拿到不可能溢出的数,
 * 1e9 远大于任何真实文档坐标。
 *
 * ★ 不要因为一个假 helper 的测试期望就把这些值收到 2048:那是在为测试改产品约束
 * (看起来是「顺手收紧」,实际会让真实文档的预览失效)。
 */
const MAX_RENDER_TILE_UNITS = 1_000_000_000
/** 回执表上限。★ 不设上限的话一个长会话里 Agent 每次调用都留一条,永不释放 */
const MAX_OPERATION_RECORDS = 2000

function isClosed(session: Session): boolean {
  return session.snapshot.status === 'closed'
}

export class DocumentSessionManager {
  private readonly sessions = new Map<string, Session>()
  private readonly byKey = new Map<string, string>()
  private readonly providers = new Map<string, DocumentEngineProvider>()
  private readonly operations = new Map<string, OperationRecord>()
  private readonly timeoutMs: number
  private readonly resolveRealPath: (path: string) => Promise<string>
  private readonly newId: () => string

  constructor(private readonly options: DocumentSessionManagerOptions) {
    this.timeoutMs = options.timeoutMs ?? DEFAULT_TIMEOUT_MS
    this.resolveRealPath = options.resolveRealPath ?? realpath
    this.newId = options.newId ?? randomUUID
  }

  /** 登记一个已安装、已批准、版本匹配的引擎。返回撤销函数(插件禁用 / 卸载时调) */
  registerProvider(provider: DocumentEngineProvider): () => void {
    this.providers.set(provider.id, provider)
    return () => { if (this.providers.get(provider.id) === provider) this.providers.delete(provider.id) }
  }

  /**
   * 打开(或加入)一个文档会话,返回快照与一个新的 viewId。
   *
   * ★ 按「账户 + realpath」去重:同一个文件经两个工作区、或者经两个 Tab 打开时,
   * 拿到的是**同一个**会话。两份独立模型各自保存,后存的会把先存的静默覆盖。
   */
  async open(input: { scope: DocumentCallerScope; absolutePath: string; providerId: string }): Promise<{
    snapshot: DocumentSessionSnapshot
    capabilities: DocumentCapabilities
    viewId: string
  }> {
    const format = documentFormatOf(input.absolutePath)
    if (format === null) throw new DocumentEngineError('unsupported_format', 'this file type is not handled by document engines')
    const provider = this.providers.get(input.providerId)
    if (provider === undefined) throw new DocumentEngineError('engine_unavailable', `document engine ${input.providerId} is not installed or not enabled`)
    if (!provider.formats.includes(format)) throw new DocumentEngineError('unsupported_format', `${input.providerId} does not open .${format}`)

    const real = await this.resolveRealPath(input.absolutePath).catch(() => {
      throw new DocumentEngineError('io', 'document does not exist')
    })
    const key = `${input.scope.accountScope}\0${real}`
    const existingId = this.byKey.get(key)
    if (existingId !== undefined) {
      const existing = this.sessions.get(existingId)
      if (existing !== undefined && existing.snapshot.status !== 'closed') {
        // 同一个文件被另一个引擎打开是配置错误,不能悄悄换引擎
        if (existing.providerId !== input.providerId) {
          throw new DocumentEngineError('engine_unavailable', 'this document is already open in another document engine')
        }
        existing.workspaceIds.add(input.scope.workspaceId)
        /*
          ★ 先等首次打开完成再加视图。首个打开者还在复制 / 起引擎时,第二个 Tab
          立刻报「引擎不可用」会被用户理解成插件坏了;而先加视图再报错会泄漏一个
          永远不会被 release 的 viewId,于是这个会话再也关不掉。
        */
        await existing.loading
        // ★ 经函数读一次:await 期间状态可能已变成 closed,TS 的窄化不会跟着失效
        if (existing.handle === null || isClosed(existing)) {
          throw new DocumentEngineError('engine_unavailable', 'the document engine stopped while opening')
        }
        const viewId = this.newId()
        existing.views.add(viewId)
        return { snapshot: existing.snapshot, capabilities: existing.handle.capabilities, viewId }
      }
    }

    const sessionId = this.newId()
    let settle: () => void = () => undefined
    const session: Session = {
      snapshot: initialSession(sessionId, format),
      key,
      accountScope: input.scope.accountScope,
      workspaceIds: new Set([input.scope.workspaceId]),
      absolutePath: input.absolutePath,
      providerId: input.providerId,
      handle: null,
      workingDir: join(this.options.privateDir, sessionId),
      views: new Set(),
      queue: Promise.resolve(),
      loading: new Promise<void>((resolve) => { settle = resolve }),
      closing: null
    }
    this.sessions.set(sessionId, session)
    this.byKey.set(key, sessionId)
    try {
      const { workingPath, diskRevision } = await createWorkingCopy(input.absolutePath, session.workingDir)
      session.handle = await this.withTimeout((signal) =>
        provider.open({ workingPath, format, onCrash: () => { this.markCrashed(session) } }, signal)
      )
      this.dispatch(session, { type: 'loaded', diskRevision })
    } catch (error) {
      // 打开失败不留半个会话:否则同一文件再开一次会命中一个永远 loading 的条目
      this.sessions.delete(sessionId)
      if (this.byKey.get(key) === sessionId) this.byKey.delete(key)
      session.snapshot = reduceSession(session.snapshot, { type: 'closed' })
      await rm(session.workingDir, { recursive: true, force: true })
      throw error
    } finally {
      settle()
    }
    const viewId = this.newId()
    session.views.add(viewId)
    return { snapshot: session.snapshot, capabilities: session.handle.capabilities, viewId }
  }

  snapshot(sessionId: string, scope: DocumentCallerScope): DocumentSessionSnapshot {
    return this.require(sessionId, scope).snapshot
  }

  capabilities(sessionId: string, scope: DocumentCallerScope): DocumentCapabilities {
    const session = this.require(sessionId, scope)
    if (session.handle === null) throw new DocumentEngineError('engine_unavailable', 'the document engine is not running')
    return session.handle.capabilities
  }

  getOperation(operationId: string): OperationRecord | undefined {
    return this.operations.get(operationId)
  }

  /**
   * 提交一批修改。进串行队列,按 generation / modelRevision 校验前置条件。
   *
   * ★ 同一个 `operationId` 再提交:已生效 → 原样返回上次回执(幂等);结果未知 →
   * 继续报 `result_unknown`。**绝不重放** —— 重放一次「追加段落」就是追加了两段。
   */
  apply(input: {
    sessionId: string
    scope: DocumentCallerScope
    operationId: string
    generation: number
    modelRevision: number
    operations: unknown
  }): Promise<DocumentApplyResult> {
    const session = this.require(input.sessionId, input.scope)
    if (input.operationId === '' || input.operationId.length > 128) {
      return Promise.reject(new DocumentEngineError('invalid_operation', 'operationId is required'))
    }
    const previous = this.operations.get(input.operationId)
    if (previous !== undefined) {
      if (previous.sessionId !== session.snapshot.sessionId) return Promise.reject(new DocumentEngineError('invalid_operation', 'operationId belongs to another document'))
      if (previous.status === 'applied') return Promise.resolve(previous.result)
      if (previous.status === 'unknown') return Promise.reject(new DocumentEngineError('result_unknown', 'this operation may or may not have been applied; read the document state before retrying with a new operationId'))
      return Promise.reject(new DocumentEngineError('invalid_operation', `this operationId was already rejected (${previous.code}); use a new one`))
    }
    return this.enqueue(session, async () => {
      const handle = session.handle
      const blocked = applyPrecondition(session.snapshot, { generation: input.generation, modelRevision: input.modelRevision })
      if (blocked !== null || handle === null) {
        const code = blocked ?? 'engine_unavailable'
        this.record(input.operationId, { status: 'rejected', sessionId: session.snapshot.sessionId, code })
        throw new DocumentEngineError(code, `cannot apply: ${code}`)
      }
      const validated = validateOperations(input.operations, handle.capabilities)
      if (!validated.ok) {
        const code = validated.reason.includes('unsupported_operation') ? 'unsupported_operation' : 'invalid_operation'
        this.record(input.operationId, { status: 'rejected', sessionId: session.snapshot.sessionId, code })
        throw new DocumentEngineError(code, validated.reason)
      }
      /*
        ★ 操作里的语义引用也要核 generation,不只是批次头上那一个:批次头是调用方
        「以为」的 generation,而引用是它当初查询时拿到的。两者都要等于当前 generation,
        否则引擎重启后一个旧引用会指到新模型里另一个碰巧同 id 的对象上。
      */
      const staleRef = validated.operations.find((op) => 'target' in op && op.target.generation !== session.snapshot.generation)
      if (staleRef !== undefined) {
        this.record(input.operationId, { status: 'rejected', sessionId: session.snapshot.sessionId, code: 'stale_generation' })
        throw new DocumentEngineError('stale_generation', 'an operation target comes from an older engine generation; query the document again')
      }
      let outcome: { warnings: string[]; undoable: boolean; results?: DocumentOperationResult[] }
      try {
        outcome = await this.withTimeout((signal) => handle.apply(validated.operations, signal))
      } catch (error) {
        /*
          ★ 引擎没有给出明确「未生效」的拒绝(`invalid_operation` / `unsupported_operation` / `busy`,见 `isEngineRefusal`)
          时,一律按**结果未知**处理并把会话标为崩溃:超时或 helper 中途退出之后,
          模型可能已经改了一半。继续在上面叠修改,修订号就和真实内容对不上了。
        */
        if (isEngineRefusal(error)) {
          // busy 不是对这批操作的裁决(只是用户正在组字):不记账,同一个 operationId 稍后可以原样重试
          if (error.code !== 'busy') this.record(input.operationId, { status: 'rejected', sessionId: session.snapshot.sessionId, code: error.code })
          throw error
        }
        this.record(input.operationId, { status: 'unknown', sessionId: session.snapshot.sessionId })
        this.markCrashed(session)
        throw new DocumentEngineError('result_unknown', `the engine did not confirm the operation: ${(error as Error).message}`)
      }
      const revision = session.snapshot.modelRevision + 1
      this.dispatch(session, { type: 'applied', revision })
      const result: DocumentApplyResult = {
        operationId: input.operationId,
        appliedRevision: revision,
        dirty: isSessionDirty(session.snapshot),
        warnings: outcome.warnings,
        undoable: outcome.undoable,
        ...(outcome.results === undefined ? {} : { results: outcome.results })
      }
      this.record(input.operationId, { status: 'applied', sessionId: session.snapshot.sessionId, result })
      return result
    })
  }

  /**
   * 只读查询(大纲 / 正文 / 单元格区域)。进同一条串行队列:查询读到的必须是
   * 它前面那些修改**之后**的状态,否则 Agent 会拿着过期内容去构造下一批修改。
   *
   * ★ 查询超时同样把会话标为崩溃:引擎连只读都答不上来,继续往里塞修改只会得到更多
   * 「结果未知」。
   */
  query(input: { sessionId: string; scope: DocumentCallerScope; request: unknown }): Promise<{ generation: number; modelRevision: number; result: unknown }> {
    const session = this.require(input.sessionId, input.scope)
    const request = validateQuery(input.request)
    if (typeof request === 'string') return Promise.reject(new DocumentEngineError('invalid_operation', request))
    return this.enqueue(session, async () => {
      const handle = session.handle
      if (session.snapshot.status === 'crashed' || session.snapshot.status === 'recovering' || handle === null) {
        throw new DocumentEngineError('engine_unavailable', `cannot query while ${session.snapshot.status}`)
      }
      if (handle.query === undefined) throw new DocumentEngineError('unsupported_operation', 'this document engine does not support queries')
      const query = handle.query.bind(handle)
      try {
        const result = await this.withTimeout((signal) => query(request, signal))
        return { generation: session.snapshot.generation, modelRevision: session.snapshot.modelRevision, result }
      } catch (error) {
        if (isEngineRefusal(error)) throw error
        this.markCrashed(session)
        throw error
      }
    })
  }

  /**
   * 从当前活动模型渲染一个 RGBA tile。渲染和修改共用同一条队列，避免 helper 在
   * 用户输入与 Agent 批次之间读到半更新模型。
   *
   * ★ 附件缺失、尺寸不符或协议返回多余字节都按引擎故障处理并标记 crashed；若把
   * 这类结果当成空白预览，用户会继续编辑一个实际已经失去同步的文档。
   */
  render(input: { sessionId: string; scope: DocumentCallerScope; request: unknown }): Promise<DocumentRenderResult & { generation: number; modelRevision: number }> {
    const session = this.require(input.sessionId, input.scope)
    const request = validateRenderRequest(input.request)
    if (typeof request === 'string') return Promise.reject(new DocumentEngineError('invalid_operation', request))
    return this.enqueue(session, async () => {
      const handle = session.handle
      if (session.snapshot.status === 'crashed' || session.snapshot.status === 'recovering' || session.snapshot.status === 'loading' || handle === null) {
        throw new DocumentEngineError('engine_unavailable', `cannot render while ${session.snapshot.status}`)
      }
      // ★ 可选方法只取一次:下面每个分支都用同一个绑定,避免 `?.` 与 `??` 各判一次还各造一个错误
      const render = handle.render
      if (render === undefined) throw new DocumentEngineError('unsupported_operation', 'this document engine does not support rendering')
      const boundRender = render.bind(handle)
      let rendered: DocumentRenderResult
      try {
        rendered = await this.withTimeout((signal) => boundRender(request, signal))
      } catch (error) {
        if (isEngineRefusal(error)) throw error
        this.markCrashed(session)
        throw error instanceof DocumentEngineError ? error : new DocumentEngineError('io', (error as Error).message)
      }
      const expectedBytes = request.width * request.height * 4
      if (rendered.width !== request.width || rendered.height !== request.height || rendered.format !== 'rgba' || rendered.bytes.byteLength !== expectedBytes) {
        this.markCrashed(session)
        throw new DocumentEngineError('io', 'document engine returned an invalid render attachment')
      }
      return { ...rendered, generation: session.snapshot.generation, modelRevision: session.snapshot.modelRevision }
    })
  }

  /**
   * 画布上的一批交互输入(键鼠 / 输入法 / 切换部分),以及输入之后迟到事件的拉取(空批次)。
   *
   * 需求:用户在画布上的输入必须与 Agent 修改进同一条串行队列、共用一套修订号 ——
   * 用户打了字,Agent 手里基于旧修订号的批次就要被 `stale_revision` 挡下,而不是把
   * 刚打的字覆盖掉。
   *
   * - ★ 只在引擎回报 `modified` 时推进 modelRevision。方向键、点选、输入法组字不改模型,
   *   若也推进,Agent 每读一次就被用户挪一下光标作废一次。
   * - `generation` 必须等于当前代:坐标是视图按**当时**的版面算的,引擎重启后同一坐标
   *   指到的是别处。modelRevision 不校验 —— 用户边看 Agent 改边打字是正常用法,
   *   按键落在引擎当前的光标处,不依赖视图读到的修订号。
   * - 引擎明确拒绝(`invalid_operation` / `unsupported_operation`)= 整批未投递,原样抛;
   *   其它失败(超时、helper 退出、回执不可解析)= 不知道按键进没进模型,标崩溃,
   *   与 `apply` 的「结果未知」同一处理 —— 继续在上面叠输入,修订号就和真实内容对不上了。
   */
  input(input: { sessionId: string; scope: DocumentCallerScope; generation: number; events: unknown }): Promise<DocumentInputResult & { generation: number; modelRevision: number }> {
    return this.interactive(input, 'input', (handle) => {
      const interaction = handle.capabilities.interaction
      const send = handle.input
      if (interaction === undefined || send === undefined) throw new DocumentEngineError('unsupported_operation', 'this document engine does not take interactive input')
      const validated = validateInputEvents(input.events, interaction)
      if (!validated.ok) {
        throw new DocumentEngineError(validated.reason.includes('unsupported_operation') ? 'unsupported_operation' : 'invalid_operation', validated.reason)
      }
      const boundSend = send.bind(handle)
      return (signal) => boundSend(validated.events, signal)
    })
  }

  /**
   * 功能区命令(加粗、字号、对齐、插入表格……),在用户光标 / 选区处执行。
   *
   * 需求:与画布输入同一种账目 —— 它就是一次「用户操作」,只是来自工具栏而不是键盘:
   * 同一条队列、同一套修订号、`modified` 才推进、结果不明标崩溃(见 `input` 的说明)。
   * 命令先按 `commands.ts` 的封闭表与引擎声明的 `capabilities.commands` 收窄,不认识的不送进引擎。
   */
  command(input: { sessionId: string; scope: DocumentCallerScope; generation: number; command: unknown; args?: unknown }): Promise<DocumentInputResult & { generation: number; modelRevision: number }> {
    return this.interactive(input, 'command', (handle) => {
      const run = handle.command
      if (run === undefined) throw new DocumentEngineError('unsupported_operation', 'this document engine does not run ribbon commands')
      const validated = validateCommand(input.command, input.args, handle.capabilities.commands ?? [])
      if (!validated.ok) {
        throw new DocumentEngineError(validated.reason.startsWith('unsupported_operation') ? 'unsupported_operation' : 'invalid_operation', validated.reason)
      }
      const boundRun = run.bind(handle)
      return (signal) => boundRun(validated.command, validated.args, signal)
    })
  }

  /**
   * 画布输入与功能区命令共用的账目:状态门、generation 门、超时即结果不明、`modified` 才推进修订号。
   * (原先内联在 `input` 里;加了功能区命令之后抽出,两份复制的话迟早只改一份。)
   * `prepare` 在队列里、状态门之后执行:收窄失败直接抛,不碰引擎。
   */
  private interactive(
    input: { sessionId: string; scope: DocumentCallerScope; generation: number },
    what: 'input' | 'command',
    prepare: (handle: DocumentEngineHandle) => (signal: AbortSignal) => Promise<DocumentInputResult>
  ): Promise<DocumentInputResult & { generation: number; modelRevision: number }> {
    const session = this.require(input.sessionId, input.scope)
    return this.enqueue(session, async () => {
      const handle = session.handle
      const status = session.snapshot.status
      if (status === 'crashed' || status === 'recovering' || status === 'loading' || handle === null) {
        throw new DocumentEngineError('engine_unavailable', `cannot take ${what} while ${status}`)
      }
      if (input.generation !== session.snapshot.generation) throw new DocumentEngineError('stale_generation', 'the view was laid out by an older engine generation; reload the view')
      const send = prepare(handle)
      let result: DocumentInputResult
      try {
        result = await this.withTimeout(send)
      } catch (error) {
        if (isEngineRefusal(error)) throw error
        this.markCrashed(session)
        throw new DocumentEngineError('result_unknown', `the engine did not confirm the ${what}: ${(error as Error).message}`)
      }
      if (result.modified) this.dispatch(session, { type: 'applied', revision: session.snapshot.modelRevision + 1 })
      return { ...result, generation: session.snapshot.generation, modelRevision: session.snapshot.modelRevision }
    })
  }

  /**
   * 导出当前模型到调用方已校验的产物路径。导出不是保存:不改变 dirty / diskRevision,
   * 也不把 helper 的文件失败记成引擎崩溃。
   *
   * ★ 路径由 document RPC 在进入这里前收窄,provider 只能拿到核心决定的路径,且请求的
   * 是**会话私有目录**里的临时路径 —— helper 一次都不直接写工作区;发布由 `commitExport`
   * 在临时产物校验通过之后做。
   *
   * `overwrite` 默认 false:目标已存在时拒绝而不是覆盖(导出常见于「另存为」,盖掉别人
   * 放在那里的文件是静默丢数据)。为 true 时用**开始时**目标的摘要做外部冲突检查。
   */
  exportDocument(input: {
    sessionId: string
    scope: DocumentCallerScope
    outputPath: string
    format: DocumentFormat
    overwrite?: boolean
  }): Promise<{ snapshot: DocumentSessionSnapshot; outputPath: string }> {
    const session = this.require(input.sessionId, input.scope)
    const overwrite = input.overwrite === true
    return this.enqueue(session, async () => {
      const handle = session.handle
      if (session.snapshot.status !== 'ready' || handle === null) throw new DocumentEngineError('engine_unavailable', `cannot export while ${session.snapshot.status}`)
      if (!handle.capabilities.canExport.includes(input.format)) throw new DocumentEngineError('unsupported_operation', `this document engine cannot export ${input.format}`)
      // ★ 可选方法只取一次:下面只判一次 undefined,不重复造保护分支
      const exportMethod = handle.exportTo
      if (exportMethod === undefined) throw new DocumentEngineError('unsupported_operation', 'this document engine does not support exporting')
      const exportTo = exportMethod.bind(handle)

      /*
        需求:导出绝不能落到这个会话(或任何活动会话)正在编辑的**原文件**上 —— 那绕过
        会话的修订号直接改盘,之后保存的冲突检查就对不上真实内容了。判定必须比 inode:
        同一个文件经硬链 / 符号链 / 大小写差异有多个名字,只比路径字符串挡不住。
        不满足会怎样:导出覆盖正在编辑的文件后,会话仍以为盘上是它上次读到的版本。
      */
      const targetInfo = await lstatExportTarget(input.outputPath)
      await this.assertExportTargetAllowed(session, input.outputPath, targetInfo)
      if (targetInfo !== null && !overwrite) throw new DocumentEngineError('invalid_operation', 'export target already exists; pass overwrite to replace it')

      /*
        ★ 目标父目录的规范路径在等待 helper 期间必须不变。rpc 进场时检查过一次,但那一次
        挡不住等待窗口里有人把父目录换成软链 —— 产物于是写到链接指向的别处。realpath 失败
        直接抛(不吞),否则「解析不了」会被当成「没变化」。
      */
      const parentBefore = await this.resolveParent(input.outputPath)
      const expectedDiskRevision = targetInfo === null ? null : await digestFile(input.outputPath)

      const produced = join(session.workingDir, `export-${this.newId()}.${input.format}`)
      try {
        /*
          需求:helper 调用失败和文件发布失败要分开记账 —— 前者说明引擎可能已经不可用,
          按崩溃处理;后者只是这一步的文件操作失败,会话账目(dirty / revision)不该被
          连带改掉,更不能标 crashed。两者混在同一个 catch 里就会把「目标已存在」这种
          纯发布错误也记成引擎崩溃。
        */
        try {
          await this.withTimeout((signal) => exportTo(produced, input.format, signal))
        } catch (error) {
          if (isEngineRefusal(error)) throw error
          this.markCrashed(session)
          throw error instanceof DocumentEngineError ? error : new DocumentEngineError('io', (error as Error).message)
        }

        const parentAfter = await this.resolveParent(input.outputPath)
        if (parentAfter !== parentBefore) throw new DocumentEngineError('io', 'export directory changed while exporting')
        // helper 跑着的时候目标可能被换成了软链 / 换成非普通文件;lstatExportTarget 会当场抛 io
        const publishedTarget = await lstatExportTarget(input.outputPath)
        // 需求：等待导出时别的会话也可能打开目标，发布前必须再核对一次归属。
        await this.assertExportTargetAllowed(session, input.outputPath, publishedTarget)

        await commitExport(input.outputPath, produced, { overwrite, expectedDiskRevision })
      } finally {
        // 临时产物无论发布成功还是失败都清掉,不留半份导出物在私有目录里
        await rm(produced, { force: true })
      }
      return { snapshot: session.snapshot, outputPath: input.outputPath }
    })
  }

  /**
   * 保存到原文件。冲突(盘上被别人改过)进入 `conflict`,**不覆盖**。
   * 没有修改时是 no-op,不顶 mtime。
   */
  save(input: { sessionId: string; scope: DocumentCallerScope }): Promise<DocumentSessionSnapshot> {
    const session = this.require(input.sessionId, input.scope)
    return this.enqueue(session, async () => {
      const handle = session.handle
      const status = session.snapshot.status
      if (status === 'conflict') throw new DocumentEngineError('disk_conflict', 'resolve the disk conflict before saving')
      if (status !== 'ready' || handle === null) throw new DocumentEngineError('engine_unavailable', `cannot save while ${status}`)
      /*
        ★ 引擎说不能存就不存:PDF 经 LibreOffice Draw 导入,「保存」会整份重写 PDF
        (字体子集、结构、签名都可能变),那不是用户以为的「保存」。
      */
      if (!handle.capabilities.canSave) throw new DocumentEngineError('unsupported_operation', 'this document engine cannot save this format in place')
      if (!isSessionDirty(session.snapshot)) return session.snapshot
      // 需求：helper 保存期间父目录也可能被换成软链，提交前必须核对原始落点未改变。
      const sourceBefore = await this.resolveRealPath(session.absolutePath)
      const savedModelRevision = session.snapshot.modelRevision
      this.dispatch(session, { type: 'saveStarted' })
      const produced = join(session.workingDir, `save-${this.newId()}.${session.snapshot.format}`)
      try {
        await this.withTimeout((signal) => handle.saveTo(produced, signal))
        if (await this.resolveRealPath(session.absolutePath) !== sourceBefore) {
          throw new DocumentEngineError('disk_conflict', 'document path changed while saving')
        }
        const diskRevision = await commitSave(session.absolutePath, produced, session.snapshot.diskRevision)
        this.dispatch(session, { type: 'saved', diskRevision, savedModelRevision })
        return session.snapshot
      } catch (error) {
        if (error instanceof DocumentEngineError && error.code === 'disk_conflict') this.dispatch(session, { type: 'diskConflict' })
        else this.dispatch(session, { type: 'saveFailed' })
        throw error
      } finally {
        await rm(produced, { force: true })
      }
    })
  }

  /**
   * 丢弃活动模型、从盘上重新读取。用于引擎崩溃之后,或用户在冲突时选择「采用磁盘版本」。
   *
   * ★ 未保存的改动**会丢**(恢复快照还没有实现,见文件头)。generation +1,
   * 旧引用全部失效。
   */
  reloadFromDisk(input: { sessionId: string; scope: DocumentCallerScope }): Promise<DocumentSessionSnapshot> {
    const session = this.require(input.sessionId, input.scope)
    return this.enqueue(session, async () => {
      const status = session.snapshot.status
      if (status !== 'crashed' && status !== 'conflict') throw new DocumentEngineError('invalid_operation', `nothing to reload from while ${status}`)
      const provider = this.providers.get(session.providerId)
      if (provider === undefined) throw new DocumentEngineError('engine_unavailable', `document engine ${session.providerId} is not available`)
      if (status === 'crashed') this.dispatch(session, { type: 'recovering' })
      await this.closeHandle(session)
      await rm(session.workingDir, { recursive: true, force: true })
      const { workingPath, diskRevision } = await createWorkingCopy(session.absolutePath, session.workingDir)
      session.handle = await this.withTimeout((signal) =>
        provider.open({ workingPath, format: session.snapshot.format, onCrash: () => { this.markCrashed(session) } }, signal)
      )
      this.dispatch(session, { type: 'reloaded', diskRevision, restoredRevision: session.snapshot.savedRevision })
      return session.snapshot
    })
  }

  /**
   * 一个视图不用了。最后一个视图离开时:干净 → 关闭会话;脏 → **保留会话**并如实告诉调用方,
   * 由它决定保存还是 `force` 丢弃。
   *
   * ★ 脏会话不自动关:关了就是静默丢掉用户没保存的输入(计划 §5)。
   */
  release(input: { sessionId: string; scope: DocumentCallerScope; viewId: string; force?: boolean }): Promise<{ closed: boolean; dirty: boolean }> {
    const session = this.require(input.sessionId, input.scope)
    session.views.delete(input.viewId)
    return this.enqueue(session, async () => {
      const dirty = isSessionDirty(session.snapshot)
      if (session.views.size > 0) return { closed: false, dirty }
      if (dirty && input.force !== true) return { closed: false, dirty }
      await this.shutdown(session)
      return { closed: true, dirty: false }
    })
  }

  /** 还有未保存改动的会话。退出 / 切账户 / 禁用插件之前问它 */
  dirtySessions(): DocumentSessionSnapshot[] {
    return [...this.sessions.values()].filter((s) => s.snapshot.status !== 'closed' && isSessionDirty(s.snapshot)).map((s) => s.snapshot)
  }

  /**
   * 收掉某个引擎的全部会话(插件禁用 / 卸载),或者不给 providerId 时收掉全部(退出 / 切账户)。
   * 调用方应先用 `dirtySessions` 处理未保存改动;这里不再询问。
   * `onlyClean` 是 provider 撤销时的安全分支:脏会话留给上层保存或明确丢弃。
   */
  async closeAll(providerId?: string, options?: { onlyClean?: boolean }): Promise<void> {
    const onlyClean = options?.onlyClean === true
    const targets = [...this.sessions.values()].filter((s) => providerId === undefined || s.providerId === providerId)
    await Promise.all(targets.map((session) => this.enqueue(session, async () => {
      /*
        ★ 脏 / 干净必须在**轮到这一刻**重判,不能只按排入队列时的快照过滤:
        队列里可能已经排着一次保存(它会把这个会话改干净)或一次修改(把它变脏),
        按排队时的判断收尾就会把「刚被保存的干净会话」留下,或者把「刚变脏的会话」
        静默关掉。症状是退出时丢了用户还没来得及保存的改动,且全程零报错。
      */
      if (onlyClean && isSessionDirty(session.snapshot)) return
      await this.shutdown(session)
    }).catch(() => undefined)))
  }

  // ─────────────────────────── 内部 ───────────────────────────

  private require(sessionId: string, scope: DocumentCallerScope): Session {
    const session = this.sessions.get(sessionId)
    /*
      ★ 作用域不符和「不存在」报同一个错:区分开的话,一个工作区的调用方可以
      通过错误码探测别的工作区打开了哪些文档。
    */
    if (session === undefined || session.snapshot.status === 'closed' || session.accountScope !== scope.accountScope || !session.workspaceIds.has(scope.workspaceId)) {
      throw new DocumentEngineError('session_closed', 'no such document session')
    }
    return session
  }

  /**
   * 串行队列。★ 前一项失败不能卡住后一项:用 `then(run, run)` 接续,
   * 而返回给调用方的仍是这一项自己的结果 / 错误。
   */
  private enqueue<T>(session: Session, task: () => Promise<T>): Promise<T> {
    const run = session.queue.then(task, task)
    session.queue = run.catch(() => undefined)
    return run
  }

  private async withTimeout<T>(task: (signal: AbortSignal) => Promise<T>): Promise<T> {
    const controller = new AbortController()
    let timer: ReturnType<typeof setTimeout> | undefined
    const timeout = new Promise<never>((_, reject) => {
      timer = setTimeout(() => {
        controller.abort()
        reject(new DocumentEngineError('timeout', `document engine did not answer within ${this.timeoutMs}ms`))
      }, this.timeoutMs)
    })
    try {
      return await Promise.race([task(controller.signal), timeout])
    } finally {
      if (timer !== undefined) clearTimeout(timer)
    }
  }

  private dispatch(session: Session, event: DocumentSessionEvent): void {
    const next = reduceSession(session.snapshot, event)
    if (next === session.snapshot) return
    session.snapshot = next
    try { this.options.onChange?.(next) } catch { /* 订阅方的问题不影响会话账目 */ }
  }

  private markCrashed(session: Session): void {
    if (session.snapshot.status === 'closed') return
    this.dispatch(session, { type: 'crashed' })
    void this.closeHandle(session)
  }

  /**
   * 关掉活动 handle 并返回**这次(或上一次仍在进行的)关闭**的 promise。
   *
   * ★ 必须是共享的一个 promise,不能每次调用各起一次或直接返回:崩溃路径
   * (`markCrashed`)是 fire-and-forget 起的关闭,随后 `closeAll` 再进来时 `handle` 已是
   * null。若这时直接返回,收尾会在 helper 真正退出前完成 —— 私有目录被删、子进程还活着。
   */
  private closeHandle(session: Session): Promise<void> {
    const handle = session.handle
    if (handle !== null) {
      session.handle = null
      session.closing = handle.close().catch(() => undefined)
    }
    return session.closing ?? Promise.resolve()
  }

  private async shutdown(session: Session): Promise<void> {
    await this.closeHandle(session)
    this.dispatch(session, { type: 'closed' })
    this.sessions.delete(session.snapshot.sessionId)
    if (this.byKey.get(session.key) === session.snapshot.sessionId) this.byKey.delete(session.key)
    await rm(session.workingDir, { recursive: true, force: true })
  }

  private record(operationId: string, record: OperationRecord): void {
    this.operations.set(operationId, record)
    if (this.operations.size > MAX_OPERATION_RECORDS) {
      const oldest = this.operations.keys().next().value
      if (oldest !== undefined) this.operations.delete(oldest)
    }
  }

  /**
   * 目标父目录的规范路径。解析不了直接抛 io,**不吞**:调用方把「解析失败」当成
   * 「没变化」会让等待窗口里的替换(父目录被换成软链)蒙混过关。
   */
  private async resolveParent(path: string): Promise<string> {
    try {
      return await this.resolveRealPath(dirname(path))
    } catch (error) {
      throw new DocumentEngineError('io', `cannot resolve export directory: ${(error as Error).message}`)
    }
  }

  /**
   * 需求:导出目标不能是任何**活动会话的原文件** —— 那会绕过它的修订号直接改盘。
   * 自己这个会话的文件报 `documents.save`(那是正确的写回路径),别的会话的文件只报冲突。
   */
  private async assertExportTargetAllowed(session: Session, outputPath: string, targetInfo: Stats | null): Promise<void> {
    for (const other of this.sessions.values()) {
      if (other.snapshot.status === 'closed') continue
      if (!(await this.sameFile(outputPath, targetInfo, other.absolutePath))) continue
      if (other === session) throw new DocumentEngineError('invalid_operation', 'use documents.save to write the open document')
      throw new DocumentEngineError('invalid_operation', 'cannot export over a document that is open in another session')
    }
  }

  /**
   * 两个路径是否指向同一个文件。先比 inode(`dev` + `ino`),挡得住硬链与大小写差异;
   * 目标不存在时退化为规范路径比较(`realpath`),两边任一解析不了就当作不同。
   */
  private async sameFile(a: string, aInfo: Stats | null, b: string): Promise<boolean> {
    // 需求：源文件被外部删除也不能借 export 同路径重建来绕过保存的冲突检查。
    if (a === b) return true
    if (aInfo !== null) {
      try {
        const bInfo = await stat(b)
        if (aInfo.dev === bInfo.dev && aInfo.ino === bInfo.ino) return true
      } catch { /* 另一个会话的原文件按理存在;stat 失败时退回路径比较,不在这里报错 */ }
    }
    try {
      const [ra, rb] = await Promise.all([this.resolveRealPath(a), this.resolveRealPath(b)])
      return ra === rb
    } catch {
      return false
    }
  }
}

/**
 * 目标当前状态:不存在 → `null`;存在时必须是普通文件(软链写穿会改到别处,非文件不是导出目标)。
 * 需求:导出前与 helper 返回后各查一次,字段变化(尤其是「变成了软链」)要能被看见。
 */
async function lstatExportTarget(path: string): Promise<Stats | null> {
  try {
    const info = await lstat(path)
    if (info.isSymbolicLink()) throw new DocumentEngineError('io', 'export target is a symbolic link')
    if (!info.isFile()) throw new DocumentEngineError('io', 'export target is not a regular file')
    return info
  } catch (error) {
    if (error instanceof DocumentEngineError) throw error
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return null
    throw new DocumentEngineError('io', `cannot inspect export target: ${(error as Error).message}`)
  }
}

/**
 * 引擎是否明确回答了「这次什么都没做」。
 *
 * 需求:只有这几种拒绝能证明模型没被碰过,可以原样抛给调用方、会话照常可用;其余失败
 * (超时、helper 退出、回执不可解析)都可能改了一半,必须按结果未知 / 崩溃处理。
 * `busy`(用户正在输入法组字)属于前者 —— 把它当故障的话,用户打中文时 Agent 每读一次,
 * 会话就被标成崩溃一次。
 */
function isEngineRefusal(error: unknown): error is DocumentEngineError {
  return error instanceof DocumentEngineError && (error.code === 'invalid_operation' || error.code === 'unsupported_operation' || error.code === 'busy')
}

function validateRenderRequest(raw: unknown): DocumentRenderRequest | string {
  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) return 'render request must be an object'
  const request = raw as Record<string, unknown>
  /*
    需求:像素尺寸与文档坐标是两回事(见 MAX_RENDER_DIMENSION / MAX_RENDER_TILE_UNITS)。
    `x` / `y` / `tileWidth` / `tileHeight` 只要求是有界正 safe integer,`width` / `height`
    才是要分配 RGBA 缓冲的像素边长。把两者混在一个上限里会让 A1 表格或 CAD 图纸的正常
    坐标被判成非法 —— 那是为了迁就假 helper 的测试而收紧产品约束。
  */
  const units = ['x', 'y', 'tileWidth', 'tileHeight'] as const
  for (const key of units) {
    const value = request[key]
    if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0 || value > MAX_RENDER_TILE_UNITS) return `${key} is invalid`
  }
  const tileWidth = request.tileWidth as number
  const tileHeight = request.tileHeight as number
  if (tileWidth === 0 || tileHeight === 0) return 'render tile dimensions must be positive'
  const pixels = ['width', 'height'] as const
  for (const key of pixels) {
    const value = request[key]
    if (typeof value !== 'number' || !Number.isSafeInteger(value) || value <= 0 || value > MAX_RENDER_DIMENSION) return `${key} is invalid`
  }
  // ★ 形状不对要拒，不能丢掉 part 退回默认部分：那会把别的工作表画进当前画布，且看起来正常
  if (request.part !== undefined && !(typeof request.part === 'number' && Number.isSafeInteger(request.part) && request.part >= 0)) return 'part is invalid'
  return {
    x: request.x as number,
    y: request.y as number,
    tileWidth,
    tileHeight,
    width: request.width as number,
    height: request.height as number,
    // part 的范围只有引擎知道（工作表 / 幻灯片数随编辑变化），这里只收窄形状
    ...(request.part === undefined ? {} : { part: request.part as number })
  }
}
