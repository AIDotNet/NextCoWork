/**
 * 「打开方式」的渲染层一侧。
 *
 * 需求:同一份文件行/工具条在**三种工作区**下都要给出「用别的程序打开」——
 * 本机工作区能弹 Finder/IDE/终端,SSH 工作区没有本机文件可开,而文件树那一行
 * 现在也照样画着「在文件管理器中显示」。所以探测结果自带 `available`,
 * 让**菜单自己少画几项**,而不是画一颗点了报错的按钮(AGENTS.md「不做防御式 UI」)。
 *
 * ★ 绝对路径**不进渲染层**:两个「复制路径」由主进程直接写剪贴板。这既是
 *   `workspace-file.ts` 那条既有约定(渲染层只认工作区相对路径),也让「复制到
 *   剪贴板」这个动作不依赖渲染层有没有剪贴板权限。
 */
import type { OpenTarget, WorkspacePathKind } from '../../../shared/domain/open-target'
import { invoke } from './ipc'

/** 这台机器上现在能用的打开方式;两个通用目标(文件管理器/终端)恒在,IDE 按实际安装。 */
export function listOpenTargets(): Promise<OpenTarget[]> {
  return invoke('workspace:listOpenTargets', undefined)
}

/**
 * 用一个**具名目标**打开文件。`targetId` 只用来查主进程那张表,查不到即拒绝 ——
 * 它不携带路径或命令,渲染层也就无从指定要跑什么。
 */
export function openWithTarget(workspaceId: string, path: string, targetId: string): Promise<void> {
  return invoke('workspace:openWith', { workspaceId, path, targetId })
}

/** 把路径写进系统剪贴板。返回的是真正写进去的那一串,便于界面给一句确认。 */
export function copyWorkspacePath(workspaceId: string, path: string, kind: WorkspacePathKind): Promise<string> {
  return invoke('workspace:copyPath', { workspaceId, path, kind })
}

/**
 * 「另存为…」:主进程弹系统保存框,再把这个工作区文件复制过去。
 * 返回 false = 用户取消(不是失败,调用方不该报错)。只对本机工作区有意义。
 */
export function saveWorkspaceFileAs(workspaceId: string, path: string): Promise<boolean> {
  return invoke('workspace:saveFileAs', { workspaceId, path })
}
