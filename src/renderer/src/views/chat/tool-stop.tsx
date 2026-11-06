/**
 * 「停掉这一次工具调用」「把这一次工具调用转去后台」这两个动作的传递口。
 *
 * ★ 为什么是 context 而不是一路往下传 prop(同 `subagent-open.tsx` 的理由):
 * 卡片挂在 `Thread → AssistantTurn → ToolTimeline → ToolGroup → ToolCallCard` 的底部,
 * 而这个动作需要的 `runId` 在最顶上的 `ChatView` 手里。为一个回调把 runId 穿过
 * 四层纯展示组件,会让 `ToolTimeline` 从「把时间线摆好」变成「知道自己属于哪个 run」。
 *
 * ★ 默认值是 `undefined` 而不是一个空函数:卡片据此决定**画不画**那颗按钮。
 * 没有 provider 的地方(只读面板、单测)拿到的是一张纯展示卡片,而不是一颗
 * 按下去没反应的停止按钮 —— 后者比没有按钮难解释得多。
 */
import { createContext, useContext, type ReactNode } from 'react'

export type StopToolCall = (callId: string) => void
export type BackgroundToolCall = (callId: string) => void

const ToolStopContext = createContext<StopToolCall | undefined>(undefined)
const ToolBackgroundContext = createContext<BackgroundToolCall | undefined>(undefined)

export function ToolStopProvider({
  stop,
  background,
  children
}: {
  stop: StopToolCall | undefined
  /** 缺省 = 这里转不了后台,卡片不画那颗按钮(同 `stop` 的理由) */
  background?: BackgroundToolCall | undefined
  children: ReactNode
}): ReactNode {
  return (
    <ToolStopContext.Provider value={stop}>
      <ToolBackgroundContext.Provider value={background}>{children}</ToolBackgroundContext.Provider>
    </ToolStopContext.Provider>
  )
}

export function useStopToolCall(): StopToolCall | undefined {
  return useContext(ToolStopContext)
}

export function useBackgroundToolCall(): BackgroundToolCall | undefined {
  return useContext(ToolBackgroundContext)
}
