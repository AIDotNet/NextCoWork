/**
 * Git 面板的服务层 —— 协议 §9:组件不碰频道字符串,只调这里。
 *
 * 薄到没有逻辑是**故意的**:每一条都是一次 invoke。判断「这个仓库能不能用」
 * 之类的事情全在主进程做完了,`GitOverview` 自己就是答案。
 */
import type {
  GitBranchSummary,
  GitCommitSummary,
  GitDiff,
  GitDiffSides,
  GitOverview
} from '../../../shared/domain/git'
import { invoke } from './ipc'

export function getGitOverview(workspaceId: string): Promise<GitOverview> {
  return invoke('git:getOverview', { workspaceId })
}

export function listGitBranches(workspaceId: string): Promise<GitBranchSummary[]> {
  return invoke('git:listBranches', { workspaceId })
}

export function listGitCommits(workspaceId: string, limit?: number): Promise<GitCommitSummary[]> {
  return invoke('git:listCommits', { workspaceId, limit })
}

export function getGitDiff(workspaceId: string, path: string, staged: boolean): Promise<GitDiff> {
  return invoke('git:getDiff', { workspaceId, path, staged })
}

/** 两侧全文,给代码对比视图。`null` = 没法按全文对比,界面退回 unified 文本 */
export function getGitDiffSides(
  workspaceId: string,
  path: string,
  staged: boolean
): Promise<GitDiffSides | null> {
  return invoke('git:getDiffSides', { workspaceId, path, staged })
}

export function stageGitPaths(workspaceId: string, paths: string[]): Promise<void> {
  return invoke('git:stage', { workspaceId, paths })
}

export function unstageGitPaths(workspaceId: string, paths: string[]): Promise<void> {
  return invoke('git:unstage', { workspaceId, paths })
}

export function commitGit(workspaceId: string, message: string): Promise<GitCommitSummary> {
  return invoke('git:commit', { workspaceId, message })
}

export function checkoutGitBranch(workspaceId: string, branch: string): Promise<void> {
  return invoke('git:checkoutBranch', { workspaceId, branch })
}

export function createGitBranch(
  workspaceId: string,
  name: string,
  checkout: boolean
): Promise<GitBranchSummary> {
  return invoke('git:createBranch', { workspaceId, name, checkout })
}

export function pullGit(workspaceId: string): Promise<void> {
  return invoke('git:pull', { workspaceId })
}

export function pushGit(workspaceId: string): Promise<void> {
  return invoke('git:push', { workspaceId })
}

/**
 * 让模型照暂存区写一条提交信息草稿。
 *
 * ★ 不传模型:主进程读设置里的默认模型(同「AI 生成子代理」)。渲染层这一侧
 *   根本拿不到设置 —— 它们是 `App.tsx` 的 state,顺 props 传给 AppShell,
 *   而 Git 面板走的是 `FeatureView`,不在那条链上。
 */
export function generateGitCommitMessage(workspaceId: string): Promise<{ message: string }> {
  return invoke('git:generateCommitMessage', { workspaceId })
}
