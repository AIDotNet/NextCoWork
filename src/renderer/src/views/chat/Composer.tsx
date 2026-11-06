/**
 * 输入框 —— 连同它下面那排药丸。
 *
 * ★ **药丸不是设置项的快捷方式,它就是发送时读取的那个值**(方案 §4.5)。
 * 界面把权限档位放在输入框左下角而不是设置页里,说明档位是**每次发送时**
 * 读的当前值,run 一旦开始就冻结在 `RunRequest` 里不再变 —— 这正是设置页
 * 那句「更改会在下一次新回复生效」。
 *
 * 所以本地 state 是权威,发送时打快照;顺带写回工作区当新默认值。
 * 反过来(以工作区为权威、每次改都等一轮 IPC 回来)会让药丸点下去有延迟。
 *
 * ★ **生成中不禁用输入框**,占位符改成「当前回复完成后按队列继续执行」。
 * 队列语义在 session store 里(`queuedInputs`),这里只是不拦着用户打字。
 */
import {
  ArrowUp,
  Bot,
  BrainCircuit,
  Check,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  CircleDollarSign,
  Code2,
  Database,
  DatabaseZap,
  Gauge,
  Globe,
  Lightbulb,
  LogIn,
  LogOut,
  Maximize2,
  Monitor,
  Percent,
  Paperclip,
  Plus,
  Puzzle,
  RefreshCw,
  Server,
  Settings2,
  ShieldCheck,
  ShieldQuestion,
  Square,
  Unlock,
  Sparkles,
  Workflow,
} from "lucide-react";
import {
  useEffect,
  useId,
  useRef,
  useState,
  type ReactNode,
} from "react";
import type { PermissionMode } from "../../../../shared/agent/permission";
import {
  PERMISSION_MODES,
} from "../../../../shared/agent/permission";
import type {
  SessionMode,
  ThinkingLevel,
} from "../../../../shared/agent/run-request";
import {
  contextSegmentShare,
  effectiveContextWindow,
  formatContextWindow,
  longContextTickRatio,
  LONG_CONTEXT_THRESHOLD,
  supportsMaxContext,
  type ContextPreview,
  type ContextSegment,
  type ContextSegmentKind,
} from "../../../../shared/agent/context-management";
import { formatCostMicros, longContextSurcharge, findPricing, type RunCost } from "../../../../shared/domain/pricing";
import { usePricingTable } from "../../services/pricing";
import { formatTokensPerSecond } from "../../../../shared/agent/duration";
import { formatTokenCount } from "../../../../shared/agent/tokens";
import { findBuiltinModel } from "../../../../shared/domain/model-catalog-inventory";
import { effectiveModelThinking, modelThinkingLevels, normalizeModelThinkingLevel } from "../../../../shared/domain/model-runtime";
import type {
  Workspace,
  WorkspaceSettings,
} from "../../../../shared/domain/workspace";
import { normalizeEnvironmentRef } from "../../../../shared/domain/environment";
import type { WorkspaceCompactionOverride } from "../../../../shared/domain/compaction-model";
import {
  INHERIT_THINKING,
  SUBAGENT_THINKING_CHOICES,
} from "../../../../shared/domain/subagent-thinking";
import { parseModelSelectionKey } from "../../../../shared/domain/model-selection";
import {
  compactModelOptions,
  compactModelPatch,
  compactModelValue,
  compactThinkingPatch,
  compactThinkingValue,
  FOLLOW_GLOBAL,
  FOLLOW_SESSION,
} from "./compact-choice";
import type {
  ModelAlias,
  UpstreamProvider,
} from "../../../../shared/domain/provider";
import { isChatModelAlias } from "../../../../shared/domain/provider";
import { ProviderIcon } from "../../components/brand/ProviderIcon";
import {
  ProviderModelMenu,
  type ProviderModelMenuRow,
} from "../../components/ProviderModelMenu";
import { useChatModelGuard } from "../../components/useChatModelGuard";
import {
  Menu,
  MenuItem,
  MenuLabel,
  MenuSeparator,
} from "../../components/ui/Menu";
import { Slider } from "../../components/ui/Slider";
import { Tooltip } from "../../components/ui/Tooltip";
import { cn } from "../../lib/cn";
import { GoalPanel, GoalPill } from './GoalPanel';
import { ConversationCostDetails } from './ConversationCostDetails';
import { CodingPlanQuotaSection } from './CodingPlanQuotaSection';
import { codingPlanFamilyFor } from '../../../../shared/domain/coding-plan';
import { parseGoalCommand, type ActiveGoal } from '../../../../shared/domain/goal';
import { useI18n } from "../../i18n";
import { updateWorkspace } from "../../services/app";
import { listConnections, onConnectionsChanged } from "../../services/connections";
import { previewContext } from "../../services/context";
import { useModelsStore } from "../../stores/models";
import { usePluginsStore } from "../../stores/plugins";
import { pluginToolList } from './plugin-tool-list';
import { AttachmentTray, type TrayItem } from "./AttachmentTray";
import { MentionInput, type MentionInputHandle } from "./MentionInput";
import { MentionPopup } from "./MentionPopup";
import type { MentionQuery } from "../../../../shared/domain/file-mention";
import { insertMention, mentionQueryAt } from "../../../../shared/domain/file-mention";
import { insertCommand, insertSkill, skillQueryAt, type SkillQuery } from "../../../../shared/domain/file-mention";
import type { FileSuggestion } from "../../../../shared/domain/file-tree";
import { searchWorkspaceFiles } from "../../services/app";
import { listSkills, onSkillsChanged } from "../../services/skills";
import { listCommands } from "../../services/commands";
import type { SkillListItem } from "../../../../shared/domain/skill";
import type { CommandDefinition } from "../../../../shared/domain/command";
import { applyCommand, parseCommandInvocation } from "../../../../shared/domain/command";
import type { ModeDefinition } from '../../../../shared/domain/mode';
import { isBuiltinModeId } from '../../../../shared/domain/mode';
import { listModes, onModesChanged } from '../../services/modes';
import { SkillPopup, type SlashItem } from './SkillPopup';
import type { TranslationKey } from "../../i18n";
import type { DraftSelection } from './rich-draft';
import type { FileSuggestion as HotFile } from "../../../../shared/domain/file-tree";
import { useComposerGhost } from './useComposerGhost';

/**
 * 内置命令的副标题。★ 磁盘上的命令取 frontmatter 里的 `description`(那是用户
 * 自己写的内容,不翻译);内置那几条是应用自己的 UI 文案,必须走 i18n。
 */
const BUILTIN_COMMAND_DESCRIPTIONS: Record<string, TranslationKey> = {
  init: "commands.builtin.init",
};

/**
 * 可编辑区和它的占位符**共用**的度量类:内边距、字号、行高。
 *
 * ★ 常量而不是两处各写一遍 —— 两层是叠在一起的,任何一处单独改了 `px-4`,
 * 占位符从此偏移四个像素,而那看起来像是渲染 bug 不像是笔误。
 */
const DRAFT_METRICS = "px-4 pt-3.5 pb-1 text-[13.5px] leading-relaxed";

export interface ComposerValue {
  permissionMode: PermissionMode;
  model: string;
  /**
   * 用户显式选定的供应商。★ 和 `model` 是**一对**,任何一处写 `model` 的地方
   * 都必须同时写它(哪怕是 `undefined`),否则会留下「新别名 + 旧供应商」。
   */
  modelProviderId?: string;
  mode: SessionMode;
  thinking: ThinkingLevel;
  webSearch: boolean;
  /**
   * 「最大上下文」:关(默认)时有效窗口夹在 272K 以内。见
   * `shared/agent/context-management.ts` 文件头的三层窗口。
   *
   * ★ 药丸这一侧是**必填**,虽然 `WorkspaceSettings` 那一侧是可选的 ——
   * `fromSettings` 负责把缺席铺成 false,之后本地就没有「不知道」这个状态了。
   */
  maxContext: boolean;
}

/** 应用级默认模型(设置页那个)。别名和供应商必须一起传,拆成两个 prop 必漏 */
export interface FallbackModel {
  model: string;
  modelProviderId?: string;
}

export interface ConversationUsageSummary {
  inputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  outputTokens: number;
  latestTps?: number;
  cost?: RunCost | null;
}

export function Composer({
  workspace,
  fallbackModel,
  sessionModel,
  onModelChange,
  draft,
  onDraft,
  onPermissionModeChange,
  sessionMode,
  onSessionModeChange,
  onSetDefaultMode,
  onManageModes,
  running,
  onSend,
  onStop,
  attachments = [],
  onAttachFiles,
  onPickAttachment,
  onRemoveAttachment,
  onRetryAttachment,
  sessionId,
  contextTokens,
  contextSegments,
  contextCacheHitRate,
  contextCompacting = false,
  onCompactContext,
  onGoalCommand,
  onSkillifyCommand,
  goal,
  onManageMcp,
  conversationUsage,
  suggestTrigger,
  started = true,
  mentionedFiles,
}: {
  workspace: Workspace;
  /** 应用级默认模型(设置页那个)。工作区还没选过时用它兜底 */
  fallbackModel: FallbackModel;
  /**
   * **这条会话自己记住的模型**,优先于工作区默认值。
   *
   * ★ 它是异步到的(要读一次会话元数据),所以下面用一个 effect 补写进 `value`,
   * 而不是只在初始化时读一次 —— 挂载那一刻它还是 `undefined`。
   * 空别名 = 这条会话从没选过,那就继续走「工作区默认 → 应用默认 → 第一个」。
   */
  sessionModel?: FallbackModel;
  /**
   * 用户从菜单里点选了模型。★ 落到**会话**上由上层负责 —— 会话 id 归它管
   * (草稿这一刻可能还没有 id)。工作区默认值仍由这里写回,新会话继承它。
   */
  onModelChange?: (model: string, modelProviderId?: string) => void;
  draft: string;
  onDraft: (v: string) => void;
  /**
   * 权限档位药丸被用户切换时回调。用于把还没被消费的排队消息改标成新档位 ——
   * 否则切到「完全访问」只对之后新敲的消息生效,已经排在队列里的那些还得继续按旧档位跟审批。
   */
  onPermissionModeChange?: (mode: PermissionMode) => void;
  /** Mode persisted on the current session; drafts inherit the workspace default. */
  sessionMode?: SessionMode;
  onSessionModeChange?: (mode: SessionMode) => void;
  onSetDefaultMode?: (mode: SessionMode) => void;
  onManageModes?: () => void;
  running: boolean;
  onSend: (text: string, value: ComposerValue) => void;
  onStop: () => void;
  /**
   * 草稿附件。★ **状态不在这里** —— 它与 draft 同级,住在 ChatView,
   * 因为发送时要把它转成 `ContentPart[]`,而那是 ChatView 的职责。
   * 这里只负责渲染与三个入口。
   */
  attachments?: TrayItem[];
  /** 拖拽 / 粘贴共用 */
  onAttachFiles?: (files: File[]) => void;
  /** 点 `+` → 走主进程 dialog */
  onPickAttachment?: () => void;
  onRemoveAttachment?: (key: string) => void;
  onRetryAttachment?: (key: string) => void;
  /**
   * null = 还没落库的草稿 Tab。只给上下文预览用 —— 有会话时它的历史要算进归因,
   * 没有时预览的就是「一句话都没聊」的那个基线。
   */
  sessionId?: string | null;
  /**
   * 最近一次上游请求实际吃掉的输入 token。★ 是**瞬时量**不是累计量 ——
   * 传 `transcript.usage.inputTokens`(整轮之和)的话,聊到第三轮就会显示 200%。
   */
  contextTokens?: number;
  /** 最近一轮的占用归因(本地估算)。缺省 = 还没发生过一次请求。 */
  contextSegments?: ContextSegment[];
  contextCacheHitRate?: number;
  contextCompacting?: boolean;
  /** 双击圆环触发。生成中不给触发,由这里的按钮自己拦。 */
  onCompactContext?: () => void;
  /**
   * `/goal <参数>` 的去处。参数原样递过去（空串 = 用户只打了 `/goal`）。
   *
   * ★ 解析在 `shared/domain/goal.ts` 的 `parseGoalCommand`，不在这里：
   *   同一份规则要服务斜杠命令、`ProposeGoal` 工具、IPC 三个入口，
   *   各写一份就会各漂各的（「`stop` 算不算清除词」这种事只该有一个答案）。
   */
  goal?: ActiveGoal;
  onGoalCommand?: (args: string, value: ComposerValue) => boolean | void | Promise<boolean | void>;
  /**
   * `/skillify [补充说明]` 的去处:把当前会话提炼成项目 Skill(另开一条提炼会话)。
   * 缺省 = 这一刻提炼不了(草稿、还没聊过、或自己就是提炼会话),弹层里就不出现这一项 ——
   * 不画一个注定被主进程拒绝的命令。
   */
  onSkillifyCommand?: (args: string) => void;
  /** 归因卡里 MCP 那一行的去处。缺省 = 那一行不可点。 */
  onManageMcp?: () => void;
  /** 整个会话的累计用量，以及最近一轮可计算的输出速度。 */
  conversationUsage?: ConversationUsageSummary;
  /**
   * 「刚刚看着一轮成功跑完」的那个 `runEndedAt`(见 `suggest-trigger.ts`)。每变一次,
   * 输入框就为空草稿请求一次「建议下一步」。缺省 = 不建议。
   */
  suggestTrigger?: number;
  /** 这条会话发过消息了没有。没有时占位符多教一句 `@` 和 `/` 怎么用 */
  started?: boolean;
  /** 本会话 `@` 过的文件 —— 热文件补全的来源之一 */
  mentionedFiles?: readonly HotFile[];
}): ReactNode {
  const { t } = useI18n();
  const [goalPanelOpen, setGoalPanelOpen] = useState(false);
  // 需求：工具目录随插件启用/禁用更新；只订阅 catalog，不订阅活动日志等高频字段。
  const pluginCatalog = usePluginsStore((state) => state.catalog);
  const pluginTools = pluginToolList(pluginCatalog);
  const [pluginToolsOpen, setPluginToolsOpen] = useState(false);
  const { models: configuredModels, providers, loaded, providerOf, load } = useModelsStore();
  const models = configuredModels.filter((m) => m.enabled !== false &&
    providers.some((p) => p.id === m.providerId && p.enabled));
  const [value, setValue] = useState<ComposerValue>(() => ({
    ...fromSettings(workspace.settings),
    ...(sessionModel === undefined || sessionModel.model === ""
      ? {}
      : { model: sessionModel.model, modelProviderId: sessionModel.modelProviderId }),
    mode: sessionMode ?? workspace.settings.defaultMode,
  }));
  useEffect(() => {
    if (sessionMode === undefined) return;
    setValue((current) => current.mode === sessionMode ? current : { ...current, mode: sessionMode });
  }, [sessionMode]);
  /*
    会话自己记住的模型 → 药丸。★ 依赖写成**拍平的一对**而不是那个对象:
    上层每渲染一次都会给一个新对象,按身份比较的话这个 effect 每次都跑。
    空别名不覆盖 —— 那是「这条会话从没选过」,该由兜底链说了算。
  */
  const sessionModelAlias = sessionModel?.model ?? "";
  const sessionModelProviderId = sessionModel?.modelProviderId;
  useEffect(() => {
    if (sessionModelAlias === "") return;
    setValue((current) =>
      current.model === sessionModelAlias && current.modelProviderId === sessionModelProviderId
        ? current
        : { ...current, model: sessionModelAlias, modelProviderId: sessionModelProviderId });
  }, [sessionModelAlias, sessionModelProviderId]);
  /*
    上下文归因的**预览**:还没发过请求时,`contextSegments` 是空的,而那正是这张卡
    最该说话的时刻 —— 一个挂满 MCP 的工作区在一句话都没聊的时候就已经少掉半个窗口。
    ★ 菜单打开时才拉(见 `services/context.ts`),而且**真实归因一到就让位**。
  */
  const [preview, setPreview] = useState<ContextPreview | undefined>(undefined);
  const input = useRef<MentionInputHandle | null>(null);
  /*
    ★ **挂载即取焦点** —— 这是「新对话里第一次 Ctrl+V 一定没反应」的修法。
    在此之前首屏的 `document.activeElement` 是 `<body>`(CDP 探针实测):输入框
    看上去随时能打字,但键盘事件(粘贴在内)根本不会送到它这里,必须先用鼠标
    点一下 —— 于是「第一次失败、点一下之后第二次就好」成了稳定复现的现象。

    另一个受益的场合是草稿铸出会话 id 的那次重挂(见 `draft-handoff.ts`):
    新画出来的 contentEditable 是另一个 DOM 节点,焦点不会自己跟过去。

    ★ 只在**挂载**时跑。非激活的 Tab 在 `shell/Dock.tsx` 里压根没有挂载,
    所以「挂载」就等于「这个对话刚成为可见的那一个」—— 不会去抢别人的焦点。
  */
  useEffect(() => {
    input.current?.focus();
  }, []);
  const lastCaret = useRef<number | null>(null);
  const [skills, setSkills] = useState<SkillListItem[]>([]);
  const [commands, setCommands] = useState<CommandDefinition[]>([]);
  const [modes, setModes] = useState<readonly ModeDefinition[]>([]);
  const modeRef = useRef(value.mode);
  const modeChangeRef = useRef(onSessionModeChange);
  modeRef.current = value.mode;
  modeChangeRef.current = onSessionModeChange;
  const [skillsLoading, setSkillsLoading] = useState(true);
  const [skillsError, setSkillsError] = useState(false);
  const [skillPickerOpen, setSkillPickerOpen] = useState(false);
  const [skillPickerQuery, setSkillPickerQuery] = useState('');
  const skillInsertRange = useRef<DraftSelection | null>(null);
  const skillListId = useId();

  useEffect(() => {
    void load();
  }, [load]);
  useEffect(() => {
    let cancelled = false;
    const refresh = (): void => {
      void listModes(workspace.id).then((catalog) => {
        if (cancelled) return;
        setModes(catalog.modes);
        if (!catalog.modes.some((mode) => mode.id === modeRef.current)) {
          modeRef.current = 'code';
          setValue((current) => ({ ...current, mode: 'code' }));
          modeChangeRef.current?.('code');
        }
      }).catch(() => { if (!cancelled) setModes([]); });
    };
    refresh();
    const off = onModesChanged(refresh);
    return () => { cancelled = true; off(); };
  }, [workspace.id]);
  useEffect(() => {
    let cancelled = false;
    // 命令和 Skill 同进同出 —— 它们共用一个 `/` 弹层,各记一个 loading 会让弹层
    // 出现「一半已经列出来、另一半还在转」的中间态,而方向键正要在两者之间连续地走。
    const refreshSlash = (): void => {
      setSkillsLoading(true);
      setSkillsError(false);
      void Promise.all([
        listSkills(workspace.id).then((items) => items.filter((s) => s.globalEnabled && s.activeInWorkspace)),
        listCommands(workspace.id)
      ]).then(([skillItems, commandItems]) => {
        if (cancelled) return;
        setSkills(skillItems);
        setCommands(commandItems);
      }).catch(() => {
        if (!cancelled) { setSkills([]); setCommands([]); setSkillsError(true); }
      }).finally(() => { if (!cancelled) setSkillsLoading(false); });
    };
    refreshSlash();
    const unsubscribe = onSkillsChanged(refreshSlash);
    return () => { cancelled = true; unsubscribe() };
  }, [workspace.id]);

  /**
   * 切工作区 = 换一套默认值。
   *
   * ★ **刻意不用 `useEffect([workspace.settings])`。** 药丸每改一次就会
   * `updateWorkspace` 写回,主进程随即广播 `workspace:changed` —— 那条推送回来时
   * `settings` 是个新引用,effect 会拿它把用户刚点的值再「重置」一遍。
   * 看起来就是药丸点下去闪一下又弹回原样。
   *
   * 用 React 官方那个「渲染期按 key 调整 state」的写法:只认 workspace.id 变没变。
   */
  const [seenWorkspace, setSeenWorkspace] = useState(workspace.id);
  /** 拖拽悬停高亮。★ 纯视觉状态,不影响任何数据流 */
  const [dragging, setDragging] = useState(false);

  /*
    ── `@` 文件引用 ──

    ★ `mention` 是从「草稿 + 光标位置」算出来的,不是「刚才敲了 `@`」这个事件。
      于是用户点回一个写了一半的 `@comp` 中间时,列表会重新出现 ——
      而按事件记的话,那次点击之后就再也弹不出来了。
  */
  const [mention, setMention] = useState<MentionQuery | null>(null);
  const [suggestions, setSuggestions] = useState<FileSuggestion[]>([]);
  const [activeSuggestion, setActiveSuggestion] = useState(0);
  const [loadingFiles, setLoadingFiles] = useState(false);
  const [skillQuery, setSkillQuery] = useState<SkillQuery | null>(null);
  const [activeSkill, setActiveSkill] = useState(0);
  const dismissedSkillAt = useRef<number | null>(null);
  /**
   * 按过 Esc 的那个 `@` 的下标。★ 记**位置**而不是布尔:记布尔的话,
   * 用户接着往下打字会立刻又弹出来,Esc 等于没按。
   */
  const dismissedAt = useRef<number | null>(null);
  /**
   * 输入法组词中。★ 组词串**不在 `value` 里**,此刻算出来的 query 是残缺的
   * (打「文件」的过程中 value 里可能还是空的),据此去检索只会闪一串错的结果。
   */
  const composing = useRef(false);
  const mentionListId = useId();
  if (seenWorkspace !== workspace.id) {
    setSeenWorkspace(workspace.id);
    setValue(fromSettings(workspace.settings));
  }

  /**
   * 光标此刻是不是落在一个 `@查询` 里。文本变化和光标移动都过它。
   *
   * ★ `caret` 为 null 表示「此刻没有折叠光标」—— 有选区(用户在选文字)
   * 或者正在组词。两种情况都不该弹列表。
   */
  function syncMention(text: string, caret: number | null): void {
    if (composing.current) return;
    const q = caret === null ? null : mentionQueryAt(text, caret);
    if (q === null) {
      // 那个 `@` 已经不在了 —— 连同它的「按过 Esc」一起忘掉
      dismissedAt.current = null;
      setMention(null);
      return;
    }
    if (q.start === dismissedAt.current) {
      setMention(null);
      return;
    }
    setMention(q);
    setSkillQuery(null);
  }

  function syncSkill(text: string, caret: number | null): void {
    if (composing.current) return;
    const q = caret === null ? null : skillQueryAt(text, caret);
    // 斜杠路径/URL（`/usr/bin`、`http://…`）永远不弹 Skill 候选。
    if (q === null || q.query.includes('/') || q.query.includes('.') || q.start === dismissedSkillAt.current) {
      if (q === null) dismissedSkillAt.current = null;
      setSkillQuery(null);
      return;
    }
    setSkillQuery(q);
    setMention(null);
    setActiveSkill(0);
  }

  /*
    查询 → 候选。★ **排序在主进程做**,这里拿到的已经是最终顺序
    (见 `shared/domain/fuzzy-path.ts`);渲染层再排一遍就有两套顺序了。

    ★ 失败静默吞掉:`@` 只是个便利入口,工作区根被删掉时它给不出结果是对的,
      但不该在输入框上弹一个错误 —— 用户照样可以把路径直接打出来。
  */
  const mentionQuery = mention?.query ?? null;
  useEffect(() => {
    if (mentionQuery === null) return;
    let cancelled = false;
    setLoadingFiles(true);
    void searchWorkspaceFiles(workspace.id, mentionQuery)
      .then((r) => {
        if (cancelled) return;
        setSuggestions(r);
        setActiveSuggestion(0);
      })
      .catch(() => {
        if (!cancelled) setSuggestions([]);
      })
      .finally(() => {
        if (!cancelled) setLoadingFiles(false);
      });
    return () => {
      cancelled = true;
    };
  }, [mentionQuery, workspace.id]);

  /**
   * 选中一个文件:把 `@查询` 换成 `[名字](工作区相对路径)`。
   *
   * ★ 走输入框的 `replace` 而不是只 `onDraft`:草稿要经过 session store 才回来,
   * 等它回来再摆光标已经晚了一帧,而且那一帧里 tag 是「跳」出来的。
   * `replace` 当场把 DOM 和光标都摆好,`onDraft` 只是随后追平数据。
   */
  function pickFile(file: FileSuggestion): void {
    if (mention === null) return;
    const r = insertMention(draft, mention, file);
    dismissedAt.current = null;
    setMention(null);
    input.current?.replace(r.text, r.caret);
  }

  const skillNeedle = skillPickerOpen ? skillPickerQuery : skillQuery?.query ?? '';

  /**
   * 命令在前、Skill 在后,合成**一个**数组 —— 分组标题由 `SkillPopup` 按相邻两项的
   * `kind` 是否变化自己画,所以这里的排列顺序就是弹层里的分组顺序。
   *
   * ★ 内置命令的副标题走 i18n(那是应用自己的 UI 文案),磁盘上的命令取 frontmatter
   *   里的 `description`(那是用户自己写的内容,不该翻译)。
   */
  const slashNeedle = skillNeedle.toLocaleLowerCase();
  /*
    ★ **本地动作 —— `command.ts` 那条约定的唯一例外。**

    那份头注把斜杠命令定义为「一段存在磁盘上的提示词模板,不是一个能执行的动作」,
    正因为如此这个功能只需要一个 IPC 频道。`/compact` 破例,是因为它要压缩的对象
    **就是即将发出的这个请求本身** —— 没有任何一段提示词能表达这件事,
    展开成文本发给模型只会让模型多读一句「请压缩上下文」然后照常回答。

    `/goal` 破例的理由是同一类:它改的是**会话的状态**(挂上一个待满足的条件),
    而提示词展开不出状态变更 —— 展开成文本只会让模型口头答应一声然后照常停下。

    所以它们只在这里拼进弹层(可见、可搜),并在 `submit()` 里于 `applyCommand`
    **之前**被拦下:主进程永远收不到 `/compact` 这三个字。
    `prompt` 留空串正是这个意思 —— 它没有可展开的正文。

    ★ `run` 收一个 `args`:`/goal <条件>` 的整条参数要原样交给动作。
      `/compact` 那份忽略它(多一个用不到的参数,不影响)。
  */
  const localActions: { command: CommandDefinition; run: (args: string, value: ComposerValue) => void }[] = [
    ...(onCompactContext === undefined ? [] : [{
      command: { name: 'compact', description: t('composer.command.compact'), prompt: '', scope: 'builtin' as const, source: '' },
      run: (): void => onCompactContext()
    }]),
    ...(onGoalCommand === undefined ? [] : [{
      command: { name: 'goal', description: t('composer.command.goal'), prompt: '', scope: 'builtin' as const, source: '' },
      run: (args: string, value: ComposerValue): void => {
        if (args.trim() === '') setGoalPanelOpen(true);
        else void onGoalCommand(args, value);
      }
    }]),
    /*
      `/skillify` 破例的理由同 `/goal`:它的效果是**另开一条会话**,不是往这条会话里发一段提示词 ——
      展开成文本发出去,只会让当前会话里的 agent 口头总结一遍,Skill 文件一个都不会落地。
    */
    ...(onSkillifyCommand === undefined ? [] : [{
      command: { name: 'skillify', description: t('composer.command.skillify'), prompt: '', scope: 'builtin' as const, source: '' },
      run: (args: string): void => onSkillifyCommand(args)
    }])
  ];
  const slashItems: SlashItem[] = [
    ...localActions
      .filter((a) => `${a.command.name} ${a.command.description}`.toLocaleLowerCase().includes(slashNeedle))
      .map((a) => ({ kind: 'command' as const, key: `local:${a.command.name}`, command: a.command, description: a.command.description })),
    ...commands
      .filter((c) => `${c.name} ${c.description}`.toLocaleLowerCase().includes(slashNeedle))
      .map((command) => {
        const builtin = BUILTIN_COMMAND_DESCRIPTIONS[command.name];
        return {
          kind: 'command' as const,
          key: `command:${command.scope}:${command.name}`,
          command,
          description: command.scope === 'builtin' && builtin !== undefined ? t(builtin) : command.description
        };
      }),
    ...skills
      .filter((s) => `${s.name} ${s.description} ${s.category}`.toLocaleLowerCase().includes(slashNeedle))
      .map((skill) => ({ kind: 'skill' as const, key: `skill:${skill.id}`, skill }))
  ];

  /**
   * 弹层里选中一行。
   *
   * ★ 命令落进草稿的只是**命令名**,正文的展开留到 `submit` 那一刻(`applyCommand`)
   *   —— 当场把几千字的模板塞进输入框,用户既没法再补参数,也看不清自己要发什么。
   */
  function pickSlash(item: SlashItem): void {
    const range = skillQuery ?? skillInsertRange.current ?? input.current?.selection() ?? { start: lastCaret.current ?? draft.length, end: lastCaret.current ?? draft.length };
    const r = item.kind === 'command'
      ? insertCommand(draft, range, item.command.name)
      : insertSkill(draft, range, item.skill.name);
    dismissedSkillAt.current = null;
    setSkillQuery(null);
    setSkillPickerOpen(false);
    input.current?.replace(r.text, r.caret);
  }

  function pickSkill(skill: SkillListItem): void {
    pickSlash({ kind: 'skill', key: `skill:${skill.id}`, skill });
  }

  useEffect(() => {
    const onSkillUse = (event: Event): void => {
      const name = (event as CustomEvent<{ name?: string }>).detail?.name;
      const skill = name === undefined ? undefined : skills.find((item) => item.name === name);
      if (skill !== undefined) pickSkill(skill);
    };
    window.addEventListener('nextcowork:skill-use', onSkillUse);
    return () => window.removeEventListener('nextcowork:skill-use', onSkillUse);
  }, [skills, draft, skillQuery]);

  /**
   * 弹层开着时,方向键 / Enter / Tab / Esc 归它。**返回 true = 这一下已经用掉了。**
   *
   * ★ 必须在输入框的 `onKeyDown` 里截,不能让弹层自己收键:
   * 焦点一旦移出输入框,输入法组词就会被打断。
   */
  function handleMentionKey(e: React.KeyboardEvent<HTMLDivElement>): boolean {
    if (mention === null || e.nativeEvent.isComposing) return false;
    const n = suggestions.length;
    if (e.key === "ArrowDown" && n > 0) {
      e.preventDefault();
      setActiveSuggestion((i) => (i + 1) % n);
      return true;
    }
    if (e.key === "ArrowUp" && n > 0) {
      e.preventDefault();
      setActiveSuggestion((i) => (i - 1 + n) % n);
      return true;
    }
    if (e.key === "Escape") {
      e.preventDefault();
      dismissedAt.current = mention.start;
      setMention(null);
      return true;
    }
    // ★ 没有候选时 Enter **不拦** —— 用户打了个 `@zzz` 没找到东西,
    //   这时按回车的意思是「把这句话发出去」,不是「选中那个不存在的文件」。
    if ((e.key === "Enter" || e.key === "Tab") && n > 0) {
      e.preventDefault();
      pickFile(suggestions[activeSuggestion] ?? (suggestions[0] as FileSuggestion));
      return true;
    }
    return false;
  }

  function handleSkillKey(e: React.KeyboardEvent<HTMLElement>): boolean {
    if ((skillQuery === null && !skillPickerOpen) || e.nativeEvent.isComposing) return false;
    const n = slashItems.length;
    if ((e.key === 'ArrowDown' || e.key === 'ArrowUp') && n > 0) {
      e.preventDefault(); setActiveSkill((i) => e.key === 'ArrowDown' ? (i + 1) % n : (i - 1 + n) % n); return true;
    }
    if (e.key === 'Escape') { e.preventDefault(); dismissedSkillAt.current = skillQuery?.start ?? null; setSkillQuery(null); setSkillPickerOpen(false); input.current?.focus(); return true; }
    if ((e.key === 'Enter' || e.key === 'Tab') && n > 0) { e.preventDefault(); pickSlash(slashItems[activeSkill] ?? slashItems[0]!); return true; }
    return false;
  }

  function selectMode(mode: SessionMode): void {
    modeRef.current = mode;
    setValue((current) => ({ ...current, mode }));
    onSessionModeChange?.(mode);
  }

  function patch(p: Partial<ComposerValue>): void {
    const next = { ...value, ...p };
    setValue(next);
    // 写回工作区当新默认值。失败只记日志 —— 药丸已经生效了,
    // 一个存不下来的默认值不值得打断用户正在写的这句话。
    void updateWorkspace({
      id: workspace.id,
      settings: toSettings(next),
    }).catch((err: unknown) => {
      console.error("[composer] 工作区默认值写回失败:", err);
    });
  }

  /**
   * 压缩那两栏的写回。
   *
   * ★ **不走上面的 `patch`**,因为它不是 `ComposerValue` 的一部分:那个对象描述的是
   * 「这一条消息怎么发」,每次发送都读一遍;而压缩模型是工作区的一条常设配置,
   * 只有自动压缩和 /compact 读它。混进去会让每次切权限档位都顺带重写一遍压缩配置。
   * `toSettings` 不含这三个键,所以两条写回路径不会互相覆盖(主进程是浅合并)。
   *
   * 值直接读 `workspace.settings` —— 它由工作区 store 维持,写回成功后自己会更新。
   */
  function patchCompaction(p: Partial<WorkspaceSettings>): void {
    void updateWorkspace({ id: workspace.id, settings: p }).catch((err: unknown) => {
      console.error("[composer] 压缩配置写回失败:", err);
    });
  }

  /**
   * 生效模型 = 工作区选过的 → 应用默认 → 列表第一个。
   *
   * **兜底结果不写回工作区**:用户没选过,那 `defaultModel` 就该继续是空的。
   * 静默替他做主的话,以后他在设置页改了应用默认模型,这个工作区却不跟着变,
   * 而他并不知道自己什么时候「选」过。
   *
   * ★ 别名和供应商**整对**地兜底,不能各挑各的:三档来源里挑出别名 A、又从
   * 另一档挑出供应商 B 的话,拼出来的是一个谁也没配过的组合,候选集直接为空。
   */
  const { model, modelProviderId } =
    value.model !== ""
      ? { model: value.model, modelProviderId: value.modelProviderId }
      : fallbackModel.model !== ""
        ? { model: fallbackModel.model, modelProviderId: fallbackModel.modelProviderId }
        : { model: models[0]?.alias ?? "", modelProviderId: models[0]?.providerId };
  const provider = providerOf(model, modelProviderId);
  const selectedModel = models.find((m) => m.alias === model && m.providerId === provider?.id);
  const thinking = loaded ? normalizeModelThinkingLevel(value.thinking, selectedModel) : value.thinking;
  useEffect(() => {
    if (!loaded || thinking === value.thinking) return;
    setValue((current) => ({ ...current, thinking }));
    void updateWorkspace({ id: workspace.id, settings: { defaultThinking: thinking } }).catch(console.error);
  }, [loaded, thinking, value.thinking, workspace.id]);
  const modelLabel =
    model !== "" ? model : loaded ? t("chat.noModel") : t("common.loading");
  const builtInModes = modes.filter((mode) => isBuiltinModeId(mode.id));
  const customModes = modes.filter((mode) => !isBuiltinModeId(mode.id));
  const selectedMode = modes.find((mode) => mode.id === value.mode);
  const modeName = (mode: ModeDefinition): string => isBuiltinModeId(mode.id)
    ? t(`composer.mode.${mode.id}` as 'composer.mode.code' | 'composer.mode.plan' | 'composer.mode.acp')
    : mode.name;
  const modeDescription = (mode: ModeDefinition): string => isBuiltinModeId(mode.id)
    ? t(`composer.mode.${mode.id}Hint` as 'composer.mode.codeHint' | 'composer.mode.planHint' | 'composer.mode.acpHint')
    : mode.description;
  const selectedModeName = selectedMode === undefined ? value.mode : modeName(selectedMode);

  /*
    ── 灰字(Tab 接受):建议下一步 / 历史指令 / 热文件 / 打字续写 ──
    ★ 弹层开着时一律不出灰字:那一刻 Tab 归弹层(见 `handleMentionKey` / `handleSkillKey`)。
  */
  const ghost = useComposerGhost({
    input,
    workspace,
    sessionId: sessionId ?? null,
    draft,
    popupOpen: mention !== null || skillQuery !== null || skillPickerOpen,
    attachments: attachments.length,
    running,
    model,
    modelProviderId,
    suggestTrigger,
    mentionedFiles,
    commands: [...commands.map((c) => c.name), ...skills.map((sk) => sk.name)],
  });

  function submit(): void {
    const raw = draft.trim();
    // ★ 只有附件、没有文字也该能发 —— 拖一张图进来直接问「这是什么」是常见用法。
    //   但上传还没完成时不发:那样 parts 里会缺一张图,而用户以为发出去了。
    const hasReady = attachments.some((a) => a.status === "done");
    const pending = attachments.some((a) => a.status === "uploading");
    // Local actions below do not send attachments and remain available without a model.
    /*
      ★ 本地动作必须拦在 `applyCommand` **之前**:它不是模板,展开不出任何东西,
      放过去就会被当成普通文本原样发给模型。拦下之后只清草稿,不走 `onSend`。
    */
    const call = parseCommandInvocation(raw);
    const action = call === null
      ? undefined
      : localActions.find((a) => a.command.name === call.name.toLowerCase());
    // 需求:入口不可用时手输 /skillify 不能退化成普通模型请求;用户明确安装的同名模板仍可展开。
    if (call?.name.toLowerCase() === 'skillify' && action === undefined
      && !commands.some((command) => command.name.toLowerCase() === 'skillify')) return;
    if (action !== undefined) {
      /*
        ★ 把**此刻**药丸的值一起递出去：`/goal` 设立成功后要注入一条 kickoff，
          而那条消息必须跑在用户当前选的模型/档位上（同 `onSend` 那一行的理由）。
      */
      const goalIntent = action.command.name === 'goal' ? parseGoalCommand(call?.args ?? '') : undefined;
      const invalidGoal = goalIntent?.kind === 'invalid' || (goalIntent?.kind === 'set' && model === '');
      // Keep rejected conditions intact. Clear accepted commands before a draft tab binds to a session.
      if (!invalidGoal) onDraft("");
      dismissedAt.current = null;
      setMention(null);
      action.run(call?.args ?? "", { ...value, model, modelProviderId, thinking });
      return;
    }
    if (pending || (raw === "" && !hasReady) || model === "") return;
    // ★ 命令在**发送这一刻**才展开:草稿里一直留着 `/name args`(用户看得懂、也还能
    //   回去改),进 RunRequest 的才是展开后的完整提示词。不是命令调用就原样返回。
    const text = applyCommand(raw, commands);
    // 发送时打快照:药丸此刻的值进 RunRequest,run 跑起来后再改药丸不影响它
    // ★ `modelProviderId` 必须和 `model` 一起覆盖:两者都可能来自兜底而不在 `value` 里,
    //   只覆盖一半就会把「兜底的别名」配上「value 里那个陈旧的供应商」。
    onSend(text, { ...value, model, modelProviderId, thinking });
    ghost.onSubmit(raw);
    onDraft("");
    // 草稿清空了,弹层也得跟着走 —— 不清的话它会挂在一个已经不存在的查询上
    dismissedAt.current = null;
    setMention(null);
  }

  /**
   * 拖拽落入。★ **过滤掉目录** —— `DataTransfer` 里的目录项 `size` 为 0 且
   * 读不出内容,不拦的话会变成一堆失败的 chip。不递归展开:
   * 一个 `node_modules` 拖进来是几万个文件。
   */
  function handleDrop(e: React.DragEvent): void {
    if (onAttachFiles === undefined) return;
    const files = [...e.dataTransfer.files].filter(
      (f) => f.size > 0 || f.type !== "",
    );
    if (files.length === 0) return;
    e.preventDefault();
    setDragging(false);
    onAttachFiles(files);
  }

  /** 粘贴。★ 截图粘贴是最高频入口,而它只有 `files`,没有文件名 */
  function handlePaste(e: React.ClipboardEvent): void {
    if (onAttachFiles === undefined) return;
    const files = [...e.clipboardData.files];
    if (files.length === 0) return;
    // 不 preventDefault:剪贴板里可能同时有文字,那部分仍该正常粘进输入框
    onAttachFiles(files);
  }

  /*
    输入框外那排(环境 / 目标 / 模式 + 用量读数)量到的宽度。
    ★ 量的是**这一行自己**,不是窗口:同一个窗口里可以并排开两三个对话,
    窗口一动不动而这一列被拖窄才是常态。
  */
  const metaRow = useRef<HTMLDivElement | null>(null);
  const usageFits = useUsageFits(metaRow);

  return (
    <div className="shrink-0 px-6 pb-5">
      {/*
        ★ `surface-input` 不是 `surface-raised`:深色下两者同值,浅色下输入框是**纯白**
        (#ffffff),而 raised 卡片是 #f2eee6。合并了浅色主题下输入框就沉进背景里。

        背景和柔和阴影区分输入区,聚焦时略微抬高阴影。权限由药丸显示,
        运行状态由停止按钮显示;只有拖入文件时才显示轮廓,提示可放置区域。
      */}
      <div
        className={cn(
          // 苹果风格:大圆角玻璃输入框 —— 半透明底 + 浅色描边,聚焦时描边换成强调色。
          // ★ 不能加 backdrop-blur:它会让这个框成为 fixed 定位的包含块,框里弹出的
          //   上下文 / 权限等浮层就改成相对输入框定位,整块飞到右下角(见 ui/Menu.tsx 文件头)
          "relative mx-auto w-full max-w-[760px] rounded-[22px] border border-[var(--border-strong)] bg-surface-input/80 shadow-[0_8px_24px_rgb(0_0_0/0.08)] transition-[border-color,box-shadow] duration-200 focus-within:border-accent/60 focus-within:shadow-[0_0_0_3px_var(--focus-outline),0_8px_24px_rgb(0_0_0/0.1)] motion-reduce:transition-none",
          dragging && "ring-2 ring-accent/60",
        )}
        onDragOver={(e) => {
          if (onAttachFiles === undefined) return;
          e.preventDefault();
          setDragging(true);
        }}
        onDragLeave={() => {
          setDragging(false);
        }}
        onDrop={handleDrop}
      >
        {mention !== null && (
          <MentionPopup
            id={mentionListId}
            items={suggestions}
            active={activeSuggestion}
            loading={loadingFiles}
            onPick={pickFile}
            onHover={setActiveSuggestion}
          />
        )}
        {(skillQuery !== null || skillPickerOpen) && (
          <SkillPopup id={skillListId} items={slashItems} active={activeSkill} loading={skillsLoading} error={skillsError}
            search={skillPickerOpen ? skillPickerQuery : undefined}
            onSearch={(query) => { setSkillPickerQuery(query); setActiveSkill(0); }} onPick={pickSlash} onHover={setActiveSkill}
            labels={{ search: t('skills.searchPlaceholder'), loading: t('common.loading'), error: t('skills.loadFailed'), empty: t('skills.empty'), commands: t('commands.groupLabel'), skills: t('commands.skillsGroupLabel') }}
            onKeyDown={handleSkillKey} />
        )}

        <AttachmentTray
          items={attachments}
          onRemove={(k) => onRemoveAttachment?.(k)}
          onRetry={(k) => onRetryAttachment?.(k)}
        />

        <MentionInput
          value={draft}
          handle={input}
          metrics={DRAFT_METRICS}
          placeholder={
            ghost.view !== null && ghost.view.empty ? (
              <span className="flex min-w-0 items-baseline gap-2">
                <span className="min-w-0 truncate">{ghost.view.display}</span>
                {ghost.view.showHint && <GhostKey />}
              </span>
            ) : running ? t("chat.queuePlaceholder") : started ? t("chat.placeholder") : t("composer.placeholder.fresh")
          }
          ghost={ghost.view !== null && !ghost.view.empty ? ghost.view.display : undefined}
          skillDescriptions={Object.fromEntries(skills.map((skill) => [skill.name, skill.description]))}
          aria-expanded={mention !== null || skillQuery !== null}
          aria-controls={mention !== null ? mentionListId : skillQuery !== null ? skillListId : undefined}
          aria-activedescendant={
            mention !== null && suggestions.length > 0
              ? `${mentionListId}-${String(activeSuggestion)}`
              : skillQuery !== null && slashItems.length > 0 ? `${skillListId}-${activeSkill}` : undefined
          }
          onChange={(text, caret) => {
            onDraft(text);
            lastCaret.current = caret;
            syncMention(text, caret);
            syncSkill(text, caret);
            ghost.onInput(text, caret);
          }}
          // 点击 / 方向键挪动光标也要重算 —— `@` 的判据是位置不是按键
          onCaret={(text, caret) => {
            lastCaret.current = caret;
            syncMention(text, caret);
            syncSkill(text, caret);
            ghost.onCaret(text, caret);
          }}
          onComposing={(v) => {
            composing.current = v;
            ghost.onComposing(v);
          }}
          onFocus={ghost.onFocus}
          onBlur={() => { setMention(null); setSkillQuery(null); ghost.onBlur() }}
          onPaste={(e) => { ghost.onPaste(); handlePaste(e) }}
          onKeyDown={(e) => {
            // 弹层开着时方向键 / Enter / Tab / Esc 归它,先问一句
            if (handleMentionKey(e)) return;
            if (handleSkillKey(e)) return;
            // 然后是灰字:Tab / → / ⌘→ / Esc。★ Enter 不在其中 —— 接受和发送是两个动作
            if (ghost.handleKey(e)) return;
            // Enter 发送,Shift+Enter 换行。输入法组词期间的 Enter 是「上屏」,
            // 不是「发送」—— 少了 isComposing 这个判断,中文用户每打一个词就发一次。
            if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
              e.preventDefault();
              submit();
            }
          }}
        />

        {/*
          ★ `flex-wrap`:这一排的宽度是**面板宽度**,不是窗口宽度 —— 三四个工作区
          并排铺开时一列只有三百多像素,而这排控件的最小宽度比它大。不让它换行的话
          整排会溢出到输入框边框外面去(模型名压在边框上)。换行后 `justify-end`
          让溢出的那半排(模型 + 发送)仍然贴右,保持「左=怎么跑 / 右=发给谁」。
        */}
        <div className="flex flex-wrap items-center justify-end gap-1 px-2.5 pt-1 pb-2.5">
          {/* ── 权限档位:界面上就在这个位置 ── */}
          <Menu
            label={t("composer.permission")}
            width={280}
            triggerClassName="rounded-full focus-visible:outline-2 focus-visible:outline-accent"
            trigger={
              <Pill accent={value.permissionMode === "full"}>
                {t(`permission.${value.permissionMode}` as "permission.ask" | "permission.auto" | "permission.full")}
                <ChevronDown size={11} className="text-fg-faint" />
              </Pill>
            }
          >
            {(close) => (
              <>
                <div className="px-2 pt-1.5 pb-1 text-[11px] text-fg-faint">{t("composer.permission")}</div>
                {PERMISSION_MODES.map((m) => (
                  <ComposerMenuItem
                    key={m}
                    checked={m === value.permissionMode}
                    selection="radio"
                    icon={m === "ask" ? <ShieldQuestion size={16} /> : m === "auto" ? <ShieldCheck size={16} /> : <Unlock size={16} />}
                    description={t(`permission.${m}Hint` as "permission.askHint" | "permission.autoHint" | "permission.fullHint")}
                    onSelect={() => {
                      patch({ permissionMode: m });
                      onPermissionModeChange?.(m);
                      close();
                    }}
                  >
                    {t(`permission.${m}` as "permission.ask" | "permission.auto" | "permission.full")}
                  </ComposerMenuItem>
                ))}
                <div className="mx-2 mt-2 pt-1.5 pb-1 text-[10px] leading-4 text-fg-faint">
                  {t("composer.approvalHint")}
                </div>
              </>
            )}
          </Menu>

          {/* ── `+` 统一收纳附件、Skills 和联网开关 ── */}
          <Menu
            label={t("composer.more")}
            width={320}
            onOpenChange={(open) => { if (!open) setPluginToolsOpen(false); }}
            triggerClassName="group rounded-full focus-visible:outline-2 focus-visible:outline-accent"
            trigger={
              <span className={cn(
                "relative flex h-7 w-7 items-center justify-center rounded-full text-fg-muted transition-colors hover:bg-tint-hover hover:text-fg group-aria-expanded:bg-tint",
                value.webSearch && "bg-tint text-fg",
              )}>
                <Plus size={16} />
                {value.webSearch && (
                  <span aria-hidden="true" className="absolute top-1 right-1 h-1 w-1 rounded-full bg-accent" />
                )}
              </span>
            }
          >
            {(close) => pluginToolsOpen ? (
              <>
                <button type="button" role="menuitem" className="flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left text-fg hover:bg-tint focus-visible:outline-2 focus-visible:outline-accent" onClick={() => setPluginToolsOpen(false)}>
                  <span aria-hidden="true">←</span>{t('pluginTools.back')}
                </button>
                <div className="px-2 pt-1.5 pb-1 text-[11px] text-fg-faint">{t('pluginTools.hint')}</div>
                {pluginTools.map((plugin) => (
                  <div key={plugin.pluginId} className="px-2 py-1">
                    <div className="truncate text-[11px] font-medium text-fg-muted">{plugin.displayName}</div>
                    {plugin.tools.map((tool) => (
                      <div key={tool.name} className="truncate py-1 pl-3 text-[12px] text-fg" title={tool.name}>
                        {t(tool.titleKey as Parameters<typeof t>[0])}
                      </div>
                    ))}
                  </div>
                ))}
              </>
            ) : (
              <>
                <div className="px-2 pt-1.5 pb-1 text-[11px] text-fg-faint">{t("composer.add")}</div>
                <ComposerMenuItem
                  icon={<Paperclip size={16} />}
                  disabled={onPickAttachment === undefined}
                  description={t("composer.attachmentHint")}
                  onSelect={() => {
                    close();
                    onPickAttachment?.();
                  }}
                >
                  {t("composer.addAttachment")}
                </ComposerMenuItem>
                <ComposerMenuItem icon={<Sparkles size={16} />} description={t('composer.skillsHint')}
                  onSelect={() => {
                    skillInsertRange.current = input.current?.selection() ?? { start: draft.length, end: draft.length };
                    setSkillQuery(null); setMention(null); setSkillPickerQuery(''); setActiveSkill(0);
                    close(); setSkillPickerOpen(true);
                  }}>{t('composer.skills')}</ComposerMenuItem>
                {pluginTools.length > 0 && (
                  <ComposerMenuItem
                    icon={<Puzzle size={16} />}
                    description={t('pluginTools.hint')}
                    onSelect={() => setPluginToolsOpen(true)}
                  >{t('pluginTools.list')}</ComposerMenuItem>
                )}
                <MenuSeparator />
                <div className="px-2 pt-1 pb-1 text-[11px] text-fg-faint">{t("common.settings")}</div>
                <ComposerMenuItem
                  checked={value.webSearch}
                  icon={<Globe size={16} />}
                  // 「完全访问」也不解除这个开关(方案 §4.5),菜单上要说出来
                  description={t("composer.webSearchHint")}
                  onSelect={() => {
                    patch({ webSearch: !value.webSearch });
                  }}
                >
                  {t("composer.webSearch")}
                </ComposerMenuItem>
              </>
            )}
          </Menu>

          {/*
            非空草稿上挂着灰字时,这一格说一句「Tab 接受」。★ 放在药丸行的空白里而不是叠在
            文字上:灰字可能一直写到行尾,任何叠上去的提示都会盖住它。
          */}
          <div className="flex min-w-0 flex-1 items-center px-2">
            {ghost.view !== null && !ghost.view.empty && ghost.view.showHint && (
              <span className="flex items-center gap-1 truncate text-[11px] text-fg-faint">
                <GhostKey />
                {t("composer.ghost.accept")}
              </span>
            )}
          </div>
          <span className="sr-only" aria-live="polite">
            {ghost.view !== null ? t("composer.ghost.announce", { text: ghost.view.announce }) : ""}
          </span>

          {/*
            ── 右半边:「怎么想 / 装得下多少 / 发给谁」 ──
            左半边(权限档位 / `+`)说的是「允许它做什么」。这三颗加发送是另一件事。

            ★ 思考强度从模型菜单的二级页搬到这里。原来它的**显示**在工具栏(一个
            点不动的只读徽标)、**操作**在模型药丸 → 滚到底 → 模型配置 → 第二页,
            改一个每轮都要动的旋钮要走四步。显示和操作分居两地正是「不方便」的根因。
          */}
          <ThinkingPill
            model={selectedModel}
            thinking={thinking}
            onThinking={(next) => patch({ thinking: next })}
          />

          {/*
            ── 上下文余量:紧挨模型选择器的左边 ──
            它说的是「发给谁」之前的那个前提 —— 这一次还装得下多少,以及愿不愿意
            为超出 272K 的部分付双倍。双击仍是手动压缩(老肌肉记忆),但现在有菜单了。
          */}
          <ContextRing
            used={contextTokens}
            segments={contextSegments}
            preview={preview}
            onMenuOpen={() => {
              /* 真实归因已经在了就别再估一遍 —— 估出来的那份从这一刻起
                 不会被显示,而它每次都要把整份工具清单重新走一遍。 */
              if (contextSegments !== undefined) return;
              void previewContext({
                sessionId: sessionId ?? "",
                workspaceId: workspace.id,
                model: value.model,
                ...(value.modelProviderId === undefined
                  ? {}
                  : { modelProviderId: value.modelProviderId }),
                mode: value.mode,
                thinking: value.thinking,
                permissionMode: value.permissionMode,
                webSearch: value.webSearch,
                maxContext: value.maxContext,
              })
                .then(setPreview)
                /* 预览失败就当没有 —— 这张卡是诊断,不该把一次 IPC 抖动变成一个报错弹窗。 */
                .catch(() => setPreview(undefined));
            }}
            cacheHitRate={contextCacheHitRate}
            onManageMcp={onManageMcp}
            model={selectedModel}
            maxContext={value.maxContext}
            onMaxContext={(next) => patch({ maxContext: next })}
            compaction={{
              override: workspace.settings,
              models,
              providers,
              onChange: patchCompaction,
            }}
            running={running}
            compacting={contextCompacting}
            onCompact={onCompactContext}
          />

          {/*
            ── 模型选择器:靠右,紧挨发送按钮 ──
            截图 c6184031 里这一排是**两头分布**的:左边是「这一轮怎么执行」
            (权限档位 / `+` 会话选项),右边是「发给谁」加发送。
            模型属于后者 —— 它和发送按钮是一件事的两半,挤在左边那堆开关里
            会被当成又一个开关。
          */}
          <ModelPicker
            model={model}
            modelProviderId={modelProviderId}
            modelLabel={modelLabel}
            provider={provider}
            providers={providers}
            models={models}
            loaded={loaded}
            onModel={(nextModel, nextProviderId) => {
              patch({
                model: nextModel,
                // 用户从菜单里点选是**唯一**会把供应商写进工作区的时机(兜底不写回)。
                modelProviderId: nextProviderId,
                /*
                  ★ thinking 必须 normalize,而 maxContext **故意不 normalize**。
                  不对称的理由:下发一个新模型不认的 reasoning effort 会被上游拒;
                  而 maxContext 多留一个 true 没有任何下游后果 —— `effectiveContextWindow`
                  的 `min()` 已经兜住了窗口不够大的模型。抹掉它反而会让
                  「sol → claude → sol」这条常见路径静默丢掉用户的选择。
                */
                thinking: normalizeModelThinkingLevel(thinking, models.find(
                  (m) => m.alias === nextModel && m.providerId === nextProviderId
                ))
              });
              /*
                ★ 同一次点选还要落到**这条会话**上:工作区默认值是给新会话用的,
                只写它的话,在另一条会话里换模型就会把这条也换掉 —— 正是这次修的那件事。
              */
              onModelChange?.(nextModel, nextProviderId);
            }}
          />

          <button
            type="button"
            data-testid="composer-send"
            aria-label={running ? t("chat.stop") : t("chat.send")}
            // 生成中按钮变「停止」,但输入框仍可打字 —— 排队走 Enter
            onClick={running ? onStop : submit}
            disabled={!running && (draft.trim() === "" || model === "")}
            title={
              running
                ? t("composer.stopGeneration")
                : model === ""
                  ? t("composer.noAvailableModel")
                  : model
            }
            className={cn(
              "flex h-7 w-7 shrink-0 items-center justify-center rounded-pill transition-colors",
              running
                ? "bg-tint-strong text-fg hover:bg-tint-hover"
                : "bg-accent text-accent-fg hover:opacity-90",
              "disabled:cursor-not-allowed disabled:bg-tint disabled:text-fg-faint",
            )}
          >
            {running ? (
              <Square size={12} fill="currentColor" />
            ) : (
              <ArrowUp size={15} />
            )}
          </button>
        </div>
      </div>
      {/*
        ── 输入框**外面**的这一行:环境 + 模式 ──
        两者说的都是「这一轮跑在哪、按哪套规矩跑」,而不是「这条消息怎么发」——
        后者(权限档位 / `+` / 模型 / 发送)才留在框内那一排。放到框外还有个好处:
        框内那排在窄窗口下已经两头顶死了,再塞一颗模式药丸就会把模型名挤成省略号。
      */}
      {onGoalCommand !== undefined && <GoalPanel
        open={goalPanelOpen}
        goal={goal}
        tokens={goal === undefined ? undefined : Math.max(goal.tokens ?? 0,
          (conversationUsage === undefined ? goal.tokensAtStart : conversationUsage.inputTokens
            + conversationUsage.outputTokens + conversationUsage.cacheReadTokens + conversationUsage.cacheWriteTokens) - goal.tokensAtStart)}
        onClose={() => setGoalPanelOpen(false)}
        onSet={(condition) => Promise.resolve(onGoalCommand(condition, { ...value, model, modelProviderId, thinking }))}
        onClear={() => Promise.resolve(onGoalCommand('clear', { ...value, model, modelProviderId, thinking }))}
      />}
      {/* 同框内那排一样要能换行 —— 自定义模式名可以很长,而列宽是面板给的 */}
      <div ref={metaRow} className="mx-auto mt-1.5 flex w-full max-w-[760px] flex-wrap items-center gap-1">
        <EnvironmentPill workspace={workspace} />
        {onGoalCommand !== undefined && <GoalPill goal={goal} onOpen={() => setGoalPanelOpen(true)}
          onClear={() => { void onGoalCommand('clear', { ...value, model, modelProviderId, thinking }) }} />}


        <Menu
          label={t('composer.mode.label')}
          width={320}
          triggerClassName="rounded-full focus-visible:outline-2 focus-visible:outline-accent"
          trigger={
            <Pill accent={value.mode !== 'code'}>
              {selectedModeName}
              <ChevronDown size={11} className="text-fg-faint" />
            </Pill>
          }
        >
          {(close) => (
            <>
              <div className="px-2 pt-1.5 pb-1 text-[11px] text-fg-faint">{t('composer.mode.builtIn')}</div>
              {builtInModes.map((mode) => (
                <ComposerMenuItem
                  key={mode.id}
                  checked={mode.id === value.mode}
                  selection="radio"
                  icon={mode.id === 'code' ? <Code2 size={16} /> : mode.id === 'plan' ? <Lightbulb size={16} /> : <Workflow size={16} />}
                  description={modeDescription(mode)}
                  onSelect={() => { selectMode(mode.id); close(); }}
                >
                  {modeName(mode)}
                </ComposerMenuItem>
              ))}
              {customModes.length > 0 && <>
                <MenuSeparator />
                <div className="px-2 pt-1 pb-1 text-[11px] text-fg-faint">{t('composer.mode.custom')}</div>
                {customModes.map((mode) => (
                  <ComposerMenuItem
                    key={mode.id}
                    checked={mode.id === value.mode}
                    selection="radio"
                    icon={<Settings2 size={16} />}
                    description={mode.description}
                    onSelect={() => { selectMode(mode.id); close(); }}
                  >
                    {mode.name}
                  </ComposerMenuItem>
                ))}
              </>}
              <MenuSeparator />
              <ComposerMenuItem
                icon={<Check size={16} />}
                description={t('composer.mode.setDefaultHint')}
                onSelect={() => { onSetDefaultMode?.(value.mode); close(); }}
              >
                {t('composer.mode.setDefault')}
              </ComposerMenuItem>
              {onManageModes !== undefined && (
                <ComposerMenuItem
                  icon={<Settings2 size={16} />}
                  description={t('composer.mode.manageHint')}
                  onSelect={() => { close(); onManageModes(); }}
                >
                  {t('composer.mode.manage')}
                </ComposerMenuItem>
              )}
            </>
          )}
        </Menu>

        {/* 用量读数和左边那两颗同处一行:它们都在描述「这个会话此刻的状态」,
            各占一行会在输入框下面堆出两条几乎空着的横带。

            ★ 放不下就**整块不画**,既不换行也不截断(`usageFits`)。这七个读数是
            一组互相参照的数(输入/缓存/输出/命中率/花费),留一半在行上只会让人
            按错的口径去读;而换到第二行就等于在窄面板里把输入框往上顶一整行。
            藏掉也不丢信息:每一轮的完整用量在转录里那张「任务用量」上。 */}
        {conversationUsage !== undefined && usageFits && (
          <div className="flex min-w-0 flex-1 justify-end pl-3">
            <ConversationUsage usage={conversationUsage} sessionId={sessionId ?? null} />
          </div>
        )}
      </div>
    </div>
  );
}

/**
 * 当前环境:本机 / 某条 SSH 连接。**只读徽标**,不是切换入口 ——
 * 换环境等于换工作区根(见 `workspace:prepare`),那不是发消息途中该顺手做的事。
 *
 * ★ 连接名要单独拉一次 `connection:list`:工作区上存的只有 `connectionId`,
 * 而用户认得的是自己给那台机器起的名字。拉不到就退回中性的「远程」——
 * 编一个 id 当名字显示没有任何意义。
 */
function EnvironmentPill({ workspace }: { workspace: Workspace }): ReactNode {
  const { t } = useI18n();
  const ref = normalizeEnvironmentRef(workspace.environment);
  const connectionId = ref.kind === "connection" ? ref.connectionId : undefined;
  const [connectionName, setConnectionName] = useState<string | undefined>(undefined);
  useEffect(() => {
    if (connectionId === undefined) {
      setConnectionName(undefined);
      return;
    }
    let cancelled = false;
    const refresh = (): void => {
      void listConnections()
        .then((items) => {
          if (cancelled) return;
          setConnectionName(items.find((item) => item.profile.id === connectionId)?.profile.name);
        })
        // 名字拉不到不该在输入框下面弹错误:徽标退回「远程」照样说清楚了这不是本机。
        .catch(() => { if (!cancelled) setConnectionName(undefined); });
    };
    refresh();
    const off = onConnectionsChanged(refresh);
    return () => { cancelled = true; off(); };
  }, [connectionId]);

  const label = ref.kind === "local"
    ? t("composer.environment.local")
    : ref.kind === "unbound"
      ? t("composer.environment.unbound")
      // 连接名是用户自己起的名字 —— 域内容,不翻译。
      : connectionName ?? t("composer.environment.remote");
  return (
    <span
      title={`${t("composer.environment.label")}: ${label} · ${workspace.rootPath}`}
      aria-label={`${t("composer.environment.label")}: ${label}`}
      data-testid="composer-environment"
    >
      <Pill readonly>
        {ref.kind === "local"
          ? <Monitor aria-hidden="true" size={12} />
          : <Server aria-hidden="true" size={12} />}
        <span className="max-w-[160px] truncate">{label}</span>
      </Pill>
    </span>
  );
}

/**
 * 这排读数需要的最小行宽(px)。
 *
 * 来历:量过一次实际布局 —— 左边三颗药丸(环境 / 目标 / 模式)约 170,
 * 七个读数连着 `gap-x-4` 约 410,中间留一段呼吸位,合起来 600 出头。
 * 数值偏保守是有意的:读数的宽度随语言和位数变(`18.4M` / `1,234,567`),
 * 卡着极限画,换成英文或跑久了位数变多就又贴到药丸上去了。
 * 重新量过就改这个数,不要在渲染处另加一个特判。
 */
const USAGE_MIN_ROW_WIDTH = 620;

/**
 * 输入框下面那排装不装得下用量读数。
 *
 * ★ 用 `ResizeObserver` 而不是窗口宽度 / CSS 断点:窄的是**这一列**(dock 可以
 * 左右分栏、右侧面板可以拖宽),窗口自始至终没变过 —— 按窗口断点判会在
 * 「窗口很宽、这一列很窄」时原样把读数画出去,正是要修的那个样子。
 *
 * 初值取 true:首帧还没量到宽度,此时先画、由观察者在同一帧回调里纠正,
 * 比先藏再冒出来晃一下安分。
 */
function useUsageFits(ref: React.RefObject<HTMLElement | null>): boolean {
  const [fits, setFits] = useState(true);
  useEffect(() => {
    const element = ref.current;
    if (element === null) return;
    const observer = new ResizeObserver(([entry]) => {
      if (entry !== undefined) setFits(entry.contentRect.width >= USAGE_MIN_ROW_WIDTH);
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, [ref]);
  return fits;
}

function ConversationUsage({ usage, sessionId }: { usage: ConversationUsageSummary; sessionId: string | null }): ReactNode {
  const { t, locale } = useI18n();
  const exact = (value: number): string => value.toLocaleString(locale);
  /*
    ★ 口径与转录区那条「任务用量」保持一致（Thread.tsx 的 cacheRate）：
    分母是**输入总量**（未命中 + 缓存读 + 缓存写），不是 input 单项。
    两处若各算各的，同一轮会给出两个不同的命中率。

    输入为 0 时给「—」而不是 0.0%：这里的 0 是「还没有输入可谈」，
    而 0.0% 会被读成「缓存一次都没命中」——和 TPS、花费算不出时同一处理。
  */
  const inputTotal = usage.inputTokens + usage.cacheReadTokens + usage.cacheWriteTokens;
  const cacheRate = inputTotal > 0 ? usage.cacheReadTokens / inputTotal : undefined;
  const metrics = [
    {
      key: "input",
      icon: <LogIn size={12} />,
      label: t("chat.conversationUsageInput"),
      value: formatTokenCount(usage.inputTokens),
      exact: exact(usage.inputTokens),
    },
    {
      key: "cache-read",
      icon: <Database size={12} />,
      label: t("chat.conversationUsageCacheRead"),
      value: formatTokenCount(usage.cacheReadTokens),
      exact: exact(usage.cacheReadTokens),
    },
    {
      key: "cache-write",
      icon: <DatabaseZap size={12} />,
      label: t("chat.conversationUsageCacheWrite"),
      value: formatTokenCount(usage.cacheWriteTokens),
      exact: exact(usage.cacheWriteTokens),
    },
    {
      key: "cache-rate",
      icon: <Percent size={12} />,
      label: t("chat.conversationUsageCacheRate"),
      value: cacheRate === undefined ? "—" : `${(cacheRate * 100).toFixed(1)}%`,
      // 悬停给出算式本身 —— 百分比看不出是拿哪两个数除出来的
      exact: cacheRate === undefined
        ? "—"
        : `${exact(usage.cacheReadTokens)} / ${exact(inputTotal)}`,
    },
    {
      key: "output",
      icon: <LogOut size={12} />,
      label: t("chat.conversationUsageOutput"),
      value: formatTokenCount(usage.outputTokens),
      exact: exact(usage.outputTokens),
    },
    {
      key: "tps",
      icon: <Gauge size={12} />,
      label: t("chat.conversationUsageLatestTps"),
      value: usage.latestTps === undefined
        ? "—"
        : t("chat.conversationUsageTpsValue", { tps: formatTokensPerSecond(usage.latestTps) }),
      exact: usage.latestTps === undefined
        ? "—"
        : t("chat.conversationUsageTpsValue", { tps: formatTokensPerSecond(usage.latestTps) }),
    },
    {
      key: "cost",
      icon: <CircleDollarSign size={12} />,
      label: t("chat.conversationUsageCost"),
      value: usage.cost == null ? "—" : formatCostMicros(usage.cost.micros, usage.cost.currency),
      exact: usage.cost == null ? "—" : formatCostMicros(usage.cost.micros, usage.cost.currency),
    },
  ];
  return (
    <div
      data-testid="conversation-usage"
      className="flex min-w-0 flex-wrap items-center justify-end gap-x-4 gap-y-1 text-[10.5px] tabular-nums text-fg-faint"
    >
      {metrics.map((metric) => (
        /*
          ★ 气泡而不是原生 `title`：原生提示要停顿约一秒才弹、样式不受控，
          而这一行每个数字都是缩写过的（4.1M / $47.35），不悬停就读不到口径和精确值。
          `Tooltip` 顺带解决了键盘可达（focus 也触发）和被祖先 overflow 裁切。
        */
        <Tooltip
          key={metric.key}
          align="center"
          interactive={metric.key === "cost"}
          contentClassName={metric.key === "cost" ? "max-h-[min(60vh,480px)] overflow-y-auto" : undefined}
          content={metric.key === "cost"
            // 需求：总费用悬停要展开当前会话的逐模型与逐类账目；其余读数保留原精确值提示。
            ? <ConversationCostDetails sessionId={sessionId} cost={usage.cost}
                revision={`${usage.inputTokens}:${usage.outputTokens}:${usage.cacheReadTokens}:${usage.cacheWriteTokens}:${usage.cost?.micros ?? ''}`} />
            : <>
                <div className="font-medium">{metric.label}</div>
                {/* 原先 TPS、花费的精确值都不重复；现在花费另有明细，这里只处理普通读数。 */}
                {metric.exact !== metric.value && (
                  <div className="tabular-nums text-fg-muted">{metric.exact}</div>
                )}
              </>
          }
        >
          <span
            role="group"
            tabIndex={0}
            aria-label={`${metric.label}: ${metric.exact}`}
            className="inline-flex cursor-help items-center gap-1 whitespace-nowrap rounded-[3px] outline-none focus-visible:ring-2 focus-visible:ring-fg-faint/50"
          >
            <span aria-hidden="true" className="text-fg-muted">{metric.icon}</span>
            <span>{metric.value}</span>
          </span>
        </Tooltip>
      ))}
    </div>
  );
}

/** 输入框菜单的操作与开关共用一行布局，开关保持菜单展开以便连续调整。 */
/** 灰字旁边那枚 `Tab` 键帽 —— 用户普遍不知道灰字能按 Tab 接受,接受过几次之后收起 */
function GhostKey(): ReactNode {
  return (
    <kbd className="inline-flex h-[16px] shrink-0 items-center rounded-[4px] border border-[var(--border-strong)] px-1 font-sans text-[10px] leading-none text-fg-faint">
      Tab
    </kbd>
  );
}

function ComposerMenuItem({
  children,
  icon,
  description,
  checked,
  selection = "toggle",
  disabled = false,
  onSelect,
}: {
  children: ReactNode;
  icon: ReactNode;
  description: string;
  checked?: boolean;
  selection?: "radio" | "toggle";
  disabled?: boolean;
  onSelect: () => void;
}): ReactNode {
  return (
    <button
      type="button"
      role={checked === undefined ? "menuitem" : selection === "radio" ? "menuitemradio" : "menuitemcheckbox"}
      aria-checked={checked}
      title={description}
      disabled={disabled}
      onClick={onSelect}
      className={cn(
        "flex min-h-8 w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left text-fg transition-colors hover:bg-tint focus-visible:bg-tint focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-accent disabled:opacity-40",
        checked && selection === "radio" && "bg-tint/70",
      )}
    >
      <span aria-hidden="true" className="shrink-0 text-fg-muted [&>svg]:size-3.5">{icon}</span>
      <span className={cn("min-w-0 flex-1", selection !== "radio" && "flex items-baseline gap-1.5")}>
        <span className="block shrink-0 text-[12px] leading-[18px]">{children}</span>
        <span className="block truncate text-[11px] leading-4 text-fg-faint">{description}</span>
      </span>
      {checked !== undefined && (
        selection === "radio" ? (
          <Check aria-hidden="true" size={13} className={cn("shrink-0 text-accent", !checked && "invisible")} />
        ) : (
          <span aria-hidden="true" className={cn("flex h-3.5 w-6 shrink-0 items-center rounded-full p-0.5 transition-colors", checked ? "bg-accent" : "bg-tint-strong")}>
            <span className={cn("h-2.5 w-2.5 rounded-full transition-transform motion-reduce:transition-none", checked ? "translate-x-2.5 bg-accent-fg" : "bg-fg-muted")} />
          </span>
        )
      )}
    </button>
  );
}

function Pill({
  children,
  active = false,
  readonly = false,
  accent = false,
  className,
}: {
  children: ReactNode;
  active?: boolean;
  /** 只读徽标:重复显示 `+` 菜单里已开的项,让它们在收起状态下也看得见 */
  readonly?: boolean;
  /**
   * 参考实现里**整个浅色界面只有两处用色**,这排药丸占掉一处(另一处是发送按钮):
   * 「完全访问」是底 `accent/10` + 字/图标 accent,其余档位是中性的。
   * 所以这不是「一种药丸样式」,是「这一档要提醒你它放开了权限」——
   * 拿它去染别的药丸,界面里唯一的色相就失去意义了。
   *
   * ★ 底色写半透明而不是一个实色 token:量到的深 #2a3d33 / 浅 #e8ebe9
   *   反解出来正好都是 accent @10% 压在输入框底上(`theme.css` §5)。
   *   于是换颜色主题时药丸底自己跟着 accent 走,不用另外声明。
   */
  accent?: boolean;
  /** 只给需要**退让**的药丸用(模型名):默认 `shrink-0`,窄面板里靠它改成可压缩 */
  className?: string;
}): ReactNode {
  return (
    <span
      className={cn(
        // ★ `whitespace-nowrap`:药丸是**一行一个词**的东西。少了它,面板窄到放不下
        //   这一排时「完全访问」会一个字一行竖着排下来(不是溢出,是换行)。
        "flex h-7 shrink-0 items-center gap-1.5 rounded-pill px-2.5 text-[12.5px] whitespace-nowrap",
        readonly
          ? "bg-tint/60 text-fg-muted"
          : accent
            ? "bg-accent/10 text-accent transition-colors"
            : "transition-colors hover:bg-tint-hover " +
              (active ? "bg-tint text-fg" : "text-fg-muted hover:text-fg"),
        className,
      )}
    >
      {children}
    </span>
  );
}

/** 超过它就换成告警色 —— 和状态行那根压力条同一个判据。 */
const CONTEXT_WARN = 0.75;

/**
 * 思考强度药丸。
 *
 * ★ 它取代的是「工具栏上一个点不动的只读徽标 + 模型菜单第四层的二级页」这个组合。
 * 原来**显示**和**操作**分居两地,改一个每轮都要动的旋钮要走四步,这正是「不方便」的根因。
 *
 * 三处刻意的选择:
 * - `thinking === 'auto'` 时**照样显示**(旧徽标偏偏在 auto 时整个消失)——
 *   auto 恰恰是最需要能一键调走的那个值。
 * - 触发器用 `active` 而不是 `accent`:整个浅色界面只有两处用色,见 `Pill` 的注释。
 * - 面板里是**滑杆**不是七行单选:强度本来就是个有序量,列表把它画成了七个互不相干的
 *   选项,还要占掉 300px 的高度。滑杆顺带白拿了左右方向键(原生 `range` 的语义),
 *   而这正是这颗药丸要解决的「切一档要走四步」。
 *   代价是滑杆没有逐档的文字,所以标题行必须**拖动中实时跟着变**(`onPreview`)。
 */
function ThinkingPill({
  model,
  thinking,
  onThinking,
}: {
  model?: ModelAlias;
  thinking: ThinkingLevel;
  onThinking: (level: ThinkingLevel) => void;
}): ReactNode {
  const { t } = useI18n();
  const levels = modelThinkingLevels(model);
  const config = model === undefined ? undefined : effectiveModelThinking(model).thinkingConfig;
  const label = (level: ThinkingLevel): string =>
    t(
      config?.mode === "toggle" && level === "medium"
        ? "chat.thinkingOn"
        : `chat.thinkingLevel.${level}`,
    );
  const defaultLevel: ThinkingLevel =
    config?.defaultEnabled !== true || config.defaultEffort === "none"
      ? "off"
      : config.defaultEffort === "xhigh"
        ? "higher"
        : config.defaultEffort ?? "medium";
  const description = config?.mode === "unsupported" ? t("chat.thinkingUnsupported")
    : config?.mode === "always" ? t("chat.thinkingAlways")
    : thinking === "auto" && config !== undefined
      ? t("chat.thinkingAutomatic", { level: label(defaultLevel) })
      : t("chat.thinkingLevelDescription", { level: label(thinking) });
  /*
    ★ 滑杆的轨道上**只放有序的强度**,`auto` 不在轴上。

    「自动」的意思是"不指定,由模型和协议自己决定",把它塞进轨道的任何一个位置
    都是在声明一个它并不具有的序关系 —— 所以它单独占一行。而 `off` 在
    `THINKING_LEVELS` 里排在**末尾**(那是给菜单列表用的排法),上了轴必须挪到最左:
    强度为 0 就该在最左边,否则拖到头反而是关掉。
  */
  const track: ThinkingLevel[] = [
    ...(levels.includes("off") ? (["off"] as ThinkingLevel[]) : []),
    ...levels.filter((level) => level !== "auto" && level !== "off"),
  ];
  /*
    `auto` 档下滑杆停在**这个模型自动会落到的那一档**上并调暗 ——
    比停在 0 诚实:auto 不等于不思考。查不到就落 0(只发生在
    `defaultEffort` 指向一个该模型不提供的档位时,而那时滑杆本来就是暗的)。
  */
  const shown = thinking === "auto" ? defaultLevel : thinking;
  const index = Math.max(0, track.indexOf(shown));
  // 拖动中的实时读数,只用来更新标题那行;写回走 onCommit。null = 没在拖。
  const [preview, setPreview] = useState<number | null>(null);
  const headline = preview === null ? thinking : (track[preview] ?? thinking);

  /*
    药丸**聚焦时**的 ↑/↓ —— 面板根本不用打开。

    这颗药丸存在的全部理由是把「改一个每轮都要动的旋钮」从四步压到一步,
    而「Tab 过去 → Enter 开面板 → 拖滑杆 → Esc」仍然是四步。
    左右也收,因为面板里那根滑杆就是横的,两套方向在同一颗控件上指的是同一件事。

    ★ 从 `auto` 出发时按一下就**移动一格**,而不是原地落到 `defaultLevel`。
      滑杆本来就停在 defaultLevel 上(只是暗的),原地点亮不动看起来像没反应。
  */
  const shift = (delta: number): void => {
    if (track.length < 2) return;
    const from = track.indexOf(shown);
    const next = track[Math.min(track.length - 1, Math.max(0, (from < 0 ? 0 : from) + delta))];
    // 已经在两端时不写回 —— 顶着边连按不该每次都走一遍工作区落盘。
    if (next !== undefined && next !== thinking) onThinking(next);
  };

  /*
    只有一档可选 = 没什么可切的(unsupported / always / 模型还没加载完)。
    整颗不渲染,而不是置灰 —— 一颗永远点不动的药丸只是在工具栏上占位置。
  */
  if (levels.length < 2) return null;
  return (
    <Menu
      label={t("composer.thinking")}
      width={232}
      align="end"
      onOpenChange={(open) => {
        if (!open) setPreview(null);
      }}
      onTriggerKeyDown={(e) => {
        // ★ 不碰 Enter / Space —— 那是 button 打开菜单的原生路径。
        const delta =
          e.key === "ArrowUp" || e.key === "ArrowRight"
            ? 1
            : e.key === "ArrowDown" || e.key === "ArrowLeft"
              ? -1
              : 0;
        if (delta === 0) return;
        e.preventDefault(); // 否则上下键会把整个转录滚走
        shift(delta);
      }}
      trigger={
        <Pill active={thinking !== "auto"}>
          <BrainCircuit size={12} />
          <span>{label(thinking)}</span>
          <ChevronDown size={12} className="text-fg-faint" />
        </Pill>
      }
    >
      {(close) => (
        <>
          <MenuLabel>
            <span className="flex items-center gap-1.5 text-fg">
              <BrainCircuit size={12} className="text-fg-faint" />
              {t("composer.thinkingHeadline", { level: label(headline) })}
            </span>
          </MenuLabel>
          {/*
            ★ 轨道不足两档时退回列表(`['auto','off']` 这种模型:轴上只剩一个点,
            画出来是一根拖不动的杠)。两档的 toggle 型模型还是走滑杆 ——
            那就是「关闭 ↔ 开启」,滑杆表达得了。
          */}
          {track.length >= 2 ? (
            <div className="px-2.5 pt-1 pb-0.5">
              <Slider
                value={index}
                min={0}
                max={track.length - 1}
                ariaLabel={t("composer.thinking")}
                /*
                  拖动本身就是一次表态 —— 所以 `preview` 一出现就立刻**点亮**,
                  而不是等松手写回 `thinking` 之后才亮。暗着拖是最别扭的那种手感:
                  你已经在操作它了,它还显示着"这一档不是你选的"。
                */
                className={cn(thinking === "auto" && preview === null && "opacity-45")}
                onPreview={setPreview}
                onCommit={(v) => {
                  setPreview(null);
                  const level = track[v];
                  // 拖动即是一次明确表态,于是自动脱离 `auto`。
                  if (level !== undefined) onThinking(level);
                }}
              />
              {/* 只标两端 —— 中间每一档都标的话,232px 里七个标签会糊在一起,
                  而"现在是哪一档"由上面那行标题实时回答,不靠猜滑块落点。 */}
              <div className="mt-1 flex justify-between text-[10.5px] text-fg-faint">
                <span>{label(track[0] ?? "off")}</span>
                <span>{label(track[track.length - 1] ?? "max")}</span>
              </div>
            </div>
          ) : (
            track.map((level) => (
              <MenuItem
                key={level}
                checked={level === thinking}
                onSelect={() => {
                  onThinking(level);
                  close();
                }}
              >
                {label(level)}
              </MenuItem>
            ))
          )}
          <MenuSeparator />
          <MenuItem
            checked={thinking === "auto"}
            onSelect={() => {
              onThinking("auto");
              close();
            }}
          >
            {label("auto")}
          </MenuItem>
          <MenuLabel>{description}</MenuLabel>
        </>
      )}
    </Menu>
  );
}

/**
 * 圆环菜单里压缩那两栏要的全部东西 —— `ContextRing` 和它的二级视图共用。
 *
 * ★ 一个对象而不是四个散 prop:它们是**一件事**的四个面(当前值 / 候选绑定 /
 * 供应商名 / 怎么写回),拆开之后任何一次改动都要同时动两个组件的签名。
 */
interface CompactionMenuProps {
  /** 当前工作区设置里那三项(三态见 `shared/domain/workspace.ts`)。 */
  override: WorkspaceCompactionOverride;
  models: readonly ModelAlias[];
  providers: readonly UpstreamProvider[];
  onChange: (patch: Partial<WorkspaceSettings>) => void;
}

/**
 * 上下文余量圆环 —— 同时是「最大上下文」开关和手动压缩的入口。
 *
 * ★ 分子是**最近一次请求**报回来的输入 token,不是整轮累加 ——
 * 上下文占用是个瞬时量,累加出来的那个数几轮之内必然冲破 100%。
 *
 * ★ 分母是**有效窗口**,不是模型的协议窗口:默认夹在 272K(计费分界)以内,
 * 打开「最大上下文」才放开。所以这里收的是**协议窗口原值** ——
 * 组件要同时知道两个数(协议值用于置灰判断和「1.05M」文案,有效值当分母),
 * 传一个算好的进来就只能在这里反向再实现一遍 `effectiveContextWindow`。
 *
 * ★ 中途切开关时圆环**立刻**变。原先状态行那根压力条要等下一次发送才跟上,
 * 那时的说法是「药丸是本地权威(下一轮会怎样),转录是既成事实(上一轮实际怎样)」——
 * 这条区分仍然成立,但它只管**分子**:`used` 是上一次请求的读数,没法实时。
 * **分母**两边现在统一走本地权威了(`ChatView` 把有效窗口透给 `StatusLine`),
 * 因为旧写法会让同一屏里圆环写着 21%、旁边却催用户「另起会话」——
 * 而那正是他刚刚打开这个开关要解决的事。见 `views/chat/context-pressure.ts`。
 */
function ContextRing({
  used,
  model,
  maxContext,
  onMaxContext,
  compaction,
  running,
  compacting,
  onCompact,
  segments,
  preview,
  onMenuOpen,
  cacheHitRate,
  onManageMcp,
}: {
  used?: number;
  /** 整个别名而不是单个窗口数:计费文案还要 `upstreamModel` / `providerId` */
  model?: ModelAlias;
  maxContext: boolean;
  onMaxContext: (next: boolean) => void;
  /**
   * 压缩那两栏(工作区级覆盖)。
   *
   * ★ 一个对象而不是四个散 prop:这个组件的参数表已经很长,而这四样是**一件事**
   * 的四个面(当前值 / 候选 / 候选的供应商名 / 怎么写回),拆开之后任何一次改动
   * 都要同时动四处签名。
   */
  compaction: CompactionMenuProps;
  running: boolean;
  compacting: boolean;
  onCompact?: () => void;
  /**
   * 最近一轮的占用归因。
   *
   * ★ 它和上面的 `used` **不同源**,这是有意的,不要「修好」:`used` 是上游报回来的
   * 真实输入 token,`segments` 是发出去之前本地估的(误差英文 ±15%、中文 ±25%,
   * 见 `context-assembler.ts` 文件头)。所以这张卡**只显示百分比,不显示 token 数** ——
   * 百分比是相对量,估算误差在分子分母上同向抵消;一列写着 `14.3K` 的绝对值
   * 是在承诺一个我们给不准的数,而用户迟早会拿它去对账单。
   */
  segments?: ContextSegment[];
  /**
   * 一句话都没聊时的归因 —— 主进程装配一次但不发出去。
   *
   * ★ **只在 `segments` 缺席时顶上**,真实归因一到就让位:预览是纯本地估算,
   * 而且拿不到 git 上下文、也看不见还没连上的 MCP。它的价值全在时机上 ——
   * 「发第一条之前就已经占掉多少」只有在发第一条之前看见才是可行动的。
   */
  preview?: ContextPreview;
  /** 菜单打开 —— 预览在这一刻才去拉。 */
  onMenuOpen?: () => void;
  /** 本轮缓存命中率。`undefined` = 还没有过一次完成的请求。 */
  cacheHitRate?: number;
  onManageMcp?: () => void;
}): ReactNode {
  const { t } = useI18n();
  const pricingTable = usePricingTable();
  /*
    菜单的三个视图。压缩模型 / 压缩档位各自是一张候选表,塞进主面板会把这张
    「还装得下多少」的卡片撑成一屏下拉。

    ★ 用**同一个 Menu 的内部视图**,不嵌 `Select`:这个 `Menu` 是自绘的,靠
    「面板外 pointerdown 就关」收场,而 Radix 的 Select 把浮层 portal 到 body ——
    点它的选项等于点在面板外,菜单会在选中的同一瞬间关掉。症状是「点了没反应」。
  */
  const [view, setView] = useState<"main" | "compactModel" | "compactThinking">("main");
  const protocolWindow = model?.contextWindow;
  const total = effectiveContextWindow(protocolWindow, maxContext);
  const tickRatio = longContextTickRatio(protocolWindow, maxContext);
  const canMax = supportsMaxContext(protocolWindow);
  /*
    ★ `used === undefined`(还没发生过一次真实请求)时**不能整个不渲染**。
    那样会把「最大上下文」开关一起藏掉,而新会话恰恰是最该在发第一条之前
    决定花不花这笔钱的时刻。所以照常渲染,只是中心显示 `–` 而不是 `0` ——
    `0%` 是个断言,`–` 是「还不知道」。
  */
  const ratio = used === undefined ? 0 : Math.min(1, Math.max(0, used / total));
  const percent = used === undefined ? undefined : Math.round(ratio * 100);
  // 越界:`ratio` 被 min(1) 夹住了,看不出来,得单独算一个标志。
  const over = used !== undefined && used > total;
  const radius = 6;
  const circumference = 2 * Math.PI * radius;
  const blocked = running || compacting || onCompact === undefined;
  const color = ratio >= CONTEXT_WARN ? "var(--color-danger)" : "var(--color-accent)";

  // 计费文案按真实定价出,不写死「×2」—— 只有 OpenAI 现代四款是双档,
  // 而且倍率不对称(输入 ×2、输出 ×1.5)。查不到就降级成中性说法,不编一个数。
  const pricingModelId =
    model === undefined ? undefined : findBuiltinModel(model.upstreamModel)?.pricingModelId;
  const pricing =
    pricingModelId === undefined
      ? null
      : findPricing(pricingTable, model?.providerId ?? null, pricingModelId, Date.now());
  const surcharge =
    pricingModelId === undefined
      ? undefined
      : longContextSurcharge(pricingTable, model?.providerId ?? null, pricingModelId, Date.now());
  const protocolLabel = formatContextWindow(effectiveContextWindow(protocolWindow, true));  const maxContextHint = !canMax
    ? t("composer.maxContextUnavailable", { window: protocolLabel })
    : surcharge !== undefined
      ? t("composer.maxContextHint", {
          window: protocolLabel,
          threshold: formatContextWindow(surcharge.threshold),
          multiplier: Number(surcharge.inputMultiplier.toFixed(2)),
        })
      : pricing !== null
        ? // 查到了定价而且只有一档 —— 这个模型放开窗口是不涨价的,说清楚。
          t("composer.maxContextHintFlat", { window: protocolLabel })
        : t("composer.maxContextHintUnknown", {
            window: protocolLabel,
            threshold: formatContextWindow(LONG_CONTEXT_THRESHOLD),
          });

  /*
    菜单行上显示**当前落在哪一档**,而不是一句「去设置」。
    ★ 缺省态也要显示出来(「跟随全局设置」):看不见落点,用户就无从判断自己
    上次到底改没改过 —— 而这两栏的缺省态恰恰是绝大多数会话的实际状态。
  */
  const compactModelKey = compactModelValue(compaction.override);
  const compactModelLabel =
    compactModelKey === FOLLOW_GLOBAL
      ? t("compaction.followGlobal")
      : compactModelKey === FOLLOW_SESSION
        ? t("compaction.followSession")
        : compactModelOptions(compaction.models, compaction.providers).find(
            (o) => o.value === compactModelKey,
          )?.label
          // 配过的那条绑定现在查不到了(供应商被删/别名改名):显示别名本身,
          // 而不是退回「跟随全局」—— 那会让人以为自己没配过。真正的回落发生在
          // 主进程压缩那一刻(`kernel/compaction/binding.ts`),这里只是如实显示配置。
          ?? parseModelSelectionKey(compactModelKey).alias;
  const compactThinkingKey = compactThinkingValue(compaction.override);
  const compactThinkingLabel =
    compactThinkingKey === FOLLOW_GLOBAL
      ? t("compaction.followGlobal")
      : compactThinkingKey === INHERIT_THINKING
        ? t("compaction.thinkingInherit")
        : t(`chat.thinkingLevel.${compactThinkingKey as ThinkingLevel}`);

  return (
    <Menu
      label={compacting
        ? `${t("composer.contextUsage", { percent: percent ?? 0 })} · ${t("composer.contextCompacting")}`
        : t("composer.contextMenu")}
      width={260}
      align="end"
      onOpenChange={(open) => {
        if (open) onMenuOpen?.();
        // 关掉时回到主视图 —— 下次打开不该停在上一次翻到的那一页(`Menu` 的这个
        // prop 正是为此存在的,见它的注释)。
        else setView("main");
      }}
      /*
        双击 = 立刻压缩,保住改造之前就有的肌肉记忆。为什么不能直接包
        `onDoubleClick`、为什么用 `e.detail` 而不是延时消歧,见 `Menu.tsx` 里
        这个 prop 的注释。
      */
      onTriggerDoubleClick={() => {
        if (!blocked) onCompact?.();
      }}
      trigger={
        <span
          /* 挂在这个 `<span>` 上而不是触发按钮上:`Menu` 自己渲染按钮,不透传任意 DOM 属性。
             当前没有消费者,是留给将来 e2e 的锚点,不要顺手删。 */
          data-testid="composer-context-ring"
          data-context-percent={percent}
          className={cn(
            "relative flex h-7 w-7 shrink-0 items-center justify-center rounded-full transition-colors",
            "hover:bg-tint-hover",
          )}
        >
          <svg
            viewBox="0 0 16 16"
            aria-hidden="true"
            className={cn(
              "h-4 w-4 -rotate-90",
              compacting && "animate-spin motion-reduce:animate-none",
            )}
          >
            <circle cx="8" cy="8" r={radius} fill="none" strokeWidth="2.5" stroke="var(--color-tint-strong)" />
            <circle
              cx="8"
              cy="8"
              r={radius}
              fill="none"
              strokeWidth="2.5"
              strokeLinecap="round"
              stroke={color}
              strokeDasharray={circumference}
              strokeDashoffset={circumference * (1 - ratio)}
            />
            {/*
              272K 刻度 —— 打开「最大上下文」之后分母变了,这道线告诉你计费分界在哪儿。
              ★ 画在 +x 轴上再 `rotate`,**不手算 cos/sin**:`<circle>` 的描边正是从
              `(cx+r, cy)` 起笔顺时针走的,`strokeDashoffset` 也按这个方向算,
              于是刻度和进度弧共用同一个起点和方向,永远对得上。
              外层那个 `-rotate-90` 把整组转到 12 点起笔,不影响相对关系。
              几何:描边占据半径 4.75→7.25,刻度取 4.3→7.7,两头各探出 0.45。
              颜色只用 `--color-fg-faint` —— 一道刻度线不值得成为界面里的第三处用色。
            */}
            {tickRatio !== undefined && (
              <line
                x1={12.3}
                y1={8}
                x2={15.7}
                y2={8}
                transform={`rotate(${(tickRatio * 360).toFixed(2)} 8 8)`}
                stroke="var(--color-fg-faint)"
                strokeWidth="1"
                strokeLinecap="butt"
              />
            )}
          </svg>
          {/* 默认就显示的百分比,不是只在悬浮时才有 —— 圆环本身太小,弧长读不出精确数字。 */}
          {!compacting && (
            <span
              aria-hidden="true"
              style={{ color }}
              className="pointer-events-none absolute inset-0 flex items-center justify-center text-[8px] font-semibold tabular-nums"
            >
              {percent ?? "–"}
            </span>
          )}
        </span>
      }
    >
      {(close) => (
        <>
          {/*
            ── 二级视图:压缩模型 / 压缩思考强度 ──
            选完回主视图而不是关菜单:这两栏常常要一起调(换了便宜模型多半也想换档位),
            每选一项就把面板关掉等于让人重开一次。
          */}
          {view !== "main" ? (
            <CompactionChoiceView
              kind={view}
              compaction={compaction}
              onBack={() => setView("main")}
            />
          ) : (
          <>
          <MenuLabel>
            {t("composer.contextHeadline", {
              used: used === undefined ? "—" : formatContextWindow(used),
              window: formatContextWindow(total),
            })}
            {/*
              需求：这个数说的是**上一次请求实际发出去多大**，不是「此刻还占着多少」。
              不标来源会怎样：它在两次发送之间一动不动，于是一段已经被自动压缩过的
              会话，菜单里仍旧挂着压缩前那个越线的读数 —— 用户据此得出「压缩没生效」，
              而下一条发出去它就自己掉下来了。这一句是把「既成事实」和「下一轮会怎样」
              分开的唯一标记（圆环本身的语义见本组件抬头第三段）。
              `used === undefined` 时不标：那时显示的是 `—`，没有任何一次请求可指。
            */}
            {used !== undefined && ` · ${t("composer.contextFromLastRequest")}`}
          </MenuLabel>
          <div className="px-2 pb-2 pt-0.5">
            <div className="h-1 w-full overflow-hidden rounded-full bg-tint-strong">
              <div
                className="h-full rounded-full"
                style={{ width: `${Math.round(ratio * 100)}%`, background: color }}
              />
            </div>
          </div>
          {/*
            越界:请求照常发(协议窗口放得下),但要说出来,否则用户是在无提示的情况下
            被按长上下文计费。不塞进消息流 —— 这是个每轮都可能重复的瞬时状态。
          */}
          {over && (
            <MenuLabel>
              <span className="text-danger">
                {t("composer.contextOverLimit", { threshold: formatContextWindow(total) })}
              </span>
            </MenuLabel>
          )}
          <ContextBreakdown
            segments={segments ?? preview?.segments}
            /* 预览才给这个总量:真实归因在场时,圆环上那个数已经是真值了,
               再写一个估算的总量只会让人怀疑该信哪一个。 */
            previewShare={
              segments !== undefined || preview === undefined || preview.window <= 0
                ? undefined
                : preview.used / preview.window
            }
            cacheHitRate={cacheHitRate}
            onManageMcp={
              onManageMcp === undefined
                ? undefined
                : () => {
                    close();
                    onManageMcp();
                  }
            }
          />
          {/*
            ── 套餐额度 ──
            需求:GLM Coding Plan 订阅按 5 小时 / 每周窗口扣额度,跑满时请求会失败。
            圆环回答「窗口装不装得下」,这一块回答「套餐放不放行」—— 两个不同的
            门槛,后者在订阅制供应商上才存在(判据收口在 shared/domain/coding-plan.ts)。
            数据与设置页账号行同源(账号行 quota 快照),这里只是第二个视口。
          */}
          {model !== undefined && codingPlanFamilyFor(model.providerId) !== null && (
            <CodingPlanQuotaSection providerId={model.providerId} />
          )}
          <MenuSeparator />
          <ComposerMenuItem
            icon={<Maximize2 size={16} />}
            checked={maxContext}
            disabled={!canMax}
            description={maxContextHint}
            // 不 close:开关要能连续调,和 `+` 菜单里 webSearch 的既有约定一致。
            onSelect={() => {
              onMaxContext(!maxContext);
            }}
          >
            {t("composer.maxContext")}
          </ComposerMenuItem>
          <MenuSeparator />
          {/*
            压缩用谁、想多深 —— 和上面那颗「最大上下文」同属「这条会话的上下文怎么管」,
            所以放在同一张菜单里、紧挨「压缩上下文」那条动作。这两栏是**工作区级**的,
            缺省跟随设置 › 通用 › Agent 里的全局值。
          */}
          <ComposerMenuItem
            icon={<Bot size={16} />}
            description={compactModelLabel}
            onSelect={() => setView("compactModel")}
          >
            {t("compaction.model")}
          </ComposerMenuItem>
          <ComposerMenuItem
            icon={<BrainCircuit size={16} />}
            description={compactThinkingLabel}
            onSelect={() => setView("compactThinking")}
          >
            {t("compaction.thinking")}
          </ComposerMenuItem>
          <MenuSeparator />
          <ComposerMenuItem
            icon={<RefreshCw size={16} />}
            disabled={blocked}
            description={t(
              compacting
                ? "composer.contextCompacting"
                : running
                  ? "composer.contextCompactBusy"
                  : "composer.compactNowHint",
            )}
            /*
              ★ 这条是本次补上的**键盘入口**。改造之前手动压缩只有双击一条路,
              而 `<button>` 的 Enter/Space 走的是 click 不是 dblclick ——
              键盘用户在这里曾经没有任何入口。
            */
            onSelect={() => {
              close();
              onCompact?.();
            }}
          >
            {t("composer.compactNow")}
          </ComposerMenuItem>
          </>
          )}
        </>
      )}
    </Menu>
  );
}

/**
 * 圆环菜单的二级视图:压缩模型 / 压缩思考强度的候选表。
 *
 * ★ 每一项都带勾,而且「跟随全局设置」「跟随会话模型」也在表里占一行 ——
 *   它们是**缺省态**,不是「没选」:看不见当前落在哪一档,用户就没法判断
 *   自己上次到底改没改过。
 */
function CompactionChoiceView({
  kind,
  compaction,
  onBack,
}: {
  kind: "compactModel" | "compactThinking";
  compaction: CompactionMenuProps;
  onBack: () => void;
}): ReactNode {
  const { t } = useI18n();
  const isModel = kind === "compactModel";
  const current = isModel
    ? compactModelValue(compaction.override)
    : compactThinkingValue(compaction.override);
  const options: { value: string; label: string }[] = isModel
    ? [
        { value: FOLLOW_GLOBAL, label: t("compaction.followGlobal") },
        { value: FOLLOW_SESSION, label: t("compaction.followSession") },
        ...compactModelOptions(compaction.models, compaction.providers),
      ]
    : [
        { value: FOLLOW_GLOBAL, label: t("compaction.followGlobal") },
        ...SUBAGENT_THINKING_CHOICES.map((value) => ({
          value,
          label:
            value === INHERIT_THINKING
              ? t("compaction.thinkingInherit")
              : t(`chat.thinkingLevel.${value}`),
        })),
      ];

  return (
    <>
      <MenuLabel>{t(isModel ? "compaction.model" : "compaction.thinking")}</MenuLabel>
      <MenuItem icon={<ChevronLeft size={14} />} onSelect={onBack}>
        {t("compaction.back")}
      </MenuItem>
      <MenuSeparator />
      {options.map((option) => (
        <MenuItem
          key={option.value}
          checked={option.value === current}
          // 选完回主视图,不关菜单 —— 理由见调用点那段注释。
          onSelect={() => {
            compaction.onChange(
              isModel ? compactModelPatch(option.value) : compactThinkingPatch(option.value),
            );
            onBack();
          }}
        >
          {option.label}
        </MenuItem>
      ))}
    </>
  );
}

/** 归因行的顺序只由占比决定 —— 这张卡回答的就是「谁最大」。 */
const SEGMENT_LABEL: Record<ContextSegmentKind, string> = {
  system: "composer.contextSegSystem",
  skills: "composer.contextSegSkills",
  "tools-builtin": "composer.contextSegToolsBuiltin",
  "tools-mcp": "composer.contextSegToolsMcp",
  instructions: "composer.contextSegInstructions",
  messages: "composer.contextSegMessages",
};

/**
 * 「这些上下文被谁占掉了」。
 *
 * ★ **分母是已用量,不是窗口。** 圆环和它上面那根条回答「还剩多少」,这张卡回答
 * 「已经占掉的那些是什么」—— 两个不同的问题。一个 3% 的读数配上「MCP 占了其中
 * 42%」才是可行动的:挂满 MCP 的工作区在**一句话都没聊**的时候就已经少掉半个窗口,
 * 而单看余量只会得出「还早着呢」。
 *
 * ★ **不用分类色板。** 每一行都带着自己的文字标签,颜色一个字节的身份信息都不承载,
 * 纯装饰 —— 而这个界面的既有约定是不为装饰新增用色(见圆环里那道 272K 刻度线的
 * 注释)。圆点因此只有一个色相,浓淡跟的是**这一档自己的占比**,不是它的排名:
 * 排序会随对话变,而「同一档换个位置就换个颜色」正是让人读错的那种变化。
 *
 * ★ **一个数都不显示 token。** 理由见 `ContextRing` 的 `segments` 那段。
 */
function ContextBreakdown({
  segments,
  previewShare,
  cacheHitRate,
  onManageMcp,
}: {
  segments?: ContextSegment[];
  /** 预览态才有:估算出来的「已占窗口」比例。见调用点。 */
  previewShare?: number;
  cacheHitRate?: number;
  onManageMcp?: () => void;
}): ReactNode {
  const { t } = useI18n();

  /*
    ★ 「还没有」和「全是 0」必须长得不一样。新会话在发出第一条之前一个真实
    请求都没发生过,此时画一排 0% 是个**断言**(「这些东西都没占地方」),而它是假的 ——
    工具定义那时候已经占了几万 token,只是我们还没算过。
  */
  if (segments === undefined || segments.length === 0) {
    return (
      <MenuLabel>
        <span className="text-fg-faint">{t("composer.contextBreakdownPending")}</span>
      </MenuLabel>
    );
  }

  const used = segments.reduce((n, s) => n + s.tokens, 0);
  const rows = [...segments].sort((a, b) => b.tokens - a.tokens);
  const max = rows[0]?.tokens ?? 0;

  return (
    <>
      <MenuLabel>{t("composer.contextBreakdown")}</MenuLabel>
      <div className="px-2.5 pb-1 pt-0.5 text-[11.5px]">
        {previewShare !== undefined && (
          /*
            ★ 这一行才是整张卡的由头:下面六行是「占掉的那些是什么」(分母是已用量),
            这一行是「还没开口就已经占掉多少窗口」(分母是窗口)。两个分母不一样,
            所以它单独放在明细之前、措辞也不同 —— 混进去会让人把 42% 读成 42% 的窗口。
          */
          <div
            className="mb-2 flex items-center gap-2 pb-1.5"
            title={t("composer.contextPreviewNote")}
          >
            <span className="min-w-0 flex-1 truncate text-fg-muted">
              {t("composer.contextPreviewTotal")}
            </span>
            <span className="shrink-0 tabular-nums text-fg">
              {(Math.min(1, Math.max(0, previewShare)) * 100).toFixed(1)}%
            </span>
          </div>
        )}
        {rows.map((segment) => {
          const share = contextSegmentShare(segment.tokens, used);
          const mcp = segment.kind === "tools-mcp" && onManageMcp !== undefined;
          const Row = mcp ? "button" : "div";
          return (
            <Row
              key={segment.kind}
              {...(mcp ? { type: "button" as const, onClick: onManageMcp } : {})}
              /* MCP 那一行可点 —— 「MCP 占 42%」看完之后总得有个地方能去关掉它,
                 否则这张卡就只是个漂亮的诊断。 */
              className={cn(
                "flex w-full items-center gap-2 rounded-sm py-[3px] text-left",
                mcp && "-mx-1 px-1 hover:bg-tint-hover",
              )}
              /* 悬浮才给按 server 的拆分:六行里塞进 N 个服务器会把这张卡撑成一屏,
                 而「哪个服务器最贵」是追问出来的,不是第一眼要的。 */
              title={
                [
                  ...(segment.detail ?? []).map(
                    (d) => `${d.label} ${(contextSegmentShare(d.tokens, used) * 100).toFixed(1)}%`,
                  ),
                  ...(mcp ? [t("composer.contextManageMcp")] : []),
                ].join("\n") || undefined
              }
            >
              <span
                aria-hidden="true"
                className="h-1.5 w-1.5 shrink-0 rounded-full bg-accent"
                /* 浓淡编码的是这一档自己的占比。0.25 是下限:再淡就看不见了,
                   而一个看不见的点和一个被漏掉的行长得一样。 */
                style={{ opacity: max === 0 ? 0.25 : 0.25 + 0.75 * (segment.tokens / max) }}
              />
              <span className="min-w-0 flex-1 truncate text-fg-muted">
                {t(SEGMENT_LABEL[segment.kind])}
              </span>
              {/* 数字穿的是文字色,不穿那个圆点的颜色 —— 身份由标签给,不由颜色给。 */}
              <span className="shrink-0 tabular-nums text-fg">{(share * 100).toFixed(1)}%</span>
            </Row>
          );
        })}
        {cacheHitRate !== undefined && (
          /*
            ★ 它和上面六行**不是一件事**,所以用留白分组:上面说的是「窗口被谁占了」,
            这一行说的是「这些 token 里有多少是从缓存读的」(便宜那部分)。
            并排列进去会让人以为缓存也是六档里的一档。
          */
          <div className="mt-2 flex items-center gap-2 pt-1.5">
            <span className="min-w-0 flex-1 truncate text-fg-muted">
              {t("composer.contextCacheHit")}
            </span>
            <span className="shrink-0 tabular-nums text-fg">
              {(cacheHitRate * 100).toFixed(1)}%
            </span>
          </div>
        )}
      </div>
    </>
  );
}


/**
 * 模型选择采用两级结构：第一次打开先选供应商，进入供应商后再选模型。
 * 这样模型别名很多时不会把所有供应商混在一个长菜单里。
 *
 * ★ 这一对下拉的定位/子菜单机制现在是共享组件 `ProviderModelMenu`
 * (`components/ProviderModelMenu.tsx`)——设置页「通用 → Agent」的默认模型/
 * 默认子代理是它的第二个调用方,原因和搬迁细节写在那个文件的文件头。这里只负责
 * 把 composer 自己的 `providers`/`models` 翻译成它要的 `rows`。
 *
 * ★ 这里曾经还挂着一个「模型配置 ›」二级页(只有思考强度一项)。它被拆成了
 * 工具栏上的 `ThinkingPill` —— 一个每轮都要调的旋钮不该埋在第四层。
 */
function ModelPicker({
  model,
  modelProviderId,
  modelLabel,
  provider,
  providers,
  models,
  loaded,
  onModel,
}: {
  model: string;
  modelProviderId?: string;
  modelLabel: string;
  provider?: UpstreamProvider;
  providers: UpstreamProvider[];
  models: ModelAlias[];
  loaded: boolean;
  onModel: (model: string, modelProviderId: string) => void;
}): ReactNode {
  const { t } = useI18n();
  // 打开选择器时校正存量的「选中了图片模型」(见 useChatModelGuard 文件头的需求说明)
  const guardChatModel = useChatModelGuard(
    models,
    providers,
    { model, modelProviderId },
    onModel,
  );
  // ★ 对话输入框只列文本模型:图片模型发不出对话请求,出现在这里就是一个选了就废的
  // 选项(判据收口在 `isChatModelAlias`,别在下面的过滤里再写一份)。
  const chatModels = models.filter(isChatModelAlias);
  const availableProviders = providers.filter((p) =>
    chatModels.some((m) => m.providerId === p.id),
  );
  const rows: ProviderModelMenuRow[] = availableProviders.map((p) => {
    const providerModels = chatModels.filter((m) => m.providerId === p.id);
    return {
      id: p.id,
      label: p.name,
      description: t("chat.availableModels", { count: providerModels.length }),
      selected: p.id === provider?.id,
      models: providerModels.map((m) => ({
        value: m.alias,
        label: m.alias,
        // ★ 必须带上 providerId:同一个别名在两家的子菜单里都会出现,只比别名的话
        //   **两边同时打勾**,用户会以为自己选了两个。
        selected: m.alias === model && m.providerId === modelProviderId,
      })),
    };
  });

  return (
    <ProviderModelMenu
      trigger={
        <Pill className="min-w-0 shrink">
          <ProviderIcon
            name={[model, provider?.name, provider?.id]}
            size={13}
          />
          <span className="min-w-0 max-w-[150px] truncate">{modelLabel}</span>
          <ChevronRight size={12} className="ml-0.5 shrink-0 text-fg-faint" />
        </Pill>
      }
      // 这一排里**只有模型名可以退让** —— 其余药丸都是短词,压缩它们只会换行。
      triggerClassName="min-w-0"
      align="end"
      width={300}
      menuLabel={t("chat.selectProvider")}
      menuIcon={<Settings2 size={12} />}
      loaded={loaded}
      loadingLabel={t("common.loading")}
      emptyLabel={t("chat.noModelsConfigured")}
      rows={rows}
      onSelectModel={(selectedProviderId, alias) => onModel(alias, selectedProviderId)}
      onOpenChange={guardChatModel}
    />
  );
}

// ── 药丸值 ↔ 工作区设置。两边字段名一致,但**不是同一个类型** ──
// WorkspaceSettings 还有 activeSkillIds,而药丸不碰它。直接 spread 会把它抹掉。

function fromSettings(s: WorkspaceSettings): ComposerValue {
  return {
    permissionMode: s.permissionMode,
    model: s.defaultModel,
    modelProviderId: s.defaultModelProviderId,
    mode: s.defaultMode,
    thinking: s.defaultThinking,
    webSearch: s.webSearch,
    // 缺席的旧工作区在这里铺成 false —— 本地权威不留「不知道」。
    maxContext: s.maxContext === true,
  };
}

function toSettings(v: ComposerValue): Partial<WorkspaceSettings> {
  return {
    permissionMode: v.permissionMode,
    defaultModel: v.model,
    // ★ **无条件**写,不能条件展开:主进程那边是深合并,漏写这一项时旧的供应商
    //   会原样留下,于是得到「新别名 + 旧供应商」—— 正是这次修复要消灭的那个形状。
    defaultModelProviderId: v.modelProviderId,
    defaultThinking: v.thinking,
    webSearch: v.webSearch,
    maxContext: v.maxContext,
  };
}
