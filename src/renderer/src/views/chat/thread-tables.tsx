/**
 * 转录里两张**会话级**表的 context —— `tools` 与 `subagents`。
 *
 * ★ 为什么走 context 而不是 prop:需求是「长历史每个 token 不重渲」。
 * `subagents` 和 `tools` 的身份会随工具事件变一变,如果把它们一路 prop 下去,
 * 每个历史行的 memo 都会被这两个会话级字段的比较打掉 —— 一次子代理状态更新
 * 就把几百个历史回合全部重渲。放进 context 后,消费它们的只有真正画工具卡/
 * 子代理卡的那几个组件,历史行本身不进这层订阅。
 *
 * ★ 默认值给**空**而不是 live:没包 provider(单测里直接渲一个部件)时,
 * 卡片查不到 callId 会退化成它自己的兜底形态,而不是抛错。
 */
import { createContext, useContext, type ReactNode } from 'react'
import type { TranscriptState } from '../../../../shared/agent/transcript'

const ToolsContext = createContext<TranscriptState['tools']>({})
const SubagentsContext = createContext<TranscriptState['subagents']>({})

export function TranscriptTablesProvider({ tools, subagents, children }: {
  tools: TranscriptState['tools']
  subagents: TranscriptState['subagents']
  children: ReactNode
}): ReactNode {
  return (
    <ToolsContext.Provider value={tools}>
      <SubagentsContext.Provider value={subagents}>{children}</SubagentsContext.Provider>
    </ToolsContext.Provider>
  )
}

export function useTranscriptTools(): TranscriptState['tools'] {
  return useContext(ToolsContext)
}

export function useTranscriptSubagents(): TranscriptState['subagents'] {
  return useContext(SubagentsContext)
}
