/**
 * 转录里的三种非文本块:思考 / 工具调用 / 子代理。
 *
 * 单独一个文件是因为**已提交的消息和还在流的块要用同一套渲染**:
 * `messages[].parts` 走这里,`transcript.live` 也走这里。两处各写一份的话,
 * 一个块从「流式中」变成「已提交」的那一瞬间会跳一下 —— 而那正是用户
 * 最容易注意到的时刻。
 *
 * ★ 批次 2 之后,工具行的**长相由 `shared/domain/tool-presenter.ts` 决定**,
 * 这个文件只负责把 presenter 的输出摆进版式里。新增一个工具的展示规则
 * 不需要动这里一行。
 */
import { ArrowDownToLine, Bot, Brain, CheckCircle2, CircleAlert, Clock3, ListChecks, Square } from "lucide-react";
import { useEffect, useMemo, useRef, useState, useSyncExternalStore, type ReactNode } from "react";
import { formatCallDuration } from "../../../../shared/agent/duration";
import { elapsedOf, formatDuration } from "../../../../shared/agent/duration";
import {
  thinkingElapsedMs,
  thinkingTokenReading,
  type ThinkingStats,
  type ThinkingTokenReading,
} from "../../../../shared/agent/thinking-stats";
import { formatTokenCount } from "../../../../shared/agent/tokens";
import type { SubagentState, ToolCallState } from "../../../../shared/agent/transcript";
import {
  isPinnedShape,
  presenterOf,
  pluginPresentersSnapshot,
  subscribePluginPresenters,
} from "../../../../shared/domain/tool-presenter";
import { agentColorHex } from "../../../../shared/domain/agent-def";
import { cn } from "../../lib/cn";
import { useI18n } from "../../i18n";
import { agentErrorText } from "../../i18n/agent";
import { AgentMarkdown } from "../../components/markdown";
import { Surface, SurfaceReveal, SurfaceRow } from "../../components/ui/Surface";
import { ToolDetail } from "./ToolDetail";
import { ROW_CLASS, RowChevron, ToolRow } from "./row";
import { useWorkspaceFile } from "./workspace-file";
import { previewOf } from "./interaction-preview";
import { MAX_PARTIAL_JSON_CHARS, parsePartialJson } from "./partial-json";
import { ToolIcon, type ToolViewStatus } from "./ToolIcon";
import { abortRun, backgroundSubagent } from "../../services/agent";
import { settleSubagent } from "../../stores/session";
import { getLastAssistantMessage } from "../../services/sessions";
import { visibleText } from "../../../../shared/agent/message";
import { useOpenSubagent } from "./subagent-open";
import { useBackgroundToolCall, useStopToolCall } from "./tool-stop";
import { AgentShimmerText } from "./AgentActivity";

/**
 * 「深度思考 N 秒」—— 截图里是一条可折叠的行,默认收起。
 *
 * 流式过程中默认展开:思考先于正文到达,收着的话用户会盯着一个
 * 什么都不动的空白等好几秒。提交之后再收起来。
 */
export function ThinkingBlock({
  text,
  streaming,
  stats,
}: {
  text: string;
  streaming: boolean;
  /**
   * 标题行右端「用时 · token」读数的来源,见 `shared/agent/thinking-stats.ts`。
   * 缺席或里面没有事实(旧转录、导入的会话)时,用时那一格不画 —— 不拿 0 兜底;
   * token 仍按正文估一个约数,因为那只需要正文。
   */
  stats?: ThinkingStats;
}): ReactNode {
  const { t } = useI18n();
  const [manual, setManual] = useState<boolean | null>(null);
  const body = useRef<HTMLDivElement>(null);
  const followBottom = useRef(true);
  const open = manual ?? streaming;
  /*
    需求:标题行显示思考正文的 token 数。流式中每个增量都会重估一遍整段 ——
    估算是一次线性扫描,同一帧里 AgentMarkdown 重新解析整段正文的开销远大于它,
    所以不为它做增量计数(那得把估算的内部状态搬进 reducer)。
  */
  const reportedTokens = stats?.tokens;
  const tokens = useMemo(
    () => thinkingTokenReading(text, reportedTokens === undefined ? {} : { tokens: reportedTokens }),
    [text, reportedTokens],
  );

  useEffect(() => {
    if (!streaming || !open || !followBottom.current || body.current === null) return;
    body.current.scrollTop = body.current.scrollHeight;
  }, [open, streaming, text]);

  if (!streaming && text.trim() === '') return null;
  return (
    <Surface data-testid="thinking-block">
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setManual(!open)}
        className={cn(ROW_CLASS, "text-fg-faint")}
      >
        {/*
          ★ 流式中原先画的是像素活动图标，现按需求只留文字微光（见 AgentActivity）——
          「还在想」由下面那行 AgentShimmerText 表达，这里不再放图形。
        */}
        <Brain size={13} className="shrink-0" />
        <span className="min-w-0 flex-1 truncate">
          {streaming
            ? <AgentShimmerText>{t("chat.thinkingNow")}</AgentShimmerText>
            : t("chat.thinking")}
        </span>
        <ThinkingStatsSlot stats={stats} streaming={streaming} tokens={tokens} />
        <RowChevron open={open} />
      </button>
      <SurfaceReveal open={open}>
        <div
          ref={body}
          /*
            ★★ 左边这条竖线是**思考正文唯一的边界**,不是装饰。

            这一段是一整篇散文,而它紧挨着的上下文也是散文(模型的正文回答)——
            没有任何标记时,两者在一屏里糊成一片,用户读到一半才发现
            「这段原来是思考」。竖线比卡片轻(思考不是产物,不该做成卡),
            又比纯缩进硬,正好卡在这两者之间。
            (改造中途曾按「文本风格不画任何线」删掉过它,结果就是上面那句话
            描述的样子,所以加了回来。)
          */
          className="scroll-thin max-h-[min(40vh,320px)] overflow-y-auto border-l border-stroke pl-3 pr-1"
          onScroll={(event) => {
            const el = event.currentTarget;
            followBottom.current = el.scrollHeight - el.scrollTop - el.clientHeight < 40;
          }}
        >
          <AgentMarkdown content={text} streaming={streaming} variant="compact" />
        </div>
      </SurfaceReveal>
    </Surface>
  );
}

/**
 * 思考卡片标题行右端的「12s · 1.23K tokens」(估算值按需求也不加 ≈,见下方 data-estimated)。
 *
 * ★ 单独成组件是为了**把走表关在这一格里**:流式中每 100ms 刷一次 `now`,
 * 写在 ThinkingBlock 里的话,整段思考正文(AgentMarkdown)会跟着每秒重渲十次。
 */
function ThinkingStatsSlot({
  stats,
  streaming,
  tokens,
}: {
  stats: ThinkingStats | undefined;
  streaming: boolean;
  tokens: ThinkingTokenReading | undefined;
}): ReactNode {
  const { t } = useI18n();
  // 只有「还在长、且有起点」的流式块需要走表;已提交的块读落盘值,不必起定时器
  const ticking = streaming && stats?.durationMs === undefined && stats?.startedAt !== undefined;
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    if (!ticking) return;
    const timer = window.setInterval(() => setNow(Date.now()), 100);
    return () => window.clearInterval(timer);
  }, [ticking]);

  const elapsed = stats === undefined ? undefined : thinkingElapsedMs(stats, streaming, now);
  if (elapsed === undefined && tokens === undefined) return null;
  return (
    <span data-testid="thinking-stats" className="flex shrink-0 items-center gap-1.5 font-mono text-[12px] text-fg-faint">
      {elapsed !== undefined && (
        <span data-testid="thinking-duration" title={t("chat.thinkingStats.durationHint")}>
          {formatDuration(elapsed)}
        </span>
      )}
      {/* 分隔符是独立节点:任一格缺席时不留悬空的「·」(同 SubagentNode 的做法) */}
      {elapsed !== undefined && tokens !== undefined && <span aria-hidden>·</span>}
      {tokens !== undefined && (
        <span
          data-testid="thinking-tokens"
          // 真值还是估算,只有这一格和悬停提示分得开 —— 按需求读数本身不加「≈」
          data-estimated={tokens.estimated ? "true" : "false"}
          title={t(tokens.estimated ? "chat.thinkingStats.tokensEstimatedHint" : "chat.thinkingStats.tokensReportedHint")}
        >
          {t("chat.thinkingStats.tokens", { count: formatTokenCount(tokens.count) })}
        </span>
      )}
    </span>
  );
}

/**
 * 工具调用卡片。
 *
 * ★ 入参和结果都**折叠**:一个 `Read` 的结果可能是 60KB
 * (`MAX_TOOL_OUTPUT_CHARS` 之内的合法体积),全铺开会把对话冲垮。
 * 展开是用户的选择,不是默认 —— **除了失败**,见下面 `open` 的算法。
 *
 * ★ **props 保持三参不变**(`call` / `name` / `input`)。批次 2 新增的所有能力
 * (图标、耗时、差异化标题与详情)全部收在组件内部,`Thread.tsx` 两处调用点
 * 一行都不用改 —— 那两处必须同构,少改一处就是「已提交」和「流式中」长得不一样。
 */
export function ToolCallCard({
  call,
  name,
  input,
}: {
  /** 从 `transcript.tools[callId]` 来;还没收到 tool_start 时可能是 undefined */
  call: ToolCallState | undefined;
  /** 兜底:`tool_call` part 自带名字,即使 tools 表里还没有它 */
  name: string;
  input: unknown;
}): ReactNode {
  const { t } = useI18n();
  /**
   * `null` = 用户还没表态,按默认规则走;一旦点过就永久接管。
   *
   * ★ 不能写成 `useState(status === 'error')` —— 初始值只在挂载时算一次,
   * 而工具是先 running 后 error 的,那样失败永远不会自动展开。
   */
  const [manual, setManual] = useState<boolean | null>(null);

  // 订阅插件 presenter 注入版本:catalog 若在这张卡片**提交之后**才加载,
  // presenterOf 是命令式查表、不会自动重渲,已提交的插件工具卡片会永久停在
  // 兜底标题。订阅 version 让注入到达时重渲一次。见 tool-presenter 注入层注释。
  useSyncExternalStore(subscribePluginPresenters, pluginPresentersSnapshot);

  const status: ToolViewStatus = call === undefined ? "pending" : call.status;
  // 卡片只需要一个有界预览。否则 Write 的大段 content 每来一帧就从头重扫一次，
  // 参数生成过程会退化成二次方工作量；超过上限后前缀不变，useMemo 也不再重算。
  const pendingSource = typeof input === "string"
    ? input.slice(0, MAX_PARTIAL_JSON_CHARS)
    : input;
  const pendingInput = useMemo(() => {
    if (typeof pendingSource !== "string") return pendingSource;
    const parsed = parsePartialJson(pendingSource);
    return parsed === undefined ? pendingSource : parsed;
  }, [pendingSource]);
  // ★ 上游参数还没闭合时 `input` 是原始 JSON 前缀。只把解析出的快照交给展示层；
  // tool_start 到达后立刻切回内核严格解析的 `call.input`，绝不拿容错结果执行工具。
  const shownInput = call?.input ?? pendingInput;
  const toolName = call?.name ?? name;
  const presenter = presenterOf(toolName);

  // 结果快照卡片(output.card,静态)或运行中的实时卡片(call.card,第 2 层交互)。
  // 实时卡片优先:工具还在跑、这张才是此刻在变的。
  const liveCard = call?.card;
  const card = liveCard ?? call?.output?.card;

  // 失败默认展开:`toolFail` 的文案是设计过的可执行提示(见 fs.ts 里 Edit 失败
  // 那段三段式说明),把它藏在折叠里等于白写。
  // 运行中的实时卡片也默认展开:它可能要用户点(审批/表单),藏起来就没人应答。
  /*
    需求：AskUserQuestion / ProposeGoal 的题面要在模型**还在写参数**时就能读到，
    而不是等工具开跑、待决面板弹出来才第一次看见（题面到可作答之间常隔好几秒）。

    ★ 判据是 `status === "pending"`，即**这次调用还没开跑**：tool_start 一到，
    下面那张可作答的卡片就出现了，这张只读预览必须同时收起来 —— 同屏摆两份
    一模一样的题，用户不知道该点哪一份，而只有一份是真能提交的。
    `previewOf` 拿不出内容时不展开：ExitPlanMode 的入参是空对象（题面在计划文件里），
    自动展开只会得到一个永远空着的详情区。
  */
  const previewable = presenter.shape === "interaction"
    && status === "pending"
    && previewOf(toolName, shownInput) !== null;
  /*
    需求：`widget` 形态的产物（`visualize_show_widget` 画的那张图）**默认展开**。

    ★ 判据是形态类，不是工具名 —— 见 `ToolShape` 里 `widget` 那一段。按工具名
    分叉的话，下一个"自己画一张图"的工具要在这里再开一个口子，而漏掉它不会有
    任何编译错误：表现是**工具跑完、图上什么也没有**，因为 `SurfaceReveal`
    在收起态是直接卸载子树（`components/ui/Surface.tsx`），折叠时连 iframe 都
    不存在。用户看到的会是一个"成功的工具"配一片空白。

    ★ 与上面那条 interaction 预览的区别：那条只看 `pending`（工具一开跑就收起，
    因为下面会冒出可作答的卡片）；这条**全程展开** —— 生成期是边走边画，
    跑完就是成品本身，不存在"另一处更权威的展示"。

    `image` 形态（`generate_image` 的图片卡）同理全程展开：生成期是加载格与逐张出图，
    跑完是图本身。判据走 `isPinnedShape` 那张表（产物即回答的形态），不在这里再写
    第二个形态名。
  */
  const inlineWidget = presenter.shape === "widget" || isPinnedShape(presenter.shape);
  const open = manual ?? (status === "error" || liveCard !== undefined || previewable || inlineWidget);

  const duration = call === undefined ? undefined : formatCallDuration(call);
  const summary = presenter.summary?.(shownInput, call?.output);
  // 需求：工具的目标/进度收进紧凑 chip；底层 presenter 仍是唯一文案来源，
  // 否则视觉替换会把插件工具和流式中的参数重新降级成通用名称。
  const progress = call?.progress?.trim();
  const detail = progress !== undefined && progress !== "" ? progress : summary;
  const detailMono = presenter.shape === "read" || presenter.shape === "mutate" || presenter.shape === "command";
  /*
    需求：一条跑飞的命令（`npm test` 挂住、构建停不下来）要能被单独掐掉，
    而不是只能停掉整轮回复。主进程只为 `Bash` 寄存了停止句柄，所以这里的判据是
    presenter 里那条**能力声明**，不是「反正画上总没坏处」——
    画一颗按下去没反应的按钮，比没有按钮难解释得多（§5 不做防御式 UI）。
  */
  const stopToolCall = useStopToolCall();
  const stoppableCallId = presenter.stoppable === true && status === "running" && call !== undefined
    ? call.callId
    : undefined;
  const stop = stopToolCall === undefined || stoppableCallId === undefined
    ? undefined
    : (): void => { stopToolCall(stoppableCallId) };
  /*
    需求:跑得比预想久的前台命令(构建、长测试)可以**让它接着跑、但别再等它**。
    ★ 判据是这一次调用自己报的 `detachable`(主进程只在环境真能收编时才报),
      不是工具名 —— SSH 上的 Bash 转不了,画出来就是一颗按了没反应的按钮。
  */
  const backgroundToolCall = useBackgroundToolCall();
  const detachableCallId = call?.detachable === true && status === "running" ? call.callId : undefined;
  const background = backgroundToolCall === undefined || detachableCallId === undefined
    ? undefined
    : (): void => { backgroundToolCall(detachableCallId) };

  /*
    需求:图标要按目标文件的扩展名选(`.tsx` → 蓝色 TS 图标),所以行内容得先算出来。

    ★ 只算**一次**给行和图标共用:`line()` 在流式参数下每帧都会被重新解析,
    调两次拿到的可能是同一帧的两份不同快照,表现为图标和文件名对不上。
  */
  const line = presenter.line(shownInput);
  /*
    需求:编辑/写入这一行右端直接给出 `+7 −1`,不展开就知道这次动了多大。
    数字来自 presenter(见 `ToolLineStats`),算不准的情况它自己返回 undefined。
  */
  // ★ Edit 的统计要对新旧两段跑一次行级 LCS。卡片在流式期间每帧都会重渲,
  // 入参没变就不该重算。
  const output = call?.output;
  const lineStats = useMemo(() => presenter.stats?.(shownInput, output), [presenter, shownInput, output]);
  /*
    需求:点文件名直接打开那个文件。
    ★ 没有工作区入口(只读的子代理面板)时传 undefined —— 行会把文件名画成纯文本,
    而不是一枚点了没反应的链接。
  */
  const { open: openPath } = useWorkspaceFile();

  /*
    需求:工作流只显示画布,不要外面那一行工具标题。
    ★ 那一行的标题/节点数/状态和画布自己的标题栏(标题 + 进度 + 全屏)完全重复,
      叠在一起读起来是同一件事说了两遍。画布本身就是这一步的产物(pinned 形态),
      也就没有「收起」的必要 —— 去掉行,连同它的展开开关一起去掉。
  */
  if (presenter.shape === "workflow") {
    return (
      <div data-testid="tool-call" data-tool-status={status} data-tool-shape={presenter.shape}>
        <ToolDetail
          shape={presenter.shape}
          input={shownInput}
          output={call?.output}
          isError={status === "error"}
          toolName={toolName}
          callId={call?.callId}
          workflow={call?.workflow}
        />
      </div>
    );
  }

  /*
    需求:widget 直接长在对话里 —— 没有卡片框、没有标题行,和正文同一块底。
    ★ 那一行写的是「可视化 · <title>」,而图本身就在下面;叠一层框和一行字,读起来像是
      「这里引用了一张图」而不是「这里就是那张图」。复制源码 / 重新渲染挪到图右上角的悬停按钮里。
    ★ **失败例外**,仍然走下面的卡片:失败时没有图,只剩那句给模型也给人看的原因,
      它需要那一行告诉用户这是哪个工具、失败在哪。
  */
  if (presenter.shape === "widget" && status !== "error") {
    return (
      <div data-testid="tool-call" data-tool-status={status} data-tool-shape={presenter.shape} className="my-1">
        <ToolDetail
          shape={presenter.shape}
          input={shownInput}
          output={call?.output}
          isError={false}
          toolName={toolName}
          callId={call?.callId}
          card={card}
        />
      </div>
    );
  }

  return (
    <Surface
      // 状态同时给一个机器可读的属性:e2e 探针读它,而不是去正则「完成/失败」
      // 这几个会改的中文字(同 StatusLine 的理由)
      data-testid="tool-call"
      data-tool-status={status}
      data-tool-shape={presenter.shape}
    >
      {/*
        ★ 行的各段(标签/目标/目录/增删)来自 presenter 的 `line` + `stats`,
        不是拼好的标题串。失败时只染标签 —— 理由见 `row.tsx` 的结构注释。
      */}
      <ToolRow
        icon={(
          <ToolIcon
            shape={presenter.shape}
            status={status}
            {...(line.path === undefined ? {} : { path: line.path })}
          />
        )}
        line={line}
        danger={status === "error"}
        {...(lineStats === undefined ? {} : { stats: lineStats })}
        open={open}
        onToggle={() => setManual(!open)}
        {...(openPath === undefined ? {} : { onOpenPath: openPath })}
        trailing={
          <>
            {detail !== undefined && (
              <span className={cn(
                "min-w-0 max-w-[32%] shrink-0 truncate text-[12px] text-fg-faint",
                detailMono && "font-mono",
              )}>
                {detail}
              </span>
            )}
            <StatusSlot status={status} duration={duration} />
          </>
        }
        actions={stop === undefined && background === undefined ? undefined : (
          /* 动作按钮是行的**兄弟**,不是展开按钮的子节点 —— 按钮不能套按钮 */
          <>
            {background !== undefined && (
              <button
                type="button"
                data-testid="tool-background"
                onClick={background}
                aria-label={t("chat.tool.background")}
                title={t("chat.tool.background")}
                className="ml-1.5 inline-flex h-6 w-6 shrink-0 items-center justify-center rounded-[6px] text-fg-muted transition-colors hover:bg-tint-hover hover:text-fg motion-reduce:transition-none"
              >
                <ArrowDownToLine size={12} />
              </button>
            )}
            {stop !== undefined && (
              <button
                type="button"
                data-testid="tool-stop"
                onClick={stop}
                aria-label={t("chat.tool.stop")}
                title={t("chat.tool.stop")}
                className="ml-1.5 inline-flex h-6 w-6 shrink-0 items-center justify-center rounded-[6px] text-danger transition-colors hover:bg-danger/10 motion-reduce:transition-none"
              >
                <Square size={10} />
              </button>
            )}
          </>
        )}
      />

      <SurfaceReveal open={open}>
        <ToolDetail
          shape={presenter.shape}
          input={shownInput}
          output={call?.output}
          isError={status === "error"}
          toolName={toolName}
          callId={call?.callId}
          card={card}
          partialImages={call?.partialImages}
          workflow={call?.workflow}
        />
      </SurfaceReveal>
    </Surface>
  );
}

/**
 * 行右端那一个插槽。
 *
 * ★ **成功态显示耗时而不是「完成」二字。** 一行只有一个右侧插槽,而「完成」
 * 是默认结果、信息量接近于零;耗时才是用户会主动去看的那个数。
 * 失败态相反 —— 它必须占满这个插槽,不能被任何数字挤掉。
 */
function StatusSlot({
  status,
  duration,
}: {
  status: ToolViewStatus;
  duration: string | undefined;
}): ReactNode {
  const { t } = useI18n();
  if (status === "error") {
    return (
      <span className="shrink-0 text-[12px] text-danger">
        {t("chat.tool.failedStatus")}
      </span>
    );
  }
  if (status === "running") {
    return (
      <span className="inline-flex shrink-0 items-center gap-1.5 text-[12px] text-accent">
        <AgentShimmerText>{t("chat.tool.runningStatus")}</AgentShimmerText>
      </span>
    );
  }
  if (status === "pending") {
    return (
      <span className="shrink-0 text-[12px] text-fg-faint">
        {t("chat.tool.waitingStatus")}
      </span>
    );
  }
  // ok:有耗时就显示耗时,没有(旧转录)就什么都不显示 —— 空着比写「完成」干净
  return duration === undefined ? null : (
    <span className="shrink-0 font-mono text-[12px] text-fg-faint">
      {duration}
    </span>
  );
}

/**
 * 子代理卡片 —— 点标题行**就地展开**,展开区里有进度、最近动作、结果摘要,
 * 以及「完整记录」入口(在右侧工作区开一个只读会话,复用主智能体那套渲染)。
 *
 * ★ 这个形态以前拆过一次(改成「点一下直接开右侧面板」),理由是停止按钮藏在折叠里、
 * 详情摊在对话流里。这次恢复展开,但**那两条理由仍然成立,所以保留它们的解法**:
 *
 * 1. 停止 / 转后台按钮仍然是标题行的兄弟,**不展开就能按**。
 * 2. 展开区有高度上限(摘要区内部滚动),不会把转录顶掉半屏。
 * 3. 「打开完整记录」是**独立的一颗按钮**,不再和展开抢同一次点击 ——
 *    旧转录没有 `childSessionId` 时只是没有这颗按钮,卡片本身照样能展开。
 */
export function SubagentNode({
  summary,
  state,
  pending = false,
}: {
  summary: string | undefined;
  state?: SubagentState;
  /**
   * 子代理**还没派出去**:`Task` 的参数还在流,`subagent_start` 没到。
   *
   * 需求:那几秒里卡片就该长成子代理卡的样子(见 `thread-content.ts` 里那段),
   * 但状态得说实话。★ 不能靠「state 为空」判断 —— 旧转录里的子代理块同样没有 state,
   * 而它们早就跑完了,下面那行 `?? 'done'` 正是为它们写的。
   */
  pending?: boolean;
}): ReactNode {
  const { t } = useI18n();
  const openSubagent = useOpenSubagent();
  const [now, setNow] = useState(() => Date.now());
  const [open, setOpen] = useState(false);
  const running = state?.status === 'running';

  useEffect(() => {
    if (!running) return
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [running])

  const status = pending && state === undefined ? 'pending' : state?.status ?? 'done';
  const errorText = state?.error === undefined ? undefined : agentErrorText(state.error, t);
  /*
    ★★ 「正在退避重试」以前**只画在主对话的状态行上**,而子代理不看状态行 ——
    于是限流退避在这张卡片上和「跑得慢」一模一样,几次退避全失败之后直接跳到
    错误框,看起来就像子代理根本没重试(它和主代理走的是同一份 `router.ts`)。

    ★ 卡片不再折叠,所以完整那句话直接挂在标题行下面,不必再分「短标签 + 展开后的横幅」
    两份 —— 短标签仍然留在状态那一格,因为标题行本身没有位置写一整句。
  */
  const notice = running ? state?.notice : undefined;
  const noticeLabel = notice === undefined
    ? undefined
    : notice.kind === 'retry'
      ? t('chat.subagent.notice.retry', { attempt: notice.attempt })
      : t('chat.subagent.notice.switch', { to: notice.to });
  const noticeDetail = notice === undefined
    ? undefined
    : notice.kind === 'retry'
      ? t('chat.status.retrying', { attempt: notice.attempt, reason: notice.reason })
      : t('chat.status.providerSwitched', { to: notice.to, reason: notice.reason });
  const duration = state?.startedAt === undefined
    ? undefined
    : formatDuration(elapsedOf({ startedAt: state.startedAt, endedAt: state.endedAt }, now) ?? 0)
  const title = state?.description ?? summary ?? t("chat.subagent.default");
  /*
    ★★ 「汇报」这件事**只对后台子代理成立**。前台子代理的结果就是它那条
    `tool_result`,同步回到主代理,根本不存在「回传」这一步 —— 而这里以前的兜底是
    「没写 reportStatus 且不在跑 → reported」,前台在 `runtime.ts` 里恰恰从不写
    这个字段(只有 background 才写 'pending'),于是**每一张跑完的前台卡片**
    都挂着一句「结果已汇报给主代理」。那句话本该是后台专属的信号,
    结果人人都有,等于把两者的区别抹平了。
  */
  const background = state?.background === true;
  const reportStatus = !background
    ? 'none'
    : state?.reportStatus ?? (status === 'done' ? 'pending' : status === 'running' ? 'none' : 'reported');
  const reportLabel = reportStatus === 'pending'
    ? t('chat.subagent.report.pending')
    : reportStatus === 'injecting'
      ? t('chat.subagent.report.injecting')
      : reportStatus === 'reported'
        ? t('chat.subagent.report.reported')
        : reportStatus === 'blocked'
          ? t('chat.subagent.report.blocked')
        : undefined;
  const color = status === 'error' ? undefined : agentColorHex(state?.color);
  const stop = (): void => {
    const childRunId = state?.childRunId
    if (childRunId === undefined) return
    // abort 对已结束的 run 是空操作 —— 随后按主进程的实际状态把卡片收口,不让它卡在「运行中」。
    void abortRun(childRunId, true).catch(() => {}).then(() => settleSubagent(childRunId)).catch(() => {})
  }
  /*
    需求:前台子代理跑得比预想久时,让主代理先接着干别的 —— 子代理不停,
    结论跑完后按后台那条路送回来。只对**还在跑的前台**子代理成立。
    ★ 失败静默:false 只意味着它刚好跑完了,用户要的「别再等」已经成立。
  */
  const childRunId = state?.childRunId
  const toBackground = running && !background && childRunId !== undefined
    ? (): void => { void backgroundSubagent(childRunId).catch(() => {}) }
    : undefined
  /*
    ★ 没有 `childSessionId` 的是**旧转录**(那个字段是随这次改动才加进
    `subagent_start` 的)。它们点开会是一个空面板,所以这里索性不让点 ——
    一张点了没反应的卡片比一张不能点的卡片难解释得多。
  */
  const canOpen = openSubagent !== undefined && state !== undefined && state.childSessionId !== undefined;

  return (
    <Surface
      className="text-[13.5px] text-fg-muted"
      data-testid="subagent-node"
      data-subagent-call-id={state?.callId}
      data-subagent-status={status}
      data-subagent-background={state?.background === true ? 'true' : 'false'}
      data-subagent-color={state?.color}
    >
      {/* 停止按钮是标题行的**兄弟**,不是它的子节点 —— 按钮不能套按钮 */}
      <div className="flex w-full min-w-0 items-center">
        <button
          type="button"
          data-testid="subagent-toggle"
          aria-expanded={open}
          onClick={() => setOpen((value) => !value)}
          className={cn(ROW_CLASS, "flex-1 cursor-pointer")}
        >
          {/*
            ★ 子代理的身份色原先画在**整张卡**上(描边 + 5% 底色 + 左侧内阴影)。
            文本风格下没有卡可染,身份色改挂在图标和类型名上 —— 这两处是行内
            唯一「代表这个子代理是谁」的元素,颜色落在别处(比如整行文字)会把
            标题也染色,读起来像一条错误。
            测试 `subagent-card.test.ts` 通过 `data-testid="subagent-icon"` 守这条。
          */}
          <span
            data-testid="subagent-icon"
            className={cn(
              "flex h-4 w-4 shrink-0 items-center justify-center",
              color === undefined && (status === 'error' ? "text-danger" : "text-accent")
            )}
            style={color === undefined ? undefined : { color }}
          >
            {status === 'error'
              ? <CircleAlert size={13} />
              : status === 'done'
                ? <CheckCircle2 size={13} />
                // 还没派出去和「后台排队中」是同一种「还没开始」,共用沙漏/时钟那个图标
                : background || status === 'pending'
                  ? <Clock3 size={13} />
                  : <Bot size={13} />}
          </span>
          <span className="flex min-w-0 flex-1 items-center gap-1.5">
            <span className={cn(
              "min-w-0 truncate",
              status === 'error' ? "text-danger" : "text-fg"
            )}>{title}</span>
            <span className="flex min-w-0 shrink items-center gap-1.5 text-[11.5px] text-fg-faint">
              {state?.subagentType !== undefined && (
                <span className="truncate" style={color === undefined ? undefined : { color }}>
                  {state.subagentType}
                </span>
              )}
              {/*
                需求：卡片要能回答「这个子代理跑在哪条模型上」。
                模型由主进程在 `subagent_start` 里带着（`runtime.ts` 发的是
                `childRequestFor` 算出的那个别名，即「子代理文件声明 > 设置页默认子代理 >
                父 run」三档的结果），转录一直存着 `SubagentState.model`，只是没有消费者。

                ★ 别名和类型之间那个 `·` 是**独立的兄弟节点**，不是拼进文本里：
                旧转录、以及 `subagentType` 缺席的卡片会退化成「只剩模型」，
                拼在同一个字符串里会留下一个悬空的分隔符。

                ★ 没有这一格时不画。模型名是领域值，按 §6 不翻译、也不查别名表
                （`state.model` 本来就是别名，不是回包里的真实模型名）。
              */}
              {state?.subagentType !== undefined && state.model !== undefined && (
                <span aria-hidden className="shrink-0 text-fg-faint">·</span>
              )}
              {state?.model !== undefined && (
                <span className="truncate font-mono">{state.model}</span>
              )}
              {background && (
                <span
                  className={cn("shrink-0 text-[11px]", color === undefined && "text-accent")}
                  style={color === undefined ? undefined : { color }}
                >
                  {t('chat.subagent.mode.background')}
                </span>
              )}
            </span>
          </span>
          <span className="flex shrink-0 items-center gap-1.5">
            <span className={cn("text-[12px]",
              noticeLabel !== undefined ? "text-danger" : running ? "text-accent" : status === 'error' ? "text-danger" : "text-fg-faint")}
              style={color !== undefined && running && noticeLabel === undefined ? { color } : undefined}>
              {noticeLabel ?? t(`chat.subagent.status.${status}` as 'chat.subagent.status.running' | 'chat.subagent.status.done' | 'chat.subagent.status.error' | 'chat.subagent.status.aborted' | 'chat.subagent.status.pending')}
            </span>
            {duration !== undefined && <span className="font-mono text-[11.5px] text-fg-faint">{duration}</span>}
          </span>
          <RowChevron open={open} />
        </button>
        {canOpen && (
          <button
            type="button"
            data-testid="subagent-open"
            onClick={() => { if (state !== undefined) openSubagent?.(state) }}
            title={t('chat.subagent.open')}
            className="ml-1.5 shrink-0 rounded-[5px] px-1.5 py-0.5 text-[12px] text-accent transition-colors hover:bg-accent/10"
          >
            {t('chat.subagent.report.openRecord')}
          </button>
        )}
        {toBackground !== undefined && (
          <button
            type="button"
            data-testid="subagent-background"
            onClick={toBackground}
            aria-label={t('chat.subagent.toBackground')}
            title={t('chat.subagent.toBackground')}
            className="ml-1.5 inline-flex h-6 w-6 shrink-0 items-center justify-center rounded-[6px] text-fg-muted transition-colors hover:bg-tint-hover hover:text-fg"
          >
            <ArrowDownToLine size={12} />
          </button>
        )}
        {running && (
          <button
            type="button"
            data-testid="subagent-stop"
            onClick={stop}
            aria-label={t('chat.subagent.stop')}
            title={t('chat.subagent.stop')}
            className="ml-1.5 inline-flex h-6 w-6 shrink-0 items-center justify-center rounded-[6px] text-danger transition-colors hover:bg-danger/10"
          >
            <Square size={10} />
          </button>
        )}
      </div>
      <SurfaceReveal open={open} className="selectable">
        <div data-testid="subagent-detail" className="space-y-1.5 text-[12px] text-fg-muted">
          <p className="flex flex-wrap items-center gap-x-2 gap-y-0.5">
            {state?.phase !== undefined && (
              <span>{t(`chat.subagent.phase.${state.phase}` as 'chat.subagent.phase.starting')}</span>
            )}
            <span>{t('chat.subagent.activityCount', { count: state?.toolCalls ?? 0 })}</span>
            {(state?.toolErrors ?? 0) > 0 && (
              <span className="text-danger">{t('chat.subagent.detail.errors', { count: state?.toolErrors ?? 0 })}</span>
            )}
          </p>
          {running && state?.currentTool !== undefined && (
            <p className="truncate font-mono text-[11.5px] text-fg-faint">
              {state.currentTool}{state.currentTarget === undefined ? '' : ` ${state.currentTarget}`}
            </p>
          )}
          {(state?.activity?.length ?? 0) > 0 && (
            <ul className="space-y-0.5">
              {state?.activity?.slice(0, 5).map((entry, index) => (
                <li key={`${entry.toolName}-${String(index)}`} className="truncate font-mono text-[11.5px] text-fg-faint">
                  {entry.toolName}{entry.target === undefined ? '' : ` ${entry.target}`}
                </li>
              ))}
            </ul>
          )}
          {state?.summary !== undefined && state.summary.trim() !== '' && (
            <div className="scroll-thin max-h-[min(30vh,240px)] overflow-y-auto pr-1">
              <AgentMarkdown content={state.summary} variant="compact" />
            </div>
          )}
          {state === undefined && (
            <p className="text-fg-faint">{t('chat.subagent.detail.empty')}</p>
          )}
        </div>
      </SurfaceReveal>
      {noticeDetail !== undefined && (
        <SurfaceRow
          data-testid="subagent-notice"
          className="selectable text-[12px] leading-relaxed text-danger"
        >
          {noticeDetail}
        </SurfaceRow>
      )}
      {!running && reportLabel !== undefined && (
        <SurfaceRow className={cn(reportStatus === 'pending' ? "text-accent" : "text-fg-muted")}>
          <ListChecks size={12} className="shrink-0" />
          <span>{reportLabel}</span>
        </SurfaceRow>
      )}
      {/*
        ★ 失败原因**留一行在这里**,完整的错误框在面板里。
        详情整体搬走的那一条对错误不成立:一行只写着「错误」,等于逼着
        用户为了看一句话去开一个面板 —— 而失败恰恰是最该一眼看见的那件事。
        (原先这一行铺红底;文本风格下只留红字,标记方式见 Surface 文件头。)
      */}
      {status === 'error' && (
        <SurfaceRow data-testid="subagent-error" className="text-danger">
          <span className="min-w-0 truncate">
            {errorText === undefined || errorText.trim() === ''
              ? t('chat.subagent.detail.errorUnknown')
              : errorText}
          </span>
        </SurfaceRow>
      )}
    </Surface>
  );
}

/**
 * 后台子代理的结果**回到主线的那一行**。
 *
 * ★★ 在此之前这条消息在界面上整条不存在(它是 `internal` 的,被
 * `threadRows` 的可见性过滤直接跳过)。于是主代理会毫无来由地开口,
 * 讲一件几百轮之前派出去的事 —— 而**看不见输入的输出,比看不见输出更难解释**。
 *
 * ★ 它默认收起,只占一行。这终究不是用户说的话,不该长得像一条提问:
 * 一屏后台任务全文摊开的话,主线索性读不下去了。
 */
export function SubagentReportRow({
  summary,
  state,
}: {
  summary: string | undefined;
  state?: SubagentState;
}): ReactNode {
  const { t } = useI18n();
  const openSubagent = useOpenSubagent();
  const [open, setOpen] = useState(false);
  const canOpen = openSubagent !== undefined && state?.childSessionId !== undefined;

  /*
    ★★ **`summary` 只有 240 字,而且是主进程切的。**

    `runtime.ts` 发 `subagent_end` 和落盘时都做了 `text.slice(0, 240)` ——
    那个字段生来是给卡片当一行预览的。后台这条路把它当成了汇报正文,于是
    一份「改了八个文件、逐条说明」的报告在这里断在半个标识符上
    (真实案例:`added \`ModelStatusC`)。前台子代理没有这个问题:它的结果是
    `toolOk(outcome.text)`,全文直接进 tool_result。

    所以展开时现去子会话取全文 —— 和右侧那个只读面板同一个数据源
    (`runtime.ts` 取的也正是「最后一条 assistant 消息的 visibleText」)。
    放在展开时而不是汇报时,是因为**历史消息也能因此补全**:已经写进库里的
    那些汇报,正文里存的就是那 240 字,汇报时再取已经晚了。

    读取期间先摆着摘要,不摆空白或转圈 —— 用户点开是想看内容,
    先给他能给的那部分,全文到了再原地换掉。
  */
  const [full, setFull] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [failed, setFailed] = useState(false);
  const requested = useRef<string | undefined>(undefined);
  const childSessionId = state?.childSessionId;

  useEffect(() => {
    // 旧转录没有 `childSessionId` —— 取不到子会话,摘要就是全部了
    if (!open || childSessionId === undefined || requested.current === childSessionId) return;
    requested.current = childSessionId;
    let cancelled = false;
    setLoading(true);
    // 只取最后一条助手消息 —— 子代理交差的正文就是它,不为它读整段子转录
    void getLastAssistantMessage(childSessionId)
      .then((last) => {
        if (cancelled) return;
        const body = last === null ? "" : visibleText(last).trim();
        if (body === "") setFailed(true);
        else setFull(body);
      })
      .catch(() => { if (!cancelled) setFailed(true) })
      .finally(() => { if (!cancelled) setLoading(false) });
    return () => { cancelled = true };
  }, [open, childSessionId]);

  const brief = summary?.trim() ?? "";
  const text = full ?? brief;
  /*
    ★ 取不到全文时要**说出来**。悄悄摆着摘要的话,用户看到的是一段
    断在半路的报告,而没有任何东西告诉他这不是全部 —— 他会以为子代理
    本来就只说了这些。

    判据是「试过了没成」或「压根没得试」,不是「此刻还没有全文」——
    后者在效果跑起来之前那一帧也成立,会闪一下假的提示。
  */
  const legacy = childSessionId === undefined;
  const truncated = full === null && brief !== "" && (failed || legacy);

  return (
    <Surface
      data-testid="subagent-report-row"
      data-report-call-id={state?.callId}
      className="text-[13px] text-fg-muted"
    >
      <div className="flex w-full min-w-0 items-center">
        <button
          type="button"
          data-testid="subagent-report-toggle"
          aria-expanded={open}
          onClick={() => setOpen((value) => !value)}
          className={cn(ROW_CLASS, "flex-1 text-[13px]")}
        >
          {/*
            ★ 这一行原先靠**虚线边框 + accent 底色**说明「这不是一条消息,是一条回执」。
            文本风格下没有框可虚,那个区别改由左边这枚 accent 色的清单图标承担 ——
            删掉它的话,后台汇报行会和普通工具行长得一模一样,而它们的来历完全不同。
          */}
          <ListChecks size={13} className="shrink-0 text-accent" />
          <span className="min-w-0 flex-1 truncate">
            {state?.subagentType === undefined
              ? t("chat.subagent.report.rowGeneric")
              : t("chat.subagent.report.row", { agent: state.subagentType })}
          </span>
          {state?.description !== undefined && (
            <span className="max-w-[38%] shrink-0 truncate text-[12px] text-fg-faint">{state.description}</span>
          )}
          <RowChevron open={open} />
        </button>
        {canOpen && (
          <button
            type="button"
            data-testid="subagent-report-open"
            onClick={() => { if (state !== undefined) openSubagent?.(state) }}
            title={t("chat.subagent.open")}
            className="ml-1.5 shrink-0 rounded-[5px] px-1.5 py-0.5 text-[12px] text-accent transition-colors hover:bg-accent/10"
          >
            {t("chat.subagent.report.openRecord")}
          </button>
        )}
      </div>
      <SurfaceReveal open={open} className="selectable">
        <div className="scroll-thin max-h-[min(40vh,320px)] overflow-y-auto pr-1">
          {/*
            ★ 只渲染子代理**自己那段正文**,不是注入给模型的那整段英文
            (它外面还包着一层 "Background subagent result (...) / Review this result..."
            的指令壳)。那层壳是写给模型的,给人看只会碍事。
          */}
          {text === ""
            ? <p className="text-[12.5px] text-fg-faint">
                {loading ? t("chat.subagent.report.loading") : t("chat.subagent.report.empty")}
              </p>
            : <AgentMarkdown content={text} variant="compact" />}
          {loading && text !== "" && (
            <p data-testid="subagent-report-loading" className="mt-2 text-[12px] text-fg-faint">
              {t("chat.subagent.report.loading")}
            </p>
          )}
          {truncated && (
            <p data-testid="subagent-report-truncated" className="mt-2 text-[12px] text-fg-faint">
              {legacy ? t("chat.subagent.report.partialLegacy") : t("chat.subagent.report.partialFailed")}
            </p>
          )}
        </div>
      </SurfaceReveal>
    </Surface>
  );
}
