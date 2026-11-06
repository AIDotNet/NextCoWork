/**
 * `nextcowork/view` —— 插件**视图侧**的运行时。
 *
 * ## 它补的是哪一段
 *
 * 视图 iframe 没有 preload、拿不到 `__ncwPluginBridge`,所以 `nextcowork` 那套
 * RPC 在这里一个方法都调不了(那是插件**逻辑侧**、跑在隐藏宿主窗口里的东西)。
 * 视图和宿主之间只有一条 postMessage 通道,报文格式定义在
 * `shell/PluginViewFrame.tsx`。
 *
 * 在这个模块出现之前,每个视图都要自己写一遍:登记 message 监听、比对
 * `event.origin`、发 `ncw:doc:ready`、认 `ncw:doc:open`、记得保存时带回
 * revision……`examples/acme.markdown-studio` 和 `acme.image-studio` 各写了一份,
 * 两份的 origin 校验还不一样。**同一件事有两种写法,就一定有一种是错的。**
 *
 * ## 三条不变式
 *
 * 1. **只认自己 origin 的消息。** 不比对的话,任何被嵌进来的第三方 iframe
 *    都能伪造一条 `ncw:doc:open` 把视图里的内容换掉。
 * 2. **保存不带路径。** 路径由宿主从 Tab 绑定里取 —— 视图说了不算,
 *    这条通道的能力上界因此正好是「它自己那个文件」。
 * 3. **`setDirty` 必须报。** 宿主的「关 Tab 之前问一句」全靠它;不报的话
 *    用户没存的改动会在关 Tab 那一刻静默消失,而内置文档是会挽留的。
 */
import { StrictMode, useSyncExternalStore, type ReactNode } from 'react'
import { createRoot } from 'react-dom/client'

/** 宿主送过来的那份文档。`mime` 只在图片分支有。 */
export interface PluginDocument {
  /** 工作区相对路径。**只读**,保存时不需要也不可以带上它。 */
  path: string
  /** 文本文件是正文;图片是 `data:` URL(可直接喂 `<img>` / canvas)。 */
  data: string
  mime?: string
}

type HostMessage =
  | ({ type: 'ncw:doc:open' } & PluginDocument)
  | { type: 'ncw:doc:saved' }
  | { type: 'ncw:doc:saveFailed' }

const HOST_ORIGIN_MESSAGE_TYPES = new Set(['ncw:doc:open', 'ncw:doc:saved', 'ncw:doc:saveFailed'])

function post(message: unknown): void {
  /*
    ★ `targetOrigin` 给 `location.origin`(也就是 `ncw-plugin://<自己>`)而不是 `'*'`。
    宿主那一侧只认来自本 iframe 的消息,但写 `'*'` 意味着这条消息会被投递给
    **任何**恰好嵌着我们的页面。
  */
  globalThis.parent?.postMessage(message, '*')
}

/**
 * 订阅这个视图绑定的文档。
 *
 * 返回退订函数。★ **登记完才发 ready**:反过来的话,宿主在同一个 tick 里回的
 * `ncw:doc:open` 会打在一个还没有监听器的窗口上 —— 表现为「编辑器偶尔打开是空的」,
 * 而且只在机器快的时候复现。
 */
export function onDocument(handler: (doc: PluginDocument) => void): () => void {
  const listener = (event: MessageEvent): void => {
    if (event.origin !== location.origin) return
    const data = event.data as HostMessage | null
    if (data === null || typeof data !== 'object' || !HOST_ORIGIN_MESSAGE_TYPES.has(data.type)) return
    if (data.type !== 'ncw:doc:open') return
    handler({ path: data.path, data: data.data, ...(data.mime === undefined ? {} : { mime: data.mime }) })
  }
  globalThis.addEventListener('message', listener)
  post({ type: 'ncw:doc:ready' })
  return () => { globalThis.removeEventListener('message', listener) }
}

/**
 * 把内容存回这个视图绑定的文件。
 *
 * `encoding: 'base64'` 是图片支线(主进程只允许覆写已分类为 image 的文件)。
 * 返回的 Promise 在宿主回执到达时 settle:成功 resolve,失败 reject —— 而失败
 * 最常见的原因是**别人在你编辑期间改了同一个文件**(revision 对不上),
 * 这种时候正确的做法是提示用户,不是重试。
 */
export function saveDocument(data: string, options: { encoding?: 'base64' } = {}): Promise<void> {
  return new Promise((resolve, reject) => {
    const listener = (event: MessageEvent): void => {
      if (event.origin !== location.origin) return
      const message = event.data as HostMessage | null
      if (message === null || typeof message !== 'object') return
      if (message.type !== 'ncw:doc:saved' && message.type !== 'ncw:doc:saveFailed') return
      globalThis.removeEventListener('message', listener)
      if (message.type === 'ncw:doc:saved') resolve()
      else reject(new Error('the host refused the save (the file changed underneath, or it is not writable)'))
    }
    globalThis.addEventListener('message', listener)
    post({ type: 'ncw:doc:save', data, ...(options.encoding === undefined ? {} : { encoding: options.encoding }) })
  })
}

/** 告诉宿主「我有 / 没有没存的改动」。见文件头第 3 条。 */
export function setDirty(dirty: boolean): void {
  post({ type: 'ncw:doc:dirty', dirty })
}

/** 当前主题。值由宿主注入的垫片写在 `<html>` 上,这里只是读它。 */
export interface PluginTheme {
  appearance: 'light' | 'dark'
  motion: 'standard' | 'soft' | 'reduced' | 'off'
}

function readTheme(): PluginTheme {
  const root = document.documentElement
  return {
    appearance: root.dataset.theme === 'light' ? 'light' : 'dark',
    motion: (root.dataset.themeMotion as PluginTheme['motion'] | undefined) ?? 'standard'
  }
}

let themeSnapshot = readTheme()

/**
 * 跟随宿主的深浅色 / 动效档位。
 *
 * ★ 绝大多数情况**用不到它** —— `nextcowork/ui` 的控件和 Tailwind token 已经
 * 自己跟着 `<html>` 上的属性走了。只有当你自己画 canvas、或者用 Motion 写动画
 * 时才需要读这里(CSS 那段 `prefers-reduced-motion` 管不住 WAAPI)。
 */
export function useTheme(): PluginTheme {
  return useSyncExternalStore(
    (onChange) => {
      const listener = (): void => {
        const next = readTheme()
        // ★ 比一次再发:`ncw:theme` 每次主题**微调**都发(包括只改了壁纸),
        //   不比的话 useSyncExternalStore 会因为快照是新对象而无限重渲。
        if (next.appearance === themeSnapshot.appearance && next.motion === themeSnapshot.motion) return
        themeSnapshot = next
        onChange()
      }
      globalThis.addEventListener('ncw:theme', listener)
      return () => { globalThis.removeEventListener('ncw:theme', listener) }
    },
    () => themeSnapshot
  )
}

/**
 * 挂载视图的根组件。
 *
 * ★ 存在的理由不是「省三行」,是**根节点的归属**:宿主注入的主题垫片写的是
 * `<html>` 上的属性和变量,而插件如果把 React 挂在一个自己新建的、脱离
 * document 的容器上(有人会这么做以求"干净"),那些变量就继承不到 ——
 * 症状是控件全是无色的,且零报错。这里保证根节点就在 `<body>` 里。
 *
 * ★ `StrictMode` 是默认开的:插件视图最常见的 bug 就是 effect 里登记了监听
 * 却没有在 cleanup 里退订,而 StrictMode 的双次挂载当场就能让它暴露出来。
 */
export function mount(node: ReactNode, options: { strict?: boolean; container?: HTMLElement } = {}): void {
  const container = options.container ?? document.getElementById('root') ?? document.body.appendChild(document.createElement('div'))
  createRoot(container).render(options.strict === false ? node : <StrictMode>{node}</StrictMode>)
}

/*
  绑定了文档引擎的编辑器(Office / PDF)用的会话客户端与画布换算。
  ★ 换算函数与宿主用的是**同一份**实现(`shared/document-engine/*`):视图自己写一份的话,
  点击落点与引擎的光标会在某个缩放下差出一两个字,且只在那个缩放下复现。
*/
export { openEngineDocument, EngineDocumentError } from './engine-document'
export type { EngineDocument, EngineDocumentOpened, EngineTile } from './engine-document'
export { lokKeyOf } from '../../../shared/document-engine/keys'
export { cssPxToTwips, twipsToCssPx, twipsRectToCss, tileRequest, tilesCovering, tilesInView } from '../../../shared/document-engine/viewport'
export { DocumentCanvas } from './DocumentCanvas'
