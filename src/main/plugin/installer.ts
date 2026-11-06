/**
 * 插件安装 —— 目录 / ZIP / 市场包三条来源,**同一组防线**。
 *
 * 防线照搬 `kernel/skill/install.ts`(体积、条目数、展开体积、深度上限、
 * 拒绝符号链接、staging + backup + 失败回滚),因为那几条挡的是 ZIP 本身的
 * 攻击面,和包里装的是 Skill 还是插件无关。
 *
 * **不同的只有「一个合法的包长什么样」那部分**:
 *
 * | Skill 包 | 插件包 |
 * |---|---|
 * | 顶层目录 == frontmatter 的 name | 顶层目录 == `<publisher>.<name>` |
 * | 必须有 SKILL.md | 必须有 package.json,且过 §2.2 全部字段校验 |
 * | — | `main` 指向的文件必须存在 |
 * | — | `l10n/` 必须同时有 zh-CN.json 与 en-US.json |
 * | — | `contributes.*.title` 必须全是 `%key%` |
 * | — | `contributes.*.path` 必须指向包内存在的文件 |
 *
 * 最后两条在 `parsePluginManifest` 里已经查过形状,这里查的是**文件真的在不在** ——
 * 形状对但文件不在的包装上之后,症状是「插件装上了,点菜单没反应」。
 *
 * ## 原生包(办公插件自带 LibreOffice)
 *
 * 声明了 `nativeComponents` 的包按 `NATIVE_PACKAGE_LIMITS` 装(实测一份 LibreOffice 是
 * 800 MB、1.7 万个文件、20 层深),其余插件的限额**一个数都不变**。因此清单要先于其它条目
 * 读出来 —— 在那之前只按原生包的外层上限约束。原生包的其余核对(索引、可执行位、
 * 包内符号链接)在 `native-installer.ts`,在 staging 里、切换版本之前做。
 * 解压流式写盘:最大的单个库 144 MB,整条目读进内存再写会把主进程内存顶上去。
 */
import { createReadStream, createWriteStream, promises as fs } from 'node:fs'
import { createHash } from 'node:crypto'
import { basename, dirname, join, resolve, sep } from 'node:path'
import { Transform } from 'node:stream'
import { pipeline } from 'node:stream/promises'
import unzipper from 'unzipper'
import {
  PLUGIN_ID_RE,
  parsePluginManifest,
  type ManifestError,
  type PluginManifest
} from '../../shared/plugin/manifest'
import { NATIVE_DIR, NATIVE_PACKAGE_LIMITS } from '../../shared/plugin/native-component'
import { SUPPORTED_LOCALE_FILES } from './locale-files'
import { assertNativeFilesPresent, assertNativeTargetsAvailable, verifyInstalledNativeComponents } from './native-installer'

interface PackageLimits {
  maxZipBytes: number
  maxExpandedBytes: number
  maxEntries: number
  maxDepth: number
}

/** 普通插件的限额。★ 原生包的大限额只给声明了 nativeComponents 的包,见文件头 */
const REGULAR_LIMITS: PackageLimits = {
  maxZipBytes: 20 * 1024 * 1024,
  maxExpandedBytes: 50 * 1024 * 1024,
  maxEntries: 2000,
  maxDepth: 12
}

function limitsFor(manifest: PluginManifest): PackageLimits {
  return (manifest.nativeComponents ?? []).length > 0 ? NATIVE_PACKAGE_LIMITS : REGULAR_LIMITS
}

/** 原生包的早检:本机没有构建就别解压了(几百 MB 的包白等一分钟) */
function assertInstallable(manifest: PluginManifest): void {
  try {
    assertNativeTargetsAvailable(manifest)
  } catch (error) {
    throw new PluginInstallError((error as Error).message)
  }
}

export interface InstalledPluginPackage {
  manifest: PluginManifest
  target: string
  sha256?: string
}

export class PluginInstallError extends Error {
  constructor(message: string, readonly details: ManifestError[] = []) {
    super(message)
    this.name = 'PluginInstallError'
  }
}

function safeEntry(name: string, maxDepth: number): boolean {
  return (
    name !== '' &&
    !name.includes('\\') &&
    !name.startsWith('/') &&
    !/^[A-Za-z]:/.test(name) &&
    name.split('/').every((part) => part !== '' && part !== '.' && part !== '..') &&
    name.split('/').length <= maxDepth
  )
}

async function readEntry(entry: unzipper.File, limit: number): Promise<Buffer> {
  const stream = entry.stream()
  const chunks: Buffer[] = []
  let total = 0
  try {
    for await (const part of stream) {
      const chunk = Buffer.isBuffer(part) ? part : Buffer.from(part as Uint8Array)
      total += chunk.length
      if (total > limit) throw new PluginInstallError('package expands beyond the size limit')
      chunks.push(chunk)
    }
    if (total !== entry.uncompressedSize) throw new PluginInstallError('package entry size mismatch')
    return Buffer.concat(chunks, total)
  } finally {
    stream.destroy()
  }
}

/** 流式解压一个条目到 `destination`(必须是新文件)。返回写入的字节数 */
async function writeEntry(entry: unzipper.File, destination: string, limit: number): Promise<number> {
  let total = 0
  const counter = new Transform({
    transform(chunk: Buffer, _encoding, done) {
      total += chunk.length
      if (total > limit) done(new PluginInstallError('package expands beyond the size limit'))
      else done(null, chunk)
    }
  })
  await pipeline(entry.stream(), counter, createWriteStream(destination, { flags: 'wx' }))
  if (total !== entry.uncompressedSize) throw new PluginInstallError('package entry size mismatch')
  return total
}

/**
 * 装一个 ZIP。`expectedSha256` 来自市场的授权接口 —— **权威摘要由服务端给**,
 * 不采信包里任何自述的哈希(同 `ipc/skills.ts` 那四步)。
 */
export async function installPluginZip(
  zipPath: string,
  root: string,
  expectedSha256?: string,
  beforeReplace?: (manifest: PluginManifest) => Promise<void>
): Promise<InstalledPluginPackage> {
  const stat = await fs.stat(zipPath)
  // 读清单之前只能按原生包的外层上限约束;是不是原生包要看清单,见文件头
  if (!stat.isFile() || stat.size > NATIVE_PACKAGE_LIMITS.maxZipBytes) throw new PluginInstallError('package file is too large')

  const sha256 = await hashFile(zipPath)
  if (expectedSha256 !== undefined && expectedSha256.toLowerCase() !== sha256) {
    throw new PluginInstallError('package digest does not match the one the marketplace vouched for')
  }

  const directory = await unzipper.Open.file(zipPath)
  if (directory.files.length === 0 || directory.files.length > NATIVE_PACKAGE_LIMITS.maxEntries) {
    throw new PluginInstallError('package has an invalid number of entries')
  }

  /*
    先读清单,再按它定限额。只按精确路径取 `<顶层目录>/package.json` 这一个条目,
    其余条目一律等限额定下来之后再核(下面的循环)。
  */
  const firstTop = directory.files[0]?.path.split('/')[0] ?? ''
  const manifestCandidate = directory.files.find((entry) => entry.path === `${firstTop}/package.json`)
  if (manifestCandidate === undefined) throw new PluginInstallError('package.json is missing')
  const raw = (await readEntry(manifestCandidate, 256 * 1024)).toString('utf8')
  const manifest = parseManifestText(raw)
  const limits = limitsFor(manifest)
  if (stat.size > limits.maxZipBytes) throw new PluginInstallError('package file is too large')
  if (directory.files.length > limits.maxEntries) throw new PluginInstallError('package has an invalid number of entries')
  assertInstallable(manifest)

  let expanded = 0
  let top: string | null = null
  let manifestEntry: unzipper.File | null = null
  /**
   * 查重用的**小写**集合。大小写不敏感的文件系统上 `A.js` 与 `a.js` 是同一个
   * 文件,解压时后者会覆盖前者 —— 所以查重必须按小写算。
   */
  const lowered = new Set<string>()
  /**
   * 存在性检查用的**原样**集合。
   *
   * ★ 这两个集合必须分开。合用一个的代价很具体:`l10n` 的两个 bundle 叫
   * `zh-CN.json` / `en-US.json`,**带大写**;拿小写集合去查它们永远查不到,
   * 于是每一个带 l10n 的插件都会以「l10n bundle is required」被拒装 ——
   * 而包里明明有。目录安装走的是 `collectFiles`(原样),所以
   * `plugin-cli dev` 一路正常,只有装 ZIP 时才炸。
   */
  const names = new Set<string>()
  for (const entry of directory.files) {
    const name = entry.path.replace(/\/$/, '')
    if (!safeEntry(name, limits.maxDepth)) throw new PluginInstallError('package contains an unsafe path')
    const parts = name.split('/')
    if (top === null) top = parts[0] ?? ''
    if (parts[0] !== top) throw new PluginInstallError('package must contain exactly one top-level directory')
    const normalized = name.toLocaleLowerCase()
    if (lowered.has(normalized)) throw new PluginInstallError('package contains duplicate paths')
    lowered.add(normalized)
    names.add(name)
    if (!Number.isFinite(entry.uncompressedSize) || entry.uncompressedSize < 0) {
      throw new PluginInstallError('package entry has an invalid size')
    }
    /*
      Unix mode 0120000 = 符号链接。永不从不可信归档里还原链接 —— 原生包也一样:它需要的链接
      写在摘要受清单约束的索引里,由 `native-installer.ts` 在所有文件落盘后创建。
    */
    const unixMode = (entry.externalFileAttributes >>> 16) & 0xffff
    if ((unixMode & 0xf000) === 0xa000) throw new PluginInstallError('package must not contain symlinks')
    expanded += entry.uncompressedSize
    if (expanded > limits.maxExpandedBytes) throw new PluginInstallError('package expands beyond the size limit')
    if (name === `${top}/package.json`) manifestEntry = entry
  }

  if (top === null || !PLUGIN_ID_RE.test(top)) {
    throw new PluginInstallError('the top-level directory must be named <publisher>.<name>')
  }
  // 与上面按首个条目取到的是同一个条目(顶层目录唯一已核过);不同就是归档自相矛盾
  if (manifestEntry !== manifestCandidate) throw new PluginInstallError('package.json is missing')
  if (manifest.id !== top) {
    throw new PluginInstallError(`the top-level directory "${top}" does not match the manifest id "${manifest.id}"`)
  }

  // 包内文件存在性:先按条目名核对一遍,不落盘也能答。
  const relative = new Set([...names].map((name) => name.slice(top.length + 1)).filter((name) => name !== ''))
  assertPackageFiles(manifest, (path) => relative.has(normalizeRel(path)), (dir) => {
    return [...relative].some((name) => name.startsWith(`${normalizeRel(dir)}/`))
  })

  const target = resolveTarget(root, manifest.id)
  await materialize(target, async (staging) => {
    let actual = 0
    for (const entry of directory.files) {
      const parts = entry.path.replace(/\/$/, '').split('/')
      const child = parts.slice(1).join('/')
      if (child === '') continue
      const destination = join(staging, child)
      if (entry.path.endsWith('/')) {
        await fs.mkdir(destination, { recursive: true })
        continue
      }
      await fs.mkdir(dirname(destination), { recursive: true })
      actual += await writeEntry(entry, destination, limits.maxExpandedBytes - actual)
    }
  }, manifest, beforeReplace)

  return { manifest, target, sha256 }
}

/**
 * 装一个**目录** —— `plugin-cli dev` 与「从本地目录安装」走这条。
 *
 * ★ 同样复制一份进插件目录,**不做软链**。软链意味着插件的代码在用户的
 * 开发目录里,而那个目录随时会变成别的东西(切分支、rm -rf);更要紧的是
 * `ncw-plugin://` 的 realpath 归一会把软链指向的真实路径判成「不在插件根内」。
 */
export async function installPluginDirectory(
  sourceDir: string,
  root: string,
  beforeReplace?: (manifest: PluginManifest) => Promise<void>
): Promise<InstalledPluginPackage> {
  const stat = await fs.lstat(sourceDir)
  if (!stat.isDirectory() || stat.isSymbolicLink()) throw new PluginInstallError('source must be a real directory')

  const raw = await fs.readFile(join(sourceDir, 'package.json'), 'utf8').catch(() => {
    throw new PluginInstallError('package.json is missing')
  })
  const manifest = parseManifestText(raw)
  const limits = limitsFor(manifest)
  assertInstallable(manifest)

  /*
    原生包的开发目录里,运行时(复制来的 LibreOffice.app)带着真实的符号链接。它们由文件索引
    重建(native-installer.ts),所以这里跳过 `native/` 下的链接;不带索引的包仍然一律拒链接。
  */
  const recreatedFromIndex = (manifest.nativeComponents ?? []).some((component) => component.targets.some((t) => t.payload !== undefined))
  const files = await collectFiles(sourceDir, limits, (rel) => recreatedFromIndex && rel.startsWith(`${NATIVE_DIR}/`))
  assertPackageFiles(
    manifest,
    (path) => files.has(normalizeRel(path)),
    (dir) => [...files].some((name) => name.startsWith(`${normalizeRel(dir)}/`))
  )

  const target = resolveTarget(root, manifest.id)
  await materialize(target, async (staging) => {
    let written = 0
    for (const rel of files) {
      const destination = join(staging, rel)
      await fs.mkdir(dirname(destination), { recursive: true })
      // copyFile 而不是整个读进内存:原生包里单个库就有上百 MB
      written += (await fs.lstat(join(sourceDir, rel))).size
      if (written > limits.maxExpandedBytes) throw new PluginInstallError('package expands beyond the size limit')
      await fs.copyFile(join(sourceDir, rel), destination, fs.constants.COPYFILE_EXCL)
    }
  }, manifest, beforeReplace)

  return { manifest, target }
}

/** 读一个已经在盘上的插件目录。装载期用,不写盘。 */
export async function readInstalledManifest(directory: string): Promise<PluginManifest> {
  const raw = await fs.readFile(join(directory, 'package.json'), 'utf8')
  return parseManifestText(raw)
}

function parseManifestText(raw: string): PluginManifest {
  let parsed: unknown
  try { parsed = JSON.parse(raw) } catch { throw new PluginInstallError('package.json is not valid JSON') }
  const result = parsePluginManifest(parsed)
  if (!result.ok) {
    throw new PluginInstallError(
      `package.json is invalid: ${result.errors.map((e) => `${e.field}: ${e.message}`).join('; ')}`,
      result.errors
    )
  }
  return result.manifest
}

/**
 * 「清单指到的文件真的在包里吗」。
 *
 * ★ 和 `parsePluginManifest` 的分工:那边查**形状**(是不是包内相对路径),
 * 这边查**存在**。只查形状的话,一个 `main` 指着不存在文件的包会装得干干净净,
 * 然后在第一次激活时以「装载失败」的名义炸掉 —— 而那时用户已经点过「安装」了。
 */
function assertPackageFiles(
  manifest: PluginManifest,
  hasFile: (path: string) => boolean,
  hasDir: (path: string) => boolean
): void {
  /*
    ★ `kind: 'webapp'` 没有 `main`(清单解析那边已经保证了「写了就报错」),
    所以这一条只对有代码的插件查。对 webapp 查的话,`hasFile('')` 一定是 false,
    一个完全合法的零代码包会以「入口缺失」的名义装不上。
  */
  if (manifest.kind !== 'webapp' && !hasFile(manifest.main)) {
    throw new PluginInstallError(`main entry "${manifest.main}" is missing from the package`)
  }
  if (manifest.icon !== undefined && !hasFile(manifest.icon)) {
    throw new PluginInstallError(`icon "${manifest.icon}" is missing from the package`)
  }
  // 命令的品牌图标(iconFile)与包级 icon 同一条规矩:引用了就得真的在包里。
  // 缺了的话菜单会在运行期静默回落到拼图块,而作者不会收到任何提示。
  for (const command of manifest.contributes.commands) {
    if (command.iconFile !== undefined && !hasFile(command.iconFile)) {
      throw new PluginInstallError(`command "${command.command}" iconFile "${command.iconFile}" is missing from the package`)
    }
  }
  if (manifest.l10n !== undefined) {
    if (!hasDir(manifest.l10n)) throw new PluginInstallError(`l10n directory "${manifest.l10n}" is missing`)
    for (const file of SUPPORTED_LOCALE_FILES) {
      if (!hasFile(`${manifest.l10n}/${file}`)) {
        /*
          ★ **两种语言缺一个就拒装。** 这是把 `i18n/index.test.ts` 那条
          「en 必须覆盖 zh 的每个 key」的保障延伸到插件:只带一种语言的插件
          装上之后,另一种语言下它的每个按钮都会显示成 key 本身。
        */
        throw new PluginInstallError(`l10n bundle "${file}" is required; ship every supported locale`)
      }
    }
  }
  for (const view of manifest.contributes.views) {
    if (!hasFile(view.path)) throw new PluginInstallError(`view "${view.id}" points at a missing file: ${view.path}`)
  }
  for (const card of manifest.contributes.cardViews) {
    if (!hasFile(card.path)) throw new PluginInstallError(`cardView "${card.viewType}" points at a missing file: ${card.path}`)
  }
  for (const theme of manifest.contributes.themes) {
    if (!hasFile(theme.path)) throw new PluginInstallError(`theme file is missing: ${theme.path}`)
  }
  /*
    ★ skill 要连 `SKILL.md` 一起查,不能只查目录在不在。

    扫描器对「目录在、里面却没有 SKILL.md」的处理是**跳过并记一条诊断**
    (见 `kernel/skill/load.ts`),而那条诊断落在 Skill 页面上 —— 于是用户在
    插件页看到「提供 2 条 Skill」,在 Skill 列表里只找到 1 条,两个页面谁都不
    提另一个。在安装这一步拒掉,作者拿到的是一句话说清的失败。

    这里仍然**不**校验 frontmatter(缺 description 之类):那要解析 YAML,而
    那套规则的唯一权威在扫描器里。两处各写一份迟早会对不上,于是出现
    「装得上但扫不出来」或者反过来。目录与文件的存在性是两边都同意的事实,
    判定留给判定者。
  */
  for (const skill of manifest.contributes.skills) {
    if (!hasDir(skill.path)) throw new PluginInstallError(`skill directory is missing: ${skill.path}`)
    if (!hasFile(`${skill.path}/SKILL.md`)) {
      throw new PluginInstallError(`skill "${skill.path}" has no SKILL.md — the skill scanner would skip it silently`)
    }
  }
  // agents / modes 与 skills 同形:一个包内目录,由宿主既有的加载器去读。
  for (const agent of manifest.contributes.agents) {
    if (!hasDir(agent.path)) throw new PluginInstallError(`agent directory is missing: ${agent.path}`)
  }
  for (const mode of manifest.contributes.modes) {
    if (!hasDir(mode.path)) throw new PluginInstallError(`mode directory is missing: ${mode.path}`)
  }
  // 原生组件(办公插件自带的引擎):入口与许可证文件的存在性,见 native-installer.ts
  try {
    assertNativeFilesPresent(manifest, hasFile)
  } catch (error) {
    throw new PluginInstallError((error as Error).message)
  }
}

function normalizeRel(path: string): string {
  return path.replace(/^\.\//, '').replace(/\/+$/, '')
}

function resolveTarget(root: string, id: string): string {
  const target = resolve(root, id)
  const rootResolved = resolve(root)
  if (!(target === rootResolved || target.startsWith(rootResolved + sep))) {
    throw new PluginInstallError('resolved install path is outside the plugin root')
  }
  return target
}

/**
 * staging → backup → rename。**失败一定回滚。**
 *
 * 顺序是:全部写进 staging;把旧目录挪成 backup;staging 改名成 target;
 * 删 backup。任何一步炸了都把 backup 挪回来 —— 升级失败之后用户手里应该是
 * **旧版本**,而不是一个装了一半的目录。
 */
async function materialize(target: string, fill: (staging: string) => Promise<void>, manifest?: PluginManifest, beforeReplace?: (manifest: PluginManifest) => Promise<void>): Promise<void> {
  const stamp = `${String(Date.now())}-${Math.random().toString(36).slice(2)}`
  const staging = `${target}.installing-${stamp}`
  const backup = `${target}.backup-${stamp}`
  let backedUp = false
  await fs.mkdir(dirname(staging), { recursive: true })
  try {
    await fs.mkdir(staging, { recursive: true })
    await fill(staging)
    /*
      ★ 原生组件在 **staging 里、切换版本之前**核对:摘要不符或本机没有对应构建时
      整次安装回滚,用户手里仍是旧版本 —— 而不是换上一个起不来的引擎。
    */
    if (manifest !== undefined) {
      await verifyInstalledNativeComponents(staging, manifest).catch((error: unknown) => {
        throw new PluginInstallError((error as Error).message)
      })
    }
    // 需求：清单已校验且尚未替换旧目录，此时只检查目标插件的脏文档并收掉它的干净 helper。
    // 回调拒绝会走 staging 回滚；无关插件的未保存文档不能阻断这次安装。
    if (manifest !== undefined) await beforeReplace?.(manifest)
    try {
      await fs.rename(target, backup)
      backedUp = true
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error
    }
    await fs.rename(staging, target)
    if (backedUp) await fs.rm(backup, { recursive: true, force: true }).catch(() => undefined)
  } catch (error) {
    await fs.rm(staging, { recursive: true, force: true }).catch(() => undefined)
    if (backedUp) await fs.rename(backup, target).catch(() => undefined)
    throw error
  }
}

async function collectFiles(
  root: string,
  limits: PackageLimits,
  skipSymlink: (rel: string) => boolean,
  prefix = '',
  depth = 0,
  out = new Set<string>()
): Promise<Set<string>> {
  if (depth > limits.maxDepth) throw new PluginInstallError('package directory is nested too deeply')
  const entries = await fs.readdir(join(root, prefix), { withFileTypes: true })
  for (const entry of entries) {
    const rel = prefix === '' ? entry.name : `${prefix}/${entry.name}`
    // 开发目录里躺着 node_modules 是常态 —— 装进来只会是几万个无用文件。
    if (entry.name === 'node_modules' || entry.name === '.git' || entry.name.startsWith('.DS_Store')) continue
    if (entry.isSymbolicLink()) {
      if (skipSymlink(rel)) continue
      throw new PluginInstallError(`package must not contain symlinks: ${rel}`)
    }
    if (entry.isDirectory()) {
      await collectFiles(root, limits, skipSymlink, rel, depth + 1, out)
      continue
    }
    if (!entry.isFile()) continue
    out.add(rel)
    if (out.size > limits.maxEntries) throw new PluginInstallError('package has too many files')
  }
  return out
}

async function hashFile(path: string): Promise<string> {
  const hash = createHash('sha256')
  await new Promise<void>((done, fail) => {
    const stream = createReadStream(path)
    stream.on('data', (chunk: string | Buffer) => hash.update(chunk))
    stream.on('error', fail)
    stream.on('end', () => done())
  })
  return hash.digest('hex')
}

export { basename }
