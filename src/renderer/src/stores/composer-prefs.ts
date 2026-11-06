/**
 * 输入框补全那几个开关在渲染层的镜像。权威在主进程,App 每次收到 `settings:changed` 整份覆写。
 *
 * ★ 为什么不一路当 prop 传:设置住在 App.tsx,输入框在 App → AppShell → Dock → registry
 *   → ChatView → Composer 的第六层。为四个布尔穿五层 prop,每加一个开关就要改五个文件。
 * ★ 握手拿到设置之前一律视为**关**(同提示音那份 `MUTED`):宁可首屏少一句建议,
 *   也不在用户关掉功能的情况下先发一次请求。
 */
import { create } from 'zustand'
import type { AppSettings } from '../../../shared/domain/settings'
import { useDeviceStore } from './device'

export interface ComposerPrefs {
  promptSuggestions: boolean
  localCompletion: boolean
  /** 打字续写:开关开着,**并且**点名了模型 —— 两者缺一都不生效 */
  inlineCompletion: boolean
}

export const useComposerPrefs = create<ComposerPrefs>(() => ({
  promptSuggestions: false,
  localCompletion: false,
  inlineCompletion: false
}))

export function setComposerPrefs(settings: AppSettings): void {
  const next: ComposerPrefs = {
    promptSuggestions: settings.promptSuggestions,
    localCompletion: settings.localCompletion,
    inlineCompletion: settings.inlineCompletion && settings.inlineCompletionModel !== ''
  }
  const cur = useComposerPrefs.getState()
  if (cur.promptSuggestions === next.promptSuggestions && cur.localCompletion === next.localCompletion
    && cur.inlineCompletion === next.inlineCompletion) return
  useComposerPrefs.setState(next)
}

/**
 * 选中了远端设备。★ 补全频道在设备模式下没有路由(主进程会拒),这里先跳过,
 * 免得每一轮都去撞一次注定失败的 IPC。设备清单还没加载时按「本机」算。
 */
export function isRemoteDeviceSelected(): boolean {
  return (useDeviceStore.getState().state?.current.deviceId ?? null) !== null
}
