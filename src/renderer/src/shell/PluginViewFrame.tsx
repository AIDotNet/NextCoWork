/**
 * 插件视图的外壳 —— 受控 webview。
 *
 * ## 边界在哪
 *
 * 插件的 UI 跑在一个 `ncw-plugin://<pluginId>` 的 iframe 里,和宿主**不同源**。
 * 这不是一个实现细节,它是这一层的全部意义:
 *
 * - 插件碰不到宿主的 DOM,于是它改不了菜单、盖不住模态、偷不到输入框的内容;
 * - CSP 由协议 handler 强制注入(见 `main/plugin/protocol.ts`),
 *   `connect-src` 不给外网 —— 所有网络请求必须走 `ncw.net.fetch`,
 *   而那条路上有 `hostPermissions` 逐 URL 校验;
 * - 尺寸、位置、可见性由宿主决定,插件自己弹不了窗
 *   (`sandbox` 里没有 `allow-popups`、没有 `allow-modals`)。
 *
 * ## 主题同步为什么是 postMessage
 *
 * 跨源 iframe 的 `document` 我们碰不到,所以不能像 `theme/apply.ts` 那样
 * 直接往 `<html>` 上写 `--color-*`。这里把那 24 个 token 连同
 * `data-theme` / `data-theme-motion` 一起 **postMessage** 过去,由插件侧的
 * 运行时垫片写进自己的 `:root`。
 *
 * ★ `targetOrigin` 写死成那个插件的 origin,**不是 `'*'`**:用 `'*'` 的话,
 * 插件把自己导航到别处之后,我们还在往一个陌生页面发消息。
 *
 * ## 附带好处
 *
 * `components/ui/Menu.tsx` 那个「祖先链不能有 transform/filter」的陷阱在这里
 * 天然够不着 —— 插件想弹菜单只能 `ncw.window.showQuickPick()`,由宿主渲染。
 */
import { useEffect, useRef, type ReactNode } from 'react'
import { THEME_TOKENS } from '../../../shared/domain/theme'
import { cn } from '../lib/cn'
import { readWorkspaceFile } from '../services/workspace-files'
import { saveDocumentFile } from '../actions/workspace-files'
import { setCustomEditorDirty } from '../services/plugins'

export interface PluginViewFrameProps {
  pluginId: string
  /** 包内相对路径,例如 `dist/views/editor.html` */
  path: string
  /** 给无障碍用的名字。**已经 `t()` 过** —— 这一层不碰翻译 */
  label: string
  className?: string
  /**
   * 这个视图绑定的工作区文件。给了它,视图就能收到 `ncw:doc:open`
   * 并用 `ncw:doc:save` 存回去 —— 见下面「文档通道」那一段。
   */
  document?: { workspaceId: string; path: string }
  /**
   * 额外的报文通道(文档引擎画布用,见 `views/plugins/DocumentEngineFrame.tsx`)。
   * 挂载时调用一次,拿到 `post`(已锁定这个 iframe 与它的 origin);来源核对过的每条报文交给
   * 返回值的 `handle`,卸载时 `dispose`。★ 引用要稳定(调用方用 useCallback),
   * 每次渲染换一个就会每次渲染重开一次文档会话。
   */
  channel?: (post: (message: unknown, transfer?: Transferable[]) => void) => { handle: (data: unknown) => void; dispose: () => void }
}

/**
 * 视图侧发来的报文。**只认这三种**,而且一条都不带路径。
 *
 * ★ 路径由宿主这一侧从 Tab 绑定里取(`document.path`),视图说了不算。
 * 让视图带路径的话,一个跑在插件 origin 里的页面就能请求写工作区的任意文件 ——
 * 而它是第三方代码。不带路径意味着这条通道的能力上界就是「它自己那个文件」,
 * 而那正好是一个编辑器需要的全部。
 */
type DocumentMessage =
  | { type: 'ncw:doc:ready' }
  // `encoding: 'base64'` 见下面保存分支 —— 图片编辑器的存回支线。
  | { type: 'ncw:doc:save'; data: string; encoding?: 'base64' }
  | { type: 'ncw:doc:dirty'; dirty: boolean }

export function PluginViewFrame({ pluginId, path, label, className, document: bound, channel }: PluginViewFrameProps): ReactNode {
  const frameRef = useRef<HTMLIFrameElement>(null)
  const origin = `ncw-plugin://${pluginId}`
  const src = `${origin}/${path.replace(/^\.?\//, '')}`

  useEffect(() => {
    const frame = frameRef.current
    if (frame === null) return

    const post = (): void => {
      const root = document.documentElement
      const style = getComputedStyle(root)
      const tokens: Record<string, string> = {}
      for (const token of THEME_TOKENS) tokens[token] = style.getPropertyValue(`--color-${token}`).trim()
      frame.contentWindow?.postMessage(
        {
          type: 'ncw:theme',
          tokens,
          appearance: root.dataset.theme ?? 'dark',
          motion: root.dataset.themeMotion ?? 'full'
        },
        origin
      )
    }

    /*
      ★ 三个时机都要发,少一个都会留下一种「插件视图颜色不对」的场景:
      加载完(第一次)、主题变了(`<html>` 上的属性变化)、以及 iframe 自己
      重载之后(`load` 会再触发一次)。
    */
    frame.addEventListener('load', post)
    const observer = new MutationObserver(post)
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme', 'data-theme-motion', 'style'] })
    return () => {
      frame.removeEventListener('load', post)
      observer.disconnect()
    }
  }, [origin])

  /*
    ## 文档通道

    视图 iframe 跑在主窗口里,**没有 preload、没有 `__ncwPluginBridge`** ——
    那个 bridge 只挂在隐藏的插件宿主窗口上(`main/plugin/host-window.ts`)。
    所以视图既拿不到 `ncw.workspace.fs`,也没法直接找主进程说话。

    这里补的就是它缺的那一条,而且**刻意只补这一条**:

    ```
    视图 ──ncw:doc:ready──▶ 宿主 ──workspace:readFile──▶ 主进程
    视图 ◀─ncw:doc:open─── 宿主
    视图 ──ncw:doc:save───▶ 宿主 ──workspace:writeFile─▶ 主进程
    ```

    ★ **路径不在报文里。** 宿主用的是 Tab 绑定的那个文件,视图说了不算 ——
    于是这条通道的能力上界正好是「它自己那个文件」。给视图开一个能点名
    路径的通道,等于绕开插件清单里那套 `workspace.write` 授权。

    ★ 来源逐条核对 `event.source` 是不是这个 iframe:同一个窗口里还有别的
    frame,只认 origin 的话,另一个插件的视图也能冒充这一个发保存。
  */
  useEffect(() => {
    const frame = frameRef.current
    if (frame === null || bound === undefined) return

    const send = (message: unknown): void => { frame.contentWindow?.postMessage(message, origin) }
    /*
      ★ `revision` 是**上一次读到的那份**的摘要,保存时必须带回去。
      不带的话,Agent 或外部编辑器在用户画图期间改了同一个文件,
      这次保存会把那份改动无声盖掉(`workspace:writeFile` 就是靠它挡的)。
    */
    let revision = ''

    const onMessage = (event: MessageEvent): void => {
      if (event.source !== frame.contentWindow || event.origin !== origin) return
      const message = event.data as DocumentMessage
      if (message === null || typeof message !== 'object') return

      if (message.type === 'ncw:doc:ready') {
        void readWorkspaceFile(bound.workspaceId, bound.path)
          .then((file) => {
            revision = file.revision
            /*
              需求:图片编辑类自定义编辑器要收得到图片字节。原来这条通道只送
              文本 `content`,图片文件会收到空串 —— 表现为编辑器打开后一片空白
              且零报错。图片给 `dataUrl`(视图可直接喂 <img>/canvas),顺带
              `mime`;文本之外又读不出的(超限/损坏),维持空串由视图决定画什么。
            */
            const payload =
              file.kind === 'text'
                ? { data: file.content }
                : file.kind === 'image'
                  ? { data: file.dataUrl, mime: file.mime }
                  : { data: '' }
            send({ type: 'ncw:doc:open', path: bound.path, ...payload })
          })
          .catch(() => { send({ type: 'ncw:doc:open', path: bound.path, data: '' }) })
        return
      }

      if (message.type === 'ncw:doc:save' && typeof message.data === 'string') {
        /*
          `encoding: 'base64'` 是图片编辑器的存回支线:主进程按 base64 解码并
          只允许覆写已分类为 image 的文件。缺省不送该字段 = 文本,行为不变。
        */
        void saveDocumentFile({
          workspaceId: bound.workspaceId,
          path: bound.path,
          content: message.data,
          revision,
          ...(message.encoding === 'base64' ? { encoding: 'base64' as const } : {})
        })
          .then((saved) => {
            revision = saved.revision
            send({ type: 'ncw:doc:saved' })
          })
          .catch(() => { send({ type: 'ncw:doc:saveFailed' }) })
        return
      }

      if (message.type === 'ncw:doc:dirty') {
        /*
          ★ 这条报文**此前被静默丢弃**:类型里声明着、视图也在发,而这个 switch
          没有它的分支。后果不是「少个小圆点」—— 宿主的 `plugins:confirmClose`
          靠这张脏表回答「关掉这个 Tab 之前有没有没存的东西」,表永远空 =
          **关 Tab 从不挽留**,插件编辑器里没存的改动就这么没了,零提示。

          `documentId` 用「工作区 + 路径」拼:同一个文件在两个 Tab 里打开时,
          它们说的是同一份脏状态 —— 而那正是主进程按**文件**做挽留判断所需要的
          (见 `shared/plugin/protocol.ts` 里 `customEditors.setDirty` 的 ★)。
        */
        void setCustomEditorDirty(
          pluginId,
          `${bound.workspaceId}:${bound.path}`,
          bound.path,
          message.dirty === true
        ).catch(() => undefined)
      }
    }

    window.addEventListener('message', onMessage)
    return () => { window.removeEventListener('message', onMessage) }
  }, [origin, pluginId, bound?.workspaceId, bound?.path])

  /*
    额外通道(文档引擎画布)。来源核对与文档通道同一条 ★:只认这个 iframe、这个 origin。
    `post` 同样写死 targetOrigin,并支持转移缓冲(tile 像素不拷贝)。
  */
  useEffect(() => {
    const frame = frameRef.current
    if (frame === null || channel === undefined) return
    const instance = channel((message, transfer) => { frame.contentWindow?.postMessage(message, origin, transfer ?? []) })
    const onMessage = (event: MessageEvent): void => {
      if (event.source !== frame.contentWindow || event.origin !== origin) return
      instance.handle(event.data)
    }
    window.addEventListener('message', onMessage)
    return () => {
      window.removeEventListener('message', onMessage)
      instance.dispose()
    }
  }, [origin, channel])

  return (
    <iframe
      ref={frameRef}
      src={src}
      title={label}
      /*
        ★ `allow-same-origin` 是必须的:只给 `allow-scripts` 会让 iframe 变成
        opaque origin,`localStorage` / `IndexedDB` 全抛异常。安全性来自
        **origin 差异**(`ncw-plugin://<id>` ≠ `ncw://main`),不是 sandbox 属性。
        ★ 不给 `allow-popups` / `allow-modals` / `allow-top-navigation`:
        插件弹不了窗、也没法把整个应用导航走。
      */
      sandbox="allow-same-origin allow-scripts allow-forms"
      // z 轴留在 0 档(`styles/theme.css` 只有 5 档)—— 插件 UI 永远压不过宿主菜单与模态
      className={cn('min-h-0 w-full flex-1 border-0 bg-canvas', className)}
    />
  )
}
