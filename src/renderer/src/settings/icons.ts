/**
 * 设置浮层导航的图标。和 `nav.ts` 分开,是为了让 `nav.ts` 连同 `matchRows`
 * 保持零 React 依赖 —— vitest 跑在 node 环境里(照 `shell/icons.ts` 的先例)。
 *
 * 「每日回顾」刻意复用 `FEATURE_ICON.review`:侧边栏那一项和这一页是同一个东西
 * 的两个位置,图标各写各的迟早会漂成两个。
 */
import {
  ChartColumn,
  Cpu,
  Database,
  DownloadCloud,
  FlaskConical,
  Info,
  MonitorSmartphone,
  Plug,
  SlidersHorizontal,
  Sparkles,
  User,
  Wallet,
  type LucideIcon
} from 'lucide-react'
import { FEATURE_ICON } from '../shell/icons'
import type { SettingsPageId } from './nav'

export const SETTINGS_ICON: Record<SettingsPageId, LucideIcon> = {
  account: User,
  wallet: Wallet,
  general: SlidersHorizontal,
  // ★ 不用 `Upload`:这一页的方向是「把外面的东西搬进来」,箭头必须朝下。
  //   `Database` 已经被「数据」页占了,而那两页最不该被认混。
  import: DownloadCloud,
  preference: Sparkles,
  model: Cpu,
  // ★ 柱状,和「每日回顾」那个折线图标分开 —— 两页都是图表,图标再同源
  //   侧栏里就只剩文字能区分
  usage: ChartColumn,
  review: FEATURE_ICON.review,
  connection: Plug,
  devices: MonitorSmartphone,
  computer: FEATURE_ICON.browser,
  data: Database,
  experimental: FlaskConical,
  about: Info
}
