/**
 * 设置 handler。
 *
 * 变更后**主动广播** settings:changed —— 多窗口下,在快捷窗改了主题,
 * 主窗必须跟着变。这是 GlobalEventChannel 的正当用法(与 agent:event 那种
 * 必须按订阅定向推送的相对)。
 */
import type { AppSettings, AppSettingsPatch } from '../../shared/domain/settings'
import { applyProxy } from '../net/proxy'
import { store } from '../state/store'
import { windows } from '../window/registry'
import { applyThemePreference } from './app'
import { notifyPluginsThemeChanged } from './plugins'
import { profileSettings, syncLegacyProfile } from './theme'
import { reconcileGateway } from './gateway'

export function getSettings(): AppSettings {
  return store.getSettings()
}

export function updateSettings(patch: AppSettingsPatch): AppSettings {
  if (patch.activeThemeProfileId !== undefined && patch.themeStudio === undefined && patch.imageTheme === undefined) {
    const selected = profileSettings(patch.activeThemeProfileId)
    // Keep the requested ID in the patch; profileSettings only supplies legacy fields.
    patch = { ...patch, themeStudio: selected.themeStudio, imageTheme: selected.imageTheme }
  }
  const before = store.getSettings()
  const next = store.updateSettings(patch)
  syncLegacyProfile(next, patch)

  if (patch.theme !== undefined && patch.theme !== before.theme) {
    const resolved = applyThemePreference(next.theme)
    windows.emitToAll('theme:changed', { resolved })
    // 插件那一路是独立通道 —— 渲染层的 emitToAll 到不了插件宿主窗口。
    notifyPluginsThemeChanged(resolved)
  }

  /*
    ★ 代理要**在广播之前**推给 Chromium,而且是无条件推(只要 patch 碰了 proxy)。
    不比对 before/after:`AppSettingsPatch` 是深合并的,「变没变」要逐字段比,
    而漏比一个字段的症状是「我改了白名单,它没生效」—— 比多推一次 setProxy
    昂贵得多。setProxy 本身是幂等的。
  */
  if (patch.proxy !== undefined) {
    void applyProxy(next.proxy)
  }

  // 需求：网关开关/监听范围/端口变更立即生效，串行装配负责快速连续修改的竞态。
  if (patch.gateway !== undefined) void reconcileGateway()
  windows.emitToAll('settings:changed', next)
  return next
}
