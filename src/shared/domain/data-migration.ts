/**
 * 启动迁移闸门的状态与进度。
 *
 * ## 为什么它是一份「快照」而不是一个查询
 *
 * 迁移必须在**打开数据库之前**跑完 —— 半截的库和一份还没合并完的数据都不能交给
 * `openDatabase()`。而渲染层的第一个 invoke 一定晚于窗口创建。所以主进程把当前状态
 * 存在模块变量里,渲染层来问就整份拿走,之后靠 `dataMigration:progress` 增量推。
 *
 * ★ 这份类型里**没有**任何绝对路径。进度要播给渲染层,而渲染层不该知道用户的
 * 数据根在哪(`storage:openDataDirectory` 就是为此存在的 —— 渲染层要开那个目录,
 * 但不指定它)。所以 `MigrationStep.kind` 是枚举,文案在渲染层按 kind 取。
 */

/**
 * 迁移步骤的种类。渲染层按它取文案,**不要**把主进程的中文描述播出去。
 *
 * ★ 没有「从旧根复制库」这一种:那条路径由 `prepareProjectDatabaseDirectory()`
 *   在闸门之前处理(目标库还不存在时直接拷),闸门只在**目标库已经存在**时才
 *   介入,而那意味着复制已经不可能、只能做行级合并。
 */
export type MigrationStepKind = 'collapse-flat-layout' | 'merge-legacy-rows' | 'copy-attachment-files'

/**
 * 闸门的四个阶段。
 *
 * - `idle`     —— 什么都不用做。★ 这是绝大多数启动会落到的分支,渲染层先画首屏骨架,
 *                 在 `ipcReady` 之后把 App 挂起来；不能留空，否则启动异常会表现成永久白屏。
 * - `running`  —— 正在搬。全屏盖住,期间数据库还没打开,任何 IPC 都用不了。
 * - `failed`   —— 停在错误页。重试 / 跳过 / 打开数据目录 / 撤销本次合并。
 * - `skipped`  —— 用户在错误页选了「跳过并继续」。库是完整的(合并只 INSERT 且按
 *                 会话提交),只是少了一部分旧数据;启动继续。
 */
export type MigrationPhase = 'idle' | 'running' | 'failed' | 'skipped'

/** 一步的进度。`done` / `total` 在无法预估时为 0,渲染层据此显示不确定态。 */
export interface MigrationStepState {
  kind: MigrationStepKind
  /** 已完成的子单元数。步骤本身不可细分时是 0。 */
  done: number
  /** 子单元总数。`0` = 不可预估。 */
  total: number
}

/**
 * 失败信息。
 *
 * ★ 只有 `code` 是可翻译的,`detail` 是原始错误文本,**不翻译** —— 它要拿去搜、
 * 拿去贴给模型、拿去做关键词匹配。把 `SQLITE_FULL` 翻成「磁盘已满」就再也搜不到了。
 */
export interface MigrationFailure {
  code: MigrationErrorCode
  detail: string
  /** 失败发生在哪一步。重试从这一步继续,已经成功的步骤不重跑。 */
  stepKind: MigrationStepKind
}

/**
 * 闸门失败的分类。★ 分成这几档而不是一律 `unknown`,是因为错误页要按它决定
 * **给不给「跳过」这个出口**:磁盘满是重试有用、跳过没用的典型;
 * 而权限问题是重试和跳过都没用,只有「打开数据目录」有意义。
 *
 * `source-corrupt` 与 `target-corrupt` 必须分开:源库坏了可以放心跳过
 * (那份旧数据本来就读不出来),而**目标库**坏了跳过之后应用也打不开 ——
 * 错误页上这两种情况该说的话完全不同。
 */
export type MigrationErrorCode =
  | 'disk-full'
  | 'permission'
  | 'source-corrupt'
  | 'target-corrupt'
  | 'target-locked'
  | 'unknown'

/** 一次合并写进去了什么。用于错误页的「撤销本次合并」。 */
export interface MigrationMergeSummary {
  sessions: number
  messages: number
  attachments: number
}

export interface MigrationState {
  phase: MigrationPhase
  /** 计划里的全部步骤,按执行顺序。渲染层拿它画整条步骤列表。 */
  steps: MigrationStepKind[]
  /** 已经跑完的步骤。 */
  completed: MigrationStepKind[]
  /** 当前步骤的进度;`phase !== 'running'` 时是 null。 */
  current: MigrationStepState | null
  /** 整体进度。`0..1`;无法预估时 `null`,渲染层画不确定态。 */
  ratio: number | null
  failure: MigrationFailure | null
  /** 本次启动实际写进去的行数;没合并过是 null。 */
  merged: MigrationMergeSummary | null
  /** 是否还留着可以撤销的合并记录。 */
  undoAvailable: boolean
  /**
   * 主进程启动流程抛出的原始诊断；`null` 表示尚未失败。
   *
   * 需求：窗口早于数据库和完整 IPC 创建，后续任一步抛错都必须能落到首屏错误页，
   * 否则渲染层只会永远等着 `ipcReady`，表现为 Windows 上整窗白屏且没有任何提示。
   */
  startupFailure: string | null
  /**
   * 主进程**能不能应答 IPC** —— 即 `registerIpc()` 是否已经跑完。
   *
   * 需求:渲染层只在这一位为 true 时才放行 App(`views/migration-release.ts`)。
   * 闸门只说明「库里没有要整理的东西」,它**不**说明「主进程答得上来」—— 而启动路径上
   * 建窗被提到了 `registerIpc()` 之前(窗口必须早于闸门,闸门必须早于 `openDatabase()`,
   * 见 `main/index.ts`),所以后者是真的有可能还没到的。
   *
   * ★ 它和 `phase` 是两件事,不能合并:`idle` 说的是库里没事干,主进程可能还在开库;
   * 反过来,`failed` 之后用户点了「跳过并继续」,那一刻 `phase` 不是 `idle`,
   * 但主进程可能早就答得上来了。
   *
   * ★ 闸门这一侧恒为 false —— 它不知道、也不该猜启动序列走到哪了。播出去的那一份由
   * `main/ipc/data-migration.ts` 的 `outward()` 盖章,`announceIpcReady()` 是唯一
   * 能把它翻成 true 的地方。
   *
   * 不满足会怎样:没有迁移的那次启动里,渲染层起得比主进程快时,首屏被置成
   * 「首屏握手失败: No handler registered for 'app:getBootstrap'」—— 而重载一次就正常,
   * 于是看上去像偶发。
   */
  ipcReady: boolean
}
