/**
 * Git 的远程读写,复用桌面同一套 `ipc/git.ts`(已是零 Electron:只用 execFile 与环境端口)。
 *
 * - 读:overview / branches / commits / diff。
 * - 写(execution,幂等回执):stage / unstage / commit / checkout / createBranch。
 * - ★ 不含 pull / push:它们走网络与凭据,在无人值守的设备上弹认证无人可答。
 *
 * 错误只给稳定的错误码与固定短句 —— git 的 stderr 里常有绝对路径、远端 URL,不经网络输出。
 */
import { z } from 'zod'
import { IpcError } from '../../../main/ipc/errors'
import {
  checkoutGitBranch, commitGit, createGitBranch, getGitDiff, getGitOverview, listGitBranches, listGitCommits,
  stageGitPaths, unstageGitPaths
} from '../../../main/ipc/git'
import { store } from '../../../main/state/store'
import { DeviceProtocolError } from '../../../shared/device/errors'
import {
  DEVICE_METHODS, gitCheckoutParams, gitCommitParams, gitCommitsParams, gitCreateBranchParams, gitDiffParams, gitDoneResult,
  gitPathsParams, gitWorkspaceParams
} from '../../../shared/device/methods'
import type { DeviceValue } from '../../../shared/device/value'
import type { DeviceRpcRouter } from '../rpc'

const anyValue = z.custom<DeviceValue>(() => true)
const plain = (value: unknown): DeviceValue => JSON.parse(JSON.stringify(value ?? null)) as DeviceValue

function requireWorkspace(workspaceId: string): void {
  if (store.getWorkspace(workspaceId) === undefined) throw new DeviceProtocolError('not_found', 'Workspace does not exist')
}

/** git 层抛 IpcError(含 stderr 细节):只保留「失败了」这一事实。 */
async function guarded<T>(work: () => Promise<T>): Promise<T> {
  try {
    return await work()
  } catch (error) {
    if (error instanceof DeviceProtocolError) throw error
    if (error instanceof IpcError) {
      // 没有仓库/空提交说明等是调用方可处理的前置条件;其余归为一次失败的 git 操作。
      throw new DeviceProtocolError(error.code === 'unknown' ? 'not_found' : 'conflict', 'Git operation failed')
    }
    throw error
  }
}

export function registerGitMethods(router: DeviceRpcRouter): void {
  router.query(DEVICE_METHODS.gitOverview, gitWorkspaceParams, anyValue, (_context, p) => {
    requireWorkspace(p.workspaceId)
    return guarded(async () => plain(await getGitOverview(p)))
  })
  router.query(DEVICE_METHODS.gitBranches, gitWorkspaceParams, anyValue, (_context, p) => {
    requireWorkspace(p.workspaceId)
    return guarded(async () => plain(await listGitBranches(p)))
  })
  router.query(DEVICE_METHODS.gitCommits, gitCommitsParams, anyValue, (_context, p) => {
    requireWorkspace(p.workspaceId)
    return guarded(async () => plain(await listGitCommits(p)))
  })
  router.query(DEVICE_METHODS.gitDiff, gitDiffParams, anyValue, (_context, p) => {
    requireWorkspace(p.workspaceId)
    return guarded(async () => plain(await getGitDiff(p)))
  })

  router.execution(DEVICE_METHODS.gitStage, gitPathsParams, gitDoneResult, (_execution, p) => {
    requireWorkspace(p.workspaceId)
    return guarded(async () => { await stageGitPaths(p); return { done: true as const } })
  })
  router.execution(DEVICE_METHODS.gitUnstage, gitPathsParams, gitDoneResult, (_execution, p) => {
    requireWorkspace(p.workspaceId)
    return guarded(async () => { await unstageGitPaths(p); return { done: true as const } })
  })
  router.execution(DEVICE_METHODS.gitCommit, gitCommitParams, anyValue, (_execution, p) => {
    requireWorkspace(p.workspaceId)
    return guarded(async () => plain(await commitGit(p)))
  })
  router.execution(DEVICE_METHODS.gitCheckout, gitCheckoutParams, gitDoneResult, (_execution, p) => {
    requireWorkspace(p.workspaceId)
    return guarded(async () => { await checkoutGitBranch(p); return { done: true as const } })
  })
  router.execution(DEVICE_METHODS.gitCreateBranch, gitCreateBranchParams, anyValue, (_execution, p) => {
    requireWorkspace(p.workspaceId)
    return guarded(async () => plain(await createGitBranch(p)))
  })
}
