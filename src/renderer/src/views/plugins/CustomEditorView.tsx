/**
 * 插件接管的自定义编辑器。
 *
 * ## 这个文件真正在处理的是**降级**
 *
 * 渲染一个 iframe 是三行代码。难的是那四种「插件不在了」的情况:
 * 卸载、禁用、装载失败(engines 不匹配)、待批准。它们的共同点是
 * **Tab 还在盘上**(布局早就落过盘了),而能打开它的那个东西没了。
 *
 * 三种处理方式里只有一种是对的:
 *
 * - **让 Tab 消失** —— 用户重启一次就丢了一屏布局,而且没有任何提示;
 * - **渲染一个空白格子** —— 看起来像编辑器坏了,用户会去重装插件;
 * - **降级成只读文本预览** ← 这个。文件还能看,而且上面写清楚了为什么
 *   现在打不开、该去哪儿把它弄回来。
 *
 * ★ 只读是有意的:一个 `.excalidraw` 文件用文本编辑器存回去,大概率把它
 * 存坏。能看不能改,是这种时候唯一安全的姿势。
 */
import { AlertTriangle } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import type { InnerTab } from '../../../../shared/domain/tab'
import { isRunnable } from '../../../../shared/plugin/state'
import { EmptyState } from '../../components/arc/empty-state/empty-state'
import { useI18n } from '../../i18n'
import { activatePluginEditor } from '../../services/plugins'
import { PluginViewFrame } from '../../shell/PluginViewFrame'
import { DocumentEngineFrame } from './DocumentEngineFrame'
import { usePluginsStore } from '../../stores/plugins'

export function CustomEditorView({
  tab,
  workspaceId
}: {
  tab: Extract<InnerTab, { kind: 'custom' }>
  workspaceId: string
}): ReactNode {
  const { t } = useI18n()
  const catalog = usePluginsStore((state) => state.catalog)

  const plugin = catalog.plugins.find((item) => item.id === tab.ref.pluginId)
  const editor = plugin?.manifest.contributes.customEditors.find((item) => item.viewType === tab.ref.viewType)
  // 「能不能跑」的判定只有一份(`shared/plugin/state.ts`)—— 这里、菜单项过滤、
  // 以及「谁来打开这个文件」的挑选,三处必须同时改变,抄成三份迟早会分叉。
  const usable = plugin !== undefined && editor !== undefined && isRunnable(plugin)

  /*
    需求:渲染 iframe 之前先按 `onCustomEditor:<viewType>` 唤醒插件。
    不满足会怎样:插件视图的静态文件只对「被唤醒过」的插件可服务
    (协议层的 roots 表在 spawn 时才填),没醒的插件 iframe 第一个请求
    就是 403 —— Tab 里一片 "forbidden",且零报错。Excalidraw 之所以
    没踩中,是它声明了 onStartup(开机即醒);编辑器类插件按规范只声明
    onCustomEditor,而宿主此前没有任何地方派发这个事件。
    ★ 唤醒失败(activated=false)不渲染 iframe:渲染了也只是把 403 画出来。
    失败原因在插件详情页的诊断里,这里交给上面的降级态/下一次 catalog 更新。
  */
  const [woken, setWoken] = useState(false)
  useEffect(() => {
    if (!usable) return
    let alive = true
    void activatePluginEditor(tab.ref.pluginId, tab.ref.viewType)
      .then((result) => { if (alive && result.activated) setWoken(true) })
      .catch(() => undefined)
    return () => { alive = false }
  }, [usable, tab.ref.pluginId, tab.ref.viewType])

  if (!usable) {
    return (
      <div className="flex min-h-0 flex-1 items-center justify-center bg-canvas">
        <EmptyState
          icon={<AlertTriangle size={26} />}
          title={t('customEditor.unavailable')}
          // ★ 说清「是哪个插件」。只说「编辑器不可用」的话,用户根本不知道去禁用列表里找谁。
          description={t('customEditor.unavailableHint', { plugin: tab.ref.pluginId })}
        />
      </div>
    )
  }

  /*
    ★ 视图路径取的是插件贡献的**第一个视图**,而不是 viewType 同名的那个:
    `contributes.customEditors` 声明的是「我能打开哪种文件」,
    `contributes.views` 声明的是「我的 UI 在哪个文件里」—— 两者是两张表。
    一个编辑器没有配套视图时同样降级,而不是加载一个不存在的 HTML。
    编辑器声明了 `viewId` 时按它取(清单解析已保证它指向一个 location=editor 的视图):
    一个插件贡献 Writer / Sheets 两种编辑器时,取第一个视图会让表格打开成文字处理的界面。
    没声明时保留原先取第一个的行为,旧插件不受影响(计划 §7.1 约束 1)。
  */
  const view = editor.viewId === undefined
    ? plugin.manifest.contributes.views[0]
    : plugin.manifest.contributes.views.find((item) => item.id === editor.viewId)
  if (view === undefined) {
    return (
      <div className="flex min-h-0 flex-1 items-center justify-center bg-canvas">
        <EmptyState
          icon={<AlertTriangle size={26} />}
          title={t('customEditor.noView')}
          description={t('customEditor.noViewHint', { plugin: tab.ref.pluginId })}
        />
      </div>
    )
  }

  /*
    还没醒 / 没醒成:给一块空底色,不给 spinner —— 首次唤醒是一次隐藏窗口
    的起建(几百毫秒),spinner 一闪而过反而更像卡住;失败的话 catalog 随后
    会把 status 翻成 error,上面的降级态自然接管。
  */
  if (!woken) {
    return <div className="flex min-h-0 flex-1 flex-col bg-canvas" />
  }

  /*
    绑定了文档引擎的编辑器(Office / PDF)走会话通道,不走下面的整文件文本通道 ——
    那条通道对 OOXML 只会送空串,存回去就是一个空文件。
  */
  if (editor.documentEngine !== undefined) {
    return (
      <div className="flex min-h-0 flex-1 flex-col bg-canvas">
        <DocumentEngineFrame
          pluginId={plugin.id}
          viewType={editor.viewType}
          viewPath={view.path}
          label={`${plugin.manifest.displayName} — ${tab.ref.path}`}
          workspaceId={workspaceId}
          path={tab.ref.path}
        />
      </div>
    )
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col bg-canvas">
      <PluginViewFrame
        pluginId={plugin.id}
        path={view.path}
        label={`${plugin.manifest.displayName} — ${tab.ref.path}`}
        /*
          ★ 把 Tab 绑定的那个文件交给外壳,由它代读代写。
          视图自己**说不出**要读哪个文件 —— 见 `PluginViewFrame` 的文档通道。
        */
        document={{ workspaceId, path: tab.ref.path }}
      />
    </div>
  )
}
