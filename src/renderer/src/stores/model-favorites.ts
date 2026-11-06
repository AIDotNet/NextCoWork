/**
 * 模型选择器里「★ 收藏」那一栏。
 *
 * 键是 `modelSelectionKey(providerId, alias)` —— 收藏的是**一条绑定**,不是一个别名:
 * 同一个别名挂在按量线和订阅线上时,用户往往只想把其中一条放进常用。
 *
 * ★ 存在 localStorage 而不是 AppSettings:这是纯界面偏好,丢了不影响任何请求,
 *   不值得为它走一遍主进程合并 / 校验 / 配置同步。同源的多个窗口靠 `storage`
 *   事件对齐。读写都包 try/catch —— 存储不可用时退化成本次会话内有效。
 */
import { create } from 'zustand'

const KEY = 'ncw.modelFavorites'

function read(): string[] {
  try {
    const raw = globalThis.localStorage?.getItem(KEY)
    const parsed: unknown = raw == null ? [] : JSON.parse(raw)
    return Array.isArray(parsed) ? parsed.filter((v): v is string => typeof v === 'string') : []
  } catch {
    return []
  }
}

function write(keys: readonly string[]): void {
  try {
    globalThis.localStorage?.setItem(KEY, JSON.stringify(keys))
  } catch {
    /* 存不下就只在本次会话里生效 */
  }
}

interface ModelFavoritesState {
  keys: string[]
  toggle: (key: string) => void
}

export const useModelFavorites = create<ModelFavoritesState>((set, get) => ({
  keys: read(),
  toggle(key) {
    const cur = get().keys
    const next = cur.includes(key) ? cur.filter((k) => k !== key) : [...cur, key]
    write(next)
    set({ keys: next })
  }
}))

if (typeof window !== 'undefined') {
  window.addEventListener('storage', (e) => {
    if (e.key === KEY) useModelFavorites.setState({ keys: read() })
  })
}
