/**
 * 工作区文件的**应用动作层** —— 搬离 `services/workspace-files.ts` 之后的那一半。
 *
 * 这里钉两件在传输层看不出来的事:
 *
 * 1. **改变更枢纽 = 保护闸门。** 改名/移动/删除之前必须先等这些路径上还在飞的
 *    保存落定,否则「删掉的文件被晚到的保存写回来」。闸门放进唯一的枢纽,任何
 *    入口(tab 改名、文件树、恢复删除)都自动过它,不需要各抄一遍。
 * 2. **回执按身份认领。** 保存途中文件被改名 = entry 换了 key,`applyMutation`
 *    把它搬过去;保存的完成回执跟过去收掉 saving、更新 revision。
 */
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { WorkspaceFile } from '../../../../shared/domain/workspace-file'

vi.mock('../../services/workspace-files', () => ({
  readWorkspaceFile: vi.fn(),
  writeWorkspaceFile: vi.fn(),
  mutateWorkspaceFile: vi.fn(async (req: { path: string; destination?: string }) => ({ path: req.path, destination: req.destination })),
  revealWorkspaceFile: vi.fn(),
  workspaceFileErrorKey: vi.fn(() => 'document.error.io'),
  announceWorkspaceFileChanged: vi.fn()
}))
vi.mock('../../stores/tabs', () => ({
  useTabsStore: { getState: () => ({ applyFileMutation: vi.fn(), open: vi.fn() }) }
}))
vi.mock('../../stores/window', () => ({
  useWindowStore: { getState: () => ({ activeWorkspaceId: 'w', pendingActivation: null, setRightPanelForWorkspace: vi.fn() }) }
}))

import { awaitDocumentSaves, mutateWorkspaceFile, revealWorkspaceFile } from '../workspace-files'
import { announceWorkspaceFileChanged, mutateWorkspaceFile as requestMutation, readWorkspaceFile, revealWorkspaceFile as requestReveal, writeWorkspaceFile } from '../../services/workspace-files'
import { documentKey, isDocumentDirty, useDocumentsStore } from '../../stores/documents'

const textFile = (content = 'original', revision = 'r1'): WorkspaceFile => ({ kind: 'text', path: 'note.md', size: content.length, content, revision })
const entry = (path: string) => useDocumentsStore.getState().entries[documentKey('w', path)]

beforeEach(() => {
  vi.clearAllMocks()
  useDocumentsStore.setState({ entries: {}, confirmation: null })
  vi.mocked(readWorkspaceFile).mockResolvedValue(textFile())
})

describe('mutateWorkspaceFile · 破坏性操作前的保存闸门', () => {
  it('★ 改名会等这次路径上还在飞的保存 —— 不然删/改名后旧内容被写回来', async () => {
    let complete!: (value: WorkspaceFile) => void
    vi.mocked(writeWorkspaceFile).mockImplementation(() => new Promise((resolve) => { complete = resolve }))
    await useDocumentsStore.getState().load('w', 'note.md')
    useDocumentsStore.getState().edit('w', 'note.md', 'edited')
    const saving = useDocumentsStore.getState().save('w', 'note.md')

    const mutation = mutateWorkspaceFile({ workspaceId: 'w', operation: 'rename', path: 'note.md', destination: 'renamed.md' })
    // 保存还没落定 —— 真正的那次改名请求**一个都还没发**
    expect(requestMutation).not.toHaveBeenCalled()

    complete(textFile('edited', 'r2'))
    await saving
    await mutation
    expect(requestMutation).toHaveBeenCalledTimes(1)
    expect(vi.mocked(requestMutation).mock.calls[0]?.[0]).toMatchObject({ operation: 'rename', destination: 'renamed.md' })
    // 同步也走了:草稿搬到了新 key
    expect(entry('note.md')).toBeUndefined()
    expect(entry('renamed.md')?.path).toBe('renamed.md')
    // 一次是保存落定的通知,一次是改名落定的通知 —— 两条路都得广播
    expect(announceWorkspaceFileChanged).toHaveBeenCalledTimes(2)
    expect(vi.mocked(announceWorkspaceFileChanged).mock.calls.at(-1)?.[0]).toMatchObject({ operation: 'rename', destination: 'renamed.md' })
  })

  it('目录删除等待子文件的保存，不等待相似前缀或其它工作区', async () => {
    const complete = new Map<string, (value: WorkspaceFile) => void>()
    vi.mocked(writeWorkspaceFile).mockImplementation((req) => new Promise((resolve) => {
      complete.set(`${req.workspaceId}:${req.path}`, resolve)
    }))
    const saving: Promise<boolean>[] = []
    for (const [workspaceId, path] of [['w', 'folder/a.md'], ['w', 'folder-extra/a.md'], ['other', 'folder/a.md']] as const) {
      await useDocumentsStore.getState().load(workspaceId, path)
      useDocumentsStore.getState().edit(workspaceId, path, 'edited')
      saving.push(useDocumentsStore.getState().save(workspaceId, path))
    }
    const mutation = mutateWorkspaceFile({ workspaceId: 'w', operation: 'delete', path: 'folder' })
    expect(requestMutation).not.toHaveBeenCalled()
    complete.get('w:folder/a.md')!(textFile('edited', 'r2'))
    await mutation
    expect(requestMutation).toHaveBeenCalledOnce()
    expect(entry('folder/a.md')).toBeUndefined()
    expect(entry('folder-extra/a.md')?.saving).toBe(true)
    complete.get('w:folder-extra/a.md')!(textFile('edited', 'r2'))
    complete.get('other:folder/a.md')!(textFile('edited', 'r2'))
    await Promise.all(saving)
  })

  it('目录草稿条目已经被移除时，仍等待子文件的在途保存', async () => {
    let complete!: (value: WorkspaceFile) => void
    vi.mocked(writeWorkspaceFile).mockImplementation(() => new Promise((resolve) => { complete = resolve }))
    await useDocumentsStore.getState().load('w', 'folder/a.md')
    useDocumentsStore.getState().edit('w', 'folder/a.md', 'edited')
    const saving = useDocumentsStore.getState().save('w', 'folder/a.md')
    useDocumentsStore.getState().applyMutation({ workspaceId: 'w', operation: 'delete', path: 'folder' })
    const mutation = mutateWorkspaceFile({ workspaceId: 'w', operation: 'delete', path: 'folder' })
    expect(requestMutation).not.toHaveBeenCalled()
    complete(textFile('edited', 'r2'))
    await saving
    await mutation
    expect(requestMutation).toHaveBeenCalledOnce()
  })

  it('同路径关闭重开后的两次保存都要等，不能让新身份覆盖旧请求', async () => {
    const complete: Array<(value: WorkspaceFile) => void> = []
    vi.mocked(writeWorkspaceFile).mockImplementation(() => new Promise((resolve) => { complete.push(resolve) }))
    await useDocumentsStore.getState().load('w', 'note.md')
    useDocumentsStore.getState().edit('w', 'note.md', 'first')
    const first = useDocumentsStore.getState().save('w', 'note.md')
    useDocumentsStore.getState().applyMutation({ workspaceId: 'w', operation: 'delete', path: 'note.md' })
    await useDocumentsStore.getState().load('w', 'note.md')
    useDocumentsStore.getState().edit('w', 'note.md', 'second')
    const second = useDocumentsStore.getState().save('w', 'note.md')
    const mutation = mutateWorkspaceFile({ workspaceId: 'w', operation: 'delete', path: 'note.md' })
    complete[1]!(textFile('second', 'r3'))
    await second
    await Promise.resolve()
    expect(requestMutation).not.toHaveBeenCalled()
    complete[0]!(textFile('first', 'r2'))
    await first
    await mutation
    expect(requestMutation).toHaveBeenCalledOnce()
  })

  it('没有在途保存时闸门直接放行', async () => {
    await useDocumentsStore.getState().load('w', 'note.md')
    await awaitDocumentSaves('w', ['note.md'])
    await mutateWorkspaceFile({ workspaceId: 'w', operation: 'delete', path: 'note.md' })
    expect(requestMutation).toHaveBeenCalledTimes(1)
  })

  /**
   * ★ 闸门按**路径的索引**找保存,而不是遍历条目 —— 因为删除这条路上,条目会先
   * 被 `applyMutation` 删掉,而那次写盘还在飞。靠条目找的话,「删除时等保存落定」
   * 在条目删掉之后就成了空等,而这时恰恰最需要等。
   */
  it('★ 条目已被删除,闸门仍等得到那次在途保存', async () => {
    let complete!: (value: WorkspaceFile) => void
    vi.mocked(writeWorkspaceFile).mockImplementation(() => new Promise((resolve) => { complete = resolve }))
    await useDocumentsStore.getState().load('w', 'note.md')
    useDocumentsStore.getState().edit('w', 'note.md', 'edited')
    const saving = useDocumentsStore.getState().save('w', 'note.md')
    // 删除把条目摘掉了 —— 但写盘还在飞
    useDocumentsStore.getState().applyMutation({ workspaceId: 'w', path: 'note.md', operation: 'delete' })
    expect(entry('note.md')).toBeUndefined()
    expect(useDocumentsStore.getState().activeSave('w', 'note.md')).toBe(saving)

    let gatePassed = false
    const gate = awaitDocumentSaves('w', ['note.md']).then(() => { gatePassed = true })
    await Promise.resolve()
    expect(gatePassed).toBe(false)
    complete(textFile('edited', 'r2'))
    await gate
    expect(gatePassed).toBe(true)
  })
})

describe('revealWorkspaceFile · 远端才落到本机文件树', () => {
  it('远端工作区:在主进程给的父目录里开一个 files Tab 定位', async () => {
    vi.mocked(requestReveal).mockResolvedValue({ remote: true, path: 'src/a.ts', parent: 'src', name: 'a.ts' })
    await revealWorkspaceFile('w', 'src/a.ts')
    expect(requestReveal).toHaveBeenCalledWith('w', 'src/a.ts')
  })

  it('本地工作区:主进程返回 undefined,渲染层什么都不开', async () => {
    vi.mocked(requestReveal).mockResolvedValue(undefined)
    await revealWorkspaceFile('w', 'src/a.ts')
    expect(requestReveal).toHaveBeenCalledWith('w', 'src/a.ts')
  })
})

describe('保存途中改名的回执闭合', () => {
  it('saving 收掉、revision 更新、期间新敲的字仍在草案里', async () => {
    let complete!: (value: WorkspaceFile) => void
    vi.mocked(writeWorkspaceFile).mockImplementation(() => new Promise((resolve) => { complete = resolve }))
    await useDocumentsStore.getState().load('w', 'note.md')
    useDocumentsStore.getState().edit('w', 'note.md', 'first')
    const saving = useDocumentsStore.getState().save('w', 'note.md')
    // ★ 等到交回给调用方(resolve 之后)再开始改名 —— 上面那条闸门用例已经钉住了
    //   「保存没落定之前改名会等」,这条只关心回执落地的形态。
    complete(textFile('first', 'r2'))
    await saving
    await mutateWorkspaceFile({ workspaceId: 'w', operation: 'rename', path: 'note.md', destination: 'renamed.md' })
    useDocumentsStore.getState().edit('w', 'renamed.md', 'first + more')
    vi.mocked(writeWorkspaceFile).mockResolvedValue({ kind: 'text', path: 'renamed.md', size: 13, content: 'first + more', revision: 'r3' })
    await useDocumentsStore.getState().save('w', 'renamed.md')
    const moved = entry('renamed.md')
    expect(moved?.saving).toBe(false)
    expect(moved?.file?.revision).toBe('r3')
    expect(moved?.base).toBe('first + more')
    expect(isDocumentDirty(moved!)).toBe(false)
    expect(moved?.draft).toBe('first + more')
  })
})
