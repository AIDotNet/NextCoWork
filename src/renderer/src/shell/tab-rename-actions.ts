/**
 * 双击改名的提交端 —— 带副作用的那一半(`tab-rename.ts` 是纯的那一半)。
 *
 * 分派的依据是 `tabRenameTarget`,三条去向各自的理由写在那个文件里。
 */
import type { InnerTab } from '../../../shared/domain/tab'
import { WORKSPACE_FILE_ERROR_PREFIX } from '../../../shared/domain/workspace-file'
import { updateWorkspace } from '../services/app'
import { renameSession } from '../services/sessions'
import { mutateWorkspaceFile } from '../actions/workspace-files'
import { workspaceFileErrorKey } from '../services/workspace-files'
import { useTabsStore } from '../stores/tabs'
import { useWindowStore } from '../stores/window'
import { toast } from '../stores/toast'
import { translate } from '../i18n'
import { operationRequest } from '../views/files/file-operations'
import { confirmOverwrite } from './overwrite-confirm'
import { baseName, restoreExtension, tabRenameTarget } from './tab-rename'

/**
 * 把一次改名落下去。
 *
 * 返回值目前无人使用,留着是为了让调用点能在将来决定「失败时要不要留在编辑态」——
 * 现在一律退出编辑态,因为失败原因已经通过 toast 说清楚了,把输入框钉在那里
 * 反而让用户以为自己还没提交。
 */
export async function submitTabRename(
  workspaceId: string,
  tab: InnerTab,
  input: string
): Promise<boolean> {
  const target = tabRenameTarget(tab)
  if (target === null) return false

  if (target.kind === 'session') {
    const title = input.trim()
    if (title === '' || title === tab.title) return false
    /*
      ★ 本地标题**也要改**,而且要在 IPC 之前。`sessions:rename` 的广播回来
      需要一个来回,中间那一段用户看到的仍是旧标题 —— 而他刚刚才敲完回车。
      失败时下一次 `syncSessionTitle` 会把它纠正回来。
    */
    useTabsStore.getState().rename(workspaceId, tab.id, title)
    // 草稿态在这一刻才铸 id —— 同 `bindChatSession` 的其余调用点
    const sessionId = useTabsStore.getState().bindChatSession(workspaceId, tab.id)
    if (sessionId === null) return false
    try {
      await renameSession(sessionId, title)
      return true
    } catch {
      toast.error(translate('nav.renameFailed'), 'tab-rename')
      return false
    }
  }

  if (target.kind === 'local') {
    const title = input.trim()
    if (title === '' || title === tab.title) return false
    useTabsStore.getState().rename(workspaceId, tab.id, title)
    return true
  }

  return renameWorkspaceFile(workspaceId, target.path, input)
}

/**
 * 文件类标签:改的是**磁盘上的文件名**。
 *
 * ★ 校验复用 `views/files/file-operations.ts` 的 `operationRequest` —— 空串、
 * `.`、`..`、路径分隔符、控制字符都在那里挡掉,连报错用的 i18n key 都是现成的。
 * 在这里另写一套的话,文件树里改名和标签上改名会给出两套不同的说法。
 *
 * ★★ 提交必须走 `mutateWorkspaceFile`,不能直接 `invoke`:它是同时同步
 * documents store、tabs store 和广播的**唯一**枢纽。绕过去的表现是
 * 「文件改名了,但草稿还挂在旧路径上」。
 */
async function renameWorkspaceFile(workspaceId: string, path: string, input: string): Promise<boolean> {
  const original = baseName(path)
  const name = restoreExtension(input, original)
  if (name === original) return false

  const validated = operationRequest(workspaceId, { operation: 'rename', path, name: original }, name)
  if (validated.error !== undefined) {
    // `files.manage.unchanged` 不是错误,是"什么都没改" —— 不打扰用户
    if (validated.error !== 'files.manage.unchanged') toast.error(translate(validated.error), 'tab-rename')
    return false
  }

  try {
    await mutateWorkspaceFile(validated.request)
    return true
  } catch (error) {
    /*
      ★ 撞名走**第二次**调用,而不是第一次就带上 `overwrite`。第一次不带的
      意义在于:只有服务端真的判定撞了,才会去打扰用户 —— 客户端自己 stat
      一遍再决定,中间那段时间差里文件可能刚被别的进程建出来。
    */
    if (!isExistsError(error)) {
      toast.error(translate(workspaceFileErrorKey(error)), 'tab-rename')
      return false
    }
    if (!(await confirmOverwrite(name))) return false
    try {
      await mutateWorkspaceFile({ ...validated.request, overwrite: true })
      return true
    } catch (retryError) {
      toast.error(translate(workspaceFileErrorKey(retryError)), 'tab-rename')
      return false
    }
  }
}

function isExistsError(error: unknown): boolean {
  const message = error instanceof Error ? error.message : ''
  return message === `${WORKSPACE_FILE_ERROR_PREFIX}exists`
}

/**
 * 外层工作区标签改名。
 *
 * ★ **先乐观改本地,再发 IPC。** 真值靠主进程 `workspace:update` 里的 `announce()`
 * 广播回来(多窗口同步全靠它),但那一趟往返里 Tab 上还挂着旧名字 —— 用户刚敲完
 * 回车看到的是「没改成」。失败时把原值放回去,而不是等下一次广播来纠正:
 * 改名失败常常伴随着主进程那边什么都没发生,那就一条广播都不会来。
 *
 * ★★ 主进程**不需要改**:`workspace:update` 的 req 早就有 `name?`,
 * `ipc/workspace.ts` 也早就处理并广播了 —— 这个口子一直留着没人用。
 */
export async function submitWorkspaceRename(workspaceId: string, input: string): Promise<boolean> {
  const name = input.trim()
  const current = useWindowStore.getState().workspaceTargets[workspaceId]
  if (name === '' || current === undefined || current.name === name) return false

  const replace = (workspace: typeof current): void => {
    const state = useWindowStore.getState()
    state.updateWorkspaces([
      ...Object.values(state.workspaceTargets).filter((item) => item.id !== workspace.id),
      workspace
    ])
  }

  replace({ ...current, name })
  try {
    replace(await updateWorkspace({ id: workspaceId, name }))
    return true
  } catch {
    replace(current)
    toast.error(translate('nav.renameFailed'), 'tab-rename')
    return false
  }
}
