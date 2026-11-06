/**
 * 插件包里**原生组件**的安装期与运行期校验。
 *
 * ## 为了什么需求建的
 *
 * 办公插件在安装时就带着 LibreOffice 引擎(`nativeComponents`,见
 * `shared/plugin/native-component.ts`)。清单里写了入口和 sha256,但「清单写的」和
 * 「盘上实际会被 spawn 的文件」之间必须有一道核对,否则:
 *
 * - 解压 / 复制出来的文件被截断或替换,引擎起来是一堆随机崩溃;
 * - 安装之后有人改了插件目录里的可执行文件,下一次打开文档就在跑另一份代码。
 *
 * 所以这里做两件事:**安装时**(在 staging 里、切换版本之前)核对当前平台 target 的
 * 摘要并赋可执行位;**运行时**(每次 spawn 前)再核对一次落点与摘要。
 *
 * 随包的运行时(完整的 LibreOffice,一万多个文件)由 target 的**文件索引**覆盖
 * (`payload`,见 `shared/plugin/native-component.ts`):安装时逐个核对大小与摘要、
 * 不许多也不许少,按索引补可执行位、重建包内符号链接。
 * ★ 运行时不重核整个运行时:每次打开文档都哈希 800 MB 不可接受。安装之后运行时文件被改,
 *   与安装之后 NextCoWork 自己的文件被改是同一个信任级别 —— 入口(helper)仍然每次都核。
 *
 * ## 不变式
 *
 * - 当前机器没有匹配的 target → **拒装**。装上一个在本机永远起不来的引擎插件,
 *   用户只会在第一次打开文档时看到失败。
 * - 入口必须 realpath 之后仍在 `<插件根>/native/` 下、是普通文件。
 * - 普通插件的包体限制**不放宽**:只有声明了 `nativeComponents` 的包按
 *   `NATIVE_PACKAGE_LIMITS` 装(`installer.ts`)。
 * - **ZIP 里仍然不许有符号链接。** 运行时需要的链接写在(摘要受清单约束的)索引里,
 *   由这里在所有文件落盘之后创建,且每条都必须解析到它所在的 target 目录之内 ——
 *   解压时就跟着归档建链接,写后面的条目时就可能经链接写到包外。
 *
 * ## 故意不做的
 *
 * - 不验证平台代码签名(codesign / Authenticode):那要平台工具链,属于原生包规格
 *   那一步。摘要核对不等于签名校验,不要把它说成后者。
 */
import { createHash } from 'node:crypto'
import { createReadStream, promises as fs } from 'node:fs'
import { dirname, isAbsolute, join, posix, relative, sep } from 'node:path'
import { isSafeRelativePath, type PluginManifest } from '../../shared/plugin/manifest'
import {
  NATIVE_DIR,
  NATIVE_PACKAGE_LIMITS,
  selectNativeTarget,
  type NativeComponent,
  type NativeComponentTarget
} from '../../shared/plugin/native-component'

export class NativeComponentError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'NativeComponentError'
  }
}

/**
 * 清单里每个 target 的入口与许可证文件都在包里吗(按条目名判,不落盘)。
 *
 * ★ 查**所有平台**的 target,不只当前平台:一个 Windows 入口缺失的包在 macOS 上
 * 装得上,发布出去就是 Windows 用户装不上 —— 而作者在自己机器上永远复现不出来。
 */
export function assertNativeFilesPresent(manifest: PluginManifest, hasFile: (path: string) => boolean): void {
  for (const component of manifest.nativeComponents ?? []) {
    for (const target of component.targets) {
      if (!hasFile(target.entry)) {
        throw new NativeComponentError(`native component "${component.id}" is missing its ${target.platform}-${target.arch} entry: ${target.entry}`)
      }
    }
    if (!hasFile(component.license.notices)) {
      throw new NativeComponentError(`native component "${component.id}" is missing its license notices: ${component.license.notices}`)
    }
  }
}

/**
 * 本机有没有这个包的构建。**解压之前**调:一个几百 MB 的包,解压完才发现不是本机平台的,
 * 用户白等了一分钟。解压之后 `verifyInstalledNativeComponents` 仍会再判一次。
 */
export function assertNativeTargetsAvailable(
  manifest: PluginManifest,
  platform: string = process.platform,
  arch: string = process.arch
): void {
  for (const component of manifest.nativeComponents ?? []) {
    if (selectNativeTarget(component, platform, arch) === null) {
      throw new NativeComponentError(`native component "${component.id}" has no build for ${platform}-${arch}`)
    }
  }
}

/**
 * 在已落盘的插件目录(安装时是 staging)里核对当前平台的每个原生组件,并赋可执行位。
 *
 * ★ 必须赋可执行位:安装器是按字节写文件的(ZIP 里的 unix 权限位不被还原,
 * 那是有意的 —— 不从不可信归档里还原权限)。不赋的话 POSIX 上 spawn 直接 EACCES,
 * 而那个报错不会让人想到「是安装器丢了 +x」。随包运行时的可执行位来自文件索引
 * (只有 0755 / 0644 两档,从不还原 setuid 之类),见 `verifyPayload`。
 */
export async function verifyInstalledNativeComponents(
  pluginRoot: string,
  manifest: PluginManifest,
  platform: string = process.platform,
  arch: string = process.arch
): Promise<void> {
  for (const component of manifest.nativeComponents ?? []) {
    const target = selectNativeTarget(component, platform, arch)
    if (target === null) {
      throw new NativeComponentError(`native component "${component.id}" has no build for ${platform}-${arch}`)
    }
    if (target.payload !== undefined) await verifyPayload(pluginRoot, component, target.payload, platform)
    const entry = await resolveVerifiedEntry(pluginRoot, component, target)
    if (platform !== 'win32') await fs.chmod(entry, 0o755)
  }
}

// ─────────────────────────── 运行时文件索引 ───────────────────────────

/** 索引文件本身的大小上限。1.7 万个文件的实测索引约 2.5 MB */
const MAX_INDEX_BYTES = 64 * 1024 * 1024
/** 并发哈希数。串行哈希 800 MB 在 SSD 上也要十几秒;太多又会和解压争 IO */
const HASH_CONCURRENCY = 8

interface PayloadIndex {
  files: { path: string; size: number; sha256: string; executable: boolean }[]
  symlinks: { path: string; target: string }[]
}

/**
 * 解析索引。★ 路径全是包内相对路径、都在 `scope/` 之下、互不重复(按小写判,大小写不敏感的
 * 文件系统上两个只差大小写的条目是同一个文件);符号链接不能是任何条目的上级目录 ——
 * 否则写那个条目时会经链接写出去。
 */
function parsePayloadIndex(raw: string, scope: string, indexPath: string): PayloadIndex {
  let parsed: unknown
  try { parsed = JSON.parse(raw) } catch { throw new NativeComponentError('payload index is not valid JSON') }
  const record = (parsed ?? {}) as Record<string, unknown>
  if (record.format !== 1 || !Array.isArray(record.files) || !Array.isArray(record.symlinks)) {
    throw new NativeComponentError('payload index has an unsupported format')
  }
  if (record.files.length + record.symlinks.length > NATIVE_PACKAGE_LIMITS.maxEntries) {
    throw new NativeComponentError('payload index lists too many entries')
  }
  const seen = new Set<string>()
  const claim = (path: unknown): string => {
    if (typeof path !== 'string' || !isSafeRelativePath(path) || !path.startsWith(`${scope}/`) || path.split('/').length > NATIVE_PACKAGE_LIMITS.maxDepth) {
      throw new NativeComponentError(`payload index lists an invalid path: ${String(path)}`)
    }
    const key = path.toLowerCase()
    if (seen.has(key) || path === indexPath) throw new NativeComponentError(`payload index lists a path twice: ${path}`)
    seen.add(key)
    return path
  }
  const files = record.files.map((item) => {
    const f = (item ?? {}) as Record<string, unknown>
    const path = claim(f.path)
    if (typeof f.size !== 'number' || !Number.isSafeInteger(f.size) || f.size < 0) throw new NativeComponentError(`payload index has an invalid size for ${path}`)
    if (typeof f.sha256 !== 'string' || !/^[a-f0-9]{64}$/.test(f.sha256)) throw new NativeComponentError(`payload index has an invalid digest for ${path}`)
    return { path, size: f.size, sha256: f.sha256, executable: f.executable === true }
  })
  const symlinks = record.symlinks.map((item) => {
    const l = (item ?? {}) as Record<string, unknown>
    const path = claim(l.path)
    const target = l.target
    // 只收相对目标,且按词法解析仍在 scope 之内;真正的落点在创建后再按 realpath 核一次
    if (typeof target !== 'string' || target === '' || target.startsWith('/') || target.includes('\\') || /^[A-Za-z]:/.test(target) || target.includes('\0')) {
      throw new NativeComponentError(`payload symlink ${path} has an invalid target`)
    }
    const resolved = posix.normalize(posix.join(posix.dirname(path), target))
    if (!resolved.startsWith(`${scope}/`)) throw new NativeComponentError(`payload symlink ${path} points outside ${scope}/`)
    return { path, target }
  })
  const all = [...files.map((f) => f.path), ...symlinks.map((l) => l.path)]
  for (const link of symlinks) {
    const prefix = `${link.path.toLowerCase()}/`
    if (all.some((path) => path.toLowerCase().startsWith(prefix))) {
      throw new NativeComponentError(`payload symlink ${link.path} is a parent of other payload entries`)
    }
  }
  return { files, symlinks }
}

/** 盘上 `scope/` 之下的全部条目(不跟随链接)。符号链接此时还不该存在 */
async function listOnDisk(root: string, scope: string): Promise<Map<string, number>> {
  const out = new Map<string, number>()
  const walk = async (rel: string): Promise<void> => {
    for (const entry of await fs.readdir(join(root, ...rel.split('/')), { withFileTypes: true })) {
      const child = `${rel}/${entry.name}`
      if (entry.isDirectory()) { await walk(child); continue }
      if (!entry.isFile()) {
        // ★ 链接由索引重建;盘上已经有链接(或 FIFO / 设备)说明它不是从我们认得的来源来的
        throw new NativeComponentError(`unexpected non-regular file in the native payload: ${child}`)
      }
      out.set(child, (await fs.lstat(join(root, ...child.split('/')))).size)
      if (out.size > NATIVE_PACKAGE_LIMITS.maxEntries) throw new NativeComponentError('native payload has too many files')
    }
  }
  await walk(scope)
  return out
}

/**
 * 按索引核对随包运行时,并补可执行位、重建符号链接。只在安装的 staging 里调。
 *
 * ★ 多一个文件也拒:索引之外的文件没人核对过,它可能是被塞进来、会被 LibreOffice 当插件
 *   加载的动态库。少一个文件也拒:症状是「打开某类文档才崩」,而且离安装很远。
 */
export async function verifyPayload(
  pluginRoot: string,
  component: NativeComponent,
  payload: NonNullable<NativeComponentTarget['payload']>,
  platform: string = process.platform
): Promise<void> {
  const scope = payload.index.slice(0, payload.index.lastIndexOf('/'))
  const indexFile = join(pluginRoot, ...payload.index.split('/'))
  const info = await fs.lstat(indexFile).catch(() => null)
  if (info === null || !info.isFile() || info.size > MAX_INDEX_BYTES) {
    throw new NativeComponentError(`native component "${component.id}" is missing its payload index: ${payload.index}`)
  }
  const raw = await fs.readFile(indexFile)
  if (createHash('sha256').update(raw).digest('hex') !== payload.sha256) {
    throw new NativeComponentError(`native component "${component.id}" payload index digest does not match the manifest`)
  }
  const index = parsePayloadIndex(raw.toString('utf8'), scope, payload.index)

  const onDisk = await listOnDisk(pluginRoot, scope)
  onDisk.delete(payload.index)
  const expected = new Map(index.files.map((file) => [file.path, file]))
  for (const path of onDisk.keys()) {
    if (!expected.has(path)) throw new NativeComponentError(`native payload contains a file the index does not list: ${path}`)
  }
  const queue = [...index.files]
  const worker = async (): Promise<void> => {
    for (let file = queue.shift(); file !== undefined; file = queue.shift()) {
      const size = onDisk.get(file.path)
      if (size === undefined) throw new NativeComponentError(`native payload is missing ${file.path}`)
      if (size !== file.size) throw new NativeComponentError(`native payload file has the wrong size: ${file.path}`)
      const absolute = join(pluginRoot, ...file.path.split('/'))
      if ((await sha256File(absolute)) !== file.sha256) throw new NativeComponentError(`native payload file digest does not match: ${file.path}`)
      if (platform !== 'win32') await fs.chmod(absolute, file.executable ? 0o755 : 0o644)
    }
  }
  await Promise.all(Array.from({ length: HASH_CONCURRENCY }, () => worker()))

  if (index.symlinks.length === 0) return
  if (platform === 'win32') {
    // Windows 上建符号链接要额外权限;Windows 的构建本就不含链接,出现了就是装错了平台的包
    throw new NativeComponentError(`native component "${component.id}" contains symbolic links, which are not supported on Windows`)
  }
  const realScope = await fs.realpath(join(pluginRoot, ...scope.split('/')))
  for (const link of index.symlinks) {
    const absolute = join(pluginRoot, ...link.path.split('/'))
    await fs.mkdir(dirname(absolute), { recursive: true })
    await fs.symlink(link.target, absolute)
  }
  // 全部建完再逐条按真实落点核:链接指向链接时,单看词法看不出最终落到哪
  for (const link of index.symlinks) {
    const absolute = join(pluginRoot, ...link.path.split('/'))
    const real = await fs.realpath(absolute).catch(() => null)
    const rel = real === null ? '..' : relative(realScope, real)
    if (rel.startsWith('..') || isAbsolute(rel) || rel.split(sep)[0] === '..') {
      throw new NativeComponentError(`native payload symlink ${link.path} does not resolve inside ${scope}/`)
    }
  }
}

/**
 * 运行期:返回可以 spawn 的入口绝对路径,核对不过就抛。
 *
 * ★ 每次 spawn 前都要调,而不是信安装时那一次:插件目录在用户机器上是可写的。
 */
export async function resolveVerifiedEntry(
  pluginRoot: string,
  component: NativeComponent,
  target: NativeComponentTarget
): Promise<string> {
  const nativeRoot = join(pluginRoot, NATIVE_DIR)
  const lexical = join(pluginRoot, target.entry)
  let canonicalNative: string
  let canonical: string
  try {
    canonicalNative = await fs.realpath(nativeRoot)
    canonical = await fs.realpath(lexical)
  } catch {
    throw new NativeComponentError(`native component "${component.id}" entry does not exist: ${target.entry}`)
  }
  const rel = relative(canonicalNative, canonical)
  if (rel === '' || rel.startsWith('..') || isAbsolute(rel)) {
    throw new NativeComponentError(`native component "${component.id}" entry resolves outside ${NATIVE_DIR}/`)
  }
  const info = await fs.lstat(canonical)
  if (!info.isFile()) throw new NativeComponentError(`native component "${component.id}" entry is not a regular file`)
  const digest = await sha256File(canonical)
  if (digest !== target.sha256) {
    throw new NativeComponentError(`native component "${component.id}" entry digest does not match the manifest`)
  }
  return canonical
}

async function sha256File(path: string): Promise<string> {
  const hash = createHash('sha256')
  await new Promise<void>((resolve, reject) => {
    const stream = createReadStream(path)
    stream.on('data', (chunk) => { hash.update(chunk) })
    stream.on('error', reject)
    stream.on('end', () => { resolve() })
  })
  return hash.digest('hex')
}
