/**
 * Agent 的 shell —— 「这一刻它开着哪些 shell」的类型与限额。
 *
 * 需求:一条 `Bash` 命令有两种活法,而在此之前只有一种。
 * 1. **前台**:调用方等它,跑完进 `tool_result`。用户中途想掐掉**这一条**
 *    (`npm test` 跑飞了)时,以前唯一的办法是停掉整轮对话。
 * 2. **后台**:`npm run dev` 这类根本不会自己结束的进程。等它 = 撞超时 = 白跑两分钟,
 *    而模型看到的是「超时」,于是它会换个写法再试一次。
 *
 * 这个模块只定义**数据与缝**:进程真正住在 `main/agent-shells.ts`(它要 `openProcess`,
 * 本地与 SSH 两条路都走那一个端口),工具通过 `ToolContext.shells` 够到它。
 * 内核因此仍然零 electron、可单测 —— 同 `SchedulingBridge` / `SpawnSubagentFn` 的道理。
 *
 * 界面侧只有右上角活动胶囊的「Bash」栏(`ActivityCapsule`):它**不自己存状态**,
 * 列表靠 `shell:backgroundChanged` 全量广播收敛,输出靠 `shell:peekBackground`
 * 看尾巴 —— 不消费模型那份缓冲,注册表仍是唯一事实源。
 */

/**
 * 一个后台 shell 的结局。
 *
 * ★ `killed` 与 `exited` 分开:模型读到 `exited(code 143)` 会去猜是什么信号打死了它,
 * 而「是我们自己杀的」这件事没有任何办法从退出码反推出来。
 * `failed` 是**根本没起来**(shell 不存在、cwd 不存在),它该改的是命令本身。
 */
export type BackgroundShellStatus = 'running' | 'exited' | 'killed' | 'failed'

export interface BackgroundShellInfo {
  /** 给模型的短 id(`bash_1`)。★ 进程内唯一且不复用 —— 见 agent-shells.ts 的发号器。 */
  id: string
  command: string
  /** 模型自己写的那句 5-10 词说明,可能没有。 */
  description?: string
  cwd: string
  status: BackgroundShellStatus
  /** `status === 'exited'` 时才有意义;拿不到退出码(被信号带走)时缺省。 */
  exitCode?: number
  startedAt: number
  endedAt?: number
  /** 起它的那次调用。诊断与「这条 shell 属于谁」靠它,不靠猜。 */
  runId: string
  callId: string
  workspaceId: string
}

/** 一次回读的结果。**读过即清空**,所以两次读之间的新输出正好不重不漏。 */
export interface BackgroundShellRead {
  info: BackgroundShellInfo
  stdout: string
  stderr: string
  /**
   * 缓冲满过,最早的一段已经被丢掉。
   *
   * ★ 必须显式告诉模型。不说的话它会把一段缺了头的日志当成完整输出去归因 ——
   * 表现为它信誓旦旦地说「构建没有报错」,而报错正好在被丢掉的那一段里。
   */
  dropped: boolean
}

/**
 * 工具与主进程之间唯一的接触面(同 `SchedulingBridge`)。
 *
 * ★ 失败一律 **throw 一个模型读得懂的英文 Error**,由工具转成 `toolFail`。
 * 返回 `{ ok: false }` 型结果会让每个调用点都要写一遍分支,而漏写的那处
 * 会把失败当成成功报给模型。
 *
 * `P` 是「一条从前台转走的进程」的形状。★ 写成类型参数而不是直接引用:
 * 那个形状带着 Node 的流类型,而本文件也被渲染层编译 —— 具体类型由
 * 内核那一侧(`ToolContext.shells`)填上。
 */
export interface ShellBridge<P = unknown> {
  /**
   * 前台命令:登记一个「只停这一条命令」的句柄,返回注销函数。
   *
   * ★ 注销**必须**在 `finally` 里调。漏掉的话注册表会攥着一个早就跑完的
   * callId,而用户下一次点那张卡上的停止,停的是一条已经不存在的命令 ——
   * 没有任何报错,只是按钮不起作用。
   *
   * `detach`:同一条命令的「转去后台」句柄。只在 `adopt` 存在时才有意义。
   */
  hold(call: { runId: string; callId: string; command: string }, stop: () => void, detach?: () => void): () => void
  start(req: {
    command: string
    cwd: string
    description?: string
    runId: string
    callId: string
  }): Promise<BackgroundShellInfo>
  read(id: string, filter?: string): BackgroundShellRead
  kill(id: string): BackgroundShellInfo
  list(): BackgroundShellInfo[]
  /**
   * 收编一条**从前台转走、仍在跑**的进程,从此它就是一个普通的后台 shell。
   *
   * ★ 可选:缺席 = 这个环境里前台命令转不了后台(SSH 的前台命令走 exec 通道,
   * 拿不出一个活着的进程)。工具据此决定告不告诉界面「这条可以转后台」——
   * 画一颗按下去没反应的按钮,比没有按钮难解释得多。
   */
  adopt?(req: {
    command: string
    cwd: string
    description?: string
    runId: string
    callId: string
    process: P
  }): BackgroundShellInfo
}

export const BACKGROUND_SHELL_LIMITS = {
  /**
   * 同时在跑的后台 shell 上限。
   *
   * ★ 有上限不是洁癖:后台进程**不随 run 结束而结束**,而模型看不到自己开了多少个。
   * 没有闸门时一次「起服务 → 看日志 → 再起一个」的循环能在一次会话里留下几十个
   * 还占着端口的 node —— 而用户只会看到「端口被占用」,完全指不到这里。
   */
  MAX_RUNNING: 8,
  /** 连同已结束的一起记这么多条;超了从最早结束的那条开始淘汰。 */
  MAX_TRACKED: 24,
  /** 单条 shell 的 stdout / stderr 各自的缓冲上限。超出后丢最早的,并置 `dropped`。 */
  MAX_BUFFER_CHARS: 256 * 1024
} as const
