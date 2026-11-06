/**
 * 插件携带的**原生组件**描述 —— `package.json` 里的 `nativeComponents`。
 *
 * ## 为了什么需求建的
 *
 * 办公插件要在用户安装插件的那一刻,同时装上插件包里自带的 LibreOffice 引擎
 * (不放进 NextCoWork 主安装包,也不要求用户另装)。原生可执行代码的风险与
 * 一段 iframe 里的 JS 完全不在一个量级,所以它必须在清单里**逐条声明**:
 * 哪个平台、哪个架构、入口在包内哪里、摘要是多少、许可证材料在哪。
 *
 * ## 这个文件拥有的不变式
 *
 * 1. **入口必须落在包内 `native/` 目录下。** `ncw-plugin://` 协议据此拒绝把原生
 *    二进制当静态资源发给 iframe;放在任意路径的话,一个视图就能把引擎库当
 *    文件读走,或者更糟,协议层会把它当 `application/octet-stream` 下载。
 * 2. **每个 target 都带 sha256。** 安装器落盘后逐个核对,摘要对不上整包拒装 ——
 *    没有这一条,「清单写的入口」和「实际被 spawn 的文件」之间没有任何绑定。
 * 3. **原生包的资源预算是有限常量。** 声明自己是 native 不等于获得无限解包额度。
 *
 * ## 故意不做的
 *
 * - 不在这里做运行时平台探测:这是纯解析,主进程选 target 时再传 platform/arch。
 * - 不接受下载 URL:引擎 payload 就在插件包里,插件不能在运行期拉任意二进制。
 * - **不 import `manifest.ts`**:清单解析要调这里,反向再 import 就成了环。环在 ESM 里
 *   不报错,只会让某条初始化路径拿到 `undefined`。包内路径判定由调用方注入
 *   (`manifest.ts` 传入它的 `isSafeRelativePath`),保证只有那一份实现。
 */

/** 包内相对路径判定。由清单解析注入,见文件头。 */
export type SafePathCheck = (path: string) => boolean

/** 原生包规格版本。旧客户端认不出更高的值时必须整份拒装,而不是按普通包装。 */
export const NATIVE_PACKAGE_FORMAT = 1

export const NATIVE_PLATFORMS = ['darwin', 'win32', 'linux'] as const
export type NativePlatform = (typeof NATIVE_PLATFORMS)[number]

export const NATIVE_ARCHES = ['x64', 'arm64'] as const
export type NativeArch = (typeof NATIVE_ARCHES)[number]

/** 原生组件入口必须所在的包内目录。见文件头第 1 条。 */
export const NATIVE_DIR = 'native'

/**
 * 原生包的资源硬上限。只对声明了 `nativeComponents` 的包生效,普通插件仍是原来那组小限额
 * (`main/plugin/installer.ts`)。
 *
 * 实测(LibreOffice 26.8,macOS arm64 的 LibreOffice.app):804 MB、17,456 个文件、
 * 包内最深 17 层(加上插件自己的 `<id>/native/<target>/` 三层是 20)。限额按实测值留出余量;
 * Windows / Linux 的构建体积在 CI 里产出后应回来核对。
 * ★ 只能在这里改,不允许在用户机器上按插件自报值放大。
 */
export const NATIVE_PACKAGE_LIMITS = {
  maxZipBytes: 2 * 1024 * 1024 * 1024,
  maxExpandedBytes: 6 * 1024 * 1024 * 1024,
  maxEntries: 100_000,
  maxDepth: 48
} as const

/** 当前宿主能说的 helper 协议版本。清单声明的版本必须在这之内。 */
export const NATIVE_PROTOCOL_VERSION = 1

export interface NativeComponentTarget {
  platform: NativePlatform
  arch: NativeArch
  /** 包内相对路径,必须以 `native/` 开头 */
  entry: string
  /** 入口文件的 sha256(小写 64 位 hex) */
  sha256: string
  /**
   * 随包携带的运行时(例如完整的 LibreOffice)的**文件索引**。
   *
   * 需求:入口的 sha256 只保证 helper 本身;LibreOffice 是另外一万多个文件,解压截断或被
   * 替换时症状是「打开某类文档才崩」。索引逐个列出这些文件的大小、摘要、是否可执行,
   * 以及包内符号链接;安装器在 staging 里按它逐一核对、补齐可执行位、重建符号链接
   * (见 `main/plugin/native-installer.ts`)。索引自身的 sha256 写在清单里。
   *
   * 可选:只带 helper、在开发机上配合 `--lo-path` 用的包没有它。
   */
  payload?: { index: string; sha256: string }
}

export interface NativeComponent {
  /** 组件 id,同 `PLUGIN_NAME_RE` 形状。`documentEngines[].component` 按它引用 */
  id: string
  /** 组件自身版本(例如锁定的 LibreOffice 版本)。领域值 */
  version: string
  /** 与宿主之间的 helper 协议版本 */
  protocol: number
  targets: NativeComponentTarget[]
  license: {
    /** SPDX 表达式,原样展示,不翻译 */
    spdx: string
    /** 包内的许可证 / NOTICE 文件 */
    notices: string
    /** 对应源码获取地址(https)。是否必需由许可证审查决定,解析层不代判 */
    source?: string
  }
}

export interface NativeManifestError {
  field: string
  message: string
}

const COMPONENT_ID_RE = /^[a-z0-9][a-z0-9-]{0,63}$/
const SHA256_RE = /^[a-f0-9]{64}$/
const MAX_COMPONENTS = 8
const MAX_TARGETS = 16

/**
 * 解析 `nativeComponents`。**永不 throw**,错误推进 `errors` —— 同 `parsePluginManifest`
 * 的「装载期严格、整份拒绝」取向:半份生效的原生组件意味着某个平台打开文档时
 * 才发现入口不存在。
 */
export function parseNativeComponents(
  raw: unknown,
  errors: NativeManifestError[],
  isSafePath: SafePathCheck
): NativeComponent[] {
  if (raw === undefined) return []
  if (!Array.isArray(raw)) {
    errors.push({ field: 'nativeComponents', message: 'must be an array' })
    return []
  }
  if (raw.length > MAX_COMPONENTS) {
    errors.push({ field: 'nativeComponents', message: `at most ${MAX_COMPONENTS} native components` })
    return []
  }
  const out: NativeComponent[] = []
  for (const item of raw) {
    if (item === null || typeof item !== 'object' || Array.isArray(item)) {
      errors.push({ field: 'nativeComponents', message: 'each component must be an object' })
      continue
    }
    const c = item as Record<string, unknown>
    const id = typeof c.id === 'string' ? c.id : ''
    const field = `nativeComponents.${id === '' ? '?' : id}`
    if (!COMPONENT_ID_RE.test(id)) { errors.push({ field, message: 'id must match ^[a-z0-9][a-z0-9-]{0,63}$' }); continue }
    if (out.some((existing) => existing.id === id)) { errors.push({ field, message: 'duplicate component id' }); continue }
    const version = typeof c.version === 'string' ? c.version.trim() : ''
    if (version === '' || version.length > 64) { errors.push({ field: `${field}.version`, message: 'is required' }); continue }
    const protocol = c.protocol
    if (typeof protocol !== 'number' || !Number.isInteger(protocol) || protocol < 1) {
      errors.push({ field: `${field}.protocol`, message: 'must be a positive integer' }); continue
    }
    /*
      ★ 协议版本高于宿主时**拒装**,不是装上之后运行期报错:装上的话用户会在
      第一次打开文档时才看到失败,而那时他已经以为插件能用了。
    */
    if (protocol > NATIVE_PROTOCOL_VERSION) {
      errors.push({ field: `${field}.protocol`, message: `requires helper protocol ${protocol}; this host speaks ${NATIVE_PROTOCOL_VERSION}` }); continue
    }
    const targets = parseTargets(c.targets, field, errors, isSafePath)
    if (targets === null) continue
    const license = parseLicense(c.license, field, errors, isSafePath)
    if (license === null) continue
    out.push({ id, version, protocol, targets, license })
  }
  return out
}

function parseTargets(raw: unknown, field: string, errors: NativeManifestError[], isSafePath: SafePathCheck): NativeComponentTarget[] | null {
  if (!Array.isArray(raw) || raw.length === 0 || raw.length > MAX_TARGETS) {
    errors.push({ field: `${field}.targets`, message: `must list 1..${MAX_TARGETS} platform targets` })
    return null
  }
  const out: NativeComponentTarget[] = []
  for (const item of raw) {
    if (item === null || typeof item !== 'object' || Array.isArray(item)) {
      errors.push({ field: `${field}.targets`, message: 'each target must be an object' }); return null
    }
    const t = item as Record<string, unknown>
    const platform = t.platform
    const arch = t.arch
    if (!(NATIVE_PLATFORMS as readonly unknown[]).includes(platform)) {
      errors.push({ field: `${field}.targets`, message: `unknown platform ${String(platform)}` }); return null
    }
    if (!(NATIVE_ARCHES as readonly unknown[]).includes(arch)) {
      errors.push({ field: `${field}.targets`, message: `unknown arch ${String(arch)}` }); return null
    }
    const entry = typeof t.entry === 'string' ? t.entry : ''
    if (!isNativeEntry(entry, isSafePath)) {
      errors.push({ field: `${field}.targets`, message: `entry must be a package-relative path under ${NATIVE_DIR}/: ${entry}` }); return null
    }
    const sha256 = typeof t.sha256 === 'string' ? t.sha256.toLowerCase() : ''
    if (!SHA256_RE.test(sha256)) {
      errors.push({ field: `${field}.targets`, message: 'sha256 must be 64 hex characters' }); return null
    }
    // 同一平台/架构两条 target 时,「选哪一个」就成了顺序问题 —— 直接拒掉
    if (out.some((existing) => existing.platform === platform && existing.arch === arch)) {
      errors.push({ field: `${field}.targets`, message: `duplicate target ${String(platform)}-${String(arch)}` }); return null
    }
    let payload: NativeComponentTarget['payload']
    if (t.payload !== undefined) {
      const p = t.payload as Record<string, unknown> | null
      const index = typeof p?.index === 'string' ? p.index : ''
      const digest = typeof p?.sha256 === 'string' ? p.sha256.toLowerCase() : ''
      /*
        ★ 索引必须和入口在同一个 `native/<目录>/` 下:索引只覆盖它所在的目录,放到别处
        就等于入口所在目录没人核对,或者一个 target 的索引去核对另一个 target 的文件。
      */
      const entryDir = entry.slice(0, entry.lastIndexOf('/'))
      if (!isNativeEntry(index, isSafePath) || !index.startsWith(`${entryDir}/`)) {
        errors.push({ field: `${field}.targets`, message: `payload.index must be a file next to the entry, under ${entryDir}/` }); return null
      }
      if (!SHA256_RE.test(digest)) {
        errors.push({ field: `${field}.targets`, message: 'payload.sha256 must be 64 hex characters' }); return null
      }
      payload = { index, sha256: digest }
    }
    out.push({ platform: platform as NativePlatform, arch: arch as NativeArch, entry, sha256, ...(payload === undefined ? {} : { payload }) })
  }
  return out
}

function parseLicense(raw: unknown, field: string, errors: NativeManifestError[], isSafePath: SafePathCheck): NativeComponent['license'] | null {
  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) {
    errors.push({ field: `${field}.license`, message: 'is required' }); return null
  }
  const l = raw as Record<string, unknown>
  const spdx = typeof l.spdx === 'string' ? l.spdx.trim() : ''
  if (spdx === '' || spdx.length > 256) { errors.push({ field: `${field}.license.spdx`, message: 'is required' }); return null }
  const notices = typeof l.notices === 'string' ? l.notices : ''
  if (!isSafePath(notices)) { errors.push({ field: `${field}.license.notices`, message: 'must be a package-relative path' }); return null }
  const source = typeof l.source === 'string' ? l.source.trim() : ''
  if (source !== '' && !source.startsWith('https://')) {
    errors.push({ field: `${field}.license.source`, message: 'must be an https:// URL' }); return null
  }
  return { spdx, notices, ...(source === '' ? {} : { source }) }
}

/** 入口是否是 `native/` 下的包内相对路径(且不是目录本身)。 */
export function isNativeEntry(path: string, isSafePath: SafePathCheck): boolean {
  if (!isSafePath(path)) return false
  const segments = path.split('/')
  return segments.length >= 2 && segments[0] === NATIVE_DIR && segments.every((segment) => segment !== '' && segment !== '.')
}

/**
 * 包内路径是否属于原生区。协议层用它拒绝把原生文件发给 iframe。
 *
 * ★ 大小写不敏感:macOS / Windows 上 `Native/x.dylib` 与 `native/x.dylib` 是同一个
 * 文件,按大小写敏感比的话换个写法就能绕过。
 */
export function isNativePackagePath(path: string): boolean {
  const normalized = path.replaceAll('\\', '/').replace(/^\.?\/+/, '').toLowerCase()
  return normalized === NATIVE_DIR || normalized.startsWith(`${NATIVE_DIR}/`)
}

/**
 * 为当前机器选 target。没有匹配返回 `null` —— 调用方必须明确报「不支持此平台」,
 * 不能退到别的架构(x64 入口在 arm64 机器上要么起不来,要么走模拟层且不在验收矩阵里)。
 */
export function selectNativeTarget(
  component: NativeComponent,
  platform: string,
  arch: string
): NativeComponentTarget | null {
  return component.targets.find((target) => target.platform === platform && target.arch === arch) ?? null
}
