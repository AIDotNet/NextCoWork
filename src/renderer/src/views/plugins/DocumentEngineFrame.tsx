/**
 * 绑定了文档引擎的自定义编辑器画布。
 *
 * 需求(计划 §7.1):Office / PDF 编辑器不走 `ncw:doc:*` 的整文件文本通道(那条对 OOXML
 * 只会送空串,存回去就是一个空文件),而是经主进程的文档会话:视图要画、要输入、要保存,
 * 都由这里转发。转发逻辑在同目录 `document-engine-channel.ts`,这个组件只做装配:
 * 把 Tab 绑定变成打开请求。
 *
 * ★ 不把会话的脏状态报给 `plugins:setEditorDirty`。那张表驱动的「关 Tab 前挽留」会向插件
 *   发 `customEditor.save` 让它自己存,而插件运行时并不处理这种调用(`main/plugin/protocol.ts`
 *   的 `__bootstrap` 里没有这个分支,落到「unsupported invocation」)—— 报了脏,Tab 就永远关
 *   不掉。不报也不丢数据:关 Tab 只非 force 地释放会话视图,脏会话留在会话表里,重开能找回,
 *   退出 / 切账户时由 `assertCanRelease` 拦下。由主进程快照驱动的关闭对话框是计划 §7.1 的后续项。
 */
import { useCallback, type ReactNode } from 'react'
import type { EngineFrameMessage } from '../../../../shared/document-engine/view-frame'
import * as documentEngine from '../../services/document-engine'
import { PluginViewFrame } from '../../shell/PluginViewFrame'
import { createEngineChannel } from './document-engine-channel'

export function DocumentEngineFrame({
  pluginId,
  viewType,
  viewPath,
  label,
  workspaceId,
  path
}: {
  pluginId: string
  /** 清单里的 customEditors[].viewType:主进程据它找绑定的引擎 */
  viewType: string
  /** 插件包内的视图 HTML */
  viewPath: string
  /** 无障碍名字,已翻译 */
  label: string
  /** Tab 绑定的工作区与文件 —— 视图说了不算,只从这里来 */
  workspaceId: string
  path: string
}): ReactNode {
  /*
    ★ 依赖只列 Tab 绑定的四个值:换文件 / 换插件才重建通道(= 重开会话);
    父组件每次重渲都新建通道的话,每次重渲都会关掉再重开一次文档会话。
  */
  const channel = useCallback(
    (post: (message: unknown, transfer?: Transferable[]) => void) =>
      createEngineChannel({
        binding: { workspaceId, path, pluginId, viewType },
        services: documentEngine,
        post: (message: EngineFrameMessage, transfer) => { post(message, transfer) }
      }),
    [workspaceId, path, pluginId, viewType]
  )
  return <PluginViewFrame pluginId={pluginId} path={viewPath} label={label} channel={channel} />
}
