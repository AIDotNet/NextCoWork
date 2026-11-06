/**
 * 浏览器地址栏的输入解析与「这个地址值不值得回写」判断 —— 纯函数、零 DOM。
 *
 * 建这个文件是为了把两处散在 `BrowserView.tsx` 里的正则/协议判断收拢成一份：
 * 地址栏提交要补全协议，`did-navigate` 回写只认 http(s) 与 file —— 两处以前各写
 * 各的 `/^https?:\/\//i`，放开 `file://`（2026-09-22，需求与风险见
 * `main/kernel/tool/builtin/ssrf.ts` 的 `allowFileUrls`）时必须一起改，
 * 漏掉回写那处的症状是：本地页面跳转后地址栏还停在旧地址，且 Tab 标题不再跟随。
 *
 * 不做防御式扩展：`javascript:`、`data:`、`ncw://` 等一律不认，理由同
 * `main/index.ts` 的 `browserUrlProtocol` —— webview 只加载 http/https/file。
 */

/** 地址栏输入 → 可加载的完整 URL。解析不出或协议不受支持返回 null（调用方给错误态）。 */
export function resolveAddress(raw: string): string | null {
  const trimmed = raw.trim()
  if (trimmed === '') return null
  // 只对「没有 scheme 前缀」的输入补 https:// —— file://、http:// 原样保留。
  const candidate = /^[a-z][a-z0-9+.-]*:/i.test(trimmed) ? trimmed : `https://${trimmed}`
  try {
    const parsed = new URL(candidate)
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:' && parsed.protocol !== 'file:') return null
    if (parsed.username !== '' || parsed.password !== '') return null
    return parsed.href
  } catch {
    return null
  }
}

/**
 * 一次导航带回的 URL 值不值得同步进地址栏/Tab ref。
 *
 * 需求：本地页面(file://)的每次跳转也要回写 —— 否则地址栏停留在跳转前的地址、
 * `navigateBrowserTab` 收不到更新,主进程里那份 URL 与页面实际位置分叉,
 * 下一次 browser_snapshot 的说明和地址栏对不上。
 */
export function isAddressableUrl(url: string): boolean {
  return /^https?:\/\//i.test(url) || /^file:\/\//i.test(url)
}

/** 地址栏「在系统浏览器打开」只对 http(s) 有意义 —— file:// 交出去等于让系统拿默认应用跑本地路径。 */
export function isExternalOpenableUrl(url: string): boolean {
  return /^https?:\/\//i.test(url)
}
