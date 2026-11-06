/**
 * 视频后台 manager 的**单例装配** —— 与 `runtime.ts` 之间那条缝。
 *
 * ★★ 为什么单独一个文件:manager 需要的东西来自 runtime(provider / alias /
 * 凭据 / fetch / 会话视频仓),而 runtime 又需要在装配 AgentSession 时拿到 manager。
 * 直接互相 import 会成环,而且会把 electron 拖进内核的 import 图
 * (与 `runtime.ts` 里 `installChildRunLauncher` 那次接线同一种理由)。
 *
 * ★ 所以这里是**单向**的:runtime 在初始化时 `installVideoRuntime(...)` 把依赖塞进来,
 * IPC 层与工具层通过 `getVideoManager()` 拿那一个实例。没有依赖时返回 `undefined` ——
 * 「这个环境里没有视频能力」是一个合法状态(纯内核测试、启动早期)。
 */
import { VideoManager, type VideoManagerDeps, type VideoJobStore } from './manager'

let installed: VideoManager | undefined

export function installVideoRuntime(deps: VideoManagerDeps): VideoManager {
  installed?.shutdown()
  installed = new VideoManager(deps)
  return installed
}

export function getVideoManager(): VideoManager | undefined {
  return installed
}

/** 退出时停掉本机 worker。**不取消云端任务** —— 它们不该因为我们退出而消失。 */
export function shutdownVideoRuntime(): void {
  installed?.shutdown()
  installed = undefined
}

export type { VideoJobStore }
