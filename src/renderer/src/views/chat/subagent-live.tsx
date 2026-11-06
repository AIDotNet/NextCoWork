/**
 * 右侧只读面板的**实时流挂载点** —— 一个不画任何东西的组件。
 *
 * 面板里那段转录由两条路合流:
 * - **已提交的那半边**由 `sessionStore` 的 `hydrateHistory` 从库里读
 *   (`runtime.ts` 的 `onMessageCommit` 对子 run 也逐条写库,所以子代理跑到
 *   一半时就读得到);
 * - **还在飞的那半边**靠这里的 `openChildSession`:它把子 run 的事件引到子会话
 *   自己的 store 上,并 `attachRun` 补齐「开始到挂载」这一段。
 *
 * ★ 为什么是一个 `return null` 的组件,而不是 hook:它要的 `childRunId` 得先
 * `sessionStore(parent.sessionId)` 再订阅,而 `subagentOf` / `sessionId` 在
 * `ChatView` 里都可能缺席。写成组件,「缺席就不挂载」就是一句条件渲染;
 * 写成 hook 就得在顶层无条件调用,再在里面处理一路 undefined。
 *
 * ★ 这里曾经是一整条身份栏(子代理类型、模型、阶段、距上次事件、工具数、
 * 上下文占用、runId、最近活动)。删掉是因为面板的形态就是**标题 + 正文,
 * 别的什么都没有**;那些字段排查时要看的几格 —— 状态、阶段/当前工具、耗时、
 * 工具调用数 —— 卡片上本来就有,而卡片才是用户发现异常的地方。
 */
import { useEffect, type ReactNode } from 'react'
import { sessionStore, syncChildPanel } from '../../stores/session'

export function SubagentLiveFeed({
  childSessionId,
  parent
}: {
  childSessionId: string
  /** 回指存在 Tab 的 `ref.subagentOf` 里,见 `shared/domain/tab.ts` —— 子转录自己不知道是谁派的 */
  parent: { sessionId: string; callId: string }
}): ReactNode {
  const useParent = sessionStore(parent.sessionId)
  const childRunId = useParent((s) => s.transcript.subagents[parent.callId]?.childRunId)
  // ★ 面板的运行态**以父会话那张卡片为准**(唯一真源),不是子 store 自己那份 —— 见 `syncChildPanel`
  const parentStatus = useParent((s) => s.transcript.subagents[parent.callId]?.status)

  /*
    ★ 依赖里带上 `childRunId`:父转录是懒 hydrate 的,首帧多半还没有这个字段,
    等它到了才接得上实时流。`openChildSession` 自己是幂等的(`attachRun` 那侧有
    `restoreInFlight` 去重),重复调用不会重放两遍。
    ★ 带上 `parentStatus`:卡片一到终态,面板跟着离开运行态、照抄同一个终态。
  */
  useEffect(() => {
    syncChildPanel(childSessionId, childRunId, parentStatus)
  }, [childSessionId, childRunId, parentStatus])

  return null
}
