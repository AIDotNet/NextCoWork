/**
 * 参数门 —— **四道门里的第三道**(见 `shared/plugin/protocol.ts` 的文件头)。
 *
 * 静态门回答「清单里声明了吗」,授予门回答「用户批了吗」,
 * 这一层回答的是**「这一次调用的参数落在批准范围之内吗」**。
 *
 * ## 为什么必须单独一层
 *
 * 「批准了 `workspace.read`」不等于「可以读任意文件」。没有这一层的话,
 * 一个拿到读权限的插件可以 `readFile('../../../.ssh/id_rsa')` —— 能力检查
 * 会放行,因为它要的确实是「读」。范围收窄和能力授予是两个问题。
 *
 * ## 这里全是纯函数
 *
 * 一个参数都不碰文件系统、不碰 electron。路径的**词法**边界在这里判,
 * realpath 归一(挡软链)在调用点做 —— 和 `net/attachment-protocol.ts`
 * 的两层防线是同一个分工:这一层管「长得对不对」,那一层管「落点在哪」。
 */
import { isAbsolute, relative, resolve } from 'node:path'
import { matchesHostPermission } from '../../shared/plugin/manifest'

export type NarrowResult<T> = { ok: true; value: T } | { ok: false; reason: string }

function deny(reason: string): { ok: false; reason: string } {
  return { ok: false, reason }
}

/** 单次读写的字节上限。插件不是文件管理器,它要的是配置和文档。 */
export const MAX_PLUGIN_FILE_BYTES = 8 * 1024 * 1024

/**
 * 一次 `net.fetch` 能读回的**字节**上限。
 *
 * ★ 与 `MAX_PLUGIN_FILE_BYTES` 同量级,但**不是同一件事**:那个是插件一次读写
 * 的预算,这个是「宿主替它收一条响应」的预算。定成一个数是因为两者都对着同一个
 * 失败形态 —— 主进程替第三方把一整个响应读进内存。
 *
 * ★ 计数单位是**字节**,不是字符。旧实现拿 `text.length` 截,一个多字节字符算
 * 一个,于是「8MB 上限」在中文页面上实际能放进 24MB 内存。
 */
export const MAX_NET_RESPONSE_BYTES = 8 * 1024 * 1024

/**
 * 一次 `net.fetch` 最多跟几跳重定向。
 *
 * ★ 有了上限,「跳到哪里去」才是一个有限集合,每一次跳转也才有机会各过一道
 * `narrowFetchUrl`。没有上限的话,一个重定向环就能把主进程挂在那里反复请求。
 */
export const MAX_NET_REDIRECTS = 5

/** 响应头的条数与单条长度上限 —— 同 `sanitizeHeaders` 的立场:头也是不可信输入。 */
export const MAX_NET_HEADER_COUNT = 64
export const MAX_NET_HEADER_VALUE_BYTES = 4096

/**
 * 工作区内的相对路径 → 绝对路径。
 *
 * ★ 入参**只接受相对路径**。接受绝对路径意味着每一次调用都要判断
 * 「这个绝对路径是不是恰好在工作区里」,而那个判断在符号链接、大小写不敏感
 * 文件系统、UNC 路径上各有各的坑。只收相对路径把问题缩小成一件事:
 * 拼出来的结果有没有跑到根外面去。
 */
export function narrowWorkspacePath(
  workspaceRoot: string,
  path: string
): NarrowResult<string> {
  if (workspaceRoot === '') return deny('no workspace is open')
  if (typeof path !== 'string' || path === '') return deny('path is required')
  if (path.length > 1024) return deny('path is too long')
  if (path.includes('\0')) return deny('path contains a NUL byte')
  if (isAbsolute(path)) return deny('path must be relative to the workspace root')
  if (/^[a-zA-Z]:/.test(path)) return deny('path must be relative to the workspace root')

  const target = resolve(workspaceRoot, path)
  const rel = relative(resolve(workspaceRoot), target)
  // `..` 开头 = 跑到根外面了;绝对 = 换了盘符(Windows)。
  if (rel !== '' && (rel.startsWith('..') || isAbsolute(rel))) return deny('path escapes the workspace root')
  return { ok: true, value: target }
}

/**
 * 清单里可选的 `paths` glob 再收窄一次。
 *
 * 只支持前缀形式(`docs/*`、`src/**`),不引 glob 引擎:这一层是**安全边界**,
 * 而通用 glob 的语义(`{a,b}`、否定、`..` 在 pattern 里)每一条都是一次
 * 「我以为它匹配不到」的机会。
 */
export function matchesPathScope(scopes: readonly string[] | undefined, relPath: string): boolean {
  if (scopes === undefined || scopes.length === 0) return true
  const normalized = relPath.replaceAll('\\', '/')
  return scopes.some((scope) => {
    const clean = scope.replaceAll('\\', '/').replace(/\*+$/, '')
    return clean === '' || normalized === clean || normalized.startsWith(clean.endsWith('/') ? clean : `${clean}/`)
  })
}

/**
 * 命令白名单 —— 只匹配 `argv[0]`,而且只按**基名**。
 *
 * ★ 按基名而不是整条路径:`/usr/bin/cargo` 与 `cargo` 是同一个意图,
 * 而让插件作者在清单里写绝对路径会让这份清单在别人机器上一定不成立。
 * 代价是 PATH 里被放了个假 `cargo` 时这一层挡不住 —— 那是另一个威胁模型
 * (本机已被控制),不在这道门的职责里。
 */
export function narrowCommand(
  allowed: readonly string[],
  command: string,
  args: readonly string[]
): NarrowResult<{ command: string; args: string[] }> {
  if (typeof command !== 'string' || command.trim() === '') return deny('command is required')
  if (command.includes('\0')) return deny('command contains a NUL byte')
  if (!Array.isArray(args) || args.some((a) => typeof a !== 'string' || a.includes('\0'))) {
    return deny('args must be an array of strings')
  }
  if (args.length > 64) return deny('too many arguments')
  const base = command.replaceAll('\\', '/').split('/').pop() ?? command
  const stem = base.replace(/\.(exe|cmd|bat|ps1)$/i, '')
  if (allowed.length === 0) return deny('the manifest declares no command allow-list')
  if (!allowed.includes(stem)) return deny(`"${stem}" is not in the manifest command allow-list`)
  /*
    ★ **不拼 shell 串,也不接受 shell 元字符。** 这条调用最终走
    `kernel/node-spawn.ts` 的 argv 形式;这里挡掉元字符是为了让「这条命令
    到底会跑什么」在审批弹窗里是可读的 —— 一个带 `&&` 的参数会让弹窗上
    写着 `cargo check`,实际跑的是另外两条。
  */
  if (/[;&|`$><\n]/.test(command)) return deny('command must not contain shell metacharacters')
  return { ok: true, value: { command: stem, args: [...args] } }
}

/**
 * `tabs.openTerminal` 的 env 门。**只做形状与体量**,不做语义:
 * 插件往子进程环境里写什么(`ANTHROPIC_BASE_URL` 之类)是它自己的事,
 * 这一层拦的是「借环境变量夹带」—— 上百个键、几 MB 的值,或者拿环境变量
 * 当一条隐蔽的 IPC 通道用。
 *
 * ★ 键名必须长得像环境变量名:宿主要把它们合进 pty 子进程的 env
 * (`{ ...process.env, ...env }`),带 `=`、空格或空串的键在 POSIX 上不可设,
 * 在 Windows 的进程块里则是未定义行为。
 */
export function narrowLaunchEnv(env: unknown): NarrowResult<Record<string, string>> {
  if (env === undefined) return { ok: true, value: {} }
  if (env === null || typeof env !== 'object' || Array.isArray(env)) return deny('env must be an object')
  const entries = Object.entries(env as Record<string, unknown>)
  if (entries.length > 16) return deny('too many env entries (max 16)')
  const out: Record<string, string> = {}
  for (const [key, value] of entries) {
    if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(key)) return deny(`env key "${key.slice(0, 64)}" is not a valid name`)
    if (typeof value !== 'string') return deny(`env value for "${key}" must be a string`)
    if (value.length > 4096) return deny(`env value for "${key}" is too long (max 4096)`)
    if (value.includes('\0')) return deny(`env value for "${key}" contains a NUL byte`)
    out[key] = value
  }
  return { ok: true, value: out }
}

/** `net.fetch` 的 URL 门。**在主进程做**,不信 CSP。 */
export function narrowFetchUrl(
  hostPermissions: readonly string[],
  url: string
): NarrowResult<string> {
  if (typeof url !== 'string' || url === '') return deny('url is required')
  if (url.length > 2048) return deny('url is too long')
  let parsed: URL
  try { parsed = new URL(url) } catch { return deny('url is not a valid URL') }
  // ★ 只允许 https。明文 http 会让「这条请求去了哪」变成一个中间人说了算的问题。
  if (parsed.protocol !== 'https:') return deny('only https:// is allowed')
  if (parsed.username !== '' || parsed.password !== '') return deny('credentials in the URL are not allowed')
  if (isPrivateHost(parsed.hostname)) return deny('private and loopback addresses are blocked')
  if (!matchesHostPermission(hostPermissions, url)) {
    return deny('url does not match any entry in hostPermissions')
  }
  return { ok: true, value: parsed.toString() }
}

/**
 * 内网/环回地址一律挡掉 —— 和 `kernel/tool/builtin/ssrf.ts` 同一个理由:
 * `hostPermissions` 是作者写的,而作者可以写 `https://169.254.169.254/*`
 * 然后在审核那里以「我要访问我自己的元数据服务」蒙混过去。
 *
 * ★ **IPv6 的唯一本地判定必须带冒号。** 这里原本写的是
 * `host.startsWith('fc') || host.startsWith('fd')`,而主机名可以是任意字符串:
 * 「fc2.com」「fdroid.org」这类正常域名全部以 fc/fd 开头,于是它们被当成唯一
 * 本地地址(fc00::/7)**静默拒绝** —— 插件拿到的是一句「内网地址被挡」,
 * 而那个域名根本不在内网,错误信息指向的原因和实际原因毫无关系。
 *
 * ★ **只挡字面量。** 域名解析到内网(或 DNS rebinding)这一层在这里判不了:
 * 预查询 DNS 再连接是两次不同的解析,拦不住重绑定。要闭合这一项得让实际建立的
 * 连接绑定到已校验的那个地址 —— 见 `rpc.ts` 里 `net.fetch` 的说明,那里写着
 * 这一项**仍是未解决**。
 */
function isPrivateHost(hostname: string): boolean {
  const host = hostname.toLowerCase().replace(/^\[|\]$/g, '')
  if (host === 'localhost' || host.endsWith('.localhost') || host.endsWith('.local')) return true
  if (host === '::1' || host === '::' || host === '0.0.0.0') return true
  // ★ 冒号是判据:唯一本地地址 fc00::/7 与链路本地 fe80::/10 只出现在 IPv6 字面量里。
  //   按前缀字符串判会把 fc2.com 一起带走,那正是上面注释里那个 bug。
  if (host.includes(':') && (/^f[cd][0-9a-f]{2}:/.test(host) || /^fe[89ab]/i.test(host))) return true
  const v4 = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(host)
  if (v4 === null) return false
  const [a, b] = [Number(v4[1]), Number(v4[2])]
  if (a === 10 || a === 127 || a === 0) return true
  if (a === 169 && b === 254) return true
  if (a === 172 && b >= 16 && b <= 31) return true
  if (a === 192 && b === 168) return true
  return false
}

/**
 * 逐跳重定向能不能跟 —— 每一跳都重过一遍**完整**的 URL 门(https + 无凭证 +
 * 非内网字面量 + 命中 `hostPermissions`)。
 *
 * ★ **跨 host 跳转在这里被拒掉,而不是跟着走。** 一个只声明了
 * `https://api.example.com/*` 的插件,响应若把它跳到 `https://attacker.example`,
 * 跟着走等于让**远端**替它把 `hostPermissions` 改写成任意域名 —— 用户在安装
 * 界面上批准过的域名就不再是「它能访问的那些」。要跨 host,插件自己再发一次。
 *
 * ★ 相对地址由调用方按**当前这一跳**解析成绝对地址再传进来 —— 这一层只做
 * 与首跳完全相同的那道判定,所以两条路不会分叉。
 */
export function narrowRedirectUrl(hostPermissions: readonly string[], location: string): NarrowResult<string> {
  return narrowFetchUrl(hostPermissions, location)
}

/** kv 的 key。强制前缀在调用点加,这里只管形状与长度。 */
export function narrowStorageKey(key: string): NarrowResult<string> {
  if (typeof key !== 'string' || key === '') return deny('key is required')
  if (key.length > 256) return deny('key is too long')
  if (!/^[A-Za-z0-9_.:@/-]+$/.test(key)) return deny('key contains unsupported characters')
  return { ok: true, value: key }
}

/**
 * secrets 的 key 前缀。
 *
 * ★ 前缀由**宿主**拼,不是插件传进来的一部分:让插件自己拼前缀等于让它
 * 有机会写成 `plugin:other.plugin:token`,读走别人的密钥。
 */
export function secretKeyFor(pluginId: string, key: string): string {
  return `plugin:${pluginId}:${key}`
}

/** 注入上下文的强制包裹 + 截断。长度上限见 `PLUGIN_CONTEXT_LIMIT`。 */
export function wrapPluginContext(pluginId: string, text: string, limit: number): string {
  /*
    ★ 控制字符必须去掉:它们进的是发给模型的消息流,而其中几个(尤其是
    转义序列)会在转录渲染与终端回显里被解释成别的东西。
    `no-control-regex` 在这里是误报 —— 我们要匹配的**就是**控制字符。
  */
  // eslint-disable-next-line no-control-regex
  const clean = text.replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F]/g, '').trim()
  if (clean === '') return ''
  const body = clean.length > limit ? `${clean.slice(0, limit)}…` : clean
  /*
    ★ 强制包裹不是排版,是**边界声明**:模型必须能看出这一段来自插件而不是
    用户或系统提示词。同 `kernel/untrusted.ts` 给 Skill 正文加边界的理由。
  */
  return `<plugin-context source="${pluginId}">\n${body}\n</plugin-context>`
}
