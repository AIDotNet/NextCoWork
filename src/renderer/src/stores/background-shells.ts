/**
 * 后台 shell 的**渲染层唯一镜像**。真源在主进程(`agent-shells.ts` 的注册表),
 * 这里只把它跟上;任何组件要后台 shell,一律读这一份。
 *
 * ★★ 以前它是活动胶囊里的一个 `useState` + 自己的一条 IPC 订阅:每挂一颗胶囊(每个对话 Tab
 *   一颗)就多一条订阅、多一份拷贝,先挂的和后挂的在两次广播之间各说各的;换 Tab 时胶囊卸载,
 *   那份拷贝跟着没了,回来再从零拉一次 —— 中间那一刻胶囊报的是 0。
 *   状态统一管理的规则见 AGENTS.md:**一个事实一个 store,组件不持有业务状态的拷贝**。
 *
 * 收敛方式与 `agent:activeRuns` 同理:广播是**全量**(所有工作区),丢过一条之后下一条自己收敛;
 * 首次用到某个工作区时主动拉一次,但只在这期间没有收到过广播时才采纳(广播更新)。
 */
import { useEffect } from 'react'
import { create } from 'zustand'
import type { BackgroundShellInfo } from '../../../shared/domain/shell'
import { listBackgroundShells, onBackgroundShellsChanged } from '../services/shell'

interface BackgroundShellState {
  byWorkspace: Readonly<Record<string, readonly BackgroundShellInfo[]>>
}

const EMPTY: readonly BackgroundShellInfo[] = []

export const useBackgroundShellStore = create<BackgroundShellState>(() => ({ byWorkspace: {} }))

/** 收到过几次全量广播。主动拉取发出时记下它,回来时变了 = 广播更新,拉取作废 */
let broadcastRev = 0
const primed = new Set<string>()

function applyBroadcast(all: readonly BackgroundShellInfo[]): void {
  broadcastRev += 1
  const byWorkspace: Record<string, BackgroundShellInfo[]> = {}
  for (const shell of all) (byWorkspace[shell.workspaceId] ??= []).push(shell)
  // 全量替换:不在广播里的工作区就是一条都没有
  useBackgroundShellStore.setState({ byWorkspace }, true)
}

/**
 * 全应用起一次(`App.tsx`,与事件泵同一处)。
 * ★ 先订阅、再拉:反过来的话,拉取与订阅之间那一刻的变化会丢。
 */
export function startBackgroundShellSync(): () => void {
  const off = onBackgroundShellsChanged(applyBroadcast)
  return () => {
    off()
    primed.clear()
  }
}

function prime(workspaceId: string): void {
  if (primed.has(workspaceId)) return
  primed.add(workspaceId)
  const rev = broadcastRev
  void listBackgroundShells(workspaceId).then((initial) => {
    if (broadcastRev !== rev) return
    useBackgroundShellStore.setState((state) => ({ byWorkspace: { ...state.byWorkspace, [workspaceId]: initial } }))
  }).catch(() => {
    // 下次有人要它时再试
    primed.delete(workspaceId)
  })
}

/** 本工作区的后台 shell(含已结束的)。 */
export function useBackgroundShells(workspaceId: string): readonly BackgroundShellInfo[] {
  useEffect(() => prime(workspaceId), [workspaceId])
  return useBackgroundShellStore((state) => state.byWorkspace[workspaceId] ?? EMPTY)
}
