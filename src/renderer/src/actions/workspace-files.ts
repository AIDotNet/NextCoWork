/**
 * 工作区文件的**应用动作层**。
 *
 * 为什么不是 `services/workspace-files.ts`:`services/*` 只有 IPC 与纯 helper
 * (见那个文件头)。一旦它顺手去改 `documents` / `tabs` / `window` 三个 store,
 * 它就同时是传输层和编排层 —— 于是 `stores/documents.ts` 反过来读它做读盘/写盘,
 * 形成 `tabs → documents → services → tabs` 那个环。这个环不是理论洁癖:
 * 它让「动作层」不再是一个能一眼看完的东西,而拆分它的唯一办法就是把
 * 「谁在改 store」搬到这里来。
 *
 * 这里**只放会碰 store 的编排**:
 *
 * | 动作 | 同步的 store |
 * |---|---|
 * | `mutateWorkspaceFile` | documents(草稿搬家)、tabs(标签搬家) + 广播 |
 * | `revealWorkspaceFile` | window(展开面板)、tabs(在文件树里定位) |
 * | `saveDocumentFile` | documents + 广播 |
 * | `awaitDocumentSaves` | documents 的破坏性操作闸门 |
 *
 * ★ `services/workspace-files.ts` **不再 re-export 这些**:从传输层把动作再
 * 吐出来,那个环原样长回去,而且这一回连 import 图都看不出来了。
 */
import type { WorkspaceFileMutationRequest, WorkspaceFileMutationResult, WorkspaceFileWriteRequest, WorkspaceFile } from '../../../shared/domain/workspace-file'
import { isWithinPath, useDocumentsStore } from '../stores/documents'
import { pendingDocumentSavesWithin } from '../stores/document-saves'
import { useTabsStore } from '../stores/tabs'
import { useWindowStore } from '../stores/window'
import type { WorkspaceFilesChanged } from '../services/workspace-files'
import {
  announceWorkspaceFileChanged,
  mutateWorkspaceFile as requestWorkspaceMutation,
  revealWorkspaceFile as requestWorkspaceReveal,
  writeWorkspaceFile as requestWorkspaceWrite
} from '../services/workspace-files'

export type { WorkspaceFilesChanged }

/**
 * 破坏性文件操作(改名/移动/删除)之前的闸门:**等这些路径上还在飞的保存落定**。
 *
 * ★ 不等的话,一次具体表现是:用户在文档里敲着字(手动保存还在飞),顺手在
 * 文件树里把同一个文件删了 —— 保存的写盘晚到一步,把刚删掉的文件又写了回去。
 * 而且这不是「文件树才有的问题」:任何入口都要过这一道,所以闸门放在**这个
 * 唯一的变更枢纽**里,而不是每个调用点各抄一遍 —— 抄漏一处的表现就是「某个
 * 入口的名字改完又变回去」,而且只在保存恰好还在飞的时候复现。
 */
export async function awaitDocumentSaves(workspaceId: string, paths: readonly string[]): Promise<void> {
  const pending = new Set<Promise<boolean>>()
  const state = useDocumentsStore.getState()
  for (const path of paths) {
    for (const save of pendingDocumentSavesWithin(workspaceId, path)) pending.add(save)
    // 已搬家的草稿按当前路径也要等到同一次保存，注册表仍记录请求发出时的旧路径。
    for (const entry of Object.values(state.entries)) {
      if (entry.workspaceId !== workspaceId || !isWithinPath(entry.path, path)) continue
      const save = state.activeSave(workspaceId, entry.path)
      if (save !== undefined) pending.add(save)
    }
  }
  await Promise.all(pending)
}

/** 一次改名/移动/删除落定:等保存 → 发请求 → 同步本地 → 广播。 */
export async function mutateWorkspaceFile(req: WorkspaceFileMutationRequest): Promise<WorkspaceFileMutationResult> {
  await awaitDocumentSaves(req.workspaceId, [req.path, ...(req.destination === undefined ? [] : [req.destination])])
  const result = await requestWorkspaceMutation(req)
  // 形状正好是 `WorkspaceFilesChanged`(operation 还是请求里那个变更操作,不是 'save')
  const change = { ...req, ...result }
  useDocumentsStore.getState().applyMutation(change)
  useTabsStore.getState().applyFileMutation(change)
  announceWorkspaceFileChanged(change)
  return result
}

/**
 * 「在文件管理器里显示」。
 *
 * ★ 远端工作区值得一条分支:本机访达打开的是 SSH 上的路径(不存在),所以
 * 它的「显示」= 在本机文件树面板里定位 —— 展开面板 + 选中那一行。本地的走
 * 主进程 `shell.showItemInFolder`。判定放主进程(`result.remote`),渲染层
 * 不按 workspace.environment 猜。
 */
export async function revealWorkspaceFile(workspaceId: string, path: string): Promise<void> {
  const result = await requestWorkspaceReveal(workspaceId, path)
  if (!result?.remote) return
  const window = useWindowStore.getState()
  if (window.activeWorkspaceId !== workspaceId || window.pendingActivation !== null) return
  useTabsStore.getState().open(workspaceId, 'files', 'right', { path: result.parent, title: result.name, selectedPath: result.path })
  window.setRightPanelForWorkspace(workspaceId, true)
}

/**
 * 写盘 + 广播。
 *
 * ★ 走这里而不是直接 `writeWorkspaceFile`:广播**必须**捎上「哪个工作区、
 * 哪个文件」—— 文件树和聊天里的计划卡都靠它刷新。谁写盘谁广播,不能靠调用方
 * 记得补一句。
 */
export async function saveDocumentFile(req: WorkspaceFileWriteRequest): Promise<WorkspaceFile> {
  const file = await requestWorkspaceWrite(req)
  announceWorkspaceFileChanged({ workspaceId: req.workspaceId, path: req.path, operation: 'save' })
  return file
}
