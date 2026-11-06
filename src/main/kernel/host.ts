/**
 * KernelHost —— 方案 §2 的端口集。**内核对外部世界的唯一开口。**
 *
 * 「内核零 electron import」不是洁癖,它买到两样具体的东西:
 * 1. `src/main/kernel/**` 的单测在普通 Node 里跑,不用启动 Electron
 *    (`vitest.config.ts` 的 `environment: 'node'` 就是靠这条成立的);
 * 2. `fetch` 是**注入**的,所以「换成 net.fetch 走公司代理」「测试里打桩」
 *    「内置演示上游」三件事共用同一个口子,而不是三处 if。
 *
 * ★ 本文件同时提供 `nodeHost()` —— 一个纯 Node 实现。它不是「测试替身」,
 * 而是**真实默认值**:Electron 侧只覆盖 `secrets` 与 `paths` 两项
 * (旧 safeStorage 密文迁移和 app.getPath 是仅有的两个真正需要 Electron 的能力)。
 * 这样「内核能不能脱离 Electron 跑」这个问题不靠自律维持,靠默认路径维持。
 */
import { release, tmpdir } from 'node:os'
import { join } from 'node:path'
import type { Readable } from 'node:stream'
import { nodeFs } from './node-fs'
import { agentShell, nodeSpawn } from './node-spawn'
import type { EnvironmentFacts } from '../../shared/domain/environment'
import type { ProxyDialTarget } from '../../shared/domain/proxy'

export interface Logger {
  debug(msg: string, ...args: unknown[]): void
  info(msg: string, ...args: unknown[]): void
  warn(msg: string, ...args: unknown[]): void
  error(msg: string, ...args: unknown[]): void
}

/**
 * 文件工具的能力面。**所有路径必须先过 `resolveAnywhere`**(方案 §9)——
 * 这里一律收绝对路径,是为了让「路径在哪儿被归一化」这个问题只有一个答案:在调用方。
 *
 * ★ 刻意**没有** `remove` / `rename`。内置工具用不到,而一个「先留着以后再说」的
 * 空方法会静默变成一次误删 —— 端口宁可缺,不要哑。
 */
export interface KernelFs {
  readFile(absPath: string): Promise<string>
  writeFile(absPath: string, content: string): Promise<void>
  readDir(absPath: string): Promise<Array<{ name: string; isDir: boolean }>>
  stat(absPath: string): Promise<{ size: number; mtimeMs: number; isDir: boolean }>
  realpath(absPath: string): Promise<string>
  /**
   * 只读前 `maxBytes` 个**字节**,不解码。二进制探测必须在解码之前做完 ——
   * 只有 readFile 的话,一个 300MB 的 .pack 会先被 utf8 解成一堆 U+FFFD
   * 再被判定为二进制:判断是对的,代价是主进程刚卡了两秒并分配了 600MB。
   */
  readFileBytes(absPath: string, maxBytes?: number): Promise<Uint8Array>
  /** 建 `absPath` 的**父目录**。没有它,模型写 src/a/b/c.ts 永远 ENOENT。 */
  mkdirp(absPath: string): Promise<void>
  /** 存在性是个问句不是个异常 —— 用 stat 抛错表达它,每个调用点都会变成 try/catch。 */
  exists(absPath: string): Promise<boolean>
  /**
   * 写原始字节(不经 utf8 编码)。需求:`SaveImage` 要把图片写进工作区,
   * 而 `writeFile(string)` 会把二进制按 utf8 编码成一堆乱码。
   *
   * ★ 可选:缺席 = 这个宿主写不了二进制,`SaveImage` 整体不下发(不画注定失败的承诺)。
   * 签名与 `EnvironmentFs.writeBytes` 逐字相同 —— 本地/SSH 两种工作区环境的 fs
   * 因此天然满足它,不必各自再包一层。
   */
  writeBytes?(absPath: string, bytes: Uint8Array, options?: { exclusive?: boolean; mode?: number }): Promise<void>
  /**
   * 把一个**流**写进工作区文件,全程有上限、有背压、失败即清理。
   *
   * 需求:`SaveVideo` 要把一段几十到几百兆的视频写进工作区,而 `writeBytes` 拿的是
   * "已经完整在内存里的 Uint8Array" —— 视频那个量级下那意味着主进程先分配几百兆,
   * 而 SSH 那侧还有自己的 32 MiB 上限。所以单开一条:
   *
   * - **边拉边写**,不出现"完整视频"的 Buffer;
   * - `maxBytes` 超出时**当场断并清理**半截文件(而不是写完再删);
   * - 失败不碰旧文件:写临时名,成功才 rename;`exclusive` 时由文件系统原子判定;
   * - `signal` 中断同样清理。
   *
   * ★ 可选:缺席 = 这个宿主存不了大文件,`SaveVideo` 整体不下发
   *   (不画一个会在几百兆时才失败的承诺)。
   */
  writeStream?(
    absPath: string,
    source: ReadableStream<Uint8Array>,
    options: { exclusive?: boolean; mode?: number; maxBytes: number },
    signal: AbortSignal
  ): Promise<{ ok: true; size: number } | { ok: false; reason: string }>
}

export interface SpawnResult {
  code: number
  stdout: string
  stderr: string
  /**
   * 命令被**转去后台**了(`detach` 信号响了且进程还活着)。
   *
   * 有这一格时 `code` 没有意义,`stdout` / `stderr` 是转走那一刻之前攒下的输出;
   * 进程本身连同两条管道交给调用方 —— 从这一刻起超时与 `signal` 都不再管它,
   * 它的生死归接手的人(`main/agent-shells.ts`)。
   */
  detached?: DetachedProcess
}

/**
 * 一条从前台转走、仍在跑的进程。形状是 `EnvironmentProcess` 去掉 stdin 的那部分 ——
 * 前台命令的 stdin 本来就是关着的。
 */
export interface DetachedProcess {
  stdout: Readable
  stderr: Readable
  exited: Promise<{ code: number | null; signal?: string | null }>
  /** 杀整棵进程树(同前台停止的那一条路径),不是只杀 shell。 */
  kill(): void
}

/**
 * ★ Agent 的 bash 工具**不复用**交互式 PTY(方案 §6):混用会让工具输出和用户
 * 正在敲的字交错,且工具拿不到干净的 exit code。所以这里是 spawn 语义,不是 pty。
 */
export type SpawnFn = (
  cmd: string,
  opts: {
    cwd: string
    signal: AbortSignal
    timeoutMs?: number
    /** 本地调用可冻结本次 run 的 shell，避免设置变更后提示词与执行器分叉。 */
    shell?: string
    /**
     * 边跑边拿输出。**可选**,不给就是原来的行为(只在结束时拿全量结果)。
     *
     * 需求:插件的 `process.execStream` 要让作者在一条跑几十秒的命令**进行中**
     * 就拿到输出,而 `SpawnResult` 的形状做不到这件事。加在这个端口上、而不是让
     * 插件层另写一个 spawn —— 那个实现里的进程组、背压、env 清洗三件事每一件
     * 写错都会在生产里咬人(见 `node-spawn.ts` 文件头),复制一份等于重挖三个坑。
     *
     * ★ 回调抛异常不得影响命令本身,实现方逐个 try。
     */
    onOutput?: (chunk: { stream: 'stdout' | 'stderr'; text: string }) => void
    /**
     * 这条信号一响,命令**不再被等**:超时与 `signal` 一起卸掉,进程原样交出去
     * (`SpawnResult.detached`)。需求:用户在工具卡上把一条跑得比预想久的前台命令
     * 转成后台,而不是只能二选一地「干等」或「掐掉重来」。
     *
     * ★ 可选,且**实现方可以不支持**(SSH 那条路今天就不支持)。不支持的实现
     * 忽略它即可 —— 调用方据 `SpawnResult.detached` 是否存在判断转没转走,
     * 是否画那颗按钮由 `ShellBridge.adopt` 是否存在决定。
     */
    detach?: AbortSignal
  }
) => Promise<SpawnResult>

/**
 * 平台事实 —— **给模型看的**,不是给代码用的。
 *
 * ★ 为什么要走端口,而不是在组装提示词的地方直接读 `process`:
 * 读环境和读时钟是同一类事。`context-assembler.ts` 的文件头把
 * 「不读时钟、不读环境变量」写成了契约,靠的就是这两样都从入参进来 ——
 * 否则「在测试里摆出一台 Windows」这件事根本做不到。
 *
 * ★ 为什么值得进提示词:**一条事实几乎总是比一条关于这条事实的规则更便宜、
 * 也更管用**。`Platform: darwin` 是三个 token;要用规则达到同样效果,
 * 得写「注意 macOS 的 sed -i 要带一个空串参数、readlink 没有 -f……」——
 * 三十个 token,列不全,而且模型照样会忘。提示词的预算应该花在
 * 模型**推不出来**的事实上,而不是花在它已经知道、只是不知道适不适用的规则上。
 */
export interface PlatformInfo {
  /** `process.platform`:darwin / linux / win32 */
  os: string
  /** `os.release()` */
  osVersion: string
  /** ★ 必须和 `node-spawn.ts` 真拿去跑命令的那个 shell 是同一个,见 `agentShell()` */
  shell: string
}

export interface WorkspacePaths {
  style: 'posix' | 'win32'
  join(...paths: string[]): string
  dirname(path: string): string
  basename(path: string): string
  extname(path: string): string
  isAbsolute(path: string): boolean
  relative(root: string, path: string): string
  resolve(root: string, path: string): Promise<{ abs: string; outside: boolean }>
  resolveWithin(root: string, path: string): Promise<string>
  display(root: string, absolutePath: string): string
}

export interface WorkspaceHost {
  key: string
  rootPath: string
  fs: KernelFs
  spawn: SpawnFn
  platform: PlatformInfo
  path: WorkspacePaths
  remote: boolean
  description: string
  facts?: EnvironmentFacts
}

export interface KernelHost {
  paths: {
    /**
     * 账户配置根:skills / commands / agents / settings.json。
     * ★ **跟着配置作用域走**,见 `db/config-profile.ts`。
     */
    userData(): string
    /**
     * 附件根(这台机器上的会话数据)。★ **不跟作用域走** ——
     * 会话附件由 `ipc/storage.ts` 按它扫描占用与清理孤儿,换根等于让那些文件
     * 从统计里消失,并且下一次清理会把它们当成孤儿删掉。
     */
    attachments(): string
    temp(): string
  }
  /** ★ 只存引用；明文只在显式取值的主进程调用栈内短暂存在。 */
  secrets: {
    get(ref: string): Promise<string | null>
    set(ref: string, value: string): Promise<void>
    /** 主进程整批配置事务专用；普通内核代码继续只用异步端口。 */
    setSync?(ref: string, value: string): void
    /** Remove a stored credential when an operation that wrote it rolls back. */
    remove?(ref: string): Promise<void>
    removeSync?(ref: string): void
    /** 程序主密钥可用时为 true；写入权限等故障仍由 set/get 明确抛出。 */
    available(): boolean
  }
  clock: { now(): number }
  platform: PlatformInfo
  logger: Logger
  fs: KernelFs
  spawn: SpawnFn
  /**
   * 本地子进程(Agent 的 Bash 工具、后台 shell、本地钩子)额外继承的环境变量。
   *
   * 需求:这些命令默认跟随应用/系统代理 —— CLI 只认 `HTTP_PROXY` 一类环境变量,
   * 而 Electron 从 Finder/Dock 启动时 `process.env` 里没有它们。值由 electron 侧
   * `net/proxy.ts` 的 `shellProxyEnv` 给出;这里只定义「怎么送进子进程」,
   * 不定义「是什么」。同一撮变量同时喂给 `nodeSpawn`(前台命令)和
   * `environment/local.ts` 的 `openProcess`(后台命令/钩子)—— 两条路一份答案。
   *
   * ★ 纯 Node 默认值**没有**这一项(undefined = 不注入):无头/测试环境没有
   *   Chromium session 可问,注入也只能是错的。
   */
  childEnv?: () => Record<string, string> | Promise<Record<string, string>>
  /**
   * 「连这台主机该走哪个代理」;`null` = 直连。
   *
   * 需求:SSH 连接默认跟随应用/系统代理,而 ssh 是子进程,`session.setProxy` 管不到它
   * ——只能反过来问。答案(手动代理 / 系统代理 / PAC / 直连白名单)由 electron 侧
   * `net/proxy.ts` 的 `resolveProxyForHost` 给出,和模型请求同一份。
   *
   * ★ 可选:纯 Node 默认值**没有**这一项(undefined = 直连)。无头/测试环境没有
   *   `defaultSession` 可问,也没有「用户设过的代理」;此时返回 null 正是我想要的行为。
   *   `environment/ssh/transport.ts` 的 `prepareProxy` 就是按缺席即不插手写的。
   */
  resolveProxyForHost?(hostname: string, port: number): Promise<ProxyDialTarget | null>
  fetch: typeof fetch
}

const consoleLogger: Logger = {
  debug: (m, ...a) => console.debug(m, ...a),
  info: (m, ...a) => console.log(m, ...a),
  warn: (m, ...a) => console.warn(m, ...a),
  error: (m, ...a) => console.error(m, ...a)
}

/**
 * 纯 Node 的默认 host。
 *
 * `secrets` 在这里是**进程内内存**,不是持久化 —— Electron 侧会用程序主密钥
 * + SQLite 覆盖它。留一个能用的内存实现(而不是抛错)是为了让内核的单测能走完
 * 「取凭证 → 发请求」这条路,而不必每个测试都自己搭一个 host。
 */
export function nodeHost(
  overrides: Partial<KernelHost> = {},
  resolveShell: () => string = agentShell
): KernelHost {
  const mem = new Map<string, string>()
  return {
    paths: {
      userData: () => join(process.cwd(), '.next-cowork'),
      attachments: () => join(process.cwd(), '.next-cowork', 'attachments'),
      temp: () => tmpdir()
    },
    secrets: {
      get: async (ref) => mem.get(ref) ?? null,
      set: async (ref, v) => {
        mem.set(ref, v)
      },
      setSync: (ref, v) => {
        mem.set(ref, v)
      },
      remove: async (ref) => {
        mem.delete(ref)
      },
      removeSync: (ref) => {
        mem.delete(ref)
      },
      available: () => true
    },
    clock: { now: () => Date.now() },
    platform: { os: process.platform, osVersion: release(), get shell() { return resolveShell() } },
    logger: consoleLogger,
    fs: nodeFs(),
    // childEnv 只作转发:同一份注入也给 openProcess 那条路用(local.ts 读 host.childEnv)
    spawn: nodeSpawn(resolveShell, overrides.childEnv),
    // 绑定到 globalThis:直接传 `fetch` 引用在某些运行时会丢 this
    fetch: (input, init) => globalThis.fetch(input, init),
    ...overrides
  }
}
