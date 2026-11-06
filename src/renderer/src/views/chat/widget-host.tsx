/**
 * 对话里 widget 够得着的**宿主动作**的传递口 —— 填草稿、附上下文、打开文件,以及
 * 它属于哪个会话(状态持久化、动作、模型调用都按会话走)。
 *
 * ★ 同 `tool-stop.tsx` 的理由走 context:`WidgetFrame` 挂在工具卡片的最底部,
 * 而这些动作要的东西在 `ChatView` 手里。
 * ★ 默认值 `undefined` = 这里没有宿主(只读的子代理面板、单测):widget 照样渲染,
 * 但它请求的这几项能力会被明确拒绝,而不是"点了没反应"。
 */
import { createContext, useContext, type ReactNode } from 'react'

export interface WidgetHostContextValue {
  sessionId: string
  workspaceRoot: string
  /** 接在草稿后面,不发送 */
  draft: (text: string) => void
  /** 键是 widget 的 callId;空串 = 撤掉 */
  setContext: (key: string, title: string, text: string) => void
  /** 绝对路径 */
  openFile: (path: string) => void
}

const WidgetHostContext = createContext<WidgetHostContextValue | undefined>(undefined)

export function WidgetHostProvider({ value, children }: { value: WidgetHostContextValue | undefined; children: ReactNode }): ReactNode {
  return <WidgetHostContext.Provider value={value}>{children}</WidgetHostContext.Provider>
}

export function useWidgetHost(): WidgetHostContextValue | undefined {
  return useContext(WidgetHostContext)
}
