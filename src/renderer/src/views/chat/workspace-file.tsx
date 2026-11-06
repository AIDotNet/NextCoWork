/**
 * 工具行需要的那两件**工作区事实**:根目录在哪、怎么打开一个文件。
 *
 * ★★ 需求:行里那条路径要显示成 `src/main/db/` 而不是
 * `…/Desktop/code/NextCoWork/src/main/db/`,并且文件名点一下就在右侧工作台打开 ——
 * 两件事都要知道「当前是哪个工作区」,而工具行挂在
 * `ChatView → Thread → AssistantTurn → ToolTimeline → ToolGroup → ToolCallCard` 的底部。
 * 为它们把 workspace 穿过四层纯展示组件,会让 `ToolTimeline` 从「把时间线摆好」
 * 变成「知道自己属于哪个工作区」—— 所以走 context,和 `tool-stop.tsx`、
 * `subagent-open.tsx` 同一个理由、同一个形状。
 *
 * ★ 两个字段都可缺省,**缺了就退化,不画假的东西**:
 *   没有 root → 路径仍按绝对路径的末尾几段显示(只读子代理面板就是这种);
 *   没有 open → 文件名是纯文本,不是一枚点了没反应的链接(§5 不做防御式 UI)。
 */
import { createContext, useContext, useMemo, type ReactNode } from 'react'

export interface WorkspaceFileAccess {
  /** 工作区根目录的绝对路径。用于把行里的路径裁成相对路径 */
  root?: string
  /** 在右侧工作台打开这个文件(绝对路径)。不给就不把文件名画成可点 */
  open?: (path: string) => void
}

const WorkspaceFileContext = createContext<WorkspaceFileAccess>({})

export function WorkspaceFileProvider({
  root,
  open,
  children
}: WorkspaceFileAccess & { children: ReactNode }): ReactNode {
  // ★ 必须 memo:这个 value 挂在整条转录之上,每帧新建一个对象会让
  // 流式期间每个 token 都把所有工具行重渲一遍(§9 的订阅边界)。
  const value = useMemo<WorkspaceFileAccess>(
    () => ({ ...(root === undefined ? {} : { root }), ...(open === undefined ? {} : { open }) }),
    [root, open]
  )
  return <WorkspaceFileContext.Provider value={value}>{children}</WorkspaceFileContext.Provider>
}

export function useWorkspaceFile(): WorkspaceFileAccess {
  return useContext(WorkspaceFileContext)
}
