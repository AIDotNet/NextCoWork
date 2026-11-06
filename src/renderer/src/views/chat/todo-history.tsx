/**
 * 「这次 `TodoWrite` 调用**之前**那份清单是什么」的传递口。
 *
 * 需求:消息里那张卡片要标出「这次更新改了什么」(见 `TodoWriteChecklist.tsx`),
 * 而上一份清单只有**整条转录**才能回答。卡片挂在
 * `Thread → AssistantTurn → ToolTimeline → ToolCallCard → ToolDetail` 的底部,
 * 转录在最顶上的 `Thread` 手里 —— 为一个只被一个渲染器用到的查表函数把
 * `messages` 穿过四层纯展示组件,会让 `ToolTimeline` 从「把时间线摆好」变成
 * 「知道自己属于哪条转录」(同 `tool-stop.tsx` / `subagent-open.tsx` 的取舍)。
 *
 * ★ 默认值是 `undefined` 而不是一个返回空表的函数:**没有 provider 的地方
 * (只读面板、单测)必须得到「不知道」**,于是卡片不标任何增量,而不是把整份清单
 * 标成「本次新增」—— 后者是错的,而错的增量比没有增量更糟。
 *
 * ★ 查表按 callId 走,不在渲染时重扫转录:`threadRows` 已经遍历过一遍,而工具卡片
 * 在一次 run 里可能同时存在几十张。
 */
import { createContext, useContext, useMemo, type ReactNode } from 'react'
import type { AgentMessage } from '../../../../shared/agent/message'
import { todoHistoryLookup, type TodoItem } from '../../../../shared/agent/todo'

export type TodoHistoryLookup = (callId: string | undefined) => readonly TodoItem[] | undefined

const TodoHistoryContext = createContext<TodoHistoryLookup | undefined>(undefined)

export function TodoHistoryProvider({
  messages,
  children
}: {
  messages: readonly AgentMessage[]
  children: ReactNode
}): ReactNode {
  /*
    ★ 必须 `useMemo`:`Thread` 每收到一条流式事件就重渲一次,而这张表会遍历整条
    转录。不记忆化的话,每个 token 都重扫一遍全部消息 —— 转录越长越慢,而这件事
    在界面上表现为「聊到后面越来越卡」,没有任何报错。
  */
  const lookup = useMemo<TodoHistoryLookup>(() => {
    const find = todoHistoryLookup(messages)
    return (callId) => (callId === undefined ? undefined : find(callId))
  }, [messages])
  return <TodoHistoryContext.Provider value={lookup}>{children}</TodoHistoryContext.Provider>
}

/** 没装 provider 时返回 `undefined` —— 调用方据此**不标增量**(见文件头)。 */
export function useTodoHistoryLookup(): TodoHistoryLookup | undefined {
  return useContext(TodoHistoryContext)
}

/**
 * 直接取「这次调用之前那份清单」。
 *
 * 语义与 `useTodoHistoryLookup()?.(callId)` 完全一样,单独一个 hook 只是为了让
 * 卡片里那一行读起来是「问上下文要上一份」,而不是一串可选链。
 */
export function useTodoHistory(callId: string | undefined): readonly TodoItem[] | undefined {
  return useTodoHistoryLookup()?.(callId)
}
