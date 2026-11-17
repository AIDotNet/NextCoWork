/**
 * 「有没有 NextCoWork 3.0 可以升级」的唯一真源(主进程)。渲染层只在 `stores/v3-upgrade.ts` 里留一份镜像,
 * 靠 `app:v3OfferChanged` 广播 + `app:v3OfferGetState` 首帧对账(AGENTS.md 规则 1 / 5)。
 *
 * 和 electron-updater 的检查同一个节奏(启动后 30 秒、之后每天一次,见 `main/index.ts`),外加用户在「关于」
 * 里点「检查更新」时。问不到(离线、后端还没部署 3.x 的源)就停在 `unknown`,界面什么都不显示。
 */
import { V3_LATEST_URL, parseV3Latest, v3RidOf, type V3OfferState } from '../../shared/domain/v3-upgrade'

export interface V3OfferDeps {
  fetch: (url: string) => Promise<{ ok: boolean; json: () => Promise<unknown> }>
  platform: string
  arch: string
  emit: (state: V3OfferState) => void
}

export class V3OfferService {
  private state: V3OfferState = { status: 'unknown' }
  private inflight: Promise<V3OfferState> | null = null

  constructor(private readonly deps: V3OfferDeps) {}

  getState(): V3OfferState {
    return this.state
  }

  /** 单飞:并发的几次检查共用同一个请求。 */
  refresh(): Promise<V3OfferState> {
    this.inflight ??= this.load().finally(() => { this.inflight = null })
    return this.inflight
  }

  private async load(): Promise<V3OfferState> {
    const rid = v3RidOf(this.deps.platform, this.deps.arch)
    if (rid === null) return this.publish({ status: 'none' })
    try {
      const response = await this.deps.fetch(V3_LATEST_URL)
      // 404 / 5xx:后端没有 3.x 的源(还没部署)或暂时出错 —— 不知道,而不是「没有」
      if (!response.ok) return this.state
      const offer = parseV3Latest(await response.json(), rid)
      return this.publish(offer === null ? { status: 'none' } : { status: 'available', offer })
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
