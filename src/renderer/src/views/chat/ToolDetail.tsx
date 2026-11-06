/**
 * 展开后的详情区 —— 按形态类分派的渲染器表。
 *
 * ★ **这里是 G2(零差异化)真正被解决的地方。** 现状是所有工具的入参和结果
 * 一律 `JSON.stringify` 塞进 `<pre>`,于是一次 `Write` 会把整个文件内容
 * 当作 JSON 字符串转义后显示出来 —— 换行变成 `\n`,几千字符挤成一坨,
 * 而那恰恰是用户最想看清楚的东西。
 *
 * ★ **`external` 分支保留了现状的 JSON 行为**,而且必须保留:MCP 工具的入参形状
 * 编译期不可知,任何「智能」猜测都会在某个 server 上猜错。通用 JSON 是唯一
 * 对未知输入永远正确的呈现。
 */
import type { ReactNode } from "react";
import type { ToolOutput, ToolOutputImage } from "../../../../shared/agent/message";
import type { ToolShape } from "../../../../shared/domain/tool-presenter";
import { isTodoListTool } from "../../../../shared/domain/tool-presenter";
import { ncwPreviewUrl } from "../../../../shared/domain/attachment";
import { useI18n } from "../../i18n";
import { cn } from "../../lib/cn";
import { DiffBlock } from "./DiffView";
import { CardRenderer } from "./CardRenderer";
import { CodeBlock, languageOf } from "../../components/code";
import { DETAIL_CARD_CLASS, DETAIL_CARD_DANGER_CLASS } from "./detail-card";
import { parseNumberedOutput } from "./read-output";
import { MatchList } from "./MatchList";
import { TerminalBlock } from "./TerminalBlock";
import { InteractionPreviewBlock } from "./InteractionPreviewBlock";
import { TodoWriteChecklist } from "./TodoWriteChecklist";
import { WidgetDetail } from "./WidgetDetail";
import { ImageGenDetail } from "./ImageGenDetail";
import { VideoGenDetail } from "./VideoGenDetail";
import { ScreenshotDetail } from "./ScreenshotDetail";
import { WorkflowDetail } from "./WorkflowDetail";

// ─────────────────────────── 原语 ───────────────────────────

/**
 * 详情区里的一个块。
 *
 * ★★ `label` 现在是**可选的,而且绝大多数调用点不给**。原先每个块头上都顶着
 * 一行「内容 / 更改 / 输出 / 命令」的小标题,而展开区是用户**主动点开**的 ——
 * 他已经知道自己点的是哪一行,再告诉他「这是输出」只是把真正的内容往下推一行。
 * 参考实现的展开区里一个小标题都没有:点开命令行就是终端,点开编辑行就是 diff。
 *
 * ★ 失败那一档**保留标题**:那时块里装的不是这个工具的产物,而是它失败的原因,
 * 红字能说明「出事了」,说不清「这段文字是错误信息而不是输出」。
 */
export function Labeled({
  label,
  children,
  tone = "normal",
}: {
  label?: string;
  children: ReactNode;
  tone?: "normal" | "danger";
}): ReactNode {
  return (
    <div className="mt-1.5 first:mt-0">
      {label !== undefined && (
        <p
          className={cn(
            "mb-0.5 text-[12px]",
            tone === "danger" ? "text-danger" : "text-fg-faint",
          )}
        >
          {label}
        </p>
      )}
      <pre
        className={cn(
          "selectable scroll-thin max-h-56 overflow-auto px-3 py-2 font-mono text-[12.5px] leading-[1.6] whitespace-pre-wrap",
          tone === "danger" ? DETAIL_CARD_DANGER_CLASS : DETAIL_CARD_CLASS,
          tone === "danger" ? "text-danger" : "text-fg-muted",
        )}
      >
        {children}
      </pre>
    </div>
  );
}

/** 未知形状的入参/结果:退回通用 JSON 呈现(对任何 MCP 工具都永远正确)。 */
export function stringify(v: unknown): string {
  if (typeof v === "string") return v;
  try {
    return JSON.stringify(v, null, 2) ?? String(v);
  } catch {
    // 循环引用 / BigInt / Symbol 都会走到这里。详情区显示一行退化文本,
    // 好过让整个转录因为一次序列化异常白屏。
    return String(v);
  }
}

/** 安全取字段(与 tool-presenter 的 pick 同构,但这里允许返回非字符串) */
function field(input: unknown, key: string): unknown {
  if (typeof input !== "object" || input === null) return undefined;
  return (input as Record<string, unknown>)[key];
}

function str(input: unknown, key: string): string {
  const v = field(input, key);
  return typeof v === "string" ? v : "";
}

/**
 * 超长文本按行截断,并注明省略了多少。
 *
 * 不用 CSS `max-height` 截断的原因:那样滚动条里仍然挂着几万行 DOM 文本,
 * 而这个组件在一次 run 里可能存在几十份。行数截断是把它挡在渲染之前。
 */
function clipLines(
  text: string,
  max: number,
): { text: string; omitted: number } {
  const lines = text.split("\n");
  if (lines.length <= max) return { text, omitted: 0 };
  return { text: lines.slice(0, max).join("\n"), omitted: lines.length - max };
}

function Truncated({ omitted }: { omitted: number }): ReactNode {
  const { t } = useI18n();
  if (omitted <= 0) return null;
  return (
    <span className="text-fg-faint">
      {t("chat.tool.linesOmitted", { count: omitted })}
    </span>
  );
}

/**
 * 结果块 —— 八个渲染器里有七个都要用,所以抽出来。`WidgetDetail` 用的是它。
 *
 * ★ 成功时**不带标题**(见 `Labeled`);失败时带,因为那一段是失败原因,不是产物。
 */
export function OutputBlock({
  output,
  label,
  isError = false,
  maxLines = 40,
}: {
  output: ToolOutput | undefined;
  /** 只在少数确实需要区分「这是哪一段」的地方给(目前没有)。默认不画标题 */
  label?: string;
  isError?: boolean;
  maxLines?: number;
}): ReactNode {
  const { t } = useI18n();
  if (output === undefined) return null;
  const { text, omitted } = clipLines(output.content, maxLines);
  const heading = isError ? t("chat.tool.failureReason") : label;
  return (
    <Labeled
      {...(heading === undefined ? {} : { label: heading })}
      tone={isError ? "danger" : "normal"}
    >
      {text}
      <Truncated omitted={omitted} />
      {output.truncated === true && (
        <span className="text-fg-faint">
          {t("chat.tool.truncated", { bytes: output.originalBytes ?? "?" })}
        </span>
      )}
      {(output.images ?? []).map((image, index) => (
        <img
          key={`${image.dataRef.slice(0, 48)}:${index}`}
          src={ncwPreviewUrl(image.dataRef)}
          alt={t("chat.tool.result")}
          className="mt-2 max-h-96 max-w-full rounded-lg border border-hairline object-contain"
        />
      ))}
    </Labeled>
  );
}

// ─────────────────────────── 各形态渲染器 ───────────────────────────

export interface DetailProps {
  input: unknown;
  output: ToolOutput | undefined;
  isError: boolean;
  /**
   * 转录里的工具名(externalName)。
   *
   * 需求:`interaction` 这一档的三个工具**入参形状各不相同**(题目数组 / 一句条件 /
   * 空对象),而形态类只分到「这是一次等你表态」为止。再往下分到具体工具的那一步
   * 由 `interaction-preview.ts` 的表完成,所以这里必须把名字传下去。
   * 其余渲染器用不到它 —— 别拿它在别处开按工具名分叉的口子,那正是注册表要消掉的东西。
   *
   * (例外:`orchestration` 这一档确实要按名字分流 —— 但判据来自注册表里的
   * `isTodoListTool`,不是这里现写一个 `toolName === 'TodoWrite'`。)
   */
  toolName?: string;
  /**
   * 这次调用的 id。
   *
   * 原先只为 frame 卡片反查 pluginId 而存在;现在 `orchestration` 里那个清单渲染器
   * 也用它去问「这次调用**之前**那份清单是什么」(`views/chat/todo-history.tsx`)——
   * 那是算增量唯一缺的一块事实。
   */
  callId?: string;
  /**
   * 生图工具运行中已经到手的那几张(`ToolCallState.partialImages`,按格子序号)。
   *
   * 需求:`image` 形态的卡片要逐张把加载格换成图,而那几张在 `tool_end` 之前只存在于
   * 易失的调用状态里,`output` 还没有。只有 `ImageGenDetail` 读它。
   */
  partialImages?: Readonly<Record<number, ToolOutputImage>>;
  /**
   * `Workflow` 运行中的整份节点状态(`ToolCallState.workflow`)。同 `partialImages`:
   * `tool_end` 之前只存在于易失的调用状态里。只有 `WorkflowDetail` 读它。
   */
  workflow?: import("../../../../shared/agent/workflow").WorkflowState;
}

/**
 * read:文件内容按**带高亮的代码卡**渲染。
 *
 * ★ `Read` 给模型的是 `cat -n` 格式(行号 + 制表符 + 正文)。行号必须先拆出来
 * 再喂给高亮器 —— 每行以数字开头的"源码"解析出来的树和真代码毫无关系,
 * 染出来的颜色比不染更糟。拆解在 `read-output.ts`,那里写了为什么允许失败。
 * ★ 路径那一行删了:行本身已经写着文件名和目录(见 `row.tsx` 的版式)。
 */
function ReadDetail({ input, output, isError }: DetailProps): ReactNode {
  if (output === undefined) return null;
  if (isError) return <OutputBlock output={output} isError maxLines={20} />;
  const { code, startLine } = parseNumberedOutput(output.content);
  return (
    <CodeBlock
      code={code}
      language={languageOf(str(input, "file_path") || str(input, "path"))}
      startLine={startLine}
      maxLines={40}
      className={DETAIL_CARD_CLASS}
    />
  );
}

/**
 * mutate:**入参里的 `content` / `new_string` 才是重点**,而它们正是现状
 * JSON 化之后最不可读的部分。
 *
 * ★ **改一段(Edit)走 diff,整份新内容(Write)走带高亮的代码卡。**
 * 判据是「有没有另一侧可对照」:`Write` 的 `content` 是一份**全新的文件**,
 * 把它画成「全是新增」的 diff 等于给每一行都刷上绿底并加一个 `+` ——
 * 那是一份读不动的源码,而这一步用户最想做的就是把新文件读一遍。
 * 改了多少行仍然在行右端的 `+N`(见 `ToolLineStats`),信息没丢。
 *
 * ★ **Edit 的新文本一律走 `DiffBlock`,参数还在流的时候也一样。** 判据原先是
 * 「old 和 new 都到齐」,于是一次 Edit 在流式阶段先显示成两块纯文本,
 * `new_string` 收尾的那一刻整块换成 diff —— 同一件事两套画法,而切换恰好发生在
 * 用户正盯着看的时刻。
 *
 * ★ diff 上方的「更改 / 写入内容」小标题删了,路径行同理 —— 见 `Labeled`。
 */
function MutateDetail({ input, output, isError }: DetailProps): ReactNode {
  const content = str(input, "content");
  const oldStr = str(input, "old_string");
  const newStr = str(input, "new_string");
  const language = languageOf(str(input, "file_path"));

  return (
    <>
      {newStr !== "" && <DiffBlock oldStr={oldStr} newStr={newStr} language={language} />}
      {/* new 还没开始流:此刻只有「要被换掉的那一段」,没有第二侧可对照 */}
      {newStr === "" && oldStr !== "" && (
        <CodeBlock code={oldStr} language={language} maxLines={12} className={DETAIL_CARD_CLASS} />
      )}
      {content !== "" && (
        <CodeBlock code={content} language={language} startLine={1} maxLines={40} className={DETAIL_CARD_CLASS} />
      )}
      {/*
        ★ 成功时的输出是一句「Edited a.ts: replaced 3 occurrence(s).」——
        行右端的 `+N −M` 已经把同一件事说得更准,所以只在失败时才画这一块。
      */}
      {isError && <OutputBlock output={output} isError maxLines={12} />}
    </>
  );
}

/**
 * search:模式 + **命中清单**。
 *
 * ★ 清单走 `MatchList`(文件名可点开),不是一坨文本:找到了东西之后,
 * 下一步一定是去看它 —— 让用户把路径复制出来再去文件树里翻是这一步最大的浪费。
 */
function SearchDetail({ input, output, isError }: DetailProps): ReactNode {
  const { t } = useI18n();
  const pattern = str(input, "pattern");
  const scope = str(input, "path") || str(input, "glob");
  return (
    <>
      {pattern !== "" && (
        <div className="mb-1.5 flex flex-wrap items-center gap-x-2 gap-y-1 text-[12px] text-fg-faint">
          <span className="rounded-[5px] border border-stroke bg-surface-raised px-1.5 py-0.5 font-mono text-fg-muted">
            {pattern}
          </span>
          {scope !== "" && (
            <span className="truncate">
              {t("chat.tool.query")} {scope}
            </span>
          )}
        </div>
      )}
      {isError
        ? <OutputBlock output={output} isError maxLines={20} />
        : output !== undefined && <MatchList content={output.content} />}
    </>
  );
}

/**
 * command:命令和它的输出是**同一块终端**。
 *
 * 渲染细节(输出不折行、两条流分色、`<stdout>` 信封不出现、命令那一行折行)
 * 全在 `TerminalBlock` 里 —— 那个文件的头注释写了每一条的理由。
 */
function CommandDetail({ input, output, isError }: DetailProps): ReactNode {
  return (
    <TerminalBlock
      command={str(input, "command")}
      output={output}
      isError={isError}
    />
  );
}

/**
 * network:URL + 抓回来的正文。
 *
 * ★ 正文走 `CodeBlock`:抓回来的东西大多是 JSON 或已经排好版的 Markdown/文本,
 * 折行会把 JSON 的层级和表格拆散。提示词(`prompt`)相反,那是写给模型的散文,
 * 所以仍然用会折行的 `Labeled`。
 */
function NetworkDetail({ input, output, isError }: DetailProps): ReactNode {
  const { t } = useI18n();
  const url = str(input, "url");
  const query = str(input, "query");
  const prompt = str(input, "prompt");
  return (
    <>
      {url !== "" && <PathLine path={url} />}
      {query !== "" && (
        <div className="mb-1.5 text-[12.5px] text-fg-muted">
          <span className="text-fg-faint">{t("chat.tool.query")}</span> {query}
        </div>
      )}
      {prompt !== "" && <Labeled>{clipLines(prompt, 6).text}</Labeled>}
      {isError
        ? <OutputBlock output={output} isError maxLines={20} />
        : output !== undefined && (
          <CodeBlock code={output.content} language={languageOf(url)} maxLines={40} className={DETAIL_CARD_CLASS} />
        )}
    </>
  );
}

/**
 * orchestration 形态下有两条路,判据是 `isTodoListTool(toolName)`
 * (`shared/domain/tool-presenter.ts` 里那张数据表):
 *
 * - **自带清单的那个**(`TodoWrite`)→ `TodoWriteChecklist`,见它的文件头;
 * - 其余(`Task` / `Skill` / 四个定时任务工具)→ 下面的提示词 + 输出。
 *
 * ★ 判据**不能**是「入参里有没有 `todos`」:模型往任何工具里塞一个 `todos` 字段
 * 都会让那张卡片改画成清单,而那张卡片可能根本没有清单语义。名字来自注册表,
 * 与标题行、摘要用的是同一份事实。
 */
function OrchestrationDetail({
  input,
  output,
  isError,
  toolName,
  callId,
}: DetailProps): ReactNode {
  const { t } = useI18n();
  if (toolName !== undefined && isTodoListTool(toolName)) {
    return (
      <>
        <TodoWriteChecklist
          callId={callId}
          input={input}
          className="max-w-none px-0 pb-1.5"
        />
        <OutputBlock output={output} isError={isError} maxLines={6} />
      </>
    );
  }

  // Task / Skill:提示词往往很长,截断显示
  const prompt = str(input, "prompt");
  const name = str(input, "name") || str(input, "subagent_type");
  return (
    <>
      {name !== "" && (
        <div className="mb-1.5 text-[12.5px] text-fg-muted">
          <span className="text-fg-faint">{t("chat.tool.name")}</span> {name}
        </div>
      )}
      {/* 提示词是写给模型的散文 —— 折行读,所以留在 Labeled 而不是 CodeBlock */}
      {prompt !== "" && (
        <Labeled>
          {clipLines(prompt, 10).text}
          <Truncated omitted={clipLines(prompt, 10).omitted} />
        </Labeled>
      )}
      <OutputBlock output={output} isError={isError} maxLines={30} />
    </>
  );
}

/** reasoning:纯文本流,不加等宽字体 —— 它是自然语言,不是代码 */
function ReasoningDetail({ output }: DetailProps): ReactNode {
  if (output === undefined) return null;
  return (
    <p className="selectable text-[12.5px] leading-relaxed whitespace-pre-wrap text-fg-faint">
      {output.content}
    </p>
  );
}

/**
 * interaction:入参**就是给人读的题面**,所以整块按题面渲染,而且在参数还在流的
 * 时候就渲染 —— 见 `InteractionPreviewBlock` 与 `interaction-preview.ts`。
 *
 * 结果块照样保留:答完之后那段 JSON 是「用户当时选了什么」的唯一记录,
 * 而历史转录里那张可作答的卡片早就不在了。
 */
function InteractionDetail({
  input,
  output,
  isError,
  toolName,
}: DetailProps): ReactNode {
  return (
    <>
      <InteractionPreviewBlock
        toolName={toolName}
        input={input}
        live={output === undefined}
      />
      <OutputBlock output={output} isError={isError} maxLines={20} />
    </>
  );
}

/**
 * external:兜底 —— 入参与结果都按 JSON 画。
 *
 * ★ 这是**唯一对未知输入永远正确**的呈现(见文件头);走 `CodeBlock` 之后
 * 至少 JSON 的缩进层级保得住 —— 折行的 JSON 和一行行读的 JSON 是两种东西。
 */
function ExternalDetail({ input, output, isError }: DetailProps): ReactNode {
  return (
    <>
      <CodeBlock code={stringify(input)} language="json" maxLines={30} className={DETAIL_CARD_CLASS} />
      {isError
        ? <OutputBlock output={output} isError maxLines={20} />
        : output !== undefined && (
          <CodeBlock code={output.content} maxLines={40} className={cn(DETAIL_CARD_CLASS, "mt-1.5")} />
        )}
    </>
  );
}

function PathLine({ path }: { path: string }): ReactNode {
  return (
    <p
      className="mb-1.5 truncate font-mono text-[12px] text-fg-faint"
      title={path}
      dir="rtl"
    >
      {/* dir=rtl 让超长路径从**头部**省略 —— 尾部的文件名才是有用的那一半 */}
      <span dir="ltr">{path}</span>
    </p>
  );
}

/**
 * 形态类 → 详情渲染器。
 *
 * `Record<ToolShape, …>` 而不是 `Partial<…>`:少写一个形态类会**编译期**报错,
 * 而不是运行时渲染出一片空白。
 */
export const DETAIL_RENDERERS: Record<
  ToolShape,
  (p: DetailProps) => ReactNode
> = {
  reasoning: ReasoningDetail,
  read: ReadDetail,
  mutate: MutateDetail,
  search: SearchDetail,
  command: CommandDetail,
  network: NetworkDetail,
  orchestration: OrchestrationDetail,
  interaction: InteractionDetail,
  /*
    widget:生成期由它渲染半截 HTML;跑完之后 `ToolDetail` 会先被 `card` 那条
    短路接走(`if (card !== undefined)`),走 `CardRenderer` 的 widget 分支。
    两个分支共用同一个 `WidgetFrame` —— 见 `WidgetDetail.tsx` 的文件头。
  */
  widget: WidgetDetail,
  // image:生图的专属图片卡 —— 加载格、逐张出图、成品网格都在 `ImageGenDetail.tsx`
  image: ImageGenDetail,
  // video:生成视频的专属卡片 —— 异步任务的生命周期 + 播放器,见那边的文件头
  video: VideoGenDetail,
  /*
    screenshot:浏览器页面截图(`browser_screenshot`)的卡片 —— 见它的文件头。
    与 `image` 分开的理由写在 `ToolShape` 里那一段:生图有生成期与提示词,
    截图只有一帧,复用那张卡会在图上多出一块永远空着的提示词区。
  */
  screenshot: ScreenshotDetail,
  // workflow:工作流画布(实验功能),懒加载 @xyflow/react,见 `WorkflowCanvas.tsx` 文件头
  workflow: WorkflowDetail,
  external: ExternalDetail,
};

export function ToolDetail({
  shape,
  input,
  output,
  isError,
  toolName,
  callId,
  card,
  partialImages,
  workflow,
}: {
  shape: ToolShape;
  /*
    `toolName` / `callId` 移进了 `DetailProps`(见那里的注释):前者原先只为 frame
    卡片反查 pluginId 而存在,现在 `interaction` 的分派与 `orchestration` 的清单
    分流都要它;后者现在也是清单增量那块的入参。
  */
  /**
   * 要渲染的卡片。调用方决定优先级:结果快照(`output.card`)或运行中的实时卡片
   * (`ToolCallState.card`,第 2 层)。给了它就渲染卡片,**与 shape 正交**。
   */
  card?: import("../../../../shared/agent/tool-card").ToolCard;
} & DetailProps): ReactNode {
  // ★ card 是 UI 轨专属,编码器早已 strip,模型看不到它。
  if (card !== undefined) {
    return <CardRenderer card={card} toolName={toolName} callId={callId} />;
  }
  const Renderer = DETAIL_RENDERERS[shape];
  return (
    <Renderer
      input={input}
      output={output}
      isError={isError}
      toolName={toolName}
      callId={callId}
      partialImages={partialImages}
      workflow={workflow}
    />
  );
}
