/**
 * KernelHost 之外唯一允许 import electron 的地方之一(窗口/生命周期)。
 * 内核代码永远不从这里 import —— 依赖方向是单向的:main → kernel,不反向。
 */
import { dirname, join } from 'node:path'
import { copyFileSync, cpSync, existsSync, mkdirSync, statSync } from 'node:fs'
import { app, shell, BrowserWindow, nativeImage, powerMonitor } from 'electron'
import { electronApp, is, optimizer } from '@electron-toolkit/utils'
import appIconPath from '../../resources/icon.png?asset'
import {
  closeDatabase,
  DATABASE_DIRNAME,
  DATA_SUBDIRNAME,
  DB_FILENAME,
  defaultProfileRoot,
  openDatabase,
  peekThemePreference
} from './db'
import { probeSqlite, type SqliteProbeResult } from './db/probe'
import { electronHost, migrateLegacyCredentials } from './host'
import { installProductionBrowserBindings, type BrowserBindings } from './browser/bindings'
import {
  flushPendingPersists,
  prepareStoredAccountScope,
  reconcileMigratedWorkspacesForStoredAccount,
  registerIpc,
  registerMigrationIpc,
  shutdownClientAuth,
  shutdownRuns,
  shutdownTerminals
} from './ipc'
import { sweepPendingDelete } from './ipc/pending-delete'
import { shutdownImports } from './imports/service'
import { resumeImportSync, startImportSync, stopImportSync } from './imports/sync'
import { resumeUsageRollup, startUsageRollup, stopUsageRollup } from './usage/rollup-task'
import { installAttachmentProtocol, registerAttachmentScheme } from './net/attachment-protocol'
import { installWidgetProtocol, registerWidgetScheme } from './net/widget-protocol'
import { installPluginProtocol, registerPluginScheme, setPluginAppearanceResolver } from './plugin/protocol'
import { shutdownPlugins, startPlugins } from './ipc/plugins'
import { applyProxy, installProxyAuth } from './net/proxy'
import { initRuntime, shutdownMcp, shutdownSessionTitles, shutdownEnvironments } from './runtime'
import { GLOBAL_SETTINGS_FILENAME } from './kernel/local-settings'
import { PROFILE_DIRECTORY_SEGMENT } from './db/config-profile'
import { migrateFlatLayout, rewriteMigratedPaths } from './db/flat-layout'
import { createMigrationGate, type MigrationGate } from './db/startup-migration'
import {
  announceIpcReady,
  announceMigrationState,
  installMigrationGate,
  setMigrationDataChangedListener
} from './ipc/data-migration'
import { CHROMIUM_SUBDIRNAME, migrateChromiumIntoSubdir } from './db/chromium-layout'
import { installUserAgent } from './kernel/user-agent'
import { installBundledSkills } from './kernel/skill/bundled'
import { SKILLS_DIR } from './kernel/skill/load'
import { store } from './state/store'
import { initTray, destroyTray } from './tray'
import { windows } from './window/registry'
import { QuitFlow } from './quit-flow'
import { titleBarOptions, watchMaximized } from './window/title-bar'
import { applyThemePreference, resolveTheme, setQuitRequester, setSessionWindowOpener } from './ipc/app'
import { updateService } from './update/update-service'
import { reconcileScheduler, startScheduler, stopScheduler } from './scheduled/scheduler'
import { reconcileGateway, shutdownGateway } from './ipc/gateway'
import { setAccountGatewayReconciler } from './account-switch'

// ─────────────────────────────────────────────────────────────────────────────
// 单实例锁 —— 必须在 whenReady 之前。方案 §9:两个实例开同一个 SQLite 文件,
// 即使有 WAL 也会打架。第一天就加,后补要动启动时序。
// ─────────────────────────────────────────────────────────────────────────────
const gotTheLock = app.requestSingleInstanceLock()
if (!gotTheLock) {
  app.quit()
}

// 出站 API 请求的自报家门。哪些请求带、哪些**刻意不带**,那张表在 kernel/user-agent.ts。
// 版本号只有主进程拿得到,所以装配点在这里;`app.getVersion()` 不要求 ready。
installUserAgent(app.getVersion())

/*
  退出流程的全部状态都在 `quitFlow` 里 —— 两段式、兜底时限、以及「渲染层顶住
  unload 时整次退出作废」的规则,连同这么写的理由都在 `quit-flow.ts` 的文件头。

  ★ 这里只留一个绑定,是因为**它必须在 `app.whenReady()` 之前就存在**:托盘、
  dock、`second-instance` 都可能在启动还没跑完时被点,而那些路径全都要问
  「现在是不是在退出」。真正的装配在 `app.on('before-quit')` 那段。
*/
let quitFlow: QuitFlow | null = null
let browserBindings: BrowserBindings | null = null

/**
 * 命令行显式给过 `--user-data-dir` 吗?
 *
 * ★ 这是**唯一**的数据根逃生口,也是 `scripts/` 下那十来个 Electron 探针赖以
 * 隔离的机制。以前它们靠 mkdtemp 出来的 cwd 隔离数据(数据根从 cwd 派生),
 * 数据根改成主目录之后那条路断了 —— 不认这个开关的话,每一次跑探针都会直接
 * 读写用户真实的 `~/.next-cowork`。
 *
 * 读 `process.argv` 而不是 `app.commandLine.hasSwitch`:这段代码在 ready 之前
 * 就要执行,argv 是此刻唯一保证已就绪的来源。
 */
const explicitUserDataDir = process.argv.some(
  (arg) => arg === '--user-data-dir' || arg.startsWith('--user-data-dir=')
)

/**
 * setPath 之前的 Electron 默认 userData(`%APPDATA%\NextCoWork` /
 * `~/Library/Application Support/NextCoWork`)—— 旧版**打包安装**的数据在这儿,
 * 迁移要从它搬。★ 必须在下面那次 setPath 之前取,取晚了拿到的是新根。
 */
const legacyUserDataPath = app.getPath('userData')

/**
 * Electron profile 根 —— Chromium 的 userData 指这里。会话集(`Cache` / `Cookies` /
 * `Local Storage` / `Network` / `Partitions` …)由下面那次 `setPath('sessionData')`
 * 收进根下的 `chromium/` 子目录;`Preferences` / `Local State` / `Crashpad` 这几样
 * **不是**会话数据,仍扁平躺在根层(见 `db/chromium-layout.ts`)。
 *
 * `~/.next-cowork`,dev 与打包**同一个**。
 *
 * ★ 不能从 `process.cwd()` 派生:发行版的 cwd 是没有意义的 —— Windows 从快捷方式
 * 启动时它是**安装目录**,数据会在卸载/升级时被一起清掉;从别的目录双击 exe
 * 又会凭空开出一个空库。用户看到的是「我的会话全没了」,而不是任何一条能指向
 * 工作目录的线索。
 */
function resolveProfileRoot(): string {
  return explicitUserDataDir ? legacyUserDataPath : defaultProfileRoot()
}

/**
 * 数据根 —— SQLite 主库、附件、settings.json、skills / agents / commands 文件树
 * 全部从这里派生。profile 根下的 `data/` 子目录。
 *
 * ★ 它必须是 profile 根的**子目录**,不能挪到别处去。`ipc/storage.ts` 的
 * `clearLocalData` 要在一次可回滚的 rename 里同时搬走两边的东西,而
 * `stageManagedPath` 的边界断言只认一个根 —— 父子关系让 Chromium 条目和应用
 * 条目同时落在界内。搬成兄弟目录,「删除全部数据并退出」会直接抛错整体回滚,
 * 而不是少删一点。
 */
function resolveDataRoot(): string {
  return join(resolveProfileRoot(), DATA_SUBDIRNAME)
}

// Electron profile 落在 profile 根;应用数据在它下面的 data/。
// 必须在 app ready 之前设置才生效。
// 显式传了 --user-data-dir 时不覆盖 —— 那正是调用方要的隔离。
if (!explicitUserDataDir) {
  const profileRoot = resolveProfileRoot()
  app.setPath('userData', profileRoot)
  /*
    把根层的会话集收进 `chromium/`,再让 Chromium 从那里读写。两步都**必须在
    app ready 之前**:那之后 Chromium 立刻握住 Cookies / Cache 的句柄,Windows 上
    就再也 rename 不动了(与紧随其后的 sweepPendingDelete 同一个时间窗)。迁移只在
    首次(`chromium/` 尚不存在且根层还留着会话条目)真正搬东西,之后零成本掠过。
    探针路径(--user-data-dir)刻意不走这套,保持旧扁平布局,免得无意改动真实库。
  */
  migrateChromiumIntoSubdir(profileRoot)
  app.setPath('sessionData', join(profileRoot, CHROMIUM_SUBDIRNAME))
}

/*
  上一次「删除并退出」留下的补删清单。

  ★ **必须在 `app.whenReady()` 之前**,而且要紧跟着 setPath —— 唯一能删掉 Chromium
  profile 目录的时刻就是现在:app ready 之后 Chromium 立刻把 `GPUCache` / `Cookies` /
  `Network` 这些打开并一直握着句柄,Windows 上就再也删不动了(那正是这份清单存在的
  原因,见 `ipc/pending-delete.ts`)。晚一步,清单只会一轮轮攒下去。

  没有清单时这是一次读文件失败,代价可以忽略;它自己吞掉所有异常,不会挡住启动。
*/
sweepPendingDelete(resolveProfileRoot())

/*
  ★ **必须在 `app.whenReady()` 之前** —— 与单实例锁、userData 改路径同属
  「ready 之前才有效」的那一类。

  放到 ready 之后不会报任何错,协议照样注册得上,但 `standard` / `secure` /
  `stream` 这些 privileges 会**全部丢失**。症状是彼此看起来毫无关联的一组故障:
  图片有时能显示、`fetch('ncw://…')` 报 CORS、视频不能拖进度条 ——
  没有一条会指向「注册时机不对」。
*/
registerAttachmentScheme()
/*
  ★ 和上面那行一样,必须排在 `app.whenReady()` **之前**。放到 ready 之后不报错,
  但 privileges 全部丢失 —— 表现是插件页面的 CSP、fetch、模块加载各自以
  看起来彼此无关的方式失败。
*/
registerPluginScheme()
/*
  ★ 第三条自定义 scheme,理由同上:必须在 `app.whenReady()` **之前**。
  内置可视化 widget 的外壳页面走它(`net/widget-protocol.ts`)——
  放到 ready 之后,外壳页面能加载但 CSP 与沙箱的 privileges 全丢。
*/
registerWidgetScheme()

// 步骤 0 的 sqlite 探针。留着不删:将来升 Electron 大版本时,
// 它是第一个会告诉你出事的地方(方案 §9)。
function logStartupProbe(): void {
  void probeSqlite().then((r: SqliteProbeResult) => {
    if (r.ok) {
      console.log(
        `[db] node:sqlite OK · sqlite ${r.sqliteVersion} · ` +
          `fts5 ${r.fts5 ? '✓' : '✗'} · json1 ${r.json1 ? '✓' : '✗'} · rtree ${r.rtree ? '✓' : '✗'}`
      )
    } else {
      console.error(`[db] node:sqlite 不可用 —— 需退回 better-sqlite3。原因: ${r.reason}`)
    }
    console.log(
      `[app] electron ${process.versions.electron} · node ${process.versions.node} · chrome ${process.versions.chrome}`
    )
  })
}

function isSafeBrowserUrl(raw: string): boolean {
  try {
    const parsed = new URL(raw)
    return (
      (parsed.protocol === 'http:' || parsed.protocol === 'https:') &&
      parsed.username === '' &&
      parsed.password === ''
    )
  } catch {
    return false
  }
}

function createMainWindow(sessionRoute?: { workspaceId: string; sessionId: string }): BrowserWindow {
  const win = new BrowserWindow({
    width: 1280,
    height: 820,
    minWidth: 900,
    minHeight: 600,
    show: false,
    autoHideMenuBar: true,
    /*
      ★ **跟着用户的主题偏好走,不能写死。** 这个色是窗口在渲染层画出第一帧之前
      露出来的底色 —— 写死深色的话,浅色用户看到的是「深色空块 → 界面出来 → 啪一下
      变浅」。两个值取自 theme.css 里的 `--color-app`(深 #2a2d2b / 浅 #f2eee6),
      改那边记得同步这里。

      ★ **不能用 `store.getSettings()`。** 建窗现在排在启动迁移闸门之前,而闸门
      存在的意义正是「数据库还没打开」—— 在这里读库会先把库以内存兜底方式打开,
      随后真正的 `openDatabase()` 直接抛错,启动死在那里。
      `peekThemePreference()` 自己开一个只读连接,不碰那个句柄。
    */
    backgroundColor: resolveTheme(peekThemePreference(join(resolveDataRoot(), DB_FILENAME))) === 'light' ? '#f2eee6' : '#2a2d2b',
    // Windows/Linux 的任务栏与窗口图标(macOS 忽略,那边走下面的 app.dock.setIcon)
    icon: nativeImage.createFromPath(appIconPath),
    // macOS:红绿灯嵌进侧边栏(方案 §8)。Windows/Linux:只去掉系统标题栏,
    // 三颗按钮由渲染层自绘 —— 两边的取舍写在 window/title-bar.ts 的文件头。
    ...titleBarOptions(),
    webPreferences: {
      preload: join(__dirname, '../preload/index.js'),
      // ★ 三件套。sandbox: true 是本项目 preload 必须完整打包成单文件的原因
      // (沙箱下无法 require 多文件),见 electron.vite.config.ts。
      sandbox: true,
      contextIsolation: true,
      nodeIntegration: false,
      // Browser tabs use Electron's isolated <webview> element. It remains
      // sandboxed and nodeIntegration-free; enabling the tag does not expose
      // the host preload bridge to page content.
      webviewTag: true
    }
  })

  win.on('ready-to-show', () => win.show())

  /*
    关闭按钮只隐藏,不销毁窗口 —— 保留页面状态(当前会话/滚动位置/未保存的输入),
    靠托盘图标唤回。

    ★ 退出流程问的是 `quitFlow.inProgress`,**不是「进程正在退出」**:用户点关闭
    按钮时进程当然没在退出,所以照旧隐藏;而 `Cmd+Q` / 托盘「退出」时它已经在
    关窗那一段里,于是放行真正的销毁。这两种「正在关」必须区分开,否则
    `Cmd+Q` 只会把窗口藏起来、进程留在 Dock 里不走。
  */
  win.on('close', (event) => {
    if (quitFlow?.inProgress === true) return
    event.preventDefault()
    win.hide()
  })

  /*
    ★ 渲染层顶住了这次 unload —— 有未保存的文件,而用户还没在应用自己的
    「有未保存的改动」对话框上做决定(`DocumentDialogs`)。

    **刻意不调 `event.preventDefault()`**:那个 API 的语义是「无视 beforeunload,
    照样把页面卸掉」,也就是**静默丢弃用户的未保存改动**。让它否决生效、整次退出
    作废才对:此刻停服务、封库都还没做,作废之后应用完全可用(见 `quit-flow.ts`
    文件头那段两段式)。用户随后在对话框里选完,渲染层会重发一次退出。
  */
  win.webContents.on('will-prevent-unload', () => {
    quitFlow?.veto()
  })

  // 任何 window.open / target=_blank 一律不在应用内开新窗口,交给系统浏览器。
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (/^https?:\/\//i.test(url)) void shell.openExternal(url)
    // Renderer-controlled session links open another app window, while all
    // other external targets continue to use the system browser policy.
    const appUrl = win.webContents.getURL().split('#')[0]
    const devUrl = process.env['ELECTRON_RENDERER_URL']
    if ((typeof appUrl === 'string' && appUrl !== '' && url.startsWith(appUrl)) || (typeof devUrl === 'string' && url.startsWith(devUrl))) {
      return { action: 'allow' }
    }
    return { action: 'deny' }
  })

  // Browser workbench pages run in a separate, sandboxed webview session.
  // Never allow a page to inherit the app preload or load local/custom-scheme
  // resources. Links opened by a page leave through the system browser.
  win.webContents.on('will-attach-webview', (event, webPreferences, params) => {
    delete webPreferences.preload
    webPreferences.nodeIntegration = false
    webPreferences.contextIsolation = true
    webPreferences.sandbox = true
    webPreferences.disableDialogs = true
    webPreferences.webSecurity = true
    webPreferences.allowRunningInsecureContent = false
    if (typeof params.src === 'string' && params.src !== '' && !isSafeBrowserUrl(params.src)) event.preventDefault()
  })

  win.webContents.on('did-attach-webview', (_event, contents) => {
    contents.setWindowOpenHandler(({ url }) => {
      if (isSafeBrowserUrl(url)) void shell.openExternal(url)
      return { action: 'deny' }
    })
    const guardNavigation = (event: Electron.Event, url: string): void => {
      if (!isSafeBrowserUrl(url)) event.preventDefault()
    }
    contents.on('will-navigate', guardNavigation)
    contents.on('will-redirect', guardNavigation)
    contents.session.setPermissionCheckHandler(() => false)
    contents.session.setPermissionRequestHandler((_webContents, _permission, callback) => callback(false))
  })

  // 阻止渲染层被导航到站外(拖入链接、意外的 location 赋值)。
  win.webContents.on('will-navigate', (event, url) => {
    const devUrl = process.env['ELECTRON_RENDERER_URL']
    if (devUrl && url.startsWith(devUrl)) return
    event.preventDefault()
  })

  // 先登记再加载:渲染层的 window:ready 一到就要能查到 kind。
  // 登记晚了,markReady 会走 of() 的兜底分支,kind 判断不准。
  windows.register(win.webContents, 'main')

  // 自绘的最大化/还原按钮要跟着窗口的**真实**状态走 —— 用户也可能拖窗口边缘、
  // 双击 Tab 条、按 Win+↑。挂在 register 之后:push 走的就是 registry。
  watchMaximized(win)

  /*
    路由写在 hash 里:`#/{workspaceId}` 是「这个工作区,新对话」,
    `#/{workspaceId}/{sessionId}` 是「这一段会话」。渲染层进来后按它落位,
    之后 hash 一直跟着激活的 Tab 走(见 `renderer/shell/AppShell.tsx`)。
    ★ `loadFile` 的 hash 参数**不带 `#`**,Electron 自己加。
  */
  const route = sessionRoute === undefined
    ? undefined
    : `/${encodeURIComponent(sessionRoute.workspaceId)}/${encodeURIComponent(sessionRoute.sessionId)}`
  if (is.dev && process.env['ELECTRON_RENDERER_URL']) {
    void win.loadURL(`${process.env['ELECTRON_RENDERER_URL']}${route === undefined ? '' : `#${route}`}`)
  } else {
    void win.loadFile(join(__dirname, '../renderer/index.html'), route === undefined ? undefined : { hash: route })
  }

  return win
}

/**
 * 旧数据根 —— 数据根搬到 `~/.next-cowork` 之前,用户的库可能在两个地方:
 *
 * | 旧根 | 谁在用 |
 * |---|---|
 * | `legacyUserDataPath` | 旧版**打包安装**(`~/Library/Application Support/NextCoWork` 等) |
 * | `<cwd>/.next-cowork` | 旧版**开发运行** |
 *
 * 两个都有时按 `nextcowork.db` 的 mtime 选最新的那个 —— 固定顺序会在开发机上
 * 稳定地搬错一份(「我昨天那些会话呢」),而 mtime 就是「上一次在用的是哪一份」。
 *
 * ★ 打包时**不把 cwd 列进候选**。发行版的 cwd 是随机的(从快捷方式启动时是安装
 * 目录),那里碰巧存在一个 `.next-cowork` 就会把一份毫不相干的数据搬进来。
 */
function pickLegacyRoot(targetDir: string): string | null {
  // 显式传了 --user-data-dir = 调用方要的就是一个干净的隔离根,一个字节都不要搬。
  if (explicitUserDataDir) return null

  const candidates = [legacyUserDataPath]
  if (!app.isPackaged) candidates.push(join(process.cwd(), DATABASE_DIRNAME))

  let best: { root: string; mtimeMs: number } | null = null
  for (const root of candidates) {
    // 升级路径上两者可能已经重合(比如 cwd 正好是主目录),那就没什么可搬的。
    if (root === targetDir) continue
    try {
      const legacyDb = join(root, DB_FILENAME)
      if (!existsSync(legacyDb)) continue
      const { mtimeMs } = statSync(legacyDb)
      if (best === null || mtimeMs > best.mtimeMs) best = { root, mtimeMs }
    } catch {
      // 读不到的候选直接跳过 —— 迁移是尽力而为,不能让它挡住启动。
    }
  }
  return best === null ? null : best.root
}

/**
 * 首次切换到新数据根时,把旧根整棵搬过来。
 * 只在目标库不存在时**复制**(不是移动):旧目录留着当回滚兜底,
 * 也绝不覆盖用户已经在新根生成的库。
 *
 * ★ 搬的不只是库文件。`settings.json`(全局钩子)、`commands/` / `skills/` /
 * `agents/`(用户自己写的扩展)、`attachments/`、`workspaces/` 漏掉任何一样,
 * 用户的表现都是「升级之后我的东西没了」,而日志里不会有半个字。
 *
 * ★ **不搬 Chromium profile**(Cookies / Local Storage)。代价是浏览器工具的
 * 登录态重置一次,换来的是不用去拷一棵 Chromium 自己管着的、带锁文件的目录树。
 */
function prepareProjectDatabaseDirectory(): string {
  const targetDir = resolveDataRoot()
  const targetPath = join(targetDir, DB_FILENAME)
  if (existsSync(targetPath)) return targetDir

  /*
    先看扁平布局 —— 同一个 profile 根下直接躺着一个库,那是上一个版本留下的。
    它优先于下面那两个旧根:那两个是**别的目录**里的老数据,而这个就是用户
    此刻正在用的这一份,只是层级不对。
    ★ 探针模式(--user-data-dir)也走这条:它不引入任何外部数据,只是把同一个
      根里的布局升上来。scripts/segmented-probe.mjs 直接把真实数据根传进来。
  */
  const profileRoot = resolveProfileRoot()
  if (migrateFlatLayout(profileRoot, targetDir)) {
    rewriteMigratedPaths(targetPath, profileRoot, targetDir)
    console.log(`[db] 已把扁平布局的数据从 ${profileRoot} 收进 ${targetDir}`)
    return targetDir
  }

  const legacyRoot = pickLegacyRoot(targetDir)
  if (legacyRoot !== null) {
    const legacyPath = join(legacyRoot, DB_FILENAME)
    mkdirSync(targetDir, { recursive: true })
    copyFileSync(legacyPath, targetPath)
    for (const suffix of ['-wal', '-shm']) {
      const source = `${legacyPath}${suffix}`
      if (existsSync(source)) copyFileSync(source, `${targetPath}${suffix}`)
    }
    /*
      Move application-owned file trees alongside the copied database. The
      attachment rows contain absolute paths, so rewrite those references in
      the copied database before the normal migration runner opens it.

      ★ `force: false` = **合并,但一个已有文件都不覆盖**,而不是「目标目录存在就整棵跳过」。
      新根在真正启动成功之前就可能已经有东西了(比如一次半途而废的运行留下的
      `skills/`,或者内置技能的安装),那时候整棵跳过就等于把用户自己写的技能
      静静丢掉 —— 而日志里一个字都不会有。
    */
    const managedDirs = [
      'attachments',
      'skills',
      'agents',
      'commands',
      'modes',
      'workspaces',
      'plugins',
      PROFILE_DIRECTORY_SEGMENT
    ]
    for (const name of managedDirs) {
      const source = join(legacyRoot, name)
      if (existsSync(source)) cpSync(source, join(targetDir, name), { recursive: true, force: false })
    }
    // 全局指令与全局设置(目前只有钩子)是单文件,和上面那些目录同等重要。
    for (const name of ['AGENTS.md', GLOBAL_SETTINGS_FILENAME]) {
      const source = join(legacyRoot, name)
      const target = join(targetDir, name)
      if (existsSync(source) && !existsSync(target)) copyFileSync(source, target)
    }
    // Legacy themes lived beside the old database; their new canonical home
    // is the shared attachments/themes subtree.
    const legacyThemes = join(legacyRoot, 'themes')
    const targetThemes = join(targetDir, 'attachments', 'themes')
    if (existsSync(legacyThemes)) {
      mkdirSync(dirname(targetThemes), { recursive: true })
      cpSync(legacyThemes, targetThemes, { recursive: true, force: false })
    }
    rewriteMigratedPaths(targetPath, legacyRoot, targetDir)
    // 搬了哪一份是排查这条路径时第一个要问的问题,所以两个根都打出来。
    console.log(`[db] 已将旧数据从 ${legacyRoot} 迁移到 ${targetDir}`)
  }
  return targetDir
}

/**
 * 启动迁移闸门要检查的旧数据根,**按优先级排**。
 *
 * 三个来源,后两个与 `pickLegacyRoot()` 用的是同一份判据:
 *
 * 1. **profile 根本身。** ★ 这一条是关键补充。`pickLegacyRoot()` 的候选里没有它,
 *    而同一个状态在两段代码里含义不同:`migrateFlatLayout` 认为「根下躺着一个库」
 *    是待迁移的旧布局,`pickLegacyRoot` 不认它。本机就是这么丢的数据 ——
 *    `data/` 下先被建了一个库,之后用户继续在根层的库上工作几小时,
 *    下一次启动时新库已是权威,那几小时的会话永远不会出现,而日志里没有线索。
 * 2. 旧版打包安装的数据根。
 * 3. dev 运行时 cwd 下的旧根。打包时**不列**(发行版的 cwd 是随机的)。
 */
function legacyMigrationSources(targetDir: string): Array<{ root: string; databasePath: string }> {
  // 显式传了 --user-data-dir = 调用方要的就是一个干净的隔离根,一个字节都不要搬。
  if (explicitUserDataDir) return []
  const roots = [resolveProfileRoot(), legacyUserDataPath]
  if (!app.isPackaged) roots.push(join(process.cwd(), DATABASE_DIRNAME))
  const seen = new Set<string>()
  const sources: Array<{ root: string; databasePath: string }> = []
  for (const root of roots) {
    if (root === targetDir || seen.has(root)) continue
    seen.add(root)
    sources.push({ root, databasePath: join(root, DB_FILENAME) })
  }
  return sources
}

/*
  ★ 回调是 `async`,因为中间要 `await` 一次启动迁移闸门。`await` 之前的一切照旧
  同步执行(建窗、托盘都还在闸门之前),所以这个改动不动任何既有顺序。

  ★ 末尾补了 `.catch`。以前这里没有 —— 启动路径上一旦抛出就是一个 unhandled
  rejection,而 Electron 主进程对它的处理是「打一行警告然后继续跑」,
  于是应用会停在一个半初始化的状态上。现在至少是一条指名道姓的错误。
*/
void app
  .whenReady()
  .then(async () => {
  electronApp.setAppUserModelId('com.nextcowork.app')

  // ★ dev 与未打包运行时的 dock 图标。打包后的 .app 由 electron-builder 从
  // build/icon.png 生成 icns 内嵌,但 `electron-vite dev` / 直接跑 out/ 都不经过
  // electron-builder —— 那两种情况下 dock 显示的是 Electron 二进制自带的原子图标。
  // 这一句把 dock 图标在所有运行方式下统一成我们自己的(打包后再设一次也无副作用)。
  if (process.platform === 'darwin') {
    app.dock?.setIcon(nativeImage.createFromPath(appIconPath))
  }

  app.on('browser-window-created', (_, window) => {
    // dev 下 F12 开 devtools、生产下屏蔽 CommandOrControl+R
    optimizer.watchWindowShortcuts(window)
  })

  logStartupProbe()

  /*
    ★ **在闸门之前、建窗之前先登记闸门那几条频道。** 窗口加载完就会立刻
    invoke `dataMigration:getState`,而那一刻闸门正在跑、数据库还没打开。
    完整的 `registerIpc()` 要等闸门放行才能调(它会拉起导入服务、扫孤儿文件,
    全都要库),所以这几条必须单独先登记。

    ★ 顺序反了的症状是:窗口起来了、迁移屏永远是空白(那一次 invoke 拿到
    「未登记的频道」),而用户看到的是一个「什么都没发生」的启动。
    放在建窗之前是为了不依赖「窗口的加载一定晚于这一行」这个时序假设。
  */
  registerMigrationIpc()

  /*
    ★ **建窗在闸门之前。** 迁移可能跑好几分钟,用户必须能看见它在跑;而这一屏
    自己需要的四条 IPC(`dataMigration:*`)是唯一在闸门期间可用的频道。

    这个窗口此刻是**被冻结**的:App 的握手 effect 要等 `MigrationGateHost`
    放行才会跑(见 renderer/main.tsx),所以它不会去碰还没打开的数据库。

    ★ 放行的条件有两半,**缺一不可**:闸门里没有要迁移的东西,以及主进程已经
    答得上来了(`announceIpcReady()` 那一位)。只有前一半的话,没有迁移、渲染层
    又起得比主进程快的那些启动会直接拿「未登记的频道」把首屏置成握手失败 ——
    见 `shared/domain/data-migration.ts` 的 `ipcReady`。
  */
  createMainWindow()

  // 菜单栏托盘。放在建窗之后:它的「显示窗口」要能拿到已经存在的那个窗口。
  initTray(showMainWindow)

  // dock 图标点击也走同一套「唤回」逻辑——窗口关闭后是隐藏不是销毁,
  // 所以这里几乎不会撞见 `length === 0` 的分支,但保留它兜底手动 destroy() 的情况。
  app.on('activate', showMainWindow)

  /*
    ★ **启动迁移闸门。** 位置是死的:必须在 `prepareProjectDatabaseDirectory()`
    之前(它是「目标库不存在就整体拷」,而闸门是「目标库已存在之后」那条路),
    也必须在 `openDatabase()` 之前(迁移完才轮到开库)。

    ★ 它必须 `await`。放进 `.then()` 里跑的话,下面那些初始化会在迁移进行到一半时
    开始摸库 —— 表现是「迁移成功了,但设置全丢了」,因为那些写入落进了一个
    内存兜底库(见 `openDatabase()` 在重复调用时的抛错,那是同一类时序问题的
    另一道闸)。

    ★ 失败时**不停在闸门里**:`prepareProjectDatabaseDirectory()` 和 `openDatabase()`
    照常往下走。理由有两条:(1) 用户可能已经在错误页上选了「跳过并继续」;
    (2) 闸门挂了(比如建不出来)不该等于应用起不来 —— 那种情况下最坏的后果是
    旧数据这次没合并,而下次启动会再试一次。真正的失败细节由 `console.warn` 留下。
  */
  await startMigrationGate()
  console.log(`[migration] 闸门结束:${migrationGate?.state().phase ?? 'unknown'}`)

  /**
   * ★ 必须在 `registerIpc()` 之前:渲染层握手的第一个 invoke 是 `app:getBootstrap`,
   * 而 bootstrap 要带上默认模型 —— 那是 `initRuntime` 的 seed 才填上的。
   * 顺序反了,首屏的模型选择器就是空的,直到下一次重启才对。
   *
   * 也必须在这里(而不是模块顶层)构造 host:`safeStorage` 与 `net.fetch` 都要求 app ready。
   */
  /*
    ★ **必须排在 `initRuntime()` 前面**:seed 会往 providers 表写内置上游,
    而在库打开之前碰 store 的话,那些行会落进一个内存兜底库里,
    换成文件库时凭空消失 —— 症状正好是「配置重启后没了」,也就是持久化
    根本没做时的原症状,没人会怀疑到调用顺序上来。`openDatabase()` 因此
    在重复调用时直接抛错,把这个顺序钉死。

    数据库目录是 `resolveDataRoot()`(默认 `~/.next-cowork/`,与 Electron 的
    `userData` 同一个目录);`openDatabase` 会在首次启动时自动创建它。
  */
  // SQLite 主库及应用管理的文件资源统一使用同一个用户级数据根。
  openDatabase(prepareProjectDatabaseDirectory())
  /*
    ★ 账户作用域必须在 seed / bootstrap / 建窗之前恢复。旧版升级后的库仍标着 local,
    而已保存登录态属于账户;拖到渲染层来问 clientAuth:getState 才切,首屏就会先读到
    local 的 provider/key,随后整页跳成账户配置。这里同步切一次,后面同账户调用恒等。
  */
  prepareStoredAccountScope()
  /*
    需求：失败后的迁移重试发生在应用已经启动之后，不能再依赖上面的启动期恢复。
    迁移 IPC 本身不能 import 会读库的模块，所以只在这里(开库之后)接回调；每次
    重试 / 撤销后刷新工作区与会话缓存，否则数据已变而侧边栏仍显示旧列表。
  */
  setMigrationDataChangedListener(() => {
    reconcileMigratedWorkspacesForStoredAccount()
    windows.emitToAll('workspace:changed', { workspaces: store.listWorkspaces() })
    windows.emitToAll('sessions:changed', { kind: 'reset' })
  })

  /*
    ★ 第二段。必须排在 `openDatabase` 之后 —— 附件根目录由当前数据库目录
    派生，协议读取、上传和 storage 清理必须始终指向同一棵项目级数据树。
  */
  installAttachmentProtocol()
  installPluginProtocol()
  // widget 外壳不读磁盘上的内容(它只下发两个自己写的文件),但仍然排在这里:
  // 三条 scheme 的注册时机与安装时机应该一眼看得出是同一种东西。
  installWidgetProtocol()
  /*
    ★ 协议层自己不认识 store,深浅色由这里喂进去 —— 它是插件视图垫片的初值,
    决定插件视图的**第一帧**是深是浅。排在 `openDatabase` 之后:`getSettings()`
    要读库。
  */
  setPluginAppearanceResolver(() => resolveTheme(store.getSettings().theme))

  const host = electronHost()
  const credentialMigration = migrateLegacyCredentials()
  if (credentialMigration.migrated > 0 || credentialMigration.failed > 0) {
    host.logger.info(
      `[credentials] 旧密文迁移完成:成功 ${credentialMigration.migrated},保留 ${credentialMigration.failed}`
    )
  }
  const bundledSkillsRoot = app.isPackaged
    ? join(process.resourcesPath, SKILLS_DIR)
    : join(app.getAppPath(), 'resources', SKILLS_DIR)
  for (const diagnostic of installBundledSkills(bundledSkillsRoot, join(host.paths.userData(), SKILLS_DIR))) {
    host.logger.warn(`[skill:bundled] ${diagnostic.path}: ${diagnostic.message}`)
  }
  initRuntime(host)
  // Browser IPC handlers are registered below, so their Electron/Playwright bridge must already exist.
  browserBindings = installProductionBrowserBindings(host.logger)
  /*
    ★ 插件系统在 `initRuntime` **之后**起:它要 `getHost()`。
    不 await —— 扫描插件目录是几次 readdir,但一个坏包不该让首屏等着它。
    装载结果经 `plugins:changed` 播出去,设置页照实显示。
  */
  void startPlugins()
  startScheduler()

  /*
    ★ 代理必须在**任何一次出站请求之前**装好。`initRuntime` 已经把 host 装上了,
    但它自己不发请求;第一个真的出站是渲染层握手之后的模型探活。排在这之后的话,
    那几次请求会走直连 —— 而在只有代理才能出网的网络里,表现是「刚启动那会儿
    连不上,过一会儿就好了」,一个几乎没法复现的故障。

    不 await:`setProxy` 是异步的,而 `whenReady` 这个回调是同步的。建窗和
    注册 IPC 不依赖代理装完,而 Chromium 在 setProxy 落地前发出的请求会排队。
  */
  installProxyAuth()
  void applyProxy(store.getSettings().proxy)

  /*
    ★ **必须在建窗之前。** 它把 `nativeTheme.themeSource` 设成用户在设置里选的那档,
    于是渲染层从**第一帧**起 `prefers-color-scheme` 就是对的 —— 骨架屏(首屏那几百毫秒)
    靠它选色,否则浅色用户会先看到一块深色再跳成浅色。

    在此之前 ipc/settings.ts 是 `applyThemePreference` 的唯一调用点,只在用户手动改
    设置时调。所以这行顺带修了一个既有 bug:启动时原生菜单和系统对话框不跟随用户
    选的主题,得等他去设置里拨一次才对。
  */
  applyThemePreference(store.getSettings().theme)

  /*
    契约里的每个频道在这里一次性注册完(缺一个就编译不过)。

    ★ 原先这里写的是「必须在建窗之前:渲染层的第一个 invoke 可能在窗口 show 之前就到」。
    建窗为了迁移进度屏被提到了**前面**(见上面 `createMainWindow()` 那段),所以那条约束
    不再由这个位置保证,改由渲染层那一侧保证:窗口里的 App 要等 `announceIpcReady()`
    之后才挂(见 `MigrationState.ipcReady`)。这个位置本身仍然钉死在 `openDatabase()`
    之后 —— 下面登记的那群 handler 全都要库。
  */
  setSessionWindowOpener((workspaceId, sessionId) => {
    const child = createMainWindow({ workspaceId, sessionId })
    child.once('ready-to-show', () => child.focus())
  })
  // 渲染层确认过的重新退出(见 contract 的 app:quitConfirmed)。
  setQuitRequester(() => {
    if (quitFlow === null) return
    quitFlow.begin()
    app.quit()
  })
  registerIpc()
  /*
    ★ **紧接着调,不能往后挪、更不能省。** 建窗在 `registerIpc()` 之前,所以此刻
    已经有窗口在等这一位了;漏掉的症状是窗口停在空白首屏、界面上一个错都不报
    (渲染层按 `ipcReady` 等,而它永远等不到)。也不能往前挪到 `registerIpc()`
    之前 —— 那等于让渲染层对着还没登记的 handler 握手,正是这次要修的那个错误。
  */
  announceIpcReady()
  // 需求：数据库、运行时及 IPC 广播接线齐备后才接受外部 Agent 请求。
  setAccountGatewayReconciler(reconcileGateway)
  void reconcileGateway()
  powerMonitor.on('suspend', () => { void shutdownEnvironments() })
  /*
    ★ 唤醒补扫。休眠两小时回来,30 秒定时器只会补触发一次,而这两小时里
    源侧可能积了几十个会话 —— 少这一行的表现是「合盖前导过的那些还在,
    合盖期间新增的要等很久才出现」,而用户会以为同步坏了。
  */
  powerMonitor.on('resume', () => { resumeImportSync(); resumeUsageRollup(); reconcileScheduler() })

  // 外部来源自动同步。**非阻塞**,不占启动路径;只在用户开过开关的来源上跑。
  startImportSync()

  // 用量按日汇总。同样非阻塞;统计页查询前还会各自兜一次刷新。
  startUsageRollup()

  updateService.configure()
  if (app.isPackaged) {
    setTimeout(() => { void updateService.check() }, 30_000)
    setInterval(() => { void updateService.check() }, 24 * 60 * 60 * 1000)
  }
  })
  .catch((err: unknown) => {
    /*
      ★ 走到这里意味着 `announceIpcReady()` 大概率没跑到 —— 而窗口此刻已经建出来了
      (建窗在闸门之前),于是它会**一直停在空白首屏**:闸门不放行,它对任何 invoke
      也答不上来,连一句错误都递不出去。所以这一行 console.error 是这种情况下的
      唯一线索:排查「启动后一片空白、界面无提示」时先看它,不要先怀疑渲染层。

      需求:这条路径以前能看到的是渲染层的「首屏握手失败: No handler registered」,
      那个错误指向频道注册,而真正的病因是主进程启动崩了,两者几乎没关系。
    */
    console.error('[app] 启动流程失败:', err)
  })

/**
 * 启动迁移闸门。★ 建在模块层是因为 `ipc/data-migration.ts` 的四个 handler 要拿
 * 同一个实例 —— 而它们不能反过来 import 这个文件(那个文件一被 import 就会跑
 * 整个 app 引导)。`installMigrationGate()` 是那两者之间唯一的一条线。
 */
let migrationGate: MigrationGate | null = null

/**
 * 跑一次启动迁移闸门。见 `app.whenReady()` 里那段关于位置与时序的说明。
 *
 * ★ 整个函数**不抛错**。它替掉的那段代码最大的问题就是「什么都可能发生,而且
 * 一个字都不说」—— 这里反过来:任何异常都降级成「这次不迁移」,但一定留下日志。
 */
async function startMigrationGate(): Promise<void> {
  try {
    const dataRoot = resolveDataRoot()
    const databasePath = join(dataRoot, DB_FILENAME)
    migrationGate = createMigrationGate({
      dataRoot,
      databasePath,
      sources: legacyMigrationSources(dataRoot),
      // 与 `prepareProjectDatabaseDirectory()` 用的是同一个根对。
      collapseFlatLayout: { from: resolveProfileRoot(), to: dataRoot },
      /*
        ★ 进度推给窗口。用 `emitToAll`(全局频道)而不是定向推送:迁移是全局状态,
        而且此刻窗口可能刚建出来、还没跑过 `window:ready`。
      */
      onChange: announceMigrationState
    })
    installMigrationGate(migrationGate)
    const state = await migrationGate.run()
    /*
      ★ 只有**失败**才需要留一条日志。`idle`(绝大多数启动)和 `skipped` 都是
      正常结果,每次都打一行只会让真正的异常淹在噪音里。
    */
    if (state.phase === 'failed') {
      console.warn(
        `[migration] 数据整理失败(${state.failure?.code ?? 'unknown'}),本次启动将带着部分旧数据继续:`,
        state.failure?.detail ?? ''
      )
    }
  } catch (err) {
    console.warn('[migration] 闸门未能启动,跳过本次数据整理:', err)
  }
}

/** 从托盘/dock/第二实例唤起:已有窗口就还原、显示并聚焦,一个都没有就新建一个。 */
function showMainWindow(): void {
  /*
    ★ **退出中一律不唤起。** `before-quit` 先 preventDefault,再异步收 MCP/插件/环境
    (最长 6 秒),这段时间进程还活着、还占着单实例锁、还在收 `activate` 和
    `second-instance` —— 而窗口这时已经销毁完了,于是下面走的是 `createMainWindow()`,
    它第一件事就是读设置拿主题底色,库却可能已经在 `finish()` 里封掉:

        DatabaseClosedError: 数据库已在应用退出时关闭,这次写入没有落盘
          at getSettings → createMainWindow → showMainWindow

    `electron-vite dev --watch` 每次改 `src/main/**` 都会撞上:它 `ps.kill()` 完**不等
    旧进程退出**就拉起新的,新进程抢不到锁,那次失败的抢锁就是发给旧进程的
    `second-instance`。用户看到的是一个跟热更新毫无关系的崩溃框。
    退出中被唤起本来就不该有任何效果——新界面归新进程管。

    ★ 判据是「退出已经跑完」,不再包含「正在退出」。正在退出时窗口**还在**:
    这一轮里 `win.close()` 会撞上渲染层的 `beforeunload`,而它有可能会顶住
    (用户还没在「有未保存的改动」对话框上做决定),那时整次退出会被作废 ——
    用户再点 Dock 就该把界面唤回来。旧实现在这里直接 return,于是那一下点击
    什么都不会发生。
  */
  if (quitFlow?.done === true) return

  const [win] = BrowserWindow.getAllWindows()
  if (win) {
    if (win.isMinimized()) win.restore()
    win.show()
    win.focus()
  } else {
    createMainWindow()
  }
}

// 第二个实例被拉起时,唤回已有窗口(可能正隐藏在托盘里)而不是新开一个。
app.on('second-instance', () => {
  showMainWindow()
})

/*
  退出流程的状态机。**两段式**,理由与踩过的坑都写在 `quit-flow.ts` 的文件头 ——
  那里也说明了为什么不能再把「停服务 + 封库」和「关窗」挤在同一个事件里。

  ★ **必须装在 `whenReady()` 之前**(这里就是)。`before-quit` 是全局的,而托盘、
  dock、`second-instance` 都会问 `quitFlow`,装晚了那几条路径就没有判据可用。
*/
quitFlow = new QuitFlow({
  /*
    ★ **用注册表,不用 `BrowserWindow.getAllWindows()`**:`windows` 是应用自己的
    窗口表,里面只有主窗和 ⌥Space 快捷窗。退出时把插件宿主窗一起关掉,那第三方的
    `beforeunload` 就有机会拖住退出 —— 而它连我们自己的「有未保存的改动」对话框
    都调不出来,只会变成一个退不掉的进程(见 `window/registry.ts` 的 listWindows)。
  */
  windows: () => windows.listWindows(),
  stopBackground: () => {
    // Tab 布局是防抖 500ms 落盘的(方案 §9)。退出前不 flush,用户最后一次拖出来
    // 的顺序就丢了 —— 而那正是他最可能记得的一次操作。
    flushPendingPersists()
    // ★ 停调度**在** shutdownRuns 之前:自动同步会去问「哪些 run 在跑」,
    //   而那张表正要被清空,此时起一轮新扫描等于在关灯的房间里搬东西。
    stopImportSync()
    stopUsageRollup()
    stopScheduler()
    // 需求：先停止接受外部请求，再中断 run，避免退出期间启动新的任务。
    shutdownGateway()
    // 同理:登录态刷新(5 分钟)与配置同步(5 秒)都是 unref 过的 interval,
    // 停不掉就会在封库之后继续摸库。
    shutdownClientAuth()
    shutdownImports()
    shutdownRuns()
    shutdownSessionTitles()
    shutdownTerminals()
  },
  drainAsync: () =>
    Promise.allSettled([
      shutdownMcp(),
      shutdownPlugins(),
      shutdownEnvironments(),
      browserBindings?.shutdown() ?? Promise.resolve()
    ]),
  destroyTray,
  sealDatabase: () => closeDatabase({ final: true }),
  quit: () => app.quit(),
  exit: (code) => app.exit(code)
})

app.on('window-all-closed', () => {
  /*
    macOS 上这里**就是**退出流程的第二段入口(关窗那一段跑完了)。
    非 macOS 维持原语义:窗口关完 = 用户要退出,直接从 `app.quit()` 进来。
  */
  if (process.platform !== 'darwin') {
    app.quit()
    return
  }
  quitFlow?.windowsClosed()
})

/*
  ★ **整个退出流程唯一的入口。** `Cmd+Q`、托盘「退出 NextCoWork」、`app.quit()`,
  以及上面非 macOS 的那次调用,全都先落到这个事件上 —— 不再有第二条分叉路径。

  `preventDefault()` 在这里的作用不是「拦住退出」,而是**把退出推迟到窗口关完
  之后**:关窗要走渲染层的 `beforeunload`,那是异步的。真正放行的是收尾之后
  那一次 `app.quit()`,那时 `quitFlow.done` 已经是 true,这个处理器直接让路。
*/
app.on('before-quit', (event) => {
  if (quitFlow?.done === true) return
  event.preventDefault()
  quitFlow?.begin()
})
