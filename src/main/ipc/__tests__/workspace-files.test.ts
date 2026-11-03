import {
  chmodSync,
  existsSync,
  linkSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  realpathSync,
  renameSync,
  rmSync,
  statSync,
  symlinkSync,
  truncateSync,
  writeFileSync
} from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { WORKSPACE_IMAGE_LIMIT, WORKSPACE_TEXT_LIMIT } from '../../../shared/domain/workspace-file'

const mocks = vi.hoisted(() => ({
  root: '',
  trash: vi.fn<(path: string) => Promise<void>>(),
  reveal: vi.fn(),
  clipboard: vi.fn()
}))

vi.mock('electron', () => ({
  shell: { trashItem: mocks.trash, showItemInFolder: mocks.reveal },
  clipboard: { writeText: mocks.clipboard }
}))
vi.mock('../../state/store', () => ({
  store: { getWorkspace: (id: string) => id === 'workspace' ? { rootPath: mocks.root } : undefined }
}))

import { copyWorkspacePath, mutateWorkspaceFile, readWorkspaceFile, revealWorkspaceFile, writeWorkspaceFile } from '../workspace-files'
import { toAgentError } from '../errors'

let temporary = ''
let outside = ''
const request = (path: string) => ({ workspaceId: 'workspace', path })

beforeEach(() => {
  temporary = mkdtempSync(join(tmpdir(), 'ncw-file-test-'))
  mocks.root = join(temporary, 'workspace')
  outside = join(temporary, 'outside')
  mkdirSync(mocks.root)
  mkdirSync(outside)
  mocks.root = realpathSync.native(mocks.root)
  mocks.trash.mockReset()
  mocks.reveal.mockReset()
  mocks.clipboard.mockReset()
  mocks.trash.mockImplementation(async (path: string) => {
    renameSync(path, join(outside, 'trashed'))
  })
})

afterEach(() => {
  rmSync(temporary, { recursive: true, force: true })
})

describe('workspace file previews', () => {
  it('reads UTF-8 exactly, including BOM, mixed line endings and unicode', () => {
    const content = '\ufeff# 你好\r\nline one\nlast line\r'
    writeFileSync(join(mocks.root, 'readme.md'), content)
    const result = readWorkspaceFile(request('readme.md'))
    expect(result).toMatchObject({ kind: 'text', path: 'readme.md', content, size: Buffer.byteLength(content) })
    expect(result.revision).toMatch(/^[a-f0-9]{64}$/)
    expect(JSON.stringify(result)).not.toContain(mocks.root)
  })

  it('supports empty files, dotfiles and extensionless source files', () => {
    writeFileSync(join(mocks.root, '.env'), 'TOKEN=value\n')
    writeFileSync(join(mocks.root, 'Makefile'), '')
    expect(readWorkspaceFile(request('.env'))).toMatchObject({ kind: 'text', content: 'TOKEN=value\n' })
    expect(readWorkspaceFile(request('Makefile'))).toMatchObject({ kind: 'text', content: '' })
  })

  it('returns images as bounded data URLs', () => {
    const bytes = Buffer.from('89504e470d0a1a0a', 'hex')
    writeFileSync(join(mocks.root, 'image.PNG'), bytes)
    expect(readWorkspaceFile(request('image.PNG'))).toMatchObject({
      kind: 'image', mime: 'image/png', dataUrl: `data:image/png;base64,${bytes.toString('base64')}`
    })
  })

  it.each([
    ['document.pdf', Buffer.from('%PDF-1.4'), 'unsupported'],
    ['binary.txt', Buffer.from([0, 1, 2]), 'unsupported'],
    ['invalid.txt', Buffer.from([0xc3, 0x28]), 'encoding'],
    ['utf16.txt', Buffer.from([0xff, 0xfe, 0x61, 0]), 'encoding']
  ])('falls back without corrupting %s', (path, bytes, reason) => {
    writeFileSync(join(mocks.root, path), bytes)
    expect(readWorkspaceFile(request(path))).toMatchObject({ kind: 'binary', reason })
  })

  it.each([
    ['large.txt', WORKSPACE_TEXT_LIMIT],
    ['large.png', WORKSPACE_IMAGE_LIMIT]
  ])('rejects oversized preview payloads for %s', (path, limit) => {
    const target = join(mocks.root, path)
    writeFileSync(target, '')
    truncateSync(target, limit + 1)
    const result = readWorkspaceFile(request(path))
    expect(result).toMatchObject({ kind: 'binary', reason: 'too-large', size: limit + 1 })
    expect(result).not.toHaveProperty('content')
    expect(result).not.toHaveProperty('dataUrl')
  })
})

describe('workspace file saves', () => {
  it('atomically saves exact text and retains executable permissions', () => {
    const target = join(mocks.root, 'script.sh')
    writeFileSync(target, '#!/bin/sh\r\necho old\r\n')
    chmodSync(target, 0o755)
    const before = readWorkspaceFile(request('script.sh'))
    const content = '\ufeff#!/bin/sh\r\necho 新内容\r\n'
    const saved = writeWorkspaceFile({ ...request('script.sh'), content, revision: before.revision })
    expect(saved).toMatchObject({ kind: 'text', content })
    expect(saved.revision).not.toBe(before.revision)
    expect(readFileSync(target, 'utf8')).toBe(content)
    expect(statSync(target).mode & 0o777).toBe(0o755)
    expect(readdirSync(mocks.root)).toEqual(['script.sh'])
  })

  it('preserves original bytes on a read/save round trip with BOM and mixed newlines', () => {
    const original = Buffer.from('\ufeffa\r\nb\rc\n')
    writeFileSync(join(mocks.root, 'roundtrip.txt'), original)
    const file = readWorkspaceFile(request('roundtrip.txt'))
    if (file.kind !== 'text') throw new Error('Expected text')
    writeWorkspaceFile({ ...request(file.path), revision: file.revision, content: file.content })
    expect(readFileSync(join(mocks.root, file.path))).toEqual(original)
  })

  it('detects external changes even if content length is unchanged', () => {
    const target = join(mocks.root, 'source.ts')
    writeFileSync(target, 'const a = 1\n')
    const before = readWorkspaceFile(request('source.ts'))
    writeFileSync(target, 'const a = 2\n')
    expect(() => writeWorkspaceFile({ ...request('source.ts'), content: 'const a = 3\n', revision: before.revision }))
      .toThrow('workspace_file:conflict')
    expect(readFileSync(target, 'utf8')).toBe('const a = 2\n')
  })

  it('rejects a file replaced by a symbolic link after reading', () => {
    writeFileSync(join(mocks.root, 'file.txt'), 'original')
    const before = readWorkspaceFile(request('file.txt'))
    rmSync(join(mocks.root, 'file.txt'))
    writeFileSync(join(outside, 'external.txt'), 'external')
    symlinkSync(join(outside, 'external.txt'), join(mocks.root, 'file.txt'))
    expect(() => writeWorkspaceFile({ ...request('file.txt'), content: 'changed', revision: before.revision })).toThrow()
    expect(readFileSync(join(outside, 'external.txt'), 'utf8')).toBe('external')
  })

  it('replaces a hard link locally without changing its external target', () => {
    writeFileSync(join(outside, 'external.txt'), 'external')
    linkSync(join(outside, 'external.txt'), join(mocks.root, 'local.txt'))
    const before = readWorkspaceFile(request('local.txt'))
    writeWorkspaceFile({ ...request('local.txt'), content: 'local edit', revision: before.revision })
    expect(readFileSync(join(outside, 'external.txt'), 'utf8')).toBe('external')
    expect(readFileSync(join(mocks.root, 'local.txt'), 'utf8')).toBe('local edit')
  })

  it('rejects oversized edits and lossy UTF-8 content before changing the file', () => {
    writeFileSync(join(mocks.root, 'file.txt'), 'original')
    const file = readWorkspaceFile(request('file.txt'))
    expect(() => writeWorkspaceFile({ ...request('file.txt'), content: 'a'.repeat(WORKSPACE_TEXT_LIMIT + 1), revision: file.revision }))
      .toThrow('workspace_file:too-large')
    expect(() => writeWorkspaceFile({ ...request('file.txt'), content: '\ud800', revision: file.revision }))
      .toThrow('workspace_file:invalid-encoding')
    expect(readFileSync(join(mocks.root, 'file.txt'), 'utf8')).toBe('original')
  })
})

/**
 * `encoding: 'base64'` 支线 —— 图片编辑器把编辑后的字节写回原文件。
 * 不变式与文本支线逐条对齐:原子替换、mode 保留、乐观锁、超限预拒、
 * 以及「内容没变就不写」。
 */
describe('workspace base64 image saves', () => {
  const pngBytes = () => Buffer.from('89504e470d0a1a0a0000000d49484452', 'hex')
  const saveImage = (path: string, bytes: Buffer, revision: string) =>
    writeWorkspaceFile({ ...request(path), content: bytes.toString('base64'), revision, encoding: 'base64' })

  it('atomically overwrites an existing image and returns the image shape', () => {
    const target = join(mocks.root, 'photo.png')
    writeFileSync(target, pngBytes())
    chmodSync(target, 0o644)
    const before = readWorkspaceFile(request('photo.png'))
    const edited = Buffer.from('89504e470d0a1a0affd8ffe0', 'hex')
    const saved = saveImage('photo.png', edited, before.revision)
    expect(saved).toMatchObject({ kind: 'image', path: 'photo.png', mime: 'image/png', size: edited.length })
    if (saved.kind !== 'image') throw new Error('Expected image')
    expect(saved.dataUrl).toBe(`data:image/png;base64,${edited.toString('base64')}`)
    expect(readFileSync(target)).toEqual(edited)
    expect(statSync(target).mode & 0o777).toBe(0o644)
    expect(readdirSync(mocks.root)).toEqual(['photo.png'])
  })

  it('skips the write entirely when the decoded bytes match the file', () => {
    writeFileSync(join(mocks.root, 'same.png'), pngBytes())
    const before = readWorkspaceFile(request('same.png'))
    const mtime = statSync(join(mocks.root, 'same.png')).mtimeMs
    const saved = saveImage('same.png', pngBytes(), before.revision)
    expect(saved).toMatchObject({ kind: 'image', revision: before.revision })
    expect(statSync(join(mocks.root, 'same.png')).mtimeMs).toBe(mtime)
  })

  it('keeps external changes when the revision is stale', () => {
    writeFileSync(join(mocks.root, 'stale.png'), pngBytes())
    const before = readWorkspaceFile(request('stale.png'))
    const external = Buffer.from('89504e470d0a1a0a1111', 'hex')
    writeFileSync(join(mocks.root, 'stale.png'), external)
    expect(() => saveImage('stale.png', pngBytes(), before.revision)).toThrow('workspace_file:conflict')
    expect(readFileSync(join(mocks.root, 'stale.png'))).toEqual(external)
  })

  it('refuses base64 writes onto non-image files', () => {
    writeFileSync(join(mocks.root, 'notes.txt'), 'plain text')
    const before = readWorkspaceFile(request('notes.txt'))
    expect(() => saveImage('notes.txt', pngBytes(), before.revision)).toThrow('workspace_file:unsupported')
    expect(readFileSync(join(mocks.root, 'notes.txt'), 'utf8')).toBe('plain text')
  })

  it('rejects malformed or truncated base64 without touching the file', () => {
    writeFileSync(join(mocks.root, 'corrupt.png'), pngBytes())
    const before = readWorkspaceFile(request('corrupt.png'))
    // 非法字符:Buffer.from 会静默丢掉空格,这条必须在这里被拦下
    expect(() => writeWorkspaceFile({ ...request('corrupt.png'), content: `${pngBytes().toString('base64')} !!!`, revision: before.revision, encoding: 'base64' }))
      .toThrow('workspace_file:invalid-encoding')
    // 往返不一致:截断后的 base64 解码再编码不再是原文
    expect(() => writeWorkspaceFile({ ...request('corrupt.png'), content: pngBytes().toString('base64').slice(0, 6), revision: before.revision, encoding: 'base64' }))
      .toThrow('workspace_file:invalid-encoding')
    expect(readFileSync(join(mocks.root, 'corrupt.png'))).toEqual(pngBytes())
  })

  it('rejects oversized payloads before decoding', () => {
    writeFileSync(join(mocks.root, 'huge.png'), pngBytes())
    const before = readWorkspaceFile(request('huge.png'))
    expect(() => writeWorkspaceFile({ ...request('huge.png'), content: 'A'.repeat(WORKSPACE_IMAGE_LIMIT * 2 + 1), revision: before.revision, encoding: 'base64' }))
      .toThrow('workspace_file:too-large')
    expect(readFileSync(join(mocks.root, 'huge.png'))).toEqual(pngBytes())
  })
})

describe('workspace file management and path boundaries', () => {
  it('creates, renames, copies and moves files and refreshable directory contents', async () => {
    await mutateWorkspaceFile({ ...request('folder'), operation: 'create-directory' })
    await mutateWorkspaceFile({ ...request('folder/first.md'), operation: 'create-file' })
    await mutateWorkspaceFile({ ...request('folder/first.md'), operation: 'rename', destination: 'folder/renamed.md' })
    writeFileSync(join(mocks.root, 'folder/renamed.md'), '# content')
    expect(await mutateWorkspaceFile({ ...request('folder'), operation: 'copy', destination: 'copied' }))
      .toEqual({ path: 'folder', destination: 'copied' })
    await mutateWorkspaceFile({ ...request('copied/renamed.md'), operation: 'move', destination: 'moved.md' })
    expect(readFileSync(join(mocks.root, 'moved.md'), 'utf8')).toBe('# content')
    expect(readFileSync(join(mocks.root, 'folder/renamed.md'), 'utf8')).toBe('# content')
    expect(readdirSync(join(mocks.root, 'copied'))).toEqual([])
    expect(readdirSync(mocks.root).some((name) => name.startsWith('.ncw-'))).toBe(false)
  })

  it('trashes files recoverably and reports trash failure without deleting', async () => {
    writeFileSync(join(mocks.root, 'file.txt'), 'recoverable')
    await mutateWorkspaceFile({ ...request('file.txt'), operation: 'delete' })
    expect(mocks.trash).toHaveBeenCalledWith(join(mocks.root, 'file.txt'))
    expect(readFileSync(join(outside, 'trashed'), 'utf8')).toBe('recoverable')
    writeFileSync(join(mocks.root, 'failure.txt'), 'keep')
    mocks.trash.mockRejectedValueOnce(new Error('trash unavailable'))
    await expect(mutateWorkspaceFile({ ...request('failure.txt'), operation: 'delete' })).rejects.toThrow('workspace_file:io')
    expect(readFileSync(join(mocks.root, 'failure.txt'), 'utf8')).toBe('keep')
  })

  it.each(['create-file', 'create-directory', 'rename', 'move', 'copy'] as const)(
    '%s refuses to replace an existing destination', async (operation) => {
      writeFileSync(join(mocks.root, 'source.txt'), 'source')
      writeFileSync(join(mocks.root, 'existing.txt'), 'keep')
      const path = operation.startsWith('create-') ? 'existing.txt' : 'source.txt'
      await expect(mutateWorkspaceFile({ ...request(path), operation, destination: 'existing.txt' }))
        .rejects.toThrow('workspace_file:exists')
      expect(readFileSync(join(mocks.root, 'existing.txt'), 'utf8')).toBe('keep')
    }
  )

  it.each(['', '.', '..', '../outside/file.txt', 'folder/../../outside/file.txt', 'C:\\external.txt', 'a\0b'])(
    'refuses root or malformed relative file paths: %j', async (path) => {
      await expect(mutateWorkspaceFile({ ...request(path), operation: 'create-file' })).rejects.toThrow('workspace_file:invalid-path')
      expect(() => readWorkspaceFile(request(path))).toThrow('workspace_file:invalid-path')
    }
  )

  /**
   * ★ 工作区外的绝对路径是**合法输入**:工具卡片和 Markdown 链接会给出这种路径,
   * 点开要能落到这儿。相对路径仍然只能在工作区内 —— 它的基点就是工作区根。
   */
  it('accepts absolute paths outside the workspace', async () => {
    writeFileSync(join(outside, 'note.txt'), 'from outside')
    expect(readWorkspaceFile(request(join(outside, 'note.txt')))).toMatchObject({
      kind: 'text', content: 'from outside'
    })
    await expect(mutateWorkspaceFile({ ...request(join(outside, 'made.txt')), operation: 'create-file' }))
      .resolves.toBeDefined()
    expect(readFileSync(join(outside, 'made.txt'), 'utf8')).toBe('')
  })

  /** 绝对路径不逐段审计祖先(`/tmp` 自己就是软链),但目标本身是软链仍然拒 —— 编辑不写穿链 */
  it('still refuses an absolute path whose own last segment is a symbolic link', () => {
    writeFileSync(join(outside, 'target.txt'), 'secret')
    symlinkSync(join(outside, 'target.txt'), join(outside, 'link.txt'))
    expect(() => readWorkspaceFile(request(join(outside, 'link.txt')))).toThrow('workspace_file:symlink')
  })

  it('refuses mutations through internal and external symbolic links', async () => {
    mkdirSync(join(mocks.root, 'real'))
    writeFileSync(join(mocks.root, 'real/file.txt'), 'keep')
    symlinkSync(join(mocks.root, 'real'), join(mocks.root, 'internal'))
    symlinkSync(outside, join(mocks.root, 'external'))
    await expect(mutateWorkspaceFile({ ...request('external/file.txt'), operation: 'create-file' })).rejects.toThrow('workspace_file:symlink')
    await expect(mutateWorkspaceFile({ ...request('internal/file.txt'), operation: 'delete' })).rejects.toThrow('workspace_file:symlink')
    await expect(mutateWorkspaceFile({ ...request('internal'), operation: 'delete' })).rejects.toThrow('workspace_file:symlink')
    expect(mocks.trash).not.toHaveBeenCalled()
  })

  /**
   * #182:工作区是一个装满 `ln -s` 的目录,每条链指向一个项目(可能在别的盘)。
   * 经链接目录进来的文件要能打开、保存、复制路径 —— 复制出的是工作区里的逻辑路径。
   */
  describe('files reached through a linked directory inside the workspace (#182)', () => {
    beforeEach(() => {
      mkdirSync(join(outside, 'project/src'), { recursive: true })
      writeFileSync(join(outside, 'project/src/main.ts'), 'linked')
      symlinkSync(join(outside, 'project'), join(mocks.root, 'project'), 'dir')
    })

    it('reads and saves a file under the linked directory', () => {
      const file = readWorkspaceFile(request('project/src/main.ts'))
      expect(file).toMatchObject({ kind: 'text', content: 'linked' })
      if (file.kind !== 'text') throw new Error('expected text')
      writeWorkspaceFile({ ...request('project/src/main.ts'), content: 'edited', revision: file.revision })
      expect(readFileSync(join(outside, 'project/src/main.ts'), 'utf8')).toBe('edited')
      // 链本身没被替换成普通文件
      expect(readdirSync(mocks.root)).toEqual(['project'])
    })

    it('copies the logical in-workspace path, for the link itself and for files under it', async () => {
      await expect(copyWorkspacePath({ ...request('project/src/main.ts'), kind: 'relative' })).resolves.toBe('project/src/main.ts')
      await expect(copyWorkspacePath({ ...request('project/src/main.ts'), kind: 'absolute' }))
        .resolves.toBe(join(mocks.root, 'project', 'src', 'main.ts'))
      await expect(copyWorkspacePath({ ...request('project'), kind: 'relative' })).resolves.toBe('project')
      expect(mocks.clipboard).toHaveBeenLastCalledWith('project')
    })

    it('still rejects a file that is itself a link, and any `..` escape', async () => {
      symlinkSync(join(outside, 'project/src/main.ts'), join(mocks.root, 'project-main.ts'))
      expect(() => readWorkspaceFile(request('project-main.ts'))).toThrow('workspace_file:symlink')
      for (const path of ['project/../../outside/project/src/main.ts', 'project/src/../../../outside/project/src/main.ts', '../outside/project/src/main.ts']) {
        expect(() => readWorkspaceFile(request(path))).toThrow('workspace_file:invalid-path')
        await expect(copyWorkspacePath({ ...request(path), kind: 'absolute' })).rejects.toThrow('workspace_file:invalid-path')
      }
      expect(mocks.clipboard).not.toHaveBeenCalled()
    })
  })

  it('refuses broken links, including an existing broken-link destination', async () => {
    symlinkSync(join(outside, 'missing'), join(mocks.root, 'broken'))
    writeFileSync(join(mocks.root, 'file.txt'), 'keep')
    await expect(mutateWorkspaceFile({ ...request('broken'), operation: 'create-file' })).rejects.toThrow('workspace_file:symlink')
    await expect(mutateWorkspaceFile({ ...request('file.txt'), operation: 'copy', destination: 'broken' })).rejects.toThrow('workspace_file:symlink')
  })

  it('refuses directory copy/move into itself and copying nested links', async () => {
    mkdirSync(join(mocks.root, 'folder'))
    await expect(mutateWorkspaceFile({ ...request('folder'), operation: 'copy', destination: 'folder/child' })).rejects.toThrow('workspace_file:invalid-path')
    await expect(mutateWorkspaceFile({ ...request('folder'), operation: 'move', destination: 'folder/child' })).rejects.toThrow('workspace_file:invalid-path')
    symlinkSync(outside, join(mocks.root, 'folder/external'))
    await expect(mutateWorkspaceFile({ ...request('folder'), operation: 'copy', destination: 'copy' })).rejects.toThrow()
    expect(existsSync(join(mocks.root, 'copy'))).toBe(false)
    // 失败的 copy 不得把暂存目录留在用户工作区里
    expect(readdirSync(mocks.root).filter((name) => name.startsWith('.ncw-copy-'))).toEqual([])
  })

  it('returns stable localized error identifiers and reveals only a checked path', () => {
    writeFileSync(join(mocks.root, 'file.txt'), 'content')
    revealWorkspaceFile(request('file.txt'))
    expect(mocks.reveal).toHaveBeenCalledWith(join(mocks.root, 'file.txt'))
    expect(() => revealWorkspaceFile(request('../outside'))).toThrow('workspace_file:invalid-path')
    try {
      readWorkspaceFile({ workspaceId: 'missing', path: 'file.txt' })
      throw new Error('Expected an IPC error')
    } catch (error) {
      expect(toAgentError(error)).toMatchObject({ code: 'tool_failed', message: 'workspace_file:workspace-unavailable', retryable: false })
    }
  })
})
