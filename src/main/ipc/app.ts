/**
 * 应用级 handler:握手 + 外链。
 *
 * 握手时序照协议 §7:`window:ready` → `app:getBootstrap` → 首屏 → 增量事件。
 * Bootstrap **一次拿全**,而不是让渲染层开局打七八个 invoke —— 那样会出现
 * 「设置到了但工作区还没到」的中间态,每个组件都得写一遍 loading 分支。
 */
import { app, clipboard, ClipboardItem, dialog, nativeTheme, nativeImage, shell } from 'electron'
import { writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { extOfMime, imageMimeOfBytes } from '../../shared/domain/attachment'
import type { Bootstrap } from '../../shared/domain/bootstrap'
import { runs } from '../kernel/run-registry'
import { activeRunIndex } from './agent'
import type { ResolvedTheme, ThemePreference } from '../../shared/domain/settings'
import type { WindowKind } from '../../shared/domain/tab'
import { EMPTY_OUTER, outerTabKey, store } from '../state/store'
import { windows } from '../window/registry'
import { notifyPluginsThemeChanged } from './plugins'
import { IpcError } from './errors'
import type { ClientUpdateInfo, UpdateCheckResult } from '../../shared/domain/update'

const UPDATE_API = 'https://nextco.work/api/client/updates/latest'

function compareVersions(left: string, right: string): number {
  const parse = (value: string) => {
    const [withoutBuild = '0.0.0', build] = value.replace(/^v/, '').split('+')
    const [core = '0.0.0', pre = ''] = withoutBuild.split('-')
    return { core: core.split('.').map(Number), pre: pre ? pre.split('.') : [], build }
  }
  const a = parse(left); const b = parse(right)
  for (let i = 0; i < 3; i += 1) { const diff = (a.core[i] ?? 0) - (b.core[i] ?? 0); if (diff !== 0) return diff }
  if (!a.pre.length || !b.pre.length) return a.pre.length === b.pre.length ? 0 : a.pre.length ? -1 : 1
  for (let i = 0; i < Math.max(a.pre.length, b.pre.length); i += 1) {
    const av = a.pre[i]; const bv = b.pre[i]
    if (av === undefined || bv === undefined) return av === undefined ? -1 : 1
    const an = /^\d+$/.test(av); const bn = /^\d+$/.test(bv)
    if (an && bn && av !== bv) return Number(av) - Number(bv)
    if (an !== bn) return an ? -1 : 1
    if (av !== bv) return av < bv ? -1 : 1
  }
  return 0
}

export async function checkForUpdates(): Promise<UpdateCheckResult> {
  const currentVersion = app.getVersion()
  const platform = process.platform === 'win32' ? 'windows' : process.platform === 'darwin' ? 'macos' : 'linux'
  const architecture = process.arch === 'arm64' ? 'arm64' : 'x64'
  try {
    const response = await fetch(`${UPDATE_API}?platform=${platform}&architecture=${architecture}&channel=stable`, { signal: AbortSignal.timeout(10_000) })
    if (response.status === 404) return { status: 'current', currentVersion }
    if (!response.ok) return { status: 'unavailable', currentVersion, message: `HTTP ${response.status}` }
    const envelope = await response.json() as { data?: ClientUpdateInfo } | ClientUpdateInfo
    const candidate = 'data' in envelope ? envelope.data : envelope
    const update = candidate !== undefined && typeof candidate === 'object' && 'version' in candidate
      ? candidate as ClientUpdateInfo
      : undefined
    if (!update || typeof update.version !== 'string' || typeof update.downloadUrl !== 'string') return { status: 'unavailable', currentVersion, message: 'Invalid update response' }
    if (compareVersions(update.version, currentVersion) <= 0) return { status: 'current', currentVersion }
    if (!update.downloadUrl.startsWith('https://')) return { status: 'unavailable', currentVersion, message: 'Invalid download URL' }
    return { status: 'available', currentVersion, update }
  } catch (error) {
    return { status: 'unavailable', currentVersion, message: error instanceof Error ? error.message : 'Network error' }
  }
}

export function resolveTheme(pref: ThemePreference): ResolvedTheme {
  if (pref === 'system') return nativeTheme.shouldUseDarkColors ? 'dark' : 'light'
  return pref
}

/** 设置页三态直接映射到 Electron 的 themeSource,系统菜单/原生控件才会跟着变 */
export function applyThemePreference(pref: ThemePreference): ResolvedTheme {
  nativeTheme.themeSource = pref
  return resolveTheme(pref)
}

export function getBootstrap(windowKind: WindowKind): Bootstrap {
  const settings = store.getSettings()
  return {
    windowKind,
    settings,
    resolvedTheme: resolveTheme(settings.theme),
    workspaces: store.listWorkspaces(),
    tabState: store.getKv(outerTabKey(windowKind), EMPTY_OUTER),
    // ★ 正常冷启动一定是空的 ——「永不恢复运行中状态」(方案 §9)。
    //   非空只发生在渲染层重载(⌘R):主进程没重启,run 还活着。
    //   首帧之后由 `agent:activeRuns` 广播接手,两边共用 `activeRunIndex()` 那一份口径。
    activeRuns: activeRunIndex(),
    activeSubagents: runs.activeRunIds().flatMap((id) => {
      const run = runs.get(id)
      const parent = run?.parentRunId === undefined ? undefined : runs.get(run.parentRunId)
      // The child uses an isolated derived session for its own transcript. The
      // bootstrap route must carry the parent's session so the Task card can
      // be restored into the conversation that launched it.
      return run === undefined || run.parentRunId === undefined || parent === undefined ? [] : [{
        runId: run.runId,
        parentRunId: run.parentRunId,
        sessionId: parent.sessionId,
        workspaceId: parent.workspaceId,
        status: run.status,
        startedAt: run.startedAt
      }]
    }),
    versions: {
      app: app.getVersion(),
      electron: process.versions.electron,
      chrome: process.versions.chrome,
      node: process.versions.node
    }
  }
}

/**
 * 外链一律交给系统浏览器,且只放行 http(s)。
 * 与 main/index.ts 里 setWindowOpenHandler 的策略保持一致 ——
 * 那边挡的是 window.open,这边挡的是渲染层显式请求。两个口子,一条规则。
 *
 * 需求(#178):终端里 ⌘+点击 `npm run dev` 打出的 `http://localhost:5173`
 * 要能跳浏览器,所以 http 也得放。真正危险的是 `file:` / 自定义协议
 * (等于「用默认应用打开任意本地路径」),这些仍然挡掉;带账号密码的也挡。
 */
export async function openExternal(url: string): Promise<void> {
  let parsed: URL | null = null
  try { parsed = new URL(url) } catch { /* 下面统一拒绝。 */ }
  if (parsed === null || (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') || parsed.username !== '' || parsed.password !== '') {
    throw new IpcError('unknown', `拒绝打开非 http(s) 链接: ${url}`)
  }
  await shell.openExternal(url)
}

let sessionWindowOpener: ((workspaceId: string, sessionId: string) => void) | null = null

export function setSessionWindowOpener(opener: (workspaceId: string, sessionId: string) => void): void {
  sessionWindowOpener = opener
}

/**
 * 渲染层确认过的「重新退出」入口。装配点也在 `main/index.ts`,和上面那个
 * opener 同一个理由:ipc 层不认识 app 的生命周期。
 *
 * ★ 用**可空**的闭包而不是让 ipc 层直接 import `QuitFlow`:注册 handler 与
 * 装配退出流程都在 `whenReady()` 里,顺序由 index.ts 保证;真到了没装配就收到
 * 这条消息的情况(理论上只有启动期极短的窗口),这里什么也不做 —— 那说明这一轮
 * 退出本来就不在跑,不需要它。
 */
let quitRequester: (() => void) | null = null

export function setQuitRequester(request: () => void): void {
  quitRequester = request
}

/** 用户已经在应用自己的对话框里选完了,别让 `beforeunload` 再挡一次退出。 */
export function requestQuit(): void {
  quitRequester?.()
}

export function copyText(text: string): void {
  clipboard.writeText(text)
}

/**
 * 渲染层递来的 base64 图 → 字节。**两条写图片的通道(剪贴板 / 另存为)共用这一道**。
 *
 * ★ 校验必须留在主进程:这条链路对插件视图同样可达(走的是同一个 preload 桥),
 * 不校验就等于「谁都能让主进程往剪贴板或磁盘里放一段由它决定的内容」——
 * 路径仍由用户选,内容却不该是一段没检查过的东西。
 * ★ 两条通道各写一份的话,迟早只有一条被加强,而弱的那个留着的正是这个口子。
 */
function decodeImageBase64(base64: string): Buffer {
  if (!/^[A-Za-z0-9+/]+={0,2}$/.test(base64) || base64.length < 32) {
    throw new IpcError('unknown', '图片数据无效')
  }
  return Buffer.from(base64, 'base64')
}

/**
 * 把一张图写进系统剪贴板(生成图卡片上的「复制」)。
 *
 * ★ **一律以 PNG 落进剪贴板,原字节不改内容、只换容器。** 粘贴这条路要跨出本应用,
 * 目标程序认的是平台粘贴板里的标准类型:png / jpeg 是四家都认的两条,而
 * `image/webp`(上游爱给)在 macOS 的粘贴板上没有对应类型 —— 直接写 webp 字节,
 * 用户会在「另一个应用里粘出来是空的」那一刻才发现,而那时已经对不上这次点击了。
 * nativeImage 的 `toPNG()` 对 png/jpeg 是纯解码再编码,像素一个不差。
 * ★ 解不开的格式直接报错(见 `if (png.length === 0)`):写进去一段谁也读不出的
 * 字节比当场说一声「没成」糟得多。
 */
export async function copyImage(base64: string): Promise<void> {
  const png = nativeImage.createFromBuffer(decodeImageBase64(base64)).toPNG()
  // 空图不会让 clipboard.write 报错,只是粘出来什么都没有 —— 在这里就拦住它
  if (png.length === 0) throw new IpcError('unknown', '图片数据无效')
  // ★ 里面那层 `new Uint8Array(...)`:Buffer 的底层是 ArrayBufferLike(可能是
  // SharedArrayBuffer),而 BlobPart 只收 ArrayBuffer 后端的视图 —— 不套这一层
  // 类型上就过不去(同 kernel/image-gen.ts 的 bytesOfSource)。
  const blob = new Blob([new Uint8Array(png)], { type: 'image/png' })
  await clipboard.write([new ClipboardItem({ 'image/png': blob })])
}

/**
 * 另存为。渲染层给的是**文件名建议**,不是路径 —— 落点由用户在系统对话框里定,
 * 所以这条通道不需要工作区边界校验:能写到哪儿是系统对话框说了算。
 *
 * ★ 建议名要过一遍清洗。它来自模型输出(导出时取回复首行当标题),里面出现
 * `/` 或 `..` 时 `join` 会把默认落点悄悄挪到别的目录 —— 用户在对话框里
 * 未必看得出来自己正要存到哪。取消返回 null,调用方据此区分「没存」和「存失败」。
 */
export async function saveTextFile(req: { defaultName: string; text: string }): Promise<{ path: string } | null> {
  const safeName = req.defaultName.replace(/[/\\:*?"<>|]/g, '_').slice(0, 120) || 'export.md'
  const result = await dialog.showSaveDialog({
    title: '导出为 Markdown',
    defaultPath: join(app.getPath('downloads'), safeName),
    filters: [{ name: 'Markdown', extensions: ['md'] }]
  })
  if (result.canceled || !result.filePath) return null
  await writeFile(result.filePath, req.text, 'utf8')
  return { path: result.filePath }
}

/**
 * 存一张图片到用户挑的位置。渲染层给的是原图字节的 base64,主进程负责落盘 ——
 * 和 `saveTextFile` 同一条约定:**路径由 showSaveDialog 产出,渲染层永不指定任意路径**。
 *
 * ★ **写下去的是原字节,扩展名跟着字节走**(`imageMimeOfBytes`:png/jpeg/gif/webp,
 * 与转录里 `ToolOutputImage.mime` 是同一张表、同一条判据)。原先扩展名与过滤器都
 * 写死成 PNG,因为当时唯一的调用点是渲染层画出来的邀请海报;生成图接上之后,
 * 一张 jpeg/webp 会顶着 `.png` 落盘 —— 内容没错、名字撒谎,而部分看图工具按扩展名
 * 挑解码器,表现是「存下来的图打不开」,且没人会想到是文件名的问题。
 *
 * ★ 认不出来的字节落成 `.bin`(同 `extOfMime` 对未知 mime 的处理),不猜成 `.png`:
 * 猜错的那一个会被当成 PNG 去解,失败发生在用户双击它的时候。
 *
 * ★ base64 在这里校验一次再解码,理由见 `decodeImageBase64`。
 */
export async function saveImageFile(req: {
  defaultName: string
  base64: string
}): Promise<{ path: string } | null> {
  const bytes = decodeImageBase64(req.base64)
  const sniffed = imageMimeOfBytes(bytes)
  const ext = sniffed === null ? '.bin' : extOfMime(sniffed)
  // 清洗后什么都不剩(空串 / 全是被换掉的那些字符)时给一个兜底名 ——
  // 否则存出来的是个名叫 `.png` 的隐藏文件(原先那份写死 `'image.png'`,同理)
  const clean = req.defaultName.replace(/[/\\:*?"<>|]/g, '_').slice(0, 120) || 'image'
  const safeName = withExtension(clean, ext)
  const result = await dialog.showSaveDialog({
    title: '保存图片',
    defaultPath: join(app.getPath('downloads'), safeName),
    filters: [{ name: ext.slice(1).toUpperCase(), extensions: [ext.slice(1)] }]
  })
  if (result.canceled || !result.filePath) return null
  await writeFile(result.filePath, bytes)
  return { path: result.filePath }
}

/**
 * 让建议名以 `ext` 结尾(`.png` / `.jpg` / …)。
 *
 * ★ 又是补、又是换:调用方给的建议名可能不带扩展名(生成图那张卡只给序号),
 * 也可能带着一个与**实际字节**不符的扩展名 —— 后者正是这条频道原先的毛病,
 * 而它在存下来的那一个文件上才现形,界面上看不出任何异常。
 */
function withExtension(name: string, ext: string): string {
  return name.toLowerCase().endsWith(ext) ? name : `${name.replace(/\.[A-Za-z0-9]+$/, '')}${ext}`
}

export function openSessionWindow(req: { workspaceId: string; sessionId: string }): void {
  if (sessionWindowOpener === null) throw new IpcError('unknown', '暂时无法打开新窗口')
  sessionWindowOpener(req.workspaceId, req.sessionId)
}

/** 跟随系统时,系统切换深浅色要能推到所有窗口 —— 以及订阅了主题的插件 */
export function registerThemeBridge(): void {
  nativeTheme.on('updated', () => {
    if (store.getSettings().theme !== 'system') return
    const resolved = nativeTheme.shouldUseDarkColors ? 'dark' : 'light'
    windows.emitToAll('theme:changed', { resolved })
    notifyPluginsThemeChanged(resolved)
  })
}
