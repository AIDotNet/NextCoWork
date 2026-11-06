/**
 * 启动迁移闸门的 IPC 面。
 *
 * ## 这个文件为什么不能 import 别的 ipc 模块
 *
 * ★ 除无状态的窗口控制外，它是**唯一在数据库打开前可调用的业务 handler 组**。`ipc/storage.ts`
 * 里那条 `storage:openDataDirectory` 看上去是同一件事,但它 import 了
 * `db/repo`,而 `repo` 的任何一个查询都会去摸库 —— 库还没打开时 `db/index.ts`
 * 会先开一个**内存兜底库**,随后真正的 `openDatabase()` 直接抛错。表现是
 * 「迁移卡住之后启动失败」,而错误信息里一个字都不会提到谁先碰了库。
 *
 * 所以这里的依赖只有三样:`electron` 的 `shell`、`db/index.ts` 的
 * `databaseDirectory()`(纯路径函数,不碰句柄)、以及闸门本身。
 *
 * ## 闸门实例为什么放在这里
 *
 * `main/index.ts` 建它、启动它、读它的最终状态;`ipc/index.ts` 的四个 handler
 * 要用同一个实例。放在模块级变量里是这两者之间最短的一条线 —— 而它**不需要**
 * 生命周期管理:进程内只有一个闸门,它自己就是单例。
 *
 * ## 它还兼着主进程启动状态
 *
 * 闸门的放行判据除了「没有要迁移的东西」,还有「`registerIpc()` 跑完了」——
 * 后者的唯一真源在这里(`announceIpcReady()`),因为它的消费者只有渲染层,
 * 而渲染层在首屏之前能问到的只有这一个频道。启动流程抛出的异常也从这里
 * 推给首屏，避免窗口永远停在等待状态。见 `MigrationState.ipcReady`。
 */
import { shell } from 'electron'
import type { MigrationState } from '../../shared/domain/data-migration'
import { databaseDirectory } from '../db'
import type { MigrationGate } from '../db/startup-migration'
import { windows } from '../window/registry'

let gate: MigrationGate | null = null
let dataChangedListener: (() => void) | null = null

/**
 * 主进程是否已经把全部 handler 装上了(`registerIpc()` 跑完)。
 *
 * 需求:渲染层不能只凭闸门的 `idle` 就挂 App。建窗被提到了 `registerIpc()` **之前**
 * (窗口必须早于闸门,闸门必须早于 `openDatabase()`,见 `main/index.ts`),而闸门检查完
 * 也很快就播 `idle` —— 渲染层那一刻挂上 App,握手的第一个 invoke 就撞上还没登记的
 * handler,首屏变成「首屏握手失败: No handler registered for 'app:getBootstrap'」。
 *
 * ★ 它必须是**模块级**的,不能只靠那一次推送:窗口可能起得比推送晚(渲染层加载慢),
 * 那一份 `dataMigration:progress` 它根本没订阅上,只能回头拉一次
 * `dataMigration:getState` —— 而这一条恰好是全应用在闸门期间唯一能应答的几条之一,
 * 所以「主进程还不能应答」这件事只能从这里回答。
 */
let ipcReady = false

/**
 * 启动编排失败的原始诊断。
 *
 * 需求：`app.whenReady()` 的异步链在建窗之后仍可能抛错；把错误留在 console 会让
 * 渲染层永远只看到 `ipcReady: false`，表现为整窗白屏。模块级保存让晚加载的窗口
 * 也能通过 `dataMigration:getState` 拿到同一份失败，而不是依赖一次可能错过的推送。
 */
let startupFailure: string | null = null

/**
 * 播给渲染层的那一份:闸门状态 + 主进程能不能应答 + 启动失败诊断。
 *
 * ★ **每一个**流出去的状态都要过这里。漏一处,渲染层拿到的就是闸门那份
 * `ipcReady: false` / `startupFailure: null`,表现是启动停在骨架屏且没有诊断。
 */
function outward(state: MigrationState): MigrationState {
  return state.ipcReady === ipcReady && state.startupFailure === startupFailure
    ? state
    : { ...state, ipcReady, startupFailure }
}

/**
 * 启动完成:`registerIpc()` 已经返回,渲染层的任何 invoke 都能被应答。
 *
 * ★ 由 `main/index.ts` 在 `registerIpc()` 之后**立刻**调,而且必须调 —— 漏掉的话
 * 所有窗口都停在首屏那道闸门上,界面上一个错都不报。
 *
 * 放在调用方、而不是塞进 `registerIpc()` 的末尾是有意的:`registerIpc()` 里还夹着
 * `initImports()` / `sweepOrphans()` 这些启动工作,而「渲染层可以握手了」是启动
 * 序列上的一个里程碑,读启动顺序的人应该在那一个文件里看到它。
 */
export function announceIpcReady(): void {
  ipcReady = true
  // 就绪与失败互斥；保留旧诊断会让渲染层盖住已经可用的 App。
  startupFailure = null
  /*
    再推一份完整快照:闸门自己那几次 publish 全都发生在这一位还是 false 的时候,
    所以渲染层手里那份必须被这份覆盖掉(它在 `failed` 那一屏上时尤其如此 ——
    只翻标志而不重推,用户点「继续」会一直没反应)。
  */
  windows.emitToAll('dataMigration:progress', getMigrationState())
}

/**
 * 把建窗后的启动异常变成首屏可以展示的状态。
 *
 * 需求：数据库损坏、权限错误或初始化模块抛错时，主进程仍存活但永远不会宣布 IPC
 * 就绪；只写 console 的症状就是 Windows 窗口永久白屏。这里既保存又广播，覆盖
 * 「窗口已经订阅」和「窗口稍后才加载」两种时序。
 */
export function announceStartupFailure(error: unknown): void {
  // 完整 IPC 已可用时，尾部后台服务的失败不能反过来卸载一个正常工作的 App。
  if (ipcReady) return
  const detail = error instanceof Error
    ? (error.stack ?? `${error.name}: ${error.message}`)
    : String(error)
  startupFailure = detail === '' ? 'UNKNOWN_STARTUP_FAILURE' : detail
  windows.emitToAll('dataMigration:progress', getMigrationState())
}

export function installMigrationGate(instance: MigrationGate): void {
  gate = instance
}

/**
 * 数据库打开后接上迁移完成回调。
 *
 * 需求：这组 IPC 在开库前就会登记，不能自己 import 会读库的模块；但运行期重试 / 撤销
 * 又必须让账户归属和渲染缓存同步。回调由启动序列在开库后安装，开库前保持 null。
 */
export function setMigrationDataChangedListener(listener: (() => void) | null): void {
  dataChangedListener = listener
}

/**
 * 闸门 → 窗口的进度推送。
 *
 * ★ 与 `main/index.ts` 里 `setImportChangeListener` 那批接线同一种形态:
 *   `db/**` 不 import `window/registry`,方向保持「ipc 依赖 db,db 不依赖 ipc」。
 *   闸门只暴露 `onChange` 回调,推给谁由这里决定。
 *
 * ★ 用 `emitToAll` 而不是 `emitToTopic`:迁移是**全局**状态,而且此刻窗口可能
 *   刚建出来、还没跑过 `window:ready`(topic 还没订阅上)。丢帧的后果是进度条
 *   永远停在 0,而用户没有任何办法让它刷新。
 *
 * ★ 出去之前必须盖章(见 `outward`)—— 闸门那份 state 里 `ipcReady` 恒为 false。
 */
export function announceMigrationState(state: MigrationState): void {
  windows.emitToAll('dataMigration:progress', outward(state))
}

/**
 * 拿闸门。
 *
 * ★ 拿不到时**不抛** —— 返回一个「什么都不用做」的空状态。
 * 这个 handler 会在渲染层握手时被调用,而那条路径上抛异常等于首屏白屏;
 * 而拿不到闸门只有一种原因:调用方没装(比如某个只跑 IPC 子集的测试),
 * 那种情况下「无需迁移」正是正确的答案。
 */
function requireGate(): MigrationGate | null {
  return gate
}

const NO_GATE: MigrationState = {
  phase: 'idle',
  steps: [],
  completed: [],
  current: null,
  ratio: null,
  failure: null,
  merged: null,
  undoAvailable: false,
  startupFailure: null,
  /*
    ★ `ipcReady: false` 不是笔误:闸门没装只说明「没有要迁移的东西」,不说明
    「可以放行」—— 能不能放行由上面那一位说了算,这里照旧交给 `outward()` 盖章。
    窗口确实可能早于 `installMigrationGate()` 起来(`main/index.ts` 里建窗在闸门
    之前),那一瞬间走的正是这条路。
  */
  ipcReady: false
}

export function getMigrationState(): MigrationState {
  return outward(requireGate()?.state() ?? NO_GATE)
}

export async function retryMigration(): Promise<MigrationState> {
  const instance = requireGate()
  if (instance === null) return outward(NO_GATE)
  const state = await instance.run()
  // 需求：失败的重试也可能按会话提交了一部分行；归属清单与每条会话同事务落盘，
  // 必须立刻重连并刷新侧边栏，否则用户看到「重试失败」之外仍是一片空白。
  if (state.merged !== null || state.phase === 'failed') dataChangedListener?.()
  return outward(state)
}

export function skipMigration(): MigrationState {
  const instance = requireGate()
  if (instance === null) return outward(NO_GATE)
  instance.skip()
  return outward(instance.state())
}

export function undoMigration(): MigrationState {
  const instance = requireGate()
  if (instance === null) return outward(NO_GATE)
  instance.undo()
  dataChangedListener?.()
  return outward(instance.state())
}

/**
 * 打开数据目录。
 *
 * ★ 走主进程的 `shell.openPath`,渲染层拿不到也传不了路径 —— 与
 * `storage:openDataDirectory` 同一条规矩。区别只是这里不经过 `repo`,
 * 所以闸门期间也能用。
 */
export async function openMigrationDataDirectory(): Promise<void> {
  const message = await shell.openPath(databaseDirectory())
  if (message !== '') throw new Error(`打开数据目录失败: ${message}`)
}
