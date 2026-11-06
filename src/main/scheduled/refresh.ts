/**
 * 定时任务的**重排继电器** —— 一个可以安全被任何模块 import 的叶子。
 *
 * ★★ **它存在,是为了断掉 `runtime ▸ scheduled/bridge ▸ scheduled/scheduler ▸ runtime`
 * 这个值依赖环。** `bridge.ts` 每次写任务都要 `refreshScheduler()`(重排那次定时器),
 * 而 `scheduler.ts` 又要 `import { runAgent } from '../runtime'` —— 于是
 * `bridge → scheduler → runtime → bridge` 成了环。
 *
 * ★ **方向:ipc 依赖 runtime,runtime 永不依赖 scheduler。** 所以这里存的是一个
 * 模块级插槽(和 `runtime.ts` 里那排 `setXxxChangeListener` 一个套路),由
 * `ipc/index.ts` 在 `registerIpc()` 时装上。没装(纯内核测试、或 ipc 还没起来)时
 * 是一次 no-op —— 代价只是「建了但暂时不重排」,而下一次调度 tick 会顺带修好。
 *
 * 注意:no-op 是**有意的降级**,不是遗漏。bridge 的测试就靠它断言「写完重排了几次」。
 */
let refresh: () => void = () => {}

/** 装上真正的重排实现(`scheduled/scheduler.ts` 的 `refreshScheduler`)。 */
export function setSchedulerRefresh(fn: () => void): void {
  refresh = fn
}

/** 供测试复位,避免跨用例泄漏。 */
export function resetSchedulerRefreshForTest(): void {
  refresh = () => {}
}

/** 任务表变化后重排那一次定时器。写入收口(`bridge.ts`)唯一该调的口。 */
export function refreshScheduledTasks(): void {
  refresh()
}
