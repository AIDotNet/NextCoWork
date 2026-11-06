import { rename } from 'node:fs/promises'
import { extname } from 'node:path'
import { app, autoUpdater as nativeUpdater } from 'electron'
import { autoUpdater } from 'electron-updater'
import type { UpdateInfo, UpdateProgress, UpdateState } from '../../shared/domain/update'
import { windows } from '../window/registry'

const FEED_URL = 'https://nextco.work/api/client/updates/feed/stable/'

function currentVersion(): string {
  return typeof app?.getVersion === 'function' ? app.getVersion() : '0.0.0'
}

function platformSupported(): boolean {
  return (process.platform === 'win32' && process.arch === 'x64') ||
    (process.platform === 'darwin' && (process.arch === 'x64' || process.arch === 'arm64')) ||
    (process.platform === 'linux' && process.arch === 'x64')
}

function asUpdateInfo(raw: Record<string, unknown>): UpdateInfo {
  if (typeof raw.version !== 'string' || raw.version.length === 0) throw new Error('invalid-metadata')
  return {
    version: raw.version,
    releaseDate: typeof raw.releaseDate === 'string' ? raw.releaseDate : undefined,
    releaseNotes: typeof raw.releaseNotes === 'string' ? raw.releaseNotes : undefined,
    mandatory: raw.mandatory === true,
    minimumSupportedVersion: typeof raw.minimumSupportedVersion === 'string' ? raw.minimumSupportedVersion : undefined,
    graceUntil: typeof raw.graceUntil === 'string' ? raw.graceUntil : undefined
  }
}

class UpdateService {
  private state: UpdateState = { state: 'idle', currentVersion: currentVersion() }
  private configured = false
  private operation: Promise<UpdateState> | null = null
  /** Squirrel.Mac 已经从 electron-updater 的本地代理取到并暂存了更新包。 */
  private nativeStaged = false
  /** 用户点了「重启更新」且退出流程已经发起:收尾的最后一步要装更新,而不是普通退出。 */
  private installPending = false
  private quitRequester: (() => Promise<boolean>) | null = null

  configure(): void {
    if (this.configured) return
    this.configured = true
    if (!app) return
    this.state = app.isPackaged
      ? { state: 'idle', currentVersion: currentVersion() }
      : { state: 'disabled', currentVersion: currentVersion() }
    if (!app.isPackaged || !platformSupported()) {
      if (app.isPackaged) this.publish({ state: 'error', currentVersion: currentVersion(), code: 'unsupported-platform' })
      return
    }
    autoUpdater.autoDownload = false
    autoUpdater.autoInstallOnAppQuit = false
    autoUpdater.setFeedURL({ provider: 'generic', url: FEED_URL })
    if (process.platform === 'darwin') nativeUpdater.on('update-downloaded', () => { this.nativeStaged = true })
    autoUpdater.on('checking-for-update', () => this.publish({ state: 'checking', currentVersion: currentVersion() }))
    autoUpdater.on('update-available', (raw) => {
      try {
        this.publish({ state: 'available', currentVersion: currentVersion(), update: asUpdateInfo(raw as unknown as Record<string, unknown>) })
      } catch {
        this.publish({ state: 'error', currentVersion: currentVersion(), code: 'invalid-metadata' })
      }
    })
    autoUpdater.on('update-not-available', () => this.publish({ state: 'up-to-date', currentVersion: currentVersion() }))
    autoUpdater.on('download-progress', (progress) => {
      const previous = this.state
      if (!('update' in previous)) return
      const p: UpdateProgress = {
        percent: Math.max(0, Math.min(100, progress.percent)),
        transferredBytes: progress.transferred,
        totalBytes: progress.total,
        bytesPerSecond: progress.bytesPerSecond
      }
      this.publish({ state: 'downloading', currentVersion: currentVersion(), update: previous.update, progress: p })
    })
    autoUpdater.on('update-downloaded', (raw) => {
      const event = raw as unknown as Record<string, unknown>
      let info: UpdateInfo
      try {
        info = asUpdateInfo(event)
      } catch {
        this.publish({ state: 'error', currentVersion: currentVersion(), code: 'invalid-metadata' })
        return
      }
      void ensureInstallerExtension(typeof event.downloadedFile === 'string' ? event.downloadedFile : '')
        .then(() => this.publish({ state: 'downloaded', currentVersion: currentVersion(), update: info }))
        .catch(() => this.publish({ state: 'error', currentVersion: currentVersion(), code: 'download-failed' }))
    })
    autoUpdater.on('error', (error) => {
      const text = error instanceof Error ? error.message.toLowerCase() : ''
      const code = text.includes('checksum') || text.includes('sha') ? 'checksum-mismatch' :
        text.includes('download') ? 'download-failed' : 'network'
      this.publish({ state: 'error', currentVersion: currentVersion(), code })
    })
  }

  getState(): UpdateState {
    return this.state
  }

  /** Main-process guard used by Agent IPC; Renderer cannot bypass mandatory updates. */
  canStartNewRuns(): boolean {
    if (this.state.state !== 'available' && this.state.state !== 'downloading' && this.state.state !== 'downloaded') return true
    const update = this.state.update
    if (!update.mandatory || !update.minimumSupportedVersion) return true
    if (update.graceUntil && Date.parse(update.graceUntil) > Date.now()) return true
    return compareVersions(currentVersion(), update.minimumSupportedVersion) >= 0
  }

  async check(): Promise<UpdateState> {
    this.configure()
    if (!app.isPackaged) return this.state
    if (!platformSupported()) return this.state
    if (this.operation) return this.operation
    this.operation = autoUpdater.checkForUpdates()
      .then(() => this.state)
      .catch(() => {
        this.publish({ state: 'error', currentVersion: currentVersion(), code: 'network' })
        return this.state
      })
      .finally(() => { this.operation = null })
    return this.operation
  }

  async download(): Promise<UpdateState> {
    this.configure()
    if (!app.isPackaged) return this.state
    if (!platformSupported()) return this.state
    if (this.operation) return this.operation
    this.operation = autoUpdater.downloadUpdate()
      .then(() => this.state)
      .catch(() => {
        this.publish({ state: 'error', currentVersion: currentVersion(), code: 'download-failed' })
        return this.state
      })
      .finally(() => { this.operation = null })
    return this.operation
  }

  /**
   * 由 main/index.ts 注入:走应用自己的退出流程(文档预检 → 关窗 → 收尾),resolve 为「退出是否真的开始了」。
   */
  setQuitRequester(request: () => Promise<boolean>): void {
    this.quitRequester = request
  }

  /**
   * ★ 不能直接调 `quitAndInstall()`。它在 macOS 上先逐个关窗、等「窗口全关完」才退出并交给 ShipIt,
   * 而主窗的 `close` 处理器在退出流程之外只会 `hide()` —— Electron 视为关窗被取消,整次安装静默作废,
   * 进程留着,ShipIt 一直等到用户哪天手动 Cmd+Q 才装上(ShipIt 日志里「install request」到
   * 「Installation completed」隔了几个小时就是这个)。所以这里只发起应用自己的退出,
   * 真正的安装放在收尾最后一步(`quitAndInstallIfPending`),那时窗口已销毁、库已封。
   */
  async install(): Promise<void> {
    this.configure()
    if (!app.isPackaged || this.state.state !== 'downloaded') return
    const update = this.state.update
    this.publish({ state: 'installing', currentVersion: currentVersion(), update })
    try {
      // 暂存必须在关窗之前做完:Squirrel 失败时应用还完整可用,能报错、能重试。
      await this.stageNativeUpdate()
    } catch {
      this.publish({ state: 'error', currentVersion: currentVersion(), code: 'install-failed' })
      return
    }
    this.installPending = true
    if (this.quitRequester === null) {
      app.quit()
      return
    }
    if (!(await this.quitRequester())) this.installCancelled()
  }

  /**
   * 退出被作废(用户在未保存文档的确认上取消、或渲染层顶住 unload)。按钮要回到可点状态;
   * `installPending` 刻意保留 —— 用户处理完未保存的改动、渲染层重发退出时,照样装更新。
   */
  installCancelled(): void {
    if (this.state.state !== 'installing') return
    this.publish({ state: 'downloaded', currentVersion: currentVersion(), update: this.state.update })
  }

  /**
   * 退出流程收尾的最后一步(替代 `app.quit()`)。返回 false 表示没有待装的更新,调用方照常退出。
   *
   * 兜底定时器:Windows 上安装器起不来时 electron-updater 不会 quit;macOS 上有漏网的窗口挡住关窗时
   * 原生 quitAndInstall 也会作废。此刻库已封,进程不能留着 —— 到点普通退出(Squirrel 已暂存的更新
   * 在进程退出时照样会装)。
   */
  quitAndInstallIfPending(): boolean {
    if (!this.installPending) return false
    this.installPending = false
    setTimeout(() => app.quit(), 10_000)
    autoUpdater.quitAndInstall(false, true)
    return true
  }

  /**
   * electron-updater 的 MacUpdater 在 `autoInstallOnAppQuit = false` 时只起了本地代理,
   * 要等 quitAndInstall 才让 Squirrel 去取包。提前取完,收尾时的 quitAndInstall 就是同步交给 ShipIt。
   */
  private stageNativeUpdate(): Promise<void> {
    if (process.platform !== 'darwin' || this.nativeStaged) return Promise.resolve()
    return new Promise((resolve, reject) => {
      const cleanup = (): void => {
        nativeUpdater.removeListener('update-downloaded', onDownloaded)
        nativeUpdater.removeListener('error', onError)
      }
      const onDownloaded = (): void => { cleanup(); resolve() }
      const onError = (error: Error): void => { cleanup(); reject(error) }
      nativeUpdater.on('update-downloaded', onDownloaded)
      nativeUpdater.on('error', onError)
      nativeUpdater.checkForUpdates()
    })
  }

  private publish(state: UpdateState): void {
    this.state = state
    windows.emitToAll('app:updateChanged', state)
  }
}

/**
 * feed 里的下载地址是 `/api/client/updates/<id>/download`,electron-updater 会按 URL 末段把安装包
 * 存成无扩展名的 `download`,Windows 直接启动它只会弹出「选择应用以打开」。补回 .exe 并改写
 * installerPath(实例属性遮蔽原型 getter),让 quitAndInstall 拿到可执行的路径。
 */
async function ensureInstallerExtension(downloadedFile: string): Promise<void> {
  if (process.platform !== 'win32') return
  if (downloadedFile.length === 0 || extname(downloadedFile).toLowerCase() === '.exe') return
  const target = `${downloadedFile}.exe`
  await rename(downloadedFile, target)
  Object.defineProperty(autoUpdater, 'installerPath', { value: target, configurable: true, writable: true })
}

function compareVersions(left: string, right: string): number {
  const parse = (value: string): [number, number, number] => {
    const core = value.replace(/^v/, '').split('-')[0]!.split('+')[0]!.split('.').map((part) => Number(part) || 0)
    return [core[0] ?? 0, core[1] ?? 0, core[2] ?? 0]
  }
  const a = parse(left); const b = parse(right)
  for (let i = 0; i < 3; i += 1) if (a[i] !== b[i]) return a[i]! - b[i]!
  return 0
}

export const updateService = new UpdateService()
