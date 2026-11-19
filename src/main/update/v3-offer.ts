/**
 * 「有没有 NextCoWork 3.0 可以升级」的唯一真源(主进程)。渲染层只在 `stores/v3-upgrade.ts` 里留一份镜像,
 * 靠 `app:v3OfferChanged` 广播 + `app:v3OfferGetState` 首帧对账(AGENTS.md 规则 1 / 5)。
 *
 * 和 electron-updater 的检查同一个节奏(启动后 30 秒、之后每天一次,见 `main/index.ts`),外加用户在「关于」
 * 里点「检查更新」时。问不到(离线、后端还没部署 3.x 的源)就停在 `unknown`,界面什么都不显示。
 *
 * 应用内下载 / 安装的进度(`V3InstallState`)也在这份状态里,由 `V3Installer` 产生、这里并入同一个广播。
 */
import { V3_LATEST_URL, parseV3Latest, v3FileKindOf, v3RidOf, type V3InstallState, type V3OfferState } from '../../shared/domain/v3-upgrade'

export interface V3OfferDeps {
  fetch: (url: string) => Promise<{ ok: boolean; json: () => Promise<unknown> }>
  platform: string
  arch: string
  emit: (state: V3OfferState) => void
}

/** `V3Installer` 里这里用到的那部分(便于测试时替身) */
export interface V3InstallerPort {
  getState: () => V3InstallState
  reset: () => void
  download: (file: NonNullable<Extract<V3OfferState, { status: 'available' }>['offer']['file']>) => Promise<void>
  prepareInstall: () => boolean
  installCancelled: () => void
}

export class V3OfferService {
  private state: V3OfferState = { status: 'unknown' }
  private inflight: Promise<V3OfferState> | null = null
  private installer: V3InstallerPort | null = null

  constructor(private readonly deps: V3OfferDeps) {}

  /** 接上应用内安装器;安装器的状态变化经 `installChanged` 并回广播。 */
  attachInstaller(installer: V3InstallerPort): void {
    this.installer = installer
  }

  getState(): V3OfferState {
    return this.state
  }

  /** 单飞:并发的几次检查共用同一个请求。 */
  refresh(): Promise<V3OfferState> {
    this.inflight ??= this.load().finally(() => { this.inflight = null })
    return this.inflight
  }

  /** 下载这个平台的 3.0(没有可在应用内装的文件时什么也不做,界面本来就不会给这个按钮)。 */
  async download(): Promise<void> {
    if (this.state.status !== 'available' || this.state.offer.file === undefined || this.installer === null) return
    await this.installer.download(this.state.offer.file)
  }

  /** 起安装脚本。返回 true = 脚本已起,调用方接着走退出流程;false = 还没准备好。 */
  prepareInstall(): boolean {
    return this.installer?.prepareInstall() ?? false
  }

  installCancelled(): void {
    this.installer?.installCancelled()
  }

  /** 安装器状态变了 → 并进当前状态广播。 */
  installChanged(install: V3InstallState): void {
    if (this.state.status !== 'available') return
    this.publish({ ...this.state, install })
  }

  private async load(): Promise<V3OfferState> {
    const rid = v3RidOf(this.deps.platform, this.deps.arch)
    if (rid === null) return this.publish({ status: 'none' })
    try {
      const response = await this.deps.fetch(V3_LATEST_URL)
      // 404 / 5xx:后端没有 3.x 的源(还没部署)或暂时出错 —— 不知道,而不是「没有」
      if (!response.ok) return this.state
      const offer = parseV3Latest(await response.json(), rid, v3FileKindOf(this.deps.platform))
      if (offer === null) return this.publish({ status: 'none' })
      // 同一个版本在下载 / 已就绪时,别被周期检查把进度冲掉;版本换了就从头来
      const sameVersion = this.state.status === 'available' && this.state.offer.version === offer.version
      if (!sameVersion) this.installer?.reset()
      const install: V3InstallState = sameVersion && this.installer !== null ? this.installer.getState() : { phase: 'idle' }
      return this.publish({ status: 'available', offer, install })
    } catch {
      return this.state
    }
  }

  private publish(next: V3OfferState): V3OfferState {
    if (JSON.stringify(next) !== JSON.stringify(this.state)) {
      this.state = next
      this.deps.emit(next)
    }
    return this.state
  }
}
