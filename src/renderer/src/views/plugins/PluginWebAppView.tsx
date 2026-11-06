/**
 * 插件带进来的**网页应用**(`contributes.webApps`)。
 *
 * ## 它和浏览器 Tab 的区别,以及为什么不复用那一个
 *
 * `BrowserView` 是**用户自己的浏览器标签**:有地址栏、能导航到任何地方、关掉时
 * 要去主进程销毁那一侧的 BrowserTab,并且它归属于「浏览器」这个功能。
 * 这里是**某个插件的一块界面**:地址由清单写死、导航被限制在声明过的域名内、
 * 插件不在了要降级成一句说明。两者只是恰好都渲染网页。
 *
 * ★ 但 webview 的那几条安全约束**必须一模一样**,所以它们在这里是逐条重复的,
 * 不是被遗漏的:每工作区独立 partition、`allowpopups={false}`、
 * `contextIsolation=yes,nodeIntegration=no,sandbox=yes`。
 * 抄的时候删掉任何一条都不会有报错 —— 只会多一个能在本机跑脚本的网页。
 *
 * ## 登录态复用工作区 profile
 *
 * `browserPartition(workspaceId)` 与用户自己的浏览器标签是同一个分区 ——
 * 「装个插件打开 B 站」这件事之所以成立,就是因为他在浏览器里登录过之后
 * 这里直接是登录态。代价写在插件详情页的能力说明里,不藏着。
 */
import { AlertTriangle, ExternalLink } from 'lucide-react'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import type { InnerTab } from '../../../../shared/domain/tab'
import type { Workspace } from '../../../../shared/domain/workspace'
import { isLocalEnvironment } from '../../../../shared/domain/environment'
import { browserPartition } from '../../../../shared/domain/browser'
import { matchesHostPermission } from '../../../../shared/plugin/manifest'
import { isRunnable } from '../../../../shared/plugin/state'
import { EmptyState } from '../../components/arc/empty-state/empty-state'
import { IconButton } from '../../components/ui/IconButton'
import { useI18n } from '../../i18n'
import { openExternal } from '../../services/app'
import { usePluginsStore } from '../../stores/plugins'

interface WebviewElement extends HTMLElement {
  reload?: () => void
}

export function PluginWebAppView({
  tab,
  workspace
}: {
  tab: Extract<InnerTab, { kind: 'webapp' }>
  workspace: Workspace
}): ReactNode {
  const { t } = useI18n()
  const catalog = usePluginsStore((state) => state.catalog)
  const viewRef = useRef<WebviewElement | null>(null)
  /*
    需求:站外链接不在 webview 里跳,交给系统浏览器。
    不满足会怎样:一个「B 站插件」点两下就变成一个不受限的浏览器,
    而用户在安装界面上看到的域名只有 bilibili.com。
  */
  const [blocked, setBlocked] = useState<string | null>(null)

  const plugin = catalog.plugins.find((item) => item.id === tab.ref.pluginId)
  const webApp = plugin?.manifest.contributes.webApps.find((item) => item.id === tab.ref.webAppId)
  // 「能不能跑」的判定只有一份(`shared/plugin/state.ts`),同 `CustomEditorView`
  const usable = plugin !== undefined && webApp !== undefined && isRunnable(plugin)

  /*
    ★ 地址以**清单里的**为准,不是落盘那一条:插件升级换了地址时,盘里的旧记录
    不该把用户永远钉在旧站点上。落盘那条只在插件不在时用来说明「原本指着哪儿」。
  */
  const url = webApp?.url ?? tab.ref.url

  useEffect(() => { setBlocked(null) }, [url])

  useEffect(() => {
    const node = viewRef.current
    if (node === null || !usable) return
    /*
      ★ 导航拦截挂在 `will-navigate` 上,而不是事后检查当前地址:事后检查意味着
      那个页面已经加载过了(cookie 已经带出去了)。
    */
    const allowed = plugin?.manifest.hostPermissions ?? []
    const onNavigate = (event: Event): void => {
      const next = (event as Event & { url?: string }).url
      if (typeof next !== 'string') return
      if (matchesHostPermission(allowed, next) || next === url) return
      event.preventDefault()
      setBlocked(next)
      void openExternal(next)
    }
    node.addEventListener('will-navigate', onNavigate)
    // ★ 返回值必须进 cleanup(AGENTS §1 第 4 条):漏一个,HMR 每次热更叠一层监听器。
    return () => { node.removeEventListener('will-navigate', onNavigate) }
  }, [plugin, url, usable])

  if (!isLocalEnvironment(workspace.environment)) {
    return (
      <div role="status" className="flex min-h-0 flex-1 items-center justify-center p-6 text-[13px] text-fg-muted">
        {t('ssh.browserUnavailable')}
      </div>
    )
  }

  if (!usable) {
    /*
      ★ 插件没了(卸载 / 禁用 / 装载失败)时**不能让这个 Tab 消失,也不能让它空着** ——
      同 `views/plugins/CustomEditorView.tsx` 的降级立场:让它消失意味着用户重启一次
      就丢了一屏布局;画个空白格子会让人以为网站崩了。
    */
    return (
      <div className="flex min-h-0 flex-1 items-center justify-center bg-canvas">
        <EmptyState
          icon={<AlertTriangle size={26} />}
          title={t('pluginWebApp.unavailable')}
          description={t('pluginWebApp.unavailableHint', { plugin: tab.ref.pluginId })}
        />
      </div>
    )
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col bg-canvas">
      {blocked !== null && (
        <div className="flex shrink-0 items-center gap-2 border-b border-hairline bg-accent/8 px-3 py-2 text-[12px] text-fg-muted">
          <ExternalLink size={13} className="shrink-0" />
          <span className="min-w-0 flex-1 truncate">{t('pluginWebApp.openedExternally', { url: blocked })}</span>
          <IconButton label={t('common.close')} size={22} onClick={() => setBlocked(null)}>
            <span aria-hidden className="text-[13px] leading-none">×</span>
          </IconButton>
        </div>
      )}
      <div className="relative min-h-0 flex-1 overflow-hidden bg-white">
        {/* React has no built-in webview type; Electron upgrades this custom element. */}
        <webview
          ref={(node) => { viewRef.current = node as WebviewElement | null }}
          src={url}
          /* ★ 与用户的浏览器标签同一分区 —— 登录态因此是共享的,见文件头 */
          partition={browserPartition(workspace.id)}
          allowpopups={false}
          webpreferences="contextIsolation=yes,nodeIntegration=no,sandbox=yes"
          className="h-full w-full border-0"
        />
      </div>
    </div>
  )
}
