/**
 * 「添加 / 编辑 MCP 服务器」那个弹窗背后的全部纯逻辑。
 *
 * ★ **抽成 `.ts` 才测得到。** vitest 在这个仓库是 node 环境、只收 `.ts` 文件
 * (没有 jsdom),所以任何写进 `.tsx` 的判断都是不可测的。而这个表单恰恰是
 * 设置页里判断最密的地方:参数怎么切、环境变量怎么解析、什么算合法的 id ——
 * 全部落在这个文件里,弹窗组件只负责画。
 */
import type { McpServerConfig, McpTransport } from '../../../shared/domain/mcp'
import { MCP_SERVER_ID_RE } from '../../../shared/domain/mcp'

/** 表单的草稿态。**全是字符串** —— 用户正在打字的中间态本来就不是结构化数据 */
export interface McpDraft {
  id: string
  name: string
  description: string
  transport: McpTransport
  /** stdio */
  command: string
  argsText: string
  cwd: string
  /** sse / streamable-http */
  url: string
  /** 两种传输共用这一个多行框:stdio 是环境变量,http 是请求头 */
  secretsText: string
}

export function emptyDraft(): McpDraft {
  return {
    id: '',
    name: '',
    description: '',
    transport: 'stdio',
    command: '',
    argsText: '',
    cwd: '',
    url: '',
    secretsText: ''
  }
}

/**
 * 已有配置 → 草稿。
 *
 * ★ **密钥的值不在这里**,只有键名 —— 编辑一台已有服务器时,那个多行框里
 * 显示的是 `GITHUB_TOKEN=`(等号后面空着),而不是原来的值。取不回来是
 * 「凭证只写不读」的直接结果,不是遗漏;界面上要说清楚,
 * 不然用户会以为是被清空了。
 */
export function draftOf(raw: McpServerConfig): McpDraft {
  // ★ 先归一化:库里那份 JSON 是原样存取的(`repo.ts` 的 `parse<McpServerConfig>`
  //   不校验),缺一个 `envNames` 就会在这里 `.map` 到 undefined —— 渲染期异常,
  //   整个窗口白屏。见 `normalizeMcpConfig`。
  const cfg = normalizeMcpConfig(raw)
  const base = {
    ...emptyDraft(),
    id: cfg.id,
    name: cfg.name,
    description: cfg.description ?? '',
    transport: cfg.transport
  }
  if (cfg.transport === 'stdio') {
    return {
      ...base,
      command: cfg.command,
      argsText: formatArgs(cfg.args),
      cwd: cfg.cwd ?? '',
      secretsText: cfg.envNames.map((n) => `${n}=`).join('\n')
    }
  }
  return {
    ...base,
    url: cfg.url,
    secretsText: cfg.headerNames.map((n) => `${n}=`).join('\n')
  }
}

/**
 * 参数数组 → 输入框文本,是 `parseArgs` 的逆运算。
 *
 * 带空白的参数(路径里的空格)必须加引号,否则「打开编辑 → 原样保存」会把
 * 一个参数悄悄切成两个。两种引号都含的参数 `parseArgs` 表达不了,只能原样放。
 */
export function formatArgs(args: readonly string[]): string {
  return args
    .map((a) => {
      if (a !== '' && !/[\s"']/.test(a)) return a
      if (!a.includes('"')) return `"${a}"`
      if (!a.includes("'")) return `'${a}'`
      return a
    })
    .join(' ')
}

function str(v: unknown): string {
  return typeof v === 'string' ? v : typeof v === 'number' ? String(v) : ''
}

/** 键名列表:接受字符串数组,也接受 `{ KEY: value }` 这种对象(取键名,**丢掉值**) */
function names(list: unknown, fallbackMap: unknown): string[] {
  const out: string[] = []
  const push = (n: string): void => {
    const t = n.trim()
    if (t !== '' && !out.includes(t)) out.push(t)
  }
  if (Array.isArray(list)) {
    for (const item of list) {
      if (typeof item === 'string') push(item)
      else if (typeof item === 'object' && item !== null) push(str((item as Record<string, unknown>)['name']))
    }
    return out
  }
  const map = typeof list === 'object' && list !== null ? list : fallbackMap
  if (typeof map === 'object' && map !== null && !Array.isArray(map)) {
    for (const k of Object.keys(map)) push(k)
  }
  return out
}

/**
 * 任何「看起来像一台 MCP 服务器」的对象 → 合法的 `McpServerConfig`。
 *
 * ★ 存储层不校验形状,所以设置页拿到的配置不一定是当前类型声明的样子:
 * 旧版本写的、手改过数据库的、从别处 JSON 粘过来的(`env` / `headers` 是对象、
 * `transport: 'http'`、`command` 是数组、缺 `args`)。编辑器和列表行都只认这里
 * 吐出来的形状,于是**每一种能存进去的形状都能被打开编辑**,保存时再写回规范形状。
 *
 * 不做的事:不猜值、不读密钥 —— 对象里的值(可能是明文 token)一律丢弃,只留键名。
 */
export function normalizeMcpConfig(raw: unknown): McpServerConfig {
  const r = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>
  const id = str(r['id'])
  const base = {
    id,
    name: str(r['name']) || id,
    enabled: r['enabled'] !== false,
    ...(typeof r['description'] === 'string' ? { description: r['description'] } : {}),
    ...(typeof r['workspaceId'] === 'string' && r['workspaceId'] !== ''
      ? { workspaceId: r['workspaceId'] }
      : {})
  }

  const declared = str(r['transport'] ?? r['type']).toLowerCase()
  const url = str(r['url'])
  const remote =
    declared === 'sse' ||
    declared === 'streamable-http' ||
    declared === 'http' ||
    (declared !== 'stdio' && url !== '' && r['command'] === undefined)

  if (remote) {
    return {
      ...base,
      transport: declared === 'sse' ? 'sse' : 'streamable-http',
      url,
      headerNames: names(r['headerNames'], r['headers'])
    }
  }

  // `command` 可能是 `["npx", "-y", "pkg"]` 这种整条数组(OpenCode 的写法)
  const rawCommand = r['command']
  const commandParts = Array.isArray(rawCommand) ? rawCommand.map(str).filter((s) => s !== '') : []
  const command = Array.isArray(rawCommand) ? (commandParts[0] ?? '') : str(rawCommand)
  const rawArgs = r['args']
  const args = Array.isArray(rawArgs)
    ? rawArgs.map(str)
    : typeof rawArgs === 'string'
      ? parseArgs(rawArgs)
      : []
  const cwd = str(r['cwd'])
  return {
    ...base,
    transport: 'stdio',
    command,
    args: [...commandParts.slice(1), ...args],
    envNames: names(r['envNames'], r['env']),
    ...(cwd === '' ? {} : { cwd })
  }
}

/**
 * 按空格切参数,**认引号**。
 *
 * 不认引号的话,带空格的路径和带 JSON 的参数都会被切碎 —— 这两样在 MCP
 * 服务器的启动参数里都很常见。
 *
 * 但**不是**一个完整的 shell 词法分析器,两处刻意留白:
 *
 * - **不做变量展开。** 这些参数直接进 `spawn` 的 argv 数组、不经过 shell,
 *   所以展开了反而会让 `$HOME` 在这里和在终端里表现不一致。
 * - ★ **反斜杠不当转义符。** 于是 `"{\"a\":1}"` 这种 shell 写法解不出来
 *   (得改用单引号 `'{"a":1}'`),换来的是 Windows 路径 `"C:\Users\me"`
 *   原样活着。后者更要紧:stdio 的 MCP 服务器在 Windows 上就是这么配的,
 *   而把路径吃掉一半之后,报出来的错会指向完全无关的地方。
 */
export function parseArgs(text: string): string[] {
  const out: string[] = []
  let cur = ''
  let quote: '"' | "'" | null = null
  let started = false

  for (const ch of text) {
    if (quote !== null) {
      if (ch === quote) quote = null
      else cur += ch
      continue
    }
    if (ch === '"' || ch === "'") {
      quote = ch
      // 引号本身就意味着「这是一个参数」,哪怕它里面是空的:`--flag ""`
      started = true
      continue
    }
    if (/\s/.test(ch)) {
      if (started) out.push(cur)
      cur = ''
      started = false
      continue
    }
    cur += ch
    started = true
  }
  if (started) out.push(cur)
  return out
}

export interface SecretLine {
  name: string
  /** 空串 = 用户没填值(编辑已有服务器时的常态) */
  value: string
}

/**
 * 逐行解析 `KEY=VALUE`。
 *
 * 三条判断值得留意:
 * - **只按第一个 `=` 切**。值里带 `=` 是常态(base64 的填充、连接串)。
 * - `#` 开头当注释 —— 用户往往从 `.env` 里整段粘过来。
 * - **键名去空白、值不去尾部空白之外的东西**;`Authorization = Bearer x` 里
 *   等号两边的空格是排版,而 `Bearer x` 中间那个空格是值的一部分。
 */
export function parseSecretLines(text: string): SecretLine[] {
  const out: SecretLine[] = []
  const seen = new Set<string>()
  for (const raw of text.split('\n')) {
    const line = raw.trim()
    if (line === '' || line.startsWith('#')) continue
    const eq = line.indexOf('=')
    if (eq <= 0) continue // 没有等号、或以等号开头(没有键名)
    const name = line.slice(0, eq).trim()
    if (name === '' || seen.has(name)) continue
    seen.add(name)
    out.push({ name, value: line.slice(eq + 1).trim() })
  }
  return out
}

/** 只有真填了值的那几条要写进加密存储 —— 空值意味着「别动已经存着的那个」 */
export function secretValues(lines: readonly SecretLine[]): Record<string, string> {
  const out: Record<string, string> = {}
  for (const l of lines) {
    if (l.value !== '') out[l.name] = l.value
  }
  return out
}

/**
 * `Authorization` 的值几乎总是带一个方案前缀(`Bearer x`、`Basic x`),
 * 用户却常常只粘了裸 token —— 服务端多半不会说「少了 Bearer」,只会报
 * 一个不相关的认证错误,排查起来要绕一圈。裸 token 本身不含空格,
 * 而 `Scheme x` 一定有,拿这个当启发式够用,不需要认全部方案名。
 */
export function authorizationWarning(lines: readonly SecretLine[]): string | null {
  const line = lines.find((l) => l.name.toLowerCase() === 'authorization' && l.value !== '')
  if (line === undefined || line.value.includes(' ')) return null
  return `Authorization 的值通常要带方案前缀,比如 Bearer ${line.value}`
}

/**
 * 从名字猜一个 id。参考图那个弹窗里 id 是自动填的,用户很少去改它。
 *
 * 猜不出来(比如名字是纯中文)时返回空串,让用户自己填 —— 编一个
 * `server-1` 出来的话,用户根本不会注意到那一栏,而 id 会进工具名。
 */
export function suggestId(name: string): string {
  const slug = name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9_-]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 32)
  return MCP_SERVER_ID_RE.test(slug) ? slug : ''
}

/** 逐字段的报错。key 是字段名,界面据此在那一栏下面标红 */
export type McpFormErrors = Partial<Record<'id' | 'name' | 'command' | 'url', string>>

/** `new URL` 解不开就是 `null` —— 这里不需要区分是哪一种解不开 */
function tryUrl(raw: string): URL | null {
  try {
    return new URL(raw)
  } catch {
    return null
  }
}

/**
 * 校验。**和主进程 `ipc/mcp.ts` 的 `assertValid` 是同一套规则,故意重复一遍** ——
 * 这一份是为了在用户点保存之前就把话说清楚,那一份是为了挡住绕过界面的调用。
 * 少了任何一份都不行:只有前端的话,IPC 是敞开的;只有后端的话,用户要
 * 点一次保存才知道 id 不合法。
 */
export function validateDraft(d: McpDraft, existingIds: readonly string[]): McpFormErrors {
  const e: McpFormErrors = {}

  if (d.name.trim() === '') e.name = '给它起个名字'

  const id = d.id.trim()
  if (id === '') e.id = '要一个 ID'
  else if (!MCP_SERVER_ID_RE.test(id))
    e.id = '只能用字母、数字、下划线和连字符,最多 32 个字符'
  else if (existingIds.includes(id)) e.id = '这个 ID 已经被用了'

  if (d.transport === 'stdio') {
    if (d.command.trim() === '') e.command = '要一个可执行命令,比如 npx'
  } else {
    const url = d.url.trim()
    if (url === '') e.url = '要一个地址'
    else {
      const parsed = tryUrl(url)
      if (parsed === null) e.url = '这不是一个合法的地址'
      else if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:')
        e.url = '只支持 http 和 https'
    }
  }

  return e
}

export function hasErrors(e: McpFormErrors): boolean {
  return Object.keys(e).length > 0
}

/**
 * 草稿 → 配置。**只在 `validateDraft` 返回空之后调**。
 *
 * `enabled` 由调用方给:新建时默认开(用户刚填完就是想用它),
 * 编辑时保持原样(不能因为改了个描述就把一台停用的服务器悄悄启用)。
 */
export function toConfig(d: McpDraft, enabled: boolean): McpServerConfig {
  const base = {
    id: d.id.trim(),
    name: d.name.trim(),
    enabled,
    ...(d.description.trim() === '' ? {} : { description: d.description.trim() })
  }
  const names = parseSecretLines(d.secretsText).map((l) => l.name)

  if (d.transport === 'stdio') {
    return {
      ...base,
      transport: 'stdio',
      command: d.command.trim(),
      args: parseArgs(d.argsText),
      envNames: names,
      ...(d.cwd.trim() === '' ? {} : { cwd: d.cwd.trim() })
    }
  }
  return { ...base, transport: d.transport, url: d.url.trim(), headerNames: names }
}
