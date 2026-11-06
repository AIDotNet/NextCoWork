/**
 * 闸门的放行判据:什么时候可以挂 `<App/>`。
 *
 * 需求:App 一挂上就会 invoke `app:getBootstrap`,而主进程那边 handler 还没登记完的话,
 * 首屏会被置成「首屏握手失败: No handler registered for 'app:getBootstrap'」。
 * 建窗被提到了 `registerIpc()` 之前(窗口必须早于闸门,闸门必须早于 `openDatabase()`,
 * 见 `main/index.ts`),所以这是**真的会发生的**,不是理论上的时序担忧。
 *
 * ★ 它长成纯函数、住在 `.ts` 里,是因为它错的唯一形态几乎没法手测:渲染层比主进程
 * 启动快的那一次才会错,重载一次就正常。四种结果由 `__tests__/migration-release.test.ts`
 * 直接把时序摆出来验。
 *
 * 故意不做的事:这里不碰 i18n、不碰组件 —— 它只回答「这一帧画什么」。
 */
import type { MigrationState } from '../../../shared/domain/data-migration'

/**
 * - `waiting`        —— 主进程仍在启动，画首屏骨架而不是留白。
 * - `app`            —— 放行,挂 App 去握手。
 * - `gate`           —— 画迁移那一屏(进度 / 失败 / 已跳过)。
 * - `startup-failed` —— 主进程明确失败，或等待超时，画可操作的诊断页。
 */
export type MigrationGateDecision = 'waiting' | 'app' | 'gate' | 'startup-failed'

/**
 * `resolved` = 用户在失败页/已跳过页上已经表过态(重试成功,或点了「继续」),
 * 只差主进程能答得上来了。`startupStalled` = 首份快照失败或等待 IPC 就绪超时。
 */
export function decideMigrationGate(
  state: MigrationState | null,
  resolved: boolean,
  startupStalled: boolean
): MigrationGateDecision {
  // 需求：就绪前的主进程异常必须优先于迁移状态；就绪后则不能卸载已可用的 App。
  if (state !== null && state.ipcReady !== true && state.startupFailure !== null) return 'startup-failed'

  // 首份快照尚未到达时先画骨架；一直不到则给诊断出口，绝不能永久返回空节点。
  if (state === null) return startupStalled === true ? 'startup-failed' : 'waiting'

  /*
    ★ 主进程还不能应答时**一律不放行**,哪怕用户已经点过「继续」——
    那一下点得早(闸门刚结束、`registerIpc()` 还没跑完)的话,挂上的 App 会立刻
    拿到同一个「未登记的频道」。正常等待画骨架；超时后画诊断页。
  */
  if (state.ipcReady !== true) {
    const waitingAfterResolution = state.phase === 'skipped' && resolved === true
    if (state.phase !== 'idle' && waitingAfterResolution !== true) return 'gate'
    return startupStalled === true ? 'startup-failed' : 'waiting'
  }
  if (resolved === true || state.phase === 'idle') return 'app'
  return 'gate'
}
