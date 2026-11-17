/**
 * 「有没有 NextCoWork 3.0 可以升级」在渲染层的**唯一**镜像(2.9)。真源在主进程的 `V3OfferService`。
 *
 * 照 `stores/device.ts`:订阅在第一次 `load()` 时装;广播和首帧是同一个类型(`V3OfferState`),
 * 晚到的 `getState` 结果不盖过它之后到的广播。标题栏指示器和「关于」页都只读这里、只调这里的动作
 * (AGENTS.md 规则 1 / 4)。
 */
import { create } from 'zustand'
import type { V3OfferState } from '../../../shared/domain/v3-upgrade'
import { v3OfferGetState, v3OfferRefresh, v3OpenDownload, v3Upgrade } from '../services/app'
import { on } from '../services/ipc'

interface V3UpgradeStoreState {
  state: V3OfferState
  /** 「去安装 3.0」已经发起(下载页已打开、退出流程已请求)。退出被用户取消时由 `upgrade()` 复位。 */
  upgrading: boolean
  load: () => Promise<void>
  refresh: () => Promise<void>
  /** 打开下载页并退出 2.x(3.0 在 2.x 开着时不接管数据)。 */
  upgrade: () => Promise<void>
  /** 只打开下载页。 */
  openDownload: () => Promise<void>
}

let subscribed = false
let broadcasts = 0
function subscribeOnce(): void {
  if (subscribed) return
  subscribed = true
  on('app:v3OfferChanged', (state) => { broadcasts += 1; useV3UpgradeStore.setState({ state }) })
}

export const useV3UpgradeStore = create<V3UpgradeStoreState>((set) => ({
  state: { status: 'unknown' },
  upgrading: false,

  async load() {
    subscribeOnce()
    const seen = broadcasts
    try {
      const state = await v3OfferGetState()
      if (broadcasts === seen) set({ state })
    } catch (error) {
      // 读不到就保持 unknown:界面什么都不显示,不为一个可选的引导报错
      console.warn('[v3] 读取 3.0 升级信息失败:', error)
    }
  },

  async refresh() {
    subscribeOnce()
    try {
      set({ state: await v3OfferRefresh() })
    } catch (error) {
      console.warn('[v3] 检查 3.0 失败:', error)
    }
  },

  async upgrade() {
    set({ upgrading: true })
    try {
      await v3Upgrade()
    } finally {
      // 退出被取消(未保存的文档等)时进程还活着:按钮要能再点
      set({ upgrading: false })
    }
  },

  async openDownload() {
    await v3OpenDownload()
  }
}))
