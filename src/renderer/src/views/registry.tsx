/**
 * 视图分发表。**不引 react-router**(方案 §8):Tab 化应用天然不是 URL 驱动,
 * 且生产环境 `file://` 下必须用 HashRouter,白白多一层。
 *
 * `doc` / `draw` / `browser` 本版只出空壳 —— 它们要验证的是「Tab 系统支持异构
 * kind」这件事,不是各自的领域模型(方案 §十)。
 *
 * ★ **这里同时是全应用唯一的拆包边界。** 主 bundle 一度 5.0MB,其中三坨重型依赖
 * 首屏一个都用不到,却全都静态躺在里面:
 *
 *     xterm(6.0M)         ← 只有 TerminalView 引
 *     streamdown + katex  ← 只经 views/chat/*(→ ChatView)和 MarkdownPreview(→ DocumentView)
 *     codemirror(916K)    ← CodeEditor,只被 DocumentView 和 ExtensionsFeature 下的
 *                            MarkdownResourceEditor 引
 *
 * 所以在这一层把这四个视图 `lazy` 掉,三条依赖链正好全被带走,而**那些组件自己
 * 一行都不用改** —— 不用给 `useRef<Terminal>` 想办法、不用管 `xterm.css` 那个副作用
 * import(Vite 会把动态 chunk 的 CSS 一起提出来、在 chunk 加载时注入)。
 *
 * ★ **别把 lazy 下放到组件自己那一层。** AgentMarkdown 有五个调用方,全在聊天热路径上
 * (Thread / parts / InteractionPanel),**每条消息都渲染一次**。在它那层 lazy 的话
 * 每条消息都要过一次 Suspense。放在这里,它跟着 ChatView 进同一个 chunk ——
 * ChatView 一加载它就在,消息渲染不经过任何 Suspense 边界。
 *
 * ★ **只看直接依赖会判断错。** ScheduledFeature 自己什么重型库都不引,但它
 * **静态 import 了 ChatView** —— 留成静态就等于把 streamdown + katex 原样拽回主 bundle,
 * 上面那个 lazy 白做(第一版就是这么翻的车:ChatView 已经 lazy 了,入口里
 * 还躺着 40 处 streamdown、54 处 katex,连 ChatView chunk 都没生成)。
 * 往这个表里添静态视图之前,先把它的传递依赖跟到底。
 *
 * ★ **FilesTab / BrowserView 保持静态**:它们确实不带重型依赖,拆了只多几次往返。
 * BrowserFeature 尤其不能动 —— AppShell 里还有一个绕过本文件的直接 import,
 * 在这里 lazy 它减不掉主 bundle 一个字节。
 */
import {
  PenTool,
  type LucideIcon,
} from "lucide-react";
import { lazy, Suspense, type ReactNode } from "react";
import type {
  FeatureKind,
  InnerTab,
  InnerTabKind,
} from "../../../shared/domain/tab";
import type { Workspace } from "../../../shared/domain/workspace";
import { EmptyState } from "../components/arc/empty-state/empty-state";
import { FEATURE_ICON } from "../shell/icons";
import type { FallbackModel } from "./chat/Composer";
import { FilesTab } from "./files/FilesView";
import { BrowserView } from "./browser/BrowserView";
import { useI18n, type Translate } from "../i18n";
import { BrowserFeature } from "./browser/BrowserFeature";
/*
  ★ **静态 import,不 lazy。** 它只引 `PluginViewFrame`(一个 iframe)和
  插件 store —— 一个重型依赖都没有,lazy 只会给打开插件编辑器多一次往返。
  往这个表里加视图之前先把传递依赖跟到底,理由见文件头那段。
*/
import { CustomEditorView } from "./plugins/CustomEditorView";
import { PluginWebAppView } from "./plugins/PluginWebAppView";

// `.then(m => ({ default: ... }))` 是因为这几个都是具名导出,React.lazy 要的是默认导出。
const ChatView = lazy(() => import("./chat/ChatView").then((m) => ({ default: m.ChatView })));
const TerminalView = lazy(() => import("./terminal/TerminalView").then((m) => ({ default: m.TerminalView })));
const DocumentView = lazy(() => import("./files/DocumentView").then((m) => ({ default: m.DocumentView })));
const ExtensionsFeature = lazy(() => import("./extensions/ExtensionsFeature").then((m) => ({ default: m.ExtensionsFeature })));
// ★ 它自己很轻,lazy 是因为它**静态 import 了 ChatView**(见 ScheduledFeature.tsx)——
// 留成静态的话 ChatView 连同 streamdown + katex 会被它一路拽回主 bundle,
// 上面给 ChatView 做的 lazy 就白做了。
const ScheduledFeature = lazy(() => import("./scheduled/ScheduledFeature").then((m) => ({ default: m.ScheduledFeature })));
// Git 面板只引 services + ui 组件,自身很轻;lazy 是为了让它和它的 diff 渲染
// 不占首屏 —— 和 ScheduledFeature 一样从 FeatureView 这个唯一边界进。
const GitFeature = lazy(() => import("./git/GitFeature").then((m) => ({ default: m.GitFeature })));
// 「改动审查」tab —— 只引 DiffView(轻) + review service,lazy 是为了跟着聊天链一起懒加载,
// 不占首屏。
const ChangeReviewTab = lazy(() => import("./chat/ChangeReviewTab").then((m) => ({ default: m.ChangeReviewTab })));

/**
 * chunk 还在路上时占位。**和面板同色的空块,不要 spinner** ——
 * 切 Tab 本来就是内容替换,转个圈反而把「瞬间完成」渲染成「正在等待」。
 * 命中预热(见 AppShell 的 idle 预取)时它一帧都不会出现。
 */
const VIEW_FALLBACK = <div className="flex min-h-0 flex-1 bg-canvas" />;

export interface InnerViewProps {
  tab: InnerTab;
  workspace: Workspace;
  /** 应用级默认模型;工作区没选过时兜底 */
  fallbackModel: FallbackModel;
  /**
   * 设置 › 通用 › Agent 的「最大输出 Token」。
   *
   * 需求:聊天视图要用**和主进程同一个数**算上下文压力条的输出预留 ——
   * 本地那次重判一旦比主进程严格,就会出现「圆环 21%、旁边却写着接近上限」
   * (理由全文在 `views/chat/context-pressure.ts` 的文件头)。设置的权威在
   * 主进程,所以只能从 AppShell 一路传下来,不在视图里自己存一份。
   */
  maxOutputTokens: number;
}

export function InnerView(props: InnerViewProps): ReactNode {
  // Suspense 放在本文件内部,`InnerView` 的调用方(shell/Dock)因此什么都不用改。
  // `t` 在这里取、往下传:renderInner 不是组件,在它里面调 Hook 过不了 lint。
  const { t } = useI18n();
  return <Suspense fallback={VIEW_FALLBACK}>{renderInner(props, t)}</Suspense>;
}

function renderInner(
  { tab, workspace, fallbackModel, maxOutputTokens }: InnerViewProps,
  t: Translate,
): ReactNode {
  switch (tab.kind) {
    case "chat":
      return (
        <ChatView
          /*
            ★ key 挂 **tab.id**,不是 `chatKey(tab)`。

            以前挂 chatKey,于是草稿铸出 sessionId 的那一刻(用户贴第一张图 /
            发第一条消息)整棵子树被卸载重挂。那次重挂**看得见**:托盘里的
            附件 chip、正在飞的上传、输入框的焦点和光标全部随旧的那棵树消失 ——
            症状是「新对话里第一次粘贴图片没反应,第二次才行」。

            重挂当初的理由是「store 换了,订阅必须跟着换」,但那个理由不成立:
            ChatView 的 per-session 订阅走的是 `sessionStore(storeKey)` +
            `useSyncExternalStore`,storeKey 一变 React 自己会重新订阅,不需要
            换一棵树。而**草稿铸 id 根本不是换会话**,是同一段对话拿到了自己的
            身份(草稿文本由 `adoptDraftSession` 搬过去)。

            ★ 真正「同一个 Tab 换另一条会话」的路径今天不存在
            (`bindChatSession` 是 `ref.sessionId` 唯一的写入者,只写 null→id;
            开历史会话走 `openSession`,那是**另一个 Tab**)。以后真要加,
            必须在 ChatView 里把 per-会话的本地状态(托盘、sessionMode、
            sessionModel)一并重置,而不是把 key 改回去。
          */
          key={tab.id}
          sessionId={tab.ref.sessionId}
          tabId={tab.id}
          workspace={workspace}
          fallbackModel={fallbackModel}
          maxOutputTokens={maxOutputTokens}
          readOnly={tab.ref.readOnly === true}
          subagentOf={tab.ref.subagentOf}
        />
      );
    case "terminal":
      return <TerminalView tab={tab} workspace={workspace} />;
    case "doc":
      return <DocumentView key={`${workspace.id}:${tab.ref.path}`} workspaceId={workspace.id} path={tab.ref.path} />;
    case "draw":
      return (
        <Placeholder
          icon={PenTool}
          title={t("view.draw")}
          step={t("view.shellOnly")}
        />
      );
    case "browser":
      return <BrowserView tab={tab} workspace={workspace} />;
    case "preview":
      return <DocumentView key={`${workspace.id}:${tab.ref.path}`} workspaceId={workspace.id} path={tab.ref.path} />;
    case "files":
      // key 挂子树根:换根等于换一棵树,展开状态和缓存都必须重来
      return <FilesTab key={tab.ref.path} tab={tab} workspace={workspace} />;
    case "changes":
      // key 挂 runId:同一个 tab 换轮次时重建,重新拉那一轮的改动集
      return <ChangeReviewTab key={tab.ref.runId} tab={tab} workspace={workspace} />;
    case "custom":
      /*
        插件接管的自定义编辑器。key 挂「插件 + viewType + 文件」三样:
        换任何一样都是换一个编辑器实例,iframe 必须重建 —— 不重建的话,
        换文件之后插件仍然画着上一个文件的内容,而它收不到任何通知。
      */
      return (
        <CustomEditorView
          key={`${tab.ref.pluginId}:${tab.ref.viewType}:${tab.ref.path}`}
          tab={tab}
          workspaceId={workspace.id}
        />
      );
    case "webapp":
      /*
        插件带进来的网页应用。key 挂「插件 + webApp」两样:换任何一样都是换一个
        站点,webview 必须重建 —— 不重建的话 Electron 会在同一个 webview 里导航,
        而那条导航要过 `will-navigate` 的域名检查,结果是「换个入口打不开」。
      */
      return (
        <PluginWebAppView
          key={`${tab.ref.pluginId}:${tab.ref.webAppId}`}
          tab={tab}
          workspace={workspace}
        />
      );
  }
}

/** 外层 feature Tab 的内容。设置**不走这里** —— 它是模态浮层,见 AppShell。 */
export function FeatureView({ feature, onClose }: { feature: FeatureKind; onClose?: () => void }): ReactNode {
  const { t } = useI18n();
  if (feature === "browser") return <BrowserFeature />;
  // 唯一一个 lazy 的 feature —— 它下面的 MarkdownResourceEditor 拖着整个 codemirror
  if (feature === "extensions") return <Suspense fallback={VIEW_FALLBACK}><ExtensionsFeature onClose={onClose} /></Suspense>;
  if (feature === "scheduled") return <Suspense fallback={VIEW_FALLBACK}><ScheduledFeature onClose={onClose} /></Suspense>;
  if (feature === "git") return <Suspense fallback={VIEW_FALLBACK}><GitFeature onClose={onClose} /></Suspense>;
  const Icon = FEATURE_ICON[feature];
  const key = feature as "scheduled" | "review" | "settings";
  return (
    <Placeholder
      icon={Icon}
      title={t(`view.feature.${key}` as "view.feature.scheduled" | "view.feature.review" | "view.feature.settings")}
      step={t(`view.feature.${key}Hint` as "view.feature.scheduledHint" | "view.feature.reviewHint" | "view.feature.settingsHint")}
    />
  );
}

function Placeholder({
  icon: Icon,
  title,
  step,
}: {
  icon: LucideIcon;
  title: string;
  step: string;
}): ReactNode {
  return (
    <div className="flex min-h-0 flex-1 items-center justify-center">
      <EmptyState icon={<Icon size={26} />} title={title} description={step} />
    </div>
  );
}

/** 只用于类型层面确认每种 kind 都有落点(加一种 kind 忘了写视图,这里编译期就挂)。 */
export const INNER_VIEW_KINDS: Record<InnerTabKind, true> = {
  chat: true,
  terminal: true,
  doc: true,
  draw: true,
  browser: true,
  preview: true,
  files: true,
  changes: true,
  custom: true,
  webapp: true,
};
