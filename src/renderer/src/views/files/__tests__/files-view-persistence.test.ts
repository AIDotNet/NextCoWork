/**
 * 文件树离开再回来时,展开状态必须还在。
 *
 * 复现的是用户报的那个现象:右侧工作台同一格只挂激活的 Tab,点开一个文件 = 文件树整棵卸载;
 * 以前展开状态在 `useState` 里,回来时全部收起。这里用「卸载 → 重新挂载」模拟那次切换。
 *
 * ★ 第二次挂载时 `listDir` 故意**挂起不返回**:断言的是「第一帧就画出了上次展开的子项」
 *   (来自快照),而不是「重读回来之后又展开了」—— 后者也能让测试通过,但用户看到的是
 *   先收起、再弹开的一闪。
 *
 * @vitest-environment jsdom
 */
import { act, createElement } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { DirListing, FileEntry } from '../../../../../shared/domain/file-tree'
import type { Workspace } from '../../../../../shared/domain/workspace'
import { I18nProvider } from '../../../i18n'

vi.mock('../../../services/app', () => ({ listDir: vi.fn() }))
vi.mock('../../../services/workspace-files', () => ({
  listWorkspaceRecovery: vi.fn(async () => ({ entries: [], environmentKey: '' })),
  mutateWorkspaceFile: vi.fn(),
  revealWorkspaceFile: vi.fn(),
  isResultUnknown: () => false,
  workspaceFileErrorKey: () => 'files.manage.readFailed'
}))

import { listDir } from '../../../services/app'
import { useFileTreeStore } from '../../../stores/file-tree'
import { FilesView } from '../FilesView'

const entry = (path: string, kind: FileEntry['kind']): FileEntry => ({
  name: path.slice(path.lastIndexOf('/') + 1), path, kind, hidden: false
})
const LISTINGS: Record<string, DirListing> = {
  '': { path: '', entries: [entry('src', 'dir'), entry('README.md', 'file')], truncated: false },
  src: { path: 'src', entries: [entry('src/App.tsx', 'file')], truncated: false }
}
const WORKSPACE = { id: 'w1', name: 'demo', rootPath: '/tmp/demo', environment: { kind: 'local' } } as unknown as Workspace

let container: HTMLDivElement | null = null
let root: Root | null = null

beforeEach(() => {
  useFileTreeStore.setState({ views: {} })
  vi.mocked(listDir).mockImplementation(async (_workspaceId, path) => {
    const listing = LISTINGS[path]
    if (listing === undefined) throw new Error(`no listing for ${path}`)
    return listing
  })
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  vi.stubGlobal('ResizeObserver', class {
    observe(): void {}
    unobserve(): void {}
    disconnect(): void {}
  })
})

afterEach(async () => {
  await unmount()
  vi.unstubAllGlobals()
})

async function mount(): Promise<void> {
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  await act(async () => root?.render(createElement(I18nProvider, {
    initialLocale: 'zh-CN',
    children: createElement(FilesView, { workspace: WORKSPACE, rootPath: '', selectedPath: null, onOpenFile: () => undefined })
  })))
  await act(async () => { await Promise.resolve() })
}

async function unmount(): Promise<void> {
  if (root !== null) await act(async () => root?.unmount())
  container?.remove()
  root = null
  container = null
}

const rowNames = (): string[] =>
  [...document.querySelectorAll('[role="treeitem"]')].map((node) => node.getAttribute('aria-label') ?? '')

describe('FilesView · 离开再回来', () => {
  it('★ 展开过的目录在重新挂载的第一帧就还是展开的', async () => {
    await mount()
    const src = [...document.querySelectorAll<HTMLElement>('[role="treeitem"]')].find((node) => node.getAttribute('aria-label') === 'src')
    await act(async () => src?.click())
    await act(async () => { await Promise.resolve() })
    expect(rowNames()).toEqual(['src', 'App.tsx', 'README.md'])

    await unmount()
    // 回来时盘还没读完:画面必须来自快照
    vi.mocked(listDir).mockImplementation(() => new Promise<DirListing>(() => undefined))
    await mount()
    expect(rowNames()).toEqual(['src', 'App.tsx', 'README.md'])
    expect(document.querySelector('[aria-label="src"]')?.getAttribute('aria-expanded')).toBe('true')
    // 静默重读:不转圈、不出「正在刷新」那一行
    expect(document.body.textContent).not.toContain('正在刷新文件列表')
    // 每一个展开的目录都重新读了一遍,离开期间的变化会在这一轮补上
    expect(vi.mocked(listDir).mock.calls.slice(-2).map((call) => call[1])).toEqual(['', 'src'])
  })

  it('换一个子树根是另一棵树,不继承别的根的展开状态', async () => {
    useFileTreeStore.getState().save('w1\u0000other', { expanded: ['src'], listings: LISTINGS, scrollTop: 0 })
    await mount()
    expect(rowNames()).toEqual(['src', 'README.md'])
  })

  it('整棵树只有一行能 Tab 进来,↓ 把焦点移到下一行', async () => {
    await mount()
    const rows = [...document.querySelectorAll<HTMLElement>('[role="treeitem"]')]
    expect(rows.map((node) => node.tabIndex)).toEqual([0, -1])
    await act(async () => rows[0]?.focus())
    await act(async () => {
      rows[0]?.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
    })
    expect(document.activeElement?.getAttribute('aria-label')).toBe('README.md')
  })
})
