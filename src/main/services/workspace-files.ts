import { randomUUID } from 'node:crypto'
import {
  closeSync,
  constants,
  copyFileSync,
  fstatSync,
  fsyncSync,
  lstatSync,
  mkdirSync,
  openSync,
  readdirSync,
  readSync,
  realpathSync,
  renameSync,
  rmSync,
  writeFileSync,
  type Stats
} from 'node:fs'
import { dirname, extname, isAbsolute, relative, resolve, sep } from 'node:path'
import {
  WORKSPACE_FILE_ERROR_PREFIX,
  WORKSPACE_IMAGE_LIMIT,
  WORKSPACE_TEXT_LIMIT,
  type WorkspaceFile,
  type WorkspaceFileErrorCode,
  type WorkspaceFileMutationRequest,
  type WorkspaceFileMutationResult,
  type WorkspaceFileRequest,
  type WorkspaceFileWriteRequest,
  type WorkspaceRecoveryListing
} from '../../shared/domain/workspace-file'
import { PathEscapeError, resolveAnywhere } from '../kernel/tool/path-guard'
import { store } from '../state/store'
import { IpcError } from '../ipc/errors'
import { isLocalEnvironment } from '../../shared/domain/environment'
import { getWorkspaceEnvironment } from '../runtime'
import { EnvironmentError } from '../environment/errors'
import { EnvironmentFileError, EnvironmentFiles, remoteFileFailure } from '../environment/files'
import { classifyWorkspaceFile, contentRevision as revision, decodeWorkspaceText as decodeText, isBinaryText as binaryText, workspaceReadLimit } from '../kernel/workspace-file-content'


/**
 * 「删除」的落点。桌面装系统废纸篓(`shell.trashItem`);无头服务默认把目标移进数据目录的
 * `trash/` —— 仍然可恢复,绝不降级为永久删除。
 */
export type TrashPort = (absolutePath: string) => Promise<void>
let trashItem: TrashPort | null = null
export function installWorkspaceTrash(port: TrashPort | null): void { trashItem = port }
async function trash(path: string): Promise<void> {
  if (trashItem === null) fail('unsupported')
  await trashItem(path)
}

const MAX_COPY_ENTRIES = 10_000
const MAX_COPY_BYTES = 256 * 1024 * 1024

export function fail(code: WorkspaceFileErrorCode): never {
  throw new IpcError('tool_failed', `${WORKSPACE_FILE_ERROR_PREFIX}${code}`)
}

export function translateError(error: unknown): never {
  if (error instanceof EnvironmentError) throw error
  if (error instanceof EnvironmentFileError) fail(error.fileCode)
  if (error instanceof IpcError) throw error
  if (error instanceof PathEscapeError) fail('invalid-path')
  const code = (error as NodeJS.ErrnoException | undefined)?.code
  if (code === 'ENOENT' || code === 'ENOTDIR') fail('not-found')
  if (code === 'EEXIST' || code === 'ENOTEMPTY') fail('exists')
  if (code === 'EACCES' || code === 'EPERM' || code === 'EROFS') fail('permission')
  if (code === 'ELOOP') fail('symlink')
  if (code === 'EISDIR') fail('not-file')
  fail('io')
}

export function workspaceRoot(workspaceId: string): string {
  if (typeof workspaceId !== 'string') fail('workspace-unavailable')
  const workspace = store.getWorkspace(workspaceId)
  if (!workspace) fail('workspace-unavailable')
  if (!isLocalEnvironment(workspace.environment)) throw new EnvironmentError('disconnected')
  try {
    const root = realpathSync.native(workspace.rootPath)
    if (!lstatSync(root).isDirectory()) fail('workspace-unavailable')
    return root
  } catch {
    fail('workspace-unavailable')
  }
}

export function statIfPresent(path: string): Stats | undefined {
  try {
    return lstatSync(path)
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return undefined
    throw error
  }
}

/**
 * 解析一条来自渲染层的文档路径。**读/写/新建/复制/移动/删除共用这一处。**
 *
 * 两种形态:
 *
 * 1. **工作区相对路径**(常态,文件树给出的就是这种)。逐段 lstat,任何一段是软链都拒 ——
 *    否则一次「重命名 link」可能悄悄改的是它的目标,也可能在读取和保存之间被换成外部链接。
 * 2. **绝对路径**。工具卡片和 Markdown 链接现在会给出工作区外的绝对路径,点开要能落到这儿。
 *    ★ 这种形态**只检查最后一段**不是软链,不逐段审计祖先:macOS 上 `/tmp` 本身就是
 *    指向 `/private/tmp` 的软链,逐段审计会把 `/tmp/x` 这类完全正常的路径判成 `symlink`。
 *    「编辑不会写穿一条链」这个性质对目标文件本身仍然成立。
 *
 * `links`(只影响相对路径):#182 —— 用户在工作区里 `ln -s`(Windows 上是软链/junction,
 * lstat 同样报 `isSymbolicLink`)进多个项目目录联调,文件树能列出来,打开/复制路径却报 `symlink`。
 * - `'reject'`(默认):逐段拒软链。新建/重命名/移动/复制/删除走这个,不改链背后的东西。
 * - `'follow-dirs'`:祖先目录可以是链,最后一段自身是链仍拒(编辑不写穿链)。读、保存走这个 ——
 *   和绝对路径那一支同一条规矩。
 * - `'follow'`:任何一段都可以是链。只给复制路径、打开方式、在文件夹中显示这类不碰内容的入口。
 * 三种模式都先在**词法上**拒掉 `..`/`.`/空段,所以入口一定是工作区里的那条链,`..` 逃不出去。
 */
export type LinkPolicy = 'reject' | 'follow-dirs' | 'follow'
export function checkedPath(root: string, path: string, allowRoot = false, links: LinkPolicy = 'reject'): string {
  if (typeof path !== 'string' || path.includes('\0')) fail('invalid-path')
  if (path === '' && allowRoot) return root

  if (isAbsolute(path)) {
    // ★ lstat 的是**词法形式**,不是 resolveAnywhere 返回的 realpath ——
    //   后者已经把软链解开了,拿它去问"是不是软链"永远得到否。
    const lexical = resolve(path)
    if (statIfPresent(lexical)?.isSymbolicLink() === true) fail('symlink')
    const target = resolveAnywhere(root, path).abs
    if (target === root) fail('invalid-path')
    return target
  }

  if (path.includes('\\') || /^[a-z]:/i.test(path)) fail('invalid-path')
  const segments = path.split('/')
  if (segments.some((part) => part === '' || part === '.' || part === '..')) fail('invalid-path')
  const target = resolveAnywhere(root, path).abs
  if (target === root) fail('invalid-path')
  for (let index = 1; index <= segments.length; index++) {
    const component = resolve(root, ...segments.slice(0, index))
    const stat = statIfPresent(component)
    if (stat?.isSymbolicLink() && (links === 'reject' || (links === 'follow-dirs' && index === segments.length))) fail('symlink')
    if (!stat) break
  }
  return target
}

/** O_NOFOLLOW 配合有上限的 read，拒绝设备/FIFO，也不会因读取中增长而无限分配。 */
function readBounded(path: string, limit: number): { bytes?: Buffer; stat: Stats } {
  const before = lstatSync(path)
  if (!before.isFile()) fail('not-file')
  const fd = openSync(path, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0))
  try {
    const stat = fstatSync(fd)
    if (!stat.isFile()) fail('not-file')
    if (stat.size > limit) return { stat }
    const buffer = Buffer.alloc(Math.min(stat.size + 1, limit + 1))
    let used = 0
    while (used < buffer.length) {
      const length = readSync(fd, buffer, used, buffer.length - used, used)
      if (length === 0) break
      used += length
    }
    const after = fstatSync(fd)
    if (used > limit || after.size > limit) return { stat: after }
    // 文件读取期间被外部写入时，让用户重试，不返回混合版本的内容。
    if (after.size !== stat.size || after.mtimeMs !== stat.mtimeMs || used !== stat.size) fail('conflict')
    return { bytes: buffer.subarray(0, used), stat: after }
  } finally {
    closeSync(fd)
  }
}

function readFileAt(root: string, path: string): WorkspaceFile {
  const target = checkedPath(root, path, false, 'follow-dirs')
  const extension = extname(path).toLowerCase()
  const { bytes, stat } = readBounded(target, workspaceReadLimit(extension))
  return classifyWorkspaceFile(path, extension, bytes, stat)
}

export function remoteEditor(workspaceId: string): EnvironmentFiles | undefined {
  const workspace = store.getWorkspace(workspaceId)
  if (!workspace || isLocalEnvironment(workspace.environment)) return undefined
  return new EnvironmentFiles(getWorkspaceEnvironment(workspaceId))
}

export async function readWorkspaceDocument(req: WorkspaceFileRequest): Promise<WorkspaceFile> {
  try { return await (remoteEditor(req.workspaceId)?.read(req.path) ?? readWorkspaceFile(req)) } catch (error) {
    try { remoteFileFailure(error) } catch (failure) { translateError(failure) }
  }
}
export async function writeWorkspaceDocument(req: WorkspaceFileWriteRequest): Promise<WorkspaceFile> {
  try { return await (remoteEditor(req.workspaceId)?.write(req) ?? writeWorkspaceFile(req)) } catch (error) {
    try { remoteFileFailure(error) } catch (failure) { translateError(failure) }
  }
}
export async function mutateWorkspaceDocument(req: WorkspaceFileMutationRequest): Promise<WorkspaceFileMutationResult> {
  try { return await (remoteEditor(req.workspaceId)?.mutate(req) ?? mutateWorkspaceFile(req)) } catch (error) {
    try { remoteFileFailure(error) } catch (failure) { translateError(failure) }
  }
}
export async function listWorkspaceRecovery(req: { workspaceId: string }): Promise<WorkspaceRecoveryListing> {
  const editor = remoteEditor(req.workspaceId)
  // 本机工作区走系统回收站,没有我们自己的索引可列
  if (!editor) return { entries: [], environmentKey: '' }
  try { return await editor.listRecovery() } catch (error) {
    try { remoteFileFailure(error) } catch (failure) { translateError(failure) }
  }
}

export function readWorkspaceFile(req: WorkspaceFileRequest): WorkspaceFile {
  try {
    return readFileAt(workspaceRoot(req.workspaceId), req.path)
  } catch (error) {
    translateError(error)
  }
}

export function writeWorkspaceFile(req: WorkspaceFileWriteRequest): WorkspaceFile {
  try {
    const root = workspaceRoot(req.workspaceId)
    const target = checkedPath(root, req.path, false, 'follow-dirs')
    if (typeof req.content !== 'string' || typeof req.revision !== 'string') fail('invalid-encoding')
    /*
      需求:图片编辑类自定义编辑器要把编辑后的字节写回原文件。文本支线的
      校验(UTF-8 往返 + 拒二进制文本)对图片字节必然失败,这里按 `encoding`
      分流。二进制支线只对「当前已分类为 image 的文件」开放 —— 路径校验、
      软链拒绝、乐观锁、临时文件原子替换与文本支线走的是同一条路,不开新门。
    */
    if (req.encoding === 'base64') return writeBase64File(root, req, target)
    if (req.content.length > WORKSPACE_TEXT_LIMIT) fail('too-large')
    const bytes = Buffer.from(req.content, 'utf8')
    if (bytes.length > WORKSPACE_TEXT_LIMIT) fail('too-large')
    if (decodeText(bytes) !== req.content || binaryText(req.content)) fail('invalid-encoding')
    const current = readFileAt(root, req.path)
    if (current.kind !== 'text') fail('unsupported')
    if (current.revision !== req.revision) fail('conflict')
    if (current.content === req.content) return current

    // 同目录临时文件 + 原子替换，保存中断不会留下被截断的用户文件。
    const temporary = resolve(dirname(target), `.ncw-save-${randomUUID()}.tmp`)
    let created = false
    try {
      const fd = openSync(temporary, 'wx', lstatSync(target).mode & 0o777)
      created = true
      try {
        writeFileSync(fd, bytes)
        fsyncSync(fd)
      } finally {
        closeSync(fd)
      }
      checkedPath(root, req.path, false, 'follow-dirs')
      if (readFileAt(root, req.path).revision !== req.revision) fail('conflict')
      renameSync(temporary, target)
      created = false
    } finally {
      if (created) rmSync(temporary, { force: true })
    }
    return { kind: 'text', path: req.path, size: bytes.length, revision: revision(bytes), content: req.content }
  } catch (error) {
    translateError(error)
  }
}

/**
 * `encoding: 'base64'` 的写入支线 —— 覆写一张**已存在的图片**。
 *
 * 与文本支线的唯一差异是「字节从哪来、写到哪类文件」;其余不变式
 * (乐观锁、软链拒绝、同目录临时文件 + 原子替换、mode 保留)逐条对齐,
 * 理由都在文本支线里,不在此复述。
 */
function writeBase64File(root: string, req: WorkspaceFileWriteRequest, target: string): WorkspaceFile {
  // ★ 先按长度粗拒再解码:base64 字符串长度约为字节的 4/3,这一挡把
  //   「解码前就注定超限」的请求挡在 Buffer 分配之前。
  if (req.content.length > WORKSPACE_IMAGE_LIMIT * 2) fail('too-large')
  /*
    ★ 严格校验 base64,不依赖 `Buffer.from` 的宽容:它对非法字符是**静默丢弃**,
    不报错 —— 一次被截断的 payload 会被「成功」写进半张图。这里的两条
    (字符表 + 往返一致)合起来只放行规范的补齐 base64,canvas.toDataURL
    产出的正是这种。
  */
  if (!/^[A-Za-z0-9+/]+={0,2}$/.test(req.content)) fail('invalid-encoding')
  const bytes = Buffer.from(req.content, 'base64')
  if (bytes.toString('base64') !== req.content) fail('invalid-encoding')
  if (bytes.length > WORKSPACE_IMAGE_LIMIT) fail('too-large')
  const current = readFileAt(root, req.path)
  if (current.kind === 'binary' && current.reason === 'too-large') fail('too-large')
  if (current.kind !== 'image') fail('unsupported')
  if (current.revision !== req.revision) fail('conflict')
  // 内容没变就不写:与文本支线同一条规矩,免得「只是看了看」也把 mtime 顶起来。
  if (current.revision === revision(bytes)) return current

  const temporary = resolve(dirname(target), `.ncw-save-${randomUUID()}.tmp`)
  let created = false
  try {
    const fd = openSync(temporary, 'wx', lstatSync(target).mode & 0o777)
    created = true
    try {
      writeFileSync(fd, bytes)
      fsyncSync(fd)
    } finally {
      closeSync(fd)
    }
    checkedPath(root, req.path, false, 'follow-dirs')
    if (readFileAt(root, req.path).revision !== req.revision) fail('conflict')
    renameSync(temporary, target)
    created = false
  } finally {
    if (created) rmSync(temporary, { force: true })
  }
  const dataUrl = `data:${current.mime};base64,${bytes.toString('base64')}`
  return { kind: 'image', path: req.path, size: bytes.length, revision: revision(bytes), mime: current.mime, dataUrl }
}

function requireAbsent(path: string): void {
  if (statIfPresent(path)) fail('exists')
}

function requireParent(path: string): void {
  if (!lstatSync(dirname(path)).isDirectory()) fail('invalid-path')
}

function inside(parent: string, path: string): boolean {
  const rel = relative(parent, path)
  return rel === '' || (!isAbsolute(rel) && rel !== '..' && !rel.startsWith(`..${sep}`))
}

/** 预先验证整个复制树；复制遇到软链/设备时不会给它们创建新的访问入口。 */
function inspectCopy(root: string, path: string): void {
  let count = 0
  let bytes = 0
  function inspect(relativePath: string): void {
    const absolute = checkedPath(root, relativePath)
    const stat = lstatSync(absolute)
    count++
    if (count > MAX_COPY_ENTRIES) fail('too-large')
    if (stat.isFile()) {
      bytes += stat.size
      if (bytes > MAX_COPY_BYTES) fail('too-large')
    } else if (stat.isDirectory()) {
      for (const name of readdirSync(absolute)) inspect(`${relativePath}/${name}`)
    } else {
      fail('unsupported')
    }
  }
  inspect(path)
}

function copyTree(root: string, source: string, destination: string, onCreated?: () => void): void {
  const sourcePath = checkedPath(root, source)
  const destinationPath = checkedPath(root, destination)
  requireAbsent(destinationPath)
  const stat = lstatSync(sourcePath)
  /**
   * ★ 先登记再写。原先 `onCreated` 排在写之后:顶层是文件且 `copyFileSync` 写到一半失败时,
   * 调用方的 `created` 仍是 false,那句 `rmSync(temporary)` 根本不执行 —— 半个
   * `.ncw-copy-*.tmp` 就永久留在用户工作区里。登记一个还没建成的路径是无害的,
   * 清理侧本来就是 `force: true`。
   */
  if (stat.isFile()) {
    onCreated?.()
    copyFileSync(sourcePath, destinationPath, constants.COPYFILE_EXCL)
  } else if (stat.isDirectory()) {
    onCreated?.()
    mkdirSync(destinationPath, { mode: stat.mode & 0o777 })
    for (const name of readdirSync(sourcePath)) copyTree(root, `${source}/${name}`, `${destination}/${name}`)
  } else {
    fail('unsupported')
  }
}

export async function mutateWorkspaceFile(req: WorkspaceFileMutationRequest): Promise<WorkspaceFileMutationResult> {
  try {
    const root = workspaceRoot(req.workspaceId)
    const target = checkedPath(root, req.path)
    switch (req.operation) {
      case 'create-file':
        requireParent(target)
        writeFileSync(target, '', { flag: 'wx' })
        break
      case 'create-directory':
        requireParent(target)
        mkdirSync(target)
        break
      case 'delete':
        lstatSync(target)
        // 使用系统废纸篓；失败时不降级为永久删除。
        await trash(target)
        break
      case 'rename':
      case 'move':
      case 'copy': {
        if (typeof req.destination !== 'string') fail('invalid-path')
        const destination = checkedPath(root, req.destination)
        const stat = lstatSync(target)
        if (!stat.isFile() && !stat.isDirectory()) fail('unsupported')
        if (stat.isDirectory() && inside(target, destination)) fail('invalid-path')
        /*
          ★ `overwrite` **只对 rename 生效**,而且覆盖的实现是先把目标丢进系统
          废纸篓再改名 —— 同这个文件 delete 分支的立场:失败不降级为永久删除。
          直接 `renameSync` 盖上去的话,被盖掉的那份连废纸篓里都找不到。

          ★★ 目标是**目录**时一律拒绝。确认框上写的是「覆盖这个文件」,
          而把一整棵子树丢进废纸篓是另一回事,不能用同一句话换到同意。
        */
        if (req.overwrite === true && req.operation === 'rename') {
          const existing = statIfPresent(destination)
          if (existing) {
            if (!existing.isFile()) fail('exists')
            await trash(destination)
          }
        }
        requireAbsent(destination)
        requireParent(destination)
        if (req.operation === 'copy') {
          inspectCopy(root, req.path)
          // 暂存后发布：失败只清理本次创建的暂存目录，绝不删除目标路径。
          const parent = req.destination.includes('/') ? req.destination.slice(0, req.destination.lastIndexOf('/') + 1) : ''
          const temporaryRelative = `${parent}.ncw-copy-${randomUUID()}.tmp`
          const temporary = checkedPath(root, temporaryRelative)
          let created = false
          try {
            copyTree(root, req.path, temporaryRelative, () => { created = true })
            requireAbsent(checkedPath(root, req.destination))
            renameSync(temporary, destination)
            created = false
          } finally {
            if (created) rmSync(temporary, { recursive: true, force: true })
          }
        } else {
          renameSync(target, destination)
        }
        return { path: req.path, destination: req.destination }
      }
      default:
        fail('unsupported')
    }
    return { path: req.path }
  } catch (error) {
    translateError(error)
  }
}
