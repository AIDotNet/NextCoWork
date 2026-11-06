/**
 * 文档引擎的**文件侧**:私有工作副本、字节摘要、带冲突检查的原子保存。
 *
 * ## 为了什么需求建的
 *
 * 引擎(原生 helper)绝不能直接写用户工作区里的原文件:
 *
 * - 它是第三方原生代码,写坏一半的文件没有人能恢复;
 * - 用户或 Agent 可能在编辑期间从别处改了同一个文件,直接覆盖就是静默丢数据。
 *
 * 所以引擎只读写**私有工作副本**,保存时由这里校验「盘上的原文件还是我上次读到的
 * 那一份吗」,再用同目录临时文件 + rename 原子替换。和 `ipc/workspace-files.ts` 的
 * 文本 / 图片写入同一套立场,但那条路只收 UTF-8 文本或已分类为 image 的文件,
 * 办公二进制走不了(见计划 §2),所以这里单独一份,不去给那条路开口子。
 *
 * ## 不变式
 *
 * - 首次打开只**复制**,不改原文件的字节与 mtime。
 * - 产物字节和盘上一致时保存是 no-op(不顶 mtime,Agent 不会误读成「刚被改过」)。
 * - 摘要对不上 → `disk_conflict`,**不覆盖**。
 * - 原文件是软链时拒绝:写穿一条链改到的是别处的文件。
 * - 导出(`commitExport`)是保存的**只增不改**版本:已有目标默认拒绝,目标不存在时
 *   先写同目录临时文件再 `link` 过去 —— 并发创建者赢,不出现半写损坏。
 */
import { createHash, randomUUID } from 'node:crypto'
import { createReadStream } from 'node:fs'
import type { Stats } from 'node:fs'
import { chmod, copyFile, link, lstat, mkdir, open, rename, rm, stat } from 'node:fs/promises'
import { basename, dirname, join } from 'node:path'
import { DocumentEngineError } from '../../shared/document-engine/protocol'

/**
 * 单个文档的字节上限。★ 与插件 fs 的 8 MiB 不同 —— 办公文档带图片几十 MB 很常见;
 * 但仍然是有限值:引擎要把整份文档装进内存,无上限等于允许一个文件拖垮 helper。
 */
export const MAX_DOCUMENT_BYTES = 512 * 1024 * 1024

export async function digestFile(path: string): Promise<string> {
  const hash = createHash('sha256')
  await new Promise<void>((resolve, reject) => {
    const stream = createReadStream(path)
    stream.on('data', (chunk) => { hash.update(chunk) })
    stream.on('error', reject)
    stream.on('end', () => { resolve() })
  })
  return hash.digest('hex')
}

async function assertRegularFile(path: string): Promise<number> {
  let info
  try {
    info = await lstat(path)
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') throw new DocumentEngineError('io', 'document does not exist')
    throw new DocumentEngineError('io', `cannot stat document: ${(error as Error).message}`)
  }
  if (info.isSymbolicLink()) throw new DocumentEngineError('io', 'document is a symbolic link')
  if (!info.isFile()) throw new DocumentEngineError('io', 'document is not a regular file')
  if (info.size > MAX_DOCUMENT_BYTES) throw new DocumentEngineError('io', `document exceeds ${MAX_DOCUMENT_BYTES} bytes`)
  return info.size
}

export interface WorkingCopy {
  /** 私有目录里的副本路径,只交给引擎 */
  workingPath: string
  /** 复制那一刻原文件的摘要 */
  diskRevision: string
}

/**
 * 把原文件复制进 `privateDir`,返回副本与摘要。
 *
 * ★ 先摘要、复制、再摘要一次:复制期间原文件被外部写入时,两次摘要不同,
 * 这里判 `disk_conflict` 让调用方重试,而不是把一份「一半旧一半新」的副本交给引擎。
 */
export async function createWorkingCopy(source: string, privateDir: string): Promise<WorkingCopy> {
  await assertRegularFile(source)
  await mkdir(privateDir, { recursive: true, mode: 0o700 })
  const before = await digestFile(source)
  const workingPath = join(privateDir, `${randomUUID()}-${basename(source)}`)
  await copyFile(source, workingPath)
  const after = await digestFile(source)
  if (before !== after) {
    await rm(workingPath, { force: true })
    throw new DocumentEngineError('disk_conflict', 'document changed while it was being opened')
  }
  return { workingPath, diskRevision: before }
}

/**
 * 把引擎产物原子替换到原文件。返回新的磁盘摘要。
 *
 * @param expectedDiskRevision 上次读到 / 写入的摘要。盘上不一致时拒绝。
 */
export async function commitSave(target: string, producedPath: string, expectedDiskRevision: string): Promise<string> {
  await assertRegularFile(target)
  const producedSize = (await stat(producedPath)).size
  if (producedSize > MAX_DOCUMENT_BYTES) throw new DocumentEngineError('io', `saved document exceeds ${MAX_DOCUMENT_BYTES} bytes`)
  if (producedSize === 0) {
    // ★ 引擎写出 0 字节几乎一定是导出失败而不是用户清空了文档 —— 替换过去就是把文件抹掉
    throw new DocumentEngineError('io', 'engine produced an empty file')
  }
  if ((await digestFile(target)) !== expectedDiskRevision) {
    throw new DocumentEngineError('disk_conflict', 'document changed on disk since it was opened or last saved')
  }
  const produced = await digestFile(producedPath)
  if (produced === expectedDiskRevision) return produced

  const mode = (await stat(target)).mode & 0o777
  const temporary = join(dirname(target), `.ncw-doc-${randomUUID()}.tmp`)
  let created = false
  try {
    await copyFile(producedPath, temporary)
    created = true
    const handle = await open(temporary, 'r+')
    try {
      await handle.chmod(mode)
      await handle.sync()
    } finally {
      await handle.close()
    }
    /*
      ★ rename 之前再比一次:从上面那次检查到这里,用户可能又在别的编辑器里存了一次。
      这仍然挡不住「这一行和 rename 之间」的那几微秒 —— 不遵守锁的外部写入者没有
      办法被完全挡住(计划 §5 明确不承诺),但把窗口缩到最小。
    */
    if ((await digestFile(target)) !== expectedDiskRevision) {
      throw new DocumentEngineError('disk_conflict', 'document changed on disk during save')
    }
    await rename(temporary, target)
    created = false
  } finally {
    if (created) await rm(temporary, { force: true })
  }
  return produced
}

export interface ExportPublishOptions {
  /** 调用方是否允许替换已存在的目标。默认 false:导出绝不悄悄盖掉别人放在那里的文件 */
  overwrite?: boolean
  /**
   * 导出**开始那一刻**目标的字节摘要;目标当时不存在时为 `null`。
   *
   * 需求:overwrite 时它作为 `commitSave` 的期望值,挡住「导出期间目标被外部改写」
   * 然后被我们覆盖的情况。不用「发布这一刻现取」的摘要 —— 那等于自己给自己背书,
   * 外部改动会被当成基线吞掉。
   */
  expectedDiskRevision: string | null
}

/**
 * 把引擎在**私有目录**产出的导出物发布到工作区目标路径。
 *
 * 需求:导出和保存不同 —— 目标是用户可见的产物,可能已经存在(默认不许覆盖),也
 * 可能在导出期间被另一个进程创建或改写。所以两条路分开:
 *
 * - 目标已存在:必须先拿到调用方的 overwrite 同意,再走 `commitSave` 的外部冲突检查;
 *   目标是软链 / 非普通文件一律拒绝(写穿一条链改到的是别处)。
 * - 目标不存在:**先写目标同目录的随机临时文件,再 `link` 过去**。`link` 在目标已
 *   存在时以 `EEXIST` 失败,于是「导出期间有人抢先建了这个文件」这一方赢,我们绝不
 *   覆盖它;而且目标要么不存在、要么是完整内容,不出现半写损坏。
 *
 * ★ 不满足会怎样:直接以 `open(target, 'w')` 写,会在并发创建时静默覆盖别人的文件,
 * 并在 helper 写一半失败时留下一个内容截断、谁也认不出的目标。
 */
export async function commitExport(
  target: string,
  producedPath: string,
  options: ExportPublishOptions
): Promise<void> {
  const producedSize = await assertRegularFile(producedPath)
  if (producedSize === 0) {
    // ★ 引擎 0 字节产物几乎一定是导出失败 —— 发布过去就是在目标位置留一个空文件
    throw new DocumentEngineError('io', 'engine produced an empty export')
  }

  let existing: Stats | null = null
  try {
    existing = await lstat(target)
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw new DocumentEngineError('io', `cannot inspect export target: ${(error as Error).message}`)
  }

  if (existing !== null) {
    if (existing.isSymbolicLink()) throw new DocumentEngineError('io', 'export target is a symbolic link')
    if (!existing.isFile()) throw new DocumentEngineError('io', 'export target is not a regular file')
    if (options.overwrite !== true) throw new DocumentEngineError('invalid_operation', 'export target already exists; pass overwrite to replace it')
    if (options.expectedDiskRevision === null) {
      // 开始时目标不存在,现在却在 → 并发创建,不覆盖
      throw new DocumentEngineError('disk_conflict', 'export target was created while exporting')
    }
    await commitSave(target, producedPath, options.expectedDiskRevision)
    return
  }

  // 需求：外部删除也是一次冲突，不能把已删除的文件在导出结束时悄悄复活。
  if (options.expectedDiskRevision !== null) throw new DocumentEngineError('disk_conflict', 'export target was removed while exporting')
  const temporary = join(dirname(target), `.ncw-export-${randomUUID()}.tmp`)
  try {
    await copyFile(producedPath, temporary)
    // 新文件给 0o600:导出物是文档内容,不该因为 umask 宽松而全局可读
    await chmod(temporary, 0o600)
    try {
      await link(temporary, target)
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'EEXIST') throw new DocumentEngineError('disk_conflict', 'export target was created while exporting')
      throw new DocumentEngineError('io', `cannot publish export: ${(error as Error).message}`)
    }
  } finally {
    await rm(temporary, { force: true })
  }
}
