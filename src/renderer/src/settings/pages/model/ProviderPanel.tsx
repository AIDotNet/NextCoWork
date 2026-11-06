import {
  AlertTriangle,
  Brain,
  Check,
  CloudDownload,
  Download,
  Copy,
  Eye,
  EyeOff,
  ExternalLink,
  GripVertical,
  Pencil,
  Plus,
  Trash2,
  Upload,
  X } from "lucide-react";
import { useEffect, useRef, useState, type ReactNode } from "react";
import {
  baseUrlWarnings,
  normalizeBaseUrl,
  previewUrl,
} from "../../../../../shared/domain/baseurl";
import {
  findPreset,
} from "../../../../../shared/domain/presets";
import { codingPlanFamilyFor } from "../../../../../shared/domain/coding-plan";
import type {
  AnthropicCacheTtl,
  CredentialInfo,
  RevealedCredential,
  ModelAlias,
  ModelModality,
  ProtocolFamily,
  ReasoningEffort,
  ThinkingConfig,
  ThinkingMode,
  UpstreamProtocol,
  UpstreamProvider,
} from "../../../../../shared/domain/provider";
import {
  anthropicCacheTtlOf,
  effectiveModelProtocol,
  joinProtocol,
  MAX_ALIASES_PER_PROVIDER,
  splitProtocol,
} from "../../../../../shared/domain/provider";
import { Button } from "../../../components/arc/button/button";
import { Dialog } from "../../../components/ui/Dialog";
import SegmentedControl from "../../../components/arc/segmented-control/segmented-control";
import { Select } from "../../../components/ui/Select";
import { TextInput } from "../../../components/ui/TextInput";
import { Switch } from "../../../components/arc/switch/switch";
import { cn } from "../../../lib/cn";
import { openExternal } from "../../../services/app";
import {
  cancelOAuth,
  getCredentialInfo,
  revealCredential,
  removeModel,
  removeProvider,
  renameModel,
  setCredential,
  setProviderAliases,
  signOut,
  startOAuth,
  submitOAuthCode,
  updateModel,
  upsertProvider,
  exportProviders,
  importProviders,
} from "../../../services/provider";
import { useDragReorder } from "../../../shell/useDragReorder";
import { type ProviderEntry } from "./enabled-models";
import { ImportModelsDialog } from "./ImportModelsDialog";
import { modelListAvailability } from "./import-models";
import { ProviderAvatar } from "./ProviderAvatar";
import { ProviderAccounts } from "./ProviderAccounts";
import { PROTOCOLS, protocolLabel } from "./ModelProtocol";
import { baseUrlForProtocol } from "./provider-edit";
import {
  credentialInUse,
  oauthIssuerLabel,
  oauthView,
  providerAuthMode,
  providerOAuthIssuer,
  signInEndpointSwitch,
  type OAuthPhase,
  type OAuthView,
} from "./provider-auth";
import { useI18n } from "../../../i18n";
import { Spinner } from '../../../components/ui/Spinner'

const REASONING_EFFORTS: readonly ReasoningEffort[] = [
  "none",
  "minimal",
  "low",
  "medium",
  "high",
  "xhigh",
  "max",
];

/**
 * 参考图右边那张卡片。
 *
 * ★★ **这张表单以前整个包在 `<fieldset disabled>` 里,现在拆了。**
 * 当时的理由是真的:`provider:upsert` / `setCredential` 在 `main/ipc/index.ts` 里
 * 是 `todo()`,而一个「看着能填、保存时抛错」的表单会让用户第一反应是「我填错了」。
 * 那两条频道现在接上了(`main/ipc/provider.ts` 的写入面),所以禁用的理由没了。
 *
 * **`test` 仍然是 todo**,所以这里**没有「测试连接」按钮** —— 不是漏了。
 * 它要真发一次请求,而非 Anthropic 协议的编解码还没写(步骤 13);
 * 现在放上去,给 OpenAI 格式的供应商点一下必然失败,报出来还是「连不通」。
 *
 * ★ **「从服务商拉取模型列表」和它不一样,那个通了。** 拉列表只是一个
 * `GET`,不经过编解码那条路 —— 所以三个协议今天都能拉。三处协议差异
 * (路径 / 鉴权头 / **Anthropic 的游标分页默认只给 20 条**)在
 * `main/kernel/upstream/model-list.ts`,那边是纯函数、有测试。
 *
 * ## 三个字段的提交时机不一样,这是刻意的
 *
 * | 字段 | 时机 | 为什么 |
 * |---|---|---|
 * | 名称 / API 地址 | **失焦或回车** | 逐键写入 = 每敲一个字符一次 IPC + 一次全窗口广播,而且中间态(只打了 `http://`)会被存进去 |
 * | API 格式 / Responses | **立即** | 它们是离散选择,没有中间态;而且翻开关会连带换地址,拖到失焦才生效会让人以为没生效 |
 * | API 密钥 | 显式点「保存」 | 密钥写入/查看都是有意动作,不该被一次失焦顺手提交 |
 *
 * ★ 地址在提交前跑一遍 `normalizeBaseUrl`(参考图那句「离开输入框后会自动识别并整理」),
 * 并把整理后的值**写回输入框**。不写回的话用户看到的还是自己粘的那条完整请求地址,
 * 而存进去的是另一个 —— 那种不一致比不整理更难查。
 *
 * ★ 写完**不本地 `set`**:主进程广播 `provider:changed`,`stores/models.ts` 订着。
 * 让广播成为唯一的更新入口(同 `stores/mcp.ts` 的规矩),否则多窗口下两条路会算出不同的结果。
 *
 * ★ 删除是**就地两步**,不弹确认框。`Dialog` 会 portal 到 body 并抢焦点陷阱,
 * 为一句「确定吗」搭一层模态,代价比它挡住的误触还大;而两步按钮同样需要
 * 第二次有意的点击。删除连密钥一起删(`provider:remove` 那边),所以这一步不能省。
 */
export function ProviderPanel({
  entry,
  modality = "text",
  preserveAliases = [],
  accountRotation = true,
}: {
  entry: ProviderEntry;
  modality?: ModelModality;
  preserveAliases?: readonly ModelAlias[];
  /**
   * 设置里的「账号自动切换」。只用来算账号列表顶部那句「下一次会用谁」。
   *
   * ★ 默认 `true` 和 `DEFAULT_SETTINGS.providerAccountRotation` 一致 ——
   * 两处不一致的话,没传这个 prop 的调用点(图片模型页)会把「会用 A」
   * 算成「会用 B」,而那一句话没有任何地方会提示它是猜的。
   */
  accountRotation?: boolean;
}): ReactNode {
  const { t } = useI18n();
  const { provider: p, aliases } = entry;
  const managed = p.id === "nextcowork";
  const { family, responses } = splitProtocol(p.protocol);
  const hasAnthropicModels = family === "anthropic" ||
    [...aliases, ...preserveAliases].some((model) => effectiveModelProtocol(p, model) === "anthropic");
  const preset = findPreset(p.id);
  const apiKeyUrl = preset?.apiKeyUrl;
  const apiKeyActionLabel =
    preset?.credentialKind === "access-key"
      ? t("provider.getAccessKey")
      : preset?.credentialKind === "api-password"
        ? t("provider.getApiPassword")
        : preset?.credentialKind === "subscription-key"
          ? t("provider.getSubscriptionKey")
          : t("provider.getApiKey");
  /*
    ★ 这里**只取提示文案,不再有置灰**。曾经的 `supportsModelList: false` 置灰
    是照一张不可靠的快照下禁令 —— 多数国内网关先验鉴权再路由,没有效 key 时
    401 会被误读成「这家没有列表端点」(千帆就是被标错的那个)。而且主进程的
    `fetchModels` 本来就写明了「试了再说」,前端焊死按钮等于把那条路又堵上。
    判据和三条提示分支在 `import-models.ts` 的 modelListAvailability,那边有测试。
  */
  const listAvail = modelListAvailability(p);

  const [name, setName] = useState(p.name);
  const [baseUrl, setBaseUrl] = useState(p.baseUrl);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  /** 「地址跟着协议换了」的一次性提示。换供应商或再改一次就消失 */
  /**
   * 地址刚刚被我们自己换掉了。
   *
   * ★ 带 `reason`:换地址有两个触发点(翻 API 格式开关 / 登录成功切端点),
   * 而两句提示说的不是一回事 —— 只给一句通用的「地址跟着换了」,
   * 登录那次用户会以为是自己不小心碰到了开关。
   */
  const [swapped, setSwapped] = useState<{
    url: string;
    reason: "protocol" | "sign-in";
  } | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [thinkingModel, setThinkingModel] = useState<ModelAlias | null>(null);
  const [editingModel, setEditingModel] = useState<ModelAlias | null>(null);
  const [deletingModel, setDeletingModel] = useState<ModelAlias | null>(null);
  const [addingModel, setAddingModel] = useState(false);
  const [modelDraft, setModelDraft] = useState("");
  const addingModelRequest = useRef(false);

  const [importOpen, setImportOpen] = useState(false);
  const [transferOpen, setTransferOpen] = useState<"export" | "import" | null>(null);
  const [transferCredentials, setTransferCredentials] = useState(false);
  const [transferPassword, setTransferPassword] = useState("");
  const [transferBusy, setTransferBusy] = useState(false);

  const [cred, setCred] = useState<CredentialInfo | null>(null);
  const [keyDraft, setKeyDraft] = useState("");
  const [editingKey, setEditingKey] = useState(false);
  // 明文只活在当前面板实例；切供应商、保存或退出登录都会立即清掉。
  const [revealedCredential, setRevealedCredential] = useState<RevealedCredential | null>(null);
  const [revealingCredential, setRevealingCredential] = useState(false);
  const revealRequest = useRef(0);
  /** 登录流程的阶段。`null` = 现在没有登录在跑 */
  const [authFlow, setAuthFlow] = useState<{
    phase: OAuthPhase;
    needsPastedCode?: boolean;
    userCode?: string;
    verificationUri?: string;
  } | null>(null);
  const [cacheTtl, setCacheTtl] = useState<AnthropicCacheTtl>(() =>
    anthropicCacheTtlOf(p),
  );

  const authMode = providerAuthMode(p.id);
  const issuer = providerOAuthIssuer(p.id);
  const authView = oauthView(cred, authFlow);

  /*
    ★ 只认 `p.id`,不认 `p`。挂上 `p` 的话,保存成功后广播回来会再跑一次 ——
    用户已经开始改下一个字段的草稿会被冲掉。切换供应商才该重置草稿。
  */
  useEffect(() => {
    setName(p.name);
    setBaseUrl(p.baseUrl);
    setError(null);
    setSwapped(null);
    setKeyDraft("");
    setEditingKey(false);
    revealRequest.current += 1;
    setRevealedCredential(null);
    setRevealingCredential(false);
    setCred(null);
    setAuthFlow(null);
    setConfirmDelete(false);
    setThinkingModel(null);
    setEditingModel(null);
    setDeletingModel(null);
    setAddingModel(false);
    setModelDraft("");
    addingModelRequest.current = false;
    setCacheTtl(anthropicCacheTtlOf(p));

    let alive = true;
    void getCredentialInfo(p.id)
      .then((info) => {
        if (alive) setCred(info);
      })
      .catch((e: unknown) => console.error("[provider] 读取密钥状态失败", e));
    return () => {
      alive = false;
    };
    // 依赖只有 p.id 是刻意的,理由见上面那段注释(这个仓库没开 exhaustive-deps 规则)
  }, [p.id]);

  // Provider broadcasts are the source of truth after a discrete option is
  // saved. Keep the control synchronized when another window changes it.
  useEffect(() => {
    setCacheTtl(anthropicCacheTtlOf(p));
  }, [p.protocolOptions?.anthropic?.cacheTtl]);

  /*
    登录态的两条推送。

    ★ `authChanged` **不只在登录/退出时来** —— 刷新 token 失败把凭证标成
    「需要重新登录」时主进程也推它。不订的话,用户面前那句「已登录」会一直挂着,
    直到他关掉设置页再打开。

    ★ 退订函数必须返回(preload 的协议),否则 HMR 下监听器会叠加。
  */
  useEffect(() => {
    const offChanged = window.nextcowork.on("provider:authChanged", (e) => {
      if (e.providerId !== p.id) return;
      revealRequest.current += 1;
      setRevealingCredential(false);
      setRevealedCredential(null);
      setCred(e.info);
    });
    const offProgress = window.nextcowork.on("provider:authProgress", (e) => {
      if (e.providerId !== p.id) return;
      // 终态由 startOAuth 那条 invoke 的返回值给,这里只驱动中间的三个阶段
      setAuthFlow(
        e.phase === "opening" || e.phase === "waiting" || e.phase === "exchanging"
          ? {
              phase: e.phase,
              ...(e.needsPastedCode === true ? { needsPastedCode: true } : {}),
              /* ★ 设备码的配对码只在 `waiting` 上有值，原样透传给 `oauthView` */
              ...(e.userCode === undefined ? {} : { userCode: e.userCode }),
              ...(e.verificationUri === undefined
                ? {}
                : { verificationUri: e.verificationUri }),
            }
          : null,
      );
    });
    return () => {
      offChanged();
      offProgress();
    };
  }, [p.id]);

  const doSignIn = (): void => {
    setError(null);
    setAuthFlow({ phase: "opening" });
    void startOAuth(p.id)
      .then((info) => {
        setCred(info);
        /*
          ★★ **两种凭证不一定打同一个地址。** 判断在 `signInEndpointSwitch` 里
          （那边有测试）：它按 issuer 查一张穷尽的表 —— Z.AI 那条登录换来的
          令牌确实要切到 anthropic 端点，智谱那条**不切**（见那张表上的注释）。
          不该切却切了的表现，和该切没切一样难查：用户在一个写着「已登录」的
          界面上发出第一条消息，撞上一个不解释原因的错误，而表单从头到尾
          看着都是对的。它对「用户自己改过地址」一律返回 null。
        */
        if (issuer === null) return;
        const next = signInEndpointSwitch(p, issuer);
        if (next === null) return;
        setBaseUrl(next.baseUrl);
        // ★ 静默换地址是这一整块最不该有的行为
        setSwapped({ url: next.baseUrl, reason: "sign-in" });
        save({ protocol: next.protocol, baseUrl: next.baseUrl });
      })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setAuthFlow(null));
  };

  /** 手动粘贴形态下把回调地址交回主进程。终态由那条还在跑的 startOAuth 给 */
  const doSubmitCode = (pasted: string): void => {
    setError(null);
    void submitOAuthCode(p.id, pasted).catch((e: unknown) =>
      setError(e instanceof Error ? e.message : String(e)),
    );
  };

  const doCancelSignIn = (): void => {
    void cancelOAuth(p.id).catch(() => {
      /* 取消失败没什么可做的 —— 那条流程要么已经结束，要么马上超时 */
    });
  };

  const doSignOut = (): void => {
    setBusy(true);
    setError(null);
    revealRequest.current += 1;
    setRevealingCredential(false);
    setRevealedCredential(null);
    void signOut(p.id)
      .then(setCred)
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setBusy(false));
  };

  const save = (patch: Partial<UpstreamProvider>): void => {
    setBusy(true);
    setError(null);
    void upsertProvider({ ...p, ...patch })
      .catch((e: unknown) => {
        setError(e instanceof Error ? e.message : String(e));
        // 存不下去就把草稿退回库里那份 —— 留着一个没生效的值在框里,
        // 用户下次打开会以为它已经存进去了
        setName(p.name);
        setBaseUrl(p.baseUrl);
        setCacheTtl(anthropicCacheTtlOf(p));
      })
      .finally(() => setBusy(false));
  };

  const commitName = (): void => {
    const v = name.trim();
    if (v === "" || v === p.name) return setName(p.name);
    save({ name: v });
  };

  const commitUrl = (): void => {
    const v = normalizeBaseUrl(baseUrl);
    setBaseUrl(v); // ★ 整理后的值写回框里,别让显示的和存的是两个东西
    if (v === "" || v === p.baseUrl) return;
    save({ baseUrl: v });
  };

  const switchProtocol = (next: UpstreamProvider["protocol"]): void => {
    const r = baseUrlForProtocol(p, next);
    setBaseUrl(r.baseUrl);
    setSwapped(r.changed ? { url: r.baseUrl, reason: "protocol" } : null);
    save({ protocol: next, baseUrl: r.baseUrl });
  };

  const changeCacheTtl = (next: AnthropicCacheTtl): void => {
    setCacheTtl(next);
    save({
      protocolOptions: {
        ...(p.protocolOptions ?? {}),
        anthropic: {
          ...(p.protocolOptions?.anthropic ?? {}),
          cacheTtl: next,
        },
      },
    });
  };

  const toggleCredentialReveal = (): void => {
    const request = revealRequest.current + 1;
    revealRequest.current = request;
    if (revealedCredential !== null) {
      setRevealedCredential(null);
      return;
    }
    setRevealingCredential(true);
    setError(null);
    void revealCredential(p.id)
      .then((value) => {
        if (revealRequest.current === request) setRevealedCredential(value);
      })
      .catch((e: unknown) => {
        if (revealRequest.current === request) {
          setError(e instanceof Error ? e.message : String(e));
        }
      })
      .finally(() => {
        if (revealRequest.current === request) setRevealingCredential(false);
      });
  };

  const saveKey = (): void => {
    const v = keyDraft.trim();
    if (v === "") return;
    setBusy(true);
    setError(null);
    void setCredential(p.id, v)
      .then((info) => {
        setCred(info);
        setKeyDraft(""); // ★ 存完就从 React 状态里抹掉,别留在内存里
        revealRequest.current += 1;
        setRevealingCredential(false);
        setRevealedCredential(null);
        setEditingKey(false);
      })
      .catch((e: unknown) =>
        setError(e instanceof Error ? e.message : String(e)),
      )
      .finally(() => setBusy(false));
  };

  const remove = (): void => {
    setBusy(true);
    setError(null);
    void removeProvider(p.id)
      .catch((e: unknown) => {
        setError(e instanceof Error ? e.message : String(e));
        setConfirmDelete(false);
      })
      .finally(() => setBusy(false));
    // 删成功不用收尾:广播回来这条就没了,ModelPage 的 `selected` 自己兜到第一条
  };

  const warnings = baseUrlWarnings(baseUrl, p.protocol);
  /**
   * 槽里装的是哪一种凭证。
   *
   * ★★ **`cred.hasKey` 对两种凭证都为真** —— 它的意思是「槽里有东西」。
   * 直接拿它当「已填密钥」用的话,登录成功之后密钥框会显示一串掩码点,
   * 而那串点背后是一个登录令牌;用户会以为自己那把 key 还在。
   */
  const inUse = credentialInUse(cred);
  const hasKey = inUse === "api-key";

  const reorderAliases = (from: number, to: number): void => {
    if (
      busy ||
      from === to ||
      from < 0 ||
      to < 0 ||
      from >= aliases.length ||
      to >= aliases.length
    ) {
      return;
    }
    const names = aliases.map((model) => model.upstreamModel);
    const [moved] = names.splice(from, 1);
    if (moved === undefined) return;
    names.splice(to, 0, moved);

    setBusy(true);
    setError(null);
    void setProviderAliases(p.id, names)
      .catch((e: unknown) =>
        setError(e instanceof Error ? e.message : String(e)),
      )
      .finally(() => setBusy(false));
  };
  const aliasDrag = useDragReorder(reorderAliases, "y");

  const saveModel = (
    model: ModelAlias,
    draft: Pick<
      ModelAlias,
      "alias" | "displayName" | "contextWindow" | "maxOutputTokens" | "capabilities"
    > & { protocolOverride: UpstreamProtocol | undefined },
  ): void => {
    setBusy(true);
    setError(null);
    void (async () => {
      let target = model;
      if (draft.alias !== model.alias) {
        target = await renameModel(p.id, model.alias, draft.alias);
      }
      await updateModel({
        ...target,
        displayName: draft.displayName,
        contextWindow: draft.contextWindow,
        maxOutputTokens: draft.maxOutputTokens,
        capabilities: { ...target.capabilities, ...draft.capabilities },
        // 显式写成自有属性(哪怕是 undefined),后端据此区分「清除覆盖」和「旧版渲染层没发这个字段」
        protocolOverride: draft.protocolOverride,
      });
      setEditingModel(null);
    })()
      .catch((e: unknown) =>
        setError(e instanceof Error ? e.message : String(e)),
      )
      .finally(() => setBusy(false));
  };

  const saveThinking = (
    model: ModelAlias,
    thinkingConfig: ThinkingConfig,
    reasoningEfforts: readonly ReasoningEffort[] | undefined,
  ): void => {
    setBusy(true);
    setError(null);
    void updateModel({
      ...model,
      capabilities: {
        ...model.capabilities,
        thinking: thinkingConfig.mode !== "unsupported",
      },
      thinkingConfig,
      ...(reasoningEfforts === undefined ? {} : { reasoningEfforts }),
    })
      .then(() => setThinkingModel(null))
      .catch((e: unknown) =>
        setError(e instanceof Error ? e.message : String(e)),
      )
      .finally(() => setBusy(false));
  };

  const deleteModel = (): void => {
    if (deletingModel === null) return;
    setBusy(true);
    setError(null);
    void removeModel(p.id, deletingModel.alias)
      .then(() => setDeletingModel(null))
      .catch((e: unknown) =>
        setError(e instanceof Error ? e.message : String(e)),
      )
      .finally(() => setBusy(false));
  };

  const addModel = (): void => {
    const modelId = modelDraft.trim();
    if (modelId === "" || busy || addingModelRequest.current) return;
    if (aliases.some((model) => model.upstreamModel === modelId)) {
      setError(t("provider.modelAlreadyAdded"));
      return;
    }

    addingModelRequest.current = true;
    setBusy(true);
    setError(null);
    void setProviderAliases(p.id, [
      ...aliases.map((model) => model.upstreamModel),
      modelId,
    ])
      .then(() => {
        setModelDraft("");
        setAddingModel(false);
      })
      .catch((e: unknown) =>
        setError(e instanceof Error ? e.message : String(e)),
      )
      .finally(() => {
        addingModelRequest.current = false;
        setBusy(false);
      });
  };

  const closeTransfer = (): void => {
    setTransferOpen(null);
    setTransferCredentials(false);
    setTransferPassword("");
    setTransferBusy(false);
  };

  const runTransfer = (): void => {
    if (transferBusy || transferOpen === null) return;
    setTransferBusy(true);
    setError(null);
    const action = transferOpen === "export"
      ? exportProviders({ includeCredentials: transferCredentials, ...(transferCredentials ? { password: transferPassword } : {}) })
      : importProviders(transferPassword.trim() === "" ? undefined : transferPassword);
    void action
      .then((result) => {
        if (result !== null) closeTransfer();
      })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setTransferBusy(false));
  };

  return (
    <>
      <div className="flex min-h-0 min-w-0 flex-1 flex-col overflow-hidden rounded-[12px] border border-border bg-canvas">
        <div className="flex items-center gap-2 border-b border-hairline px-4 py-3">
          <ProviderAvatar name={p.name} id={p.id} />
          <span className="min-w-0 flex-1 truncate text-[13px] text-fg">
            {p.name}
          </span>
          {busy && (
            <Spinner size="sm" className="text-fg-faint" />
          )}
          <Button type="button" variant="secondary" disabled={busy} onClick={() => { setError(null); setTransferOpen("export"); }}>
            <Download size={12} />
            {t("provider.export")}
          </Button>
          <Button type="button" variant="secondary" disabled={busy} onClick={() => { setError(null); setTransferOpen("import"); }}>
            <Upload size={12} />
            {t("provider.import")}
          </Button>
        </div>

        {error !== null && (
          <p className="border-b border-hairline bg-danger/10 px-4 py-2 text-[12px] leading-[1.6] text-danger">
            {error}
          </p>
        )}

        {/*
          ★ **禁用是逐个字段给的,不是整块给的。** 内置的 NextCoWork 那条只托管
          「身份」——名称 / 地址 / 密钥归登录流程(主进程 `upsertProvider` 那边也是
          按字段回落的,不是整条拒绝);协议格式和模型列表归用户。整块 `disabled`
          会把那两样一起焊死,而它们正是用户要改的。
        */}
        <fieldset className="scroll-thin min-h-0 flex-1 space-y-4 overflow-y-auto px-4 py-4">
          {managed && <p className="rounded-[8px] bg-accent/10 px-3 py-2 text-[11.5px] leading-[1.6] text-accent">{t("provider.builtinHint")}</p>}
          <Field label={t("provider.name")}>
            <TextInput
              value={name}
              onChange={setName}
              onCommit={commitName}
              ariaLabel={t("provider.name")}
              disabled={busy || managed}
            />
          </Field>

          <Field
            label={t("provider.apiAddress")}
            hint={t("provider.apiAddressHint")}
          >
            <TextInput
              value={baseUrl}
              onChange={setBaseUrl}
              onCommit={commitUrl}
              ariaLabel={t("provider.apiAddress")}
              inputMode="url"
              disabled={busy || managed}
            />
            <p className="mt-1.5 text-[11.5px] leading-[1.6] text-fg-faint">
              {t("provider.actualRequest")} {" "}
              <code className="text-fg-muted">
                {previewUrl(baseUrl, p.protocol)}
              </code>
            </p>
            {swapped !== null && (
              /* ★ 地址被我们改掉了就说一声。静默换掉是这一整块最不该有的行为 */
              <p className="mt-1.5 text-[11.5px] leading-[1.6] text-fg-muted">
                {swapped.reason === "sign-in"
                  ? t("provider.authEndpointSwitched", { url: swapped.url })
                  : t("provider.addressSwapped")}
              </p>
            )}
            {warnings.map((w) => (
              <p
                key={w.kind}
                className="mt-1.5 flex items-start gap-1.5 text-[11.5px] leading-[1.6] text-danger"
              >
                <AlertTriangle size={12} className="mt-[2px] shrink-0" />
                <span className="min-w-0">{w.message}</span>
              </p>
            ))}
          </Field>

          {/*
            ★★ **走账号登录的供应商藏掉这两个控件。**
            不藏的话:用户翻一下「使用 Responses API」→ 协议变成 openai-chat →
            `baseUrlForProtocol` 因为预设里没有那条端点而**保留原地址** →
            请求打到 `…/codex/chat/completions` → 404,而表单看着完全正常。
            这正是 `provider-edit.ts` 文件头点名要防的那类静默失效。
            (预设那边只给一条 endpoint 是第一道防线,这里是第二道。)
          */}
          {authMode === "oauth" ? (
            <p className="text-[11.5px] leading-[1.6] text-fg-faint">
              {t("provider.oauthFixedChannel")}
            </p>
          ) : (
            <>
              <Field label={t("provider.apiFormat")}>
                <fieldset disabled={busy} className="contents">
                  <SegmentedControl
                    label={t("provider.apiFormat")}
                    className="w-full"
                    value={family}
                    options={[
                      { value: "openai", label: t("provider.openaiFormat") },
                      { value: "anthropic", label: t("provider.anthropicFormat") },
                    ]}
                    onValueChange={(f) =>
                      switchProtocol(joinProtocol(f as ProtocolFamily, f === "openai" && responses))
                    }
                  />
                </fieldset>
              </Field>

              {/* ★ 只在 OpenAI 族下出现 —— 三个协议值到「两控件」的投影,见 provider.ts */}
              {family === "openai" && (
                <div className="flex items-start gap-4">
                  <div className="min-w-0 flex-1">
                    <p className="text-[13px] text-fg">{t("provider.responsesApi")}</p>
                    <p className="mt-1 text-[11.5px] leading-[1.6] text-fg-muted">
                      {t("provider.responseApiHint")}
                    </p>
                  </div>
                  <div className="shrink-0 pt-0.5">
                    <Switch
                      checked={responses}
                      disabled={busy}
                      onCheckedChange={(on) => switchProtocol(joinProtocol("openai", on))}
                      aria-label={t("provider.responsesApi")}
                    />
                  </div>
                </div>
              )}
            </>
          )}

          {hasAnthropicModels && (
            <Field
              label={t("provider.cache")}
              hint={t("provider.cacheHint")}
            >
              <fieldset disabled={busy} className="contents">
                <SegmentedControl
                  label={t("provider.cache")}
                  value={cacheTtl}
                  options={[
                    { value: "5m", label: t("provider.fiveMinutes") },
                    { value: "1h", label: t("provider.oneHour") },
                  ]}
                  onValueChange={(ttl) => changeCacheTtl(ttl as AnthropicCacheTtl)}
                />
              </fieldset>
            </Field>
          )}

          <Field
            label={
              authMode === "oauth"
                ? t("provider.account")
                : authMode === "both"
                  ? t("provider.accountOrKey")
                  : t("provider.apiKey")
            }
            action={
              /* ★ 纯走登录的那家没有「创建 API Key」页面，预设里也没给 apiKeyUrl */
              authMode !== "oauth" && apiKeyUrl !== undefined ? (
                <Button
                  type="button"
                  variant="secondary"
                  onClick={() => void openExternal(apiKeyUrl)}
                >
                  <ExternalLink size={12} />
                  {apiKeyActionLabel}
                </Button>
              ) : undefined
            }
          >
            {/*
              ★★ **`both` 下两样同时画,而不是让用户先选一种。**
              先选一种就得给一个「你想怎么登录」的开关,而那个开关本身要有默认值 ——
              默认给错的那一半用户会以为这家不支持他手里那种凭证。同时画出来,
              「哪一种正在用」由下面 `inUse` 那行说清楚。
            */}
            {authMode !== "api-key" && (
              <div className={cn(authMode === "both" && "mb-2")}>
                <ProviderAuthField
                  providerId={p.id}
                  view={authView}
                  busy={busy}
                  onSignIn={doSignIn}
                  onCancel={doCancelSignIn}
                  onSignOut={doSignOut}
                  onSubmitCode={doSubmitCode}
                  clearsSlot={authMode === "both"}
                  revealed={revealedCredential?.kind === "oauth" ? revealedCredential : null}
                  revealing={revealingCredential}
                  onToggleReveal={toggleCredentialReveal}
                />
                {/*
                  ★★ **账号列表和上面那块登录控件是两件事,所以并排而不是二选一。**
                  上面那块负责**这一次登录流程**(设备码、粘回调地址、取消),
                  下面这块负责**已经登录的那些号**(顺序、限流、额度、启停)。
                  合成一个组件试过一次就会明白:登录中的三个阶段各有自己的界面,
                  而它们和「三行账号」的布局没有任何共同之处。

                  ★ 登录中把列表藏起来:此刻列表里那几行的状态正要变,
                  而用户的注意力应该在浏览器那边(见 `oauthView` 的「正在登录时压过一切」)。
                */}
                {authView.state !== "signing-in" && (
                  <div className="mt-2">
                    <ProviderAccounts
                      providerId={p.id}
                      rotation={accountRotation}
                      busy={busy}
                      quotaFetchable={codingPlanFamilyFor(p.id) !== null}
                      onError={setError}
                      onSigningInChange={(active) =>
                        setAuthFlow(active ? { phase: "opening" } : null)
                      }
                    />
                  </div>
                )}
              </div>
            )}
            {authMode === "oauth" ? null : managed ? (
              /*
                托管那条的「密钥」是登录发的 access token,不能手工更换,但产品要求
                允许显式查看。它仍只在点击眼睛后经专用 IPC 进入当前组件内存。
              */
              <SecretValueRow
                value={
                  revealedCredential?.kind === "api-key"
                    ? revealedCredential.apiKey
                    : null
                }
                masked={`••••••••••••${cred?.last4 ?? ""}`}
                revealing={revealingCredential}
                onToggle={toggleCredentialReveal}
              />
            ) : editingKey || !hasKey ? (
              <div className="flex items-center gap-2">
                <div className="min-w-0 flex-1">
                  <TextInput
                    value={keyDraft}
                    onChange={setKeyDraft}
                    onCommit={saveKey}
                    ariaLabel={t("provider.apiKeyLabel", { provider: p.name })}
                    placeholder={t("provider.pasteKey")}
                    disabled={busy}
                  />
                </div>
                <Button
                  type="button"
                  variant="primary"
                  disabled={busy || keyDraft.trim() === ""}
                  onClick={saveKey}
                >
                  {t("common.save")}
                </Button>
                {hasKey && (
                  <Button
                    type="button"
                    variant="secondary"
                    disabled={busy}
                    onClick={() => {
                      setKeyDraft("");
                      setEditingKey(false);
                    }}
                  >
                    {t("common.cancel")}
                  </Button>
                )}
              </div>
            ) : (
              <div className="flex items-center gap-2">
                {/* 掩码定长,不泄露 key 的真实长度；点击查看后才显示完整明文。 */}
                <SecretValueRow
                  value={
                    revealedCredential?.kind === "api-key"
                      ? revealedCredential.apiKey
                      : null
                  }
                  masked={`••••••••••••${cred?.last4 ?? ""}`}
                  revealing={revealingCredential}
                  onToggle={toggleCredentialReveal}
                />
                <Button
                  type="button"
                  variant="secondary"
                  disabled={busy}
                  onClick={() => {
                    revealRequest.current += 1;
                    setRevealingCredential(false);
                    setRevealedCredential(null);
                    setEditingKey(true);
                  }}
                >
                  {t("provider.replace")}
                </Button>
              </div>
            )}
            {/*
              ★ `both` 下先说「现在用的是哪一种」。一个槽只装一样东西,
              而两个控件并排画着,不说的话用户没法知道刚才那次登录
              是不是把他的密钥顶掉了。
            */}
            {authMode === "both" && inUse !== null && (
              <p className="mt-1.5 flex items-center gap-1.5 text-[11.5px] leading-[1.6] text-accent">
                <Check size={11} className="shrink-0" />
                <span className="min-w-0">
                  {inUse === "oauth"
                    ? t("provider.credentialInUseOAuth", {
                        name:
                          issuer === null ? t("provider.account") : oauthIssuerLabel(issuer),
                      })
                    : t("provider.credentialInUseKey")}
                </span>
              </p>
            )}
            <p className="mt-1.5 text-[11.5px] leading-[1.6] text-fg-faint">
              {authMode === "oauth"
                ? t("provider.signInHint")
                : authMode === "both"
                  ? t("provider.bothCredentialHint")
                  : managed
                    ? t("provider.managedKeyHint")
                    : t("provider.keySavedHint")}
            </p>
          </Field>

          <Field
            label={t("provider.modelPriority")}
            hint={t("provider.modelPriorityHint")}
            action={
              <>
                {aliases.length > 0 && (
                  <span className="shrink-0 text-[11px] text-fg-faint">
                    {aliases.length}/{MAX_ALIASES_PER_PROVIDER}
                  </span>
                )}
                <Button
                  type="button"
                  variant="secondary"
                  onClick={() => setImportOpen(true)}
                >
                  <CloudDownload size={13} />
                  {t("provider.fetchModels")}
                </Button>
              </>
            }
          >
            <>
              {addingModel ? (
                <div className="flex items-center gap-2 rounded-[16px] border border-border bg-tint px-2 py-1">
                  <Plus size={14} className="shrink-0 text-fg-faint" aria-hidden />
                  <input
                    autoFocus
                    value={modelDraft}
                    onChange={(event) => setModelDraft(event.target.value)}
                    onKeyDown={(event) => {
                      if (event.key === "Enter") {
                        event.preventDefault();
                        addModel();
                      } else if (event.key === "Escape") {
                        event.preventDefault();
                        setModelDraft("");
                        setAddingModel(false);
                      }
                    }}
                    aria-label={t("provider.modelIdLabel")}
                    placeholder={t("provider.modelIdPlaceholder")}
                    disabled={busy}
                    className="apple-field selectable min-w-0 flex-1 rounded-[8px] border border-border bg-canvas px-2 text-[12px] text-fg outline-none placeholder:text-fg-faint focus:border-fg-faint"
                  />
                  <button
                    type="button"
                    aria-label={t("provider.confirmAddModel")}
                    title={t("provider.confirmAddModel")}
                    disabled={busy || modelDraft.trim() === ""}
                    onClick={addModel}
                    className="flex size-7 shrink-0 items-center justify-center rounded-full bg-accent text-accent-fg transition-opacity hover:opacity-90 disabled:opacity-40"
                  >
                    <Check size={14} aria-hidden />
                  </button>
                  <button
                    type="button"
                    aria-label={t("provider.cancelAddModel")}
                    title={t("provider.cancelAddModel")}
                    disabled={busy}
                    onClick={() => {
                      setModelDraft("");
                      setAddingModel(false);
                    }}
                    className="flex size-7 shrink-0 items-center justify-center rounded-full text-fg-muted transition-colors hover:bg-tint-strong hover:text-fg disabled:opacity-40"
                  >
                    <X size={14} aria-hidden />
                  </button>
                </div>
              ) : (
                <Button
                  type="button"
                  variant="secondary"
                  onClick={() => {
                    setError(null);
                    setModelDraft("");
                    setAddingModel(true);
                  }}
                  className="w-full justify-start rounded-[16px]"
                >
                  <Plus size={14} />
                  {t("provider.addModel")}
                </Button>
              )}
              {aliases.length === 0 ? null : (
              /*
              ★★ **前一版这里写着「别名的写入频道契约里根本没有」—— 那句已经不成立了。**
              `provider:fetchModels` 和 `provider:setAliases` 现在都在契约里,
              整表的增删就走右上角那颗按钮(弹窗是替换语义:取消勾选 = 删掉)。

              ★ 模型优先级通过拖动整行写回 `setAliases`;逐行操作则走模型专用频道:
              推理能力、模型编辑和单条删除各自独立提交，不会把同一供应商下的其他模型
              一并覆盖。
            */
              <ul className="overflow-hidden rounded-[8px] border border-border">
                {aliases.map((m, i) => (
                  <li
                    key={m.alias}
                    data-drag-item
                    style={aliasDrag.styleFor(i)}
                    className="flex items-center gap-2 border-b border-hairline bg-canvas px-2.5 py-2 transition-[transform,background-color,box-shadow] duration-200 ease-out last:border-b-0 motion-reduce:transition-none"
                  >
                    <button
                      type="button"
                      aria-label={t("provider.dragToReorder")}
                      aria-keyshortcuts="ArrowUp ArrowDown"
                      title={t("provider.dragToReorder")}
                      disabled={busy}
                      onPointerDown={(event) => aliasDrag.onPointerDown(event, i)}
                      onKeyDown={(event) => {
                        if (event.key === "ArrowUp" && i > 0) {
                          event.preventDefault();
                          reorderAliases(i, i - 1);
                        }
                        if (event.key === "ArrowDown" && i < aliases.length - 1) {
                          event.preventDefault();
                          reorderAliases(i, i + 1);
                        }
                      }}
                      className="app-no-drag -ml-1 flex size-6 shrink-0 cursor-grab items-center justify-center rounded-[6px] text-fg-faint transition-colors hover:bg-tint hover:text-fg active:cursor-grabbing disabled:cursor-default disabled:opacity-30"
                    >
                      <GripVertical size={14} aria-hidden />
                    </button>
                    {i === 0 && (
                      <span className="shrink-0 rounded-[5px] bg-tint px-1.5 py-0.5 text-[10.5px] text-fg-muted">
                        {t("provider.primaryModel")}
                      </span>
                    )}
                    <span className="min-w-0 flex-1 truncate font-mono text-[12px] text-fg">
                      {m.alias}
                    </span>
                    <RowAction
                      label={t("provider.configureReasoning")}
                      disabled={busy}
                      onClick={() => setThinkingModel(m)}
                    >
                      <Brain size={13} />
                    </RowAction>
                    <RowAction
                      label={t("provider.editModel")}
                      disabled={busy}
                      onClick={() => setEditingModel(m)}
                    >
                      <Pencil size={13} />
                    </RowAction>
                    <RowAction
                      label={
                        deletingModel?.alias === m.alias
                          ? t("common.confirmDelete")
                          : t("provider.deleteModel")
                      }
                      disabled={busy}
                      danger
                      onClick={() => {
                        if (deletingModel?.alias === m.alias) deleteModel();
                        else setDeletingModel(m);
                      }}
                    >
                      <Trash2 size={13} />
                    </RowAction>
                  </li>
                ))}
              </ul>
              )}
            </>
            <p className="mt-1.5 text-[11.5px] leading-[1.6] text-fg-faint">
              {listAvail.hint ?? t("provider.rowActionsHint")}
            </p>
          </Field>
        </fieldset>

        <div className="border-t border-hairline px-4 py-3">
          <div className="flex items-center gap-2">
            {/*
            ★ **两句话不是同一句的两种说法,是两种不同的后果,所以必须分开写。**
            内置那条是种子数据:删了下次启动它自己回来 —— 不说的话用户重启看见它又在,
            第一反应是「删除没生效」,而实际上删是生效了的(密钥就没回来)。
            自己加的那条删了就真没了,得回目录重新添 —— 把「会回来」这句显示在它下面,
            等于骗用户放心删。
          */}
            <p className="min-w-0 flex-1 text-[11.5px] leading-[1.6] text-fg-faint">
              {confirmDelete
                ? t("provider.deleteHint")
                : managed
                  ? t("provider.builtinDeleteHint")
                  : t("provider.customDeleteHint")}
            </p>
            {confirmDelete && (
              <Button
                type="button"
                variant="secondary"
                disabled={busy}
                onClick={() => setConfirmDelete(false)}
              >
                {t("common.cancel")}
              </Button>
            )}
            <Button
              type="button"
              variant={confirmDelete ? "danger" : "secondary"}
              disabled={busy || managed}
              onClick={() => {
                if (confirmDelete) remove();
                else setConfirmDelete(true);
              }}
            >
              <Trash2 size={12} />
              {confirmDelete ? t("common.confirmDelete") : t("provider.delete")}
            </Button>
          </div>
        </div>
      </div>

      <ImportModelsDialog
        open={importOpen}
        providerId={p.id}
        providerName={p.name}
        aliases={aliases}
        modality={modality === "image" ? "image" : "text"}
        preserveAliases={preserveAliases}
        onClose={() => setImportOpen(false)}
      />
      <Dialog
        open={transferOpen !== null}
        onClose={closeTransfer}
        title={transferOpen === "export" ? t("provider.exportTitle") : t("provider.importTitle")}
        description={t("provider.transferHint")}
        footer={
          <>
            <Button type="button" variant="secondary" disabled={transferBusy} onClick={closeTransfer}>{t("common.cancel")}</Button>
            <Button type="button" variant="primary" disabled={transferBusy || (transferOpen === "export" && transferCredentials && transferPassword.length < 8)} onClick={runTransfer}>
              {transferOpen === "export" ? t("provider.export") : t("provider.import")}
            </Button>
          </>
        }
      >
        {error !== null && <p className="mb-3 rounded-[8px] bg-danger/10 px-3 py-2 text-[12px] leading-[1.6] text-danger">{error}</p>}
        {transferOpen === "export" ? (
          <div className="space-y-3">
            <Switch checked={transferCredentials} onCheckedChange={setTransferCredentials} aria-label={t("provider.includeCredentials")} />
            <p className="text-[11.5px] leading-[1.6] text-fg-faint">{t("provider.credentialsHint")}</p>
            {transferCredentials && (
              <TextInput value={transferPassword} onChange={setTransferPassword} ariaLabel={t("provider.exportPassword")} placeholder={t("provider.exportPasswordPlaceholder")} type="password" />
            )}
          </div>
        ) : (
          <div className="space-y-3">
            <p className="text-[12px] leading-[1.6] text-fg-muted">{t("provider.importHint")}</p>
            <TextInput value={transferPassword} onChange={setTransferPassword} ariaLabel={t("provider.importPassword")} placeholder={t("provider.importPasswordPlaceholder")} type="password" />
          </div>
        )}
      </Dialog>
      {thinkingModel !== null && (
        <ThinkingDialog
          key={thinkingModel.alias}
          model={thinkingModel}
          busy={busy}
          onClose={() => setThinkingModel(null)}
          onSave={(thinkingConfig, reasoningEfforts) =>
            saveThinking(thinkingModel, thinkingConfig, reasoningEfforts)
          }
        />
      )}
      {editingModel !== null && (
        <ModelEditDialog
          key={editingModel.alias}
          model={editingModel}
          provider={p}
          busy={busy}
          onClose={() => setEditingModel(null)}
          onSave={(draft) => saveModel(editingModel, draft)}
        />
      )}
    </>
  );
}

function Field({
  label,
  hint,
  action,
  children,
}: {
  label: string;
  hint?: string;
  /** 标签那一行右端的按钮(「从服务商拉取模型列表」) */
  action?: ReactNode;
  children: ReactNode;
}): ReactNode {
  return (
    <div>
      {/* ★ 只有带按钮的那一行才撑高 —— 无条件加 min-h 会把另外四个字段的行高
          一起改掉,而那套间距是照着设置浮层量准的 */}
      <div
        className={cn(
          "mb-1.5 flex items-center gap-2",
          action !== undefined && "min-h-[26px]",
        )}
      >
        <p className="min-w-0 flex-1 text-[12.5px] text-fg-muted">{label}</p>
        {action}
      </div>
      {children}
      {hint !== undefined && (
        <p className="mt-1.5 text-[11.5px] leading-[1.6] text-fg-faint">
          {hint}
        </p>
      )}
    </div>
  );
}

/**
 * 账号登录那一格 —— 四态各一种样子。
 *
 * ★ 不新开文件:它只是那块 JSX 的一个名字,四态的**判断**在
 * `provider-auth.ts` 的 `oauthView` 里(那边有测试)。这里只负责画。
 *
 * ★ 「退出登录」是**就地两步**,和删除供应商同一套:退出之后要重走一遍
 * 浏览器授权才能回来,代价不对称,值得挡一次误触。
 */
function ProviderAuthField({
  providerId,
  view,
  busy,
  onSignIn,
  onCancel,
  onSignOut,
  onSubmitCode,
  clearsSlot,
  revealed,
  revealing,
  onToggleReveal,
}: {
  providerId: string;
  view: OAuthView;
  busy: boolean;
  onSignIn: () => void;
  onCancel: () => void;
  onSignOut: () => void;
  onSubmitCode: (pasted: string) => void;
  /** 这一栏还兼着密钥 —— 退出登录会把密钥一起删掉，确认文案得说清楚 */
  clearsSlot: boolean;
  /** 用户显式展开后才有;不随 CredentialInfo 或广播下发。 */
  revealed: Extract<RevealedCredential, { kind: "oauth" }> | null;
  revealing: boolean;
  onToggleReveal: () => void;
}): ReactNode {
  const { t } = useI18n();
  const [confirmOut, setConfirmOut] = useState(false);
  const [pasteDraft, setPasteDraft] = useState("");
  const issuer = providerOAuthIssuer(providerId);
  /*
    ★★ 名字来自一张**穷尽的** Record(`provider-auth.ts` 的 `oauthIssuerLabel`),
    不是 `issuer === "chatgpt" ? "ChatGPT" : ""`。后者在加 issuer 时一个编译错
    都不报,表现是按钮上写着「使用  账号登录」—— 中间空一个词,
    而界面上没有任何一处指向 issuer 漏登记了。
  */
  const signInLabel = t("provider.signInWith", {
    name: issuer === null ? "" : oauthIssuerLabel(issuer),
  });

  if (view.state === "signing-in") {
    /*
      ★★ **设备码流程(Kimi):真正要用户做的事只存在于这个配对码里。**
      没有这个分支时它在界面上和别的登录一样是个转圈的 spinner —— 而浏览器那边
      正停在一个「请输入配对码」的页面上,用户手里没有码。
      配对码摆在最显眼的位置,旁边给一颗复制按钮:那串码是拿来**手输进浏览器**的,
      而人手抄一串带连字符的字符是这条流程最容易出错的一步。
    */
    if (view.phase === "waiting" && view.device !== null) {
      const device = view.device;
      return (
        <div className="space-y-1.5">
          <div className="flex items-center gap-2">
            <div className="flex h-8 min-w-0 flex-1 items-center gap-2 rounded-[8px] border border-border bg-surface-field px-2.5">
              <Spinner size="xs" className="text-fg-muted" />
              <span className="min-w-0 shrink-0 text-[13px] text-fg-muted">
                {t("provider.authDeviceCodeLabel")}
              </span>
              {/* ★ 等宽 + 加宽字距:这串码是给人照着念、照着敲的 */}
              <span className="min-w-0 flex-1 truncate font-mono text-[14px] font-semibold tracking-[0.12em] text-fg">
                {device.userCode}
              </span>
            </div>
            <Button
              type="button"
              variant="secondary"
              onClick={() => {
                /*
                  ★ 复制失败**不报错**:剪贴板权限被拒时码仍然明晃晃地显示在
                  左边,用户照着敲就行 —— 为此弹一条红色错误只会让他以为登录挂了。
                */
                void navigator.clipboard?.writeText(device.userCode).catch(() => undefined);
              }}
            >
              <Copy size={12} />
              {t("common.copy")}
            </Button>
            <Button type="button" variant="secondary" onClick={onCancel}>
              {t("common.cancel")}
            </Button>
          </div>
          <p className="text-[11.5px] leading-[1.6] text-fg-faint">
            {t("provider.authDeviceHint", { url: device.verificationUri })}
          </p>
        </div>
      );
    }

    /*
      ★★ **这条流程要用户自己把回调地址粘回来。**
      没有这个分支时,粘贴形态的登录在界面上是一个转到超时为止的 spinner ——
      而用户手里正拿着那条回调地址无处可放。
    */
    if (view.phase === "waiting" && view.needsPaste) {
      const submit = (): void => {
        const v = pasteDraft.trim();
        if (v === "") return;
        setPasteDraft(""); // ★ 提交完就抹掉:里面带着授权码
        onSubmitCode(v);
      };
      return (
        <div className="space-y-1.5">
          <div className="flex items-center gap-2">
            <div className="min-w-0 flex-1">
              <TextInput
                value={pasteDraft}
                onChange={setPasteDraft}
                onCommit={submit}
                ariaLabel={t("provider.authPasteLabel")}
                placeholder={t("provider.authPastePlaceholder")}
              />
            </div>
            <Button
              type="button"
              variant="primary"
              disabled={pasteDraft.trim() === ""}
              onClick={submit}
            >
              {t("provider.authPasteSubmit")}
            </Button>
            <Button type="button" variant="secondary" onClick={onCancel}>
              {t("common.cancel")}
            </Button>
          </div>
          <p className="text-[11.5px] leading-[1.6] text-fg-faint">
            {t("provider.authPasteHint")}
          </p>
        </div>
      );
    }

    const phaseLabel =
      view.phase === "opening"
        ? t("provider.authOpeningBrowser")
        : view.phase === "waiting"
          ? t("provider.authWaiting")
          : t("provider.authExchanging");
    return (
      <div className="flex items-center gap-2">
        <div className="flex h-8 min-w-0 flex-1 items-center gap-2 rounded-[8px] border border-border bg-surface-field px-2.5">
          <Spinner size="xs" className="text-fg-muted" />
          <span className="min-w-0 flex-1 truncate text-[13px] text-fg-muted">
            {phaseLabel}
          </span>
        </div>
        <Button type="button" variant="secondary" onClick={onCancel}>
          {t("common.cancel")}
        </Button>
      </div>
    );
  }

  if (view.state === "signed-out") {
    return (
      <Button type="button" variant="primary" onClick={onSignIn}>
        <ExternalLink size={12} />
        {signInLabel}
      </Button>
    );
  }

  const expired = view.state === "expired";
  return (
    <>
      <div className="flex items-center gap-2">
        <div
          className={cn(
            "flex h-8 min-w-0 flex-1 items-center gap-2 rounded-[8px] border px-2.5",
            "bg-surface-field",
            expired ? "border-danger/50" : "border-border",
          )}
        >
          <span className="min-w-0 flex-1 truncate text-[13px] text-fg-muted">
            {view.email ?? t("provider.authAccountFallback")}
          </span>
          {view.state === "signed-in" && view.plan !== null && (
            <span className="shrink-0 text-[11px] text-fg-faint">{view.plan}</span>
          )}
          {expired ? (
            <span className="flex shrink-0 items-center gap-1 text-[11px] text-danger">
              <AlertTriangle size={11} />
              {t("provider.signInExpired")}
            </span>
          ) : (
            <span className="flex shrink-0 items-center gap-1 text-[11px] text-accent">
              <Check size={11} />
              {t("provider.signedIn")}
            </span>
          )}
        </div>
        {expired && (
          <Button type="button" variant="primary" disabled={busy} onClick={onSignIn}>
            {t("provider.reSignIn")}
          </Button>
        )}
        <Button
          type="button"
          variant="secondary"
          disabled={busy || revealing}
          onClick={onToggleReveal}
        >
          {revealed === null ? <Eye size={12} /> : <EyeOff size={12} />}
          {revealed === null ? t("provider.viewToken") : t("provider.hideToken")}
        </Button>
        <Button
          type="button"
          variant="secondary"
          disabled={busy}
          onClick={() => {
            if (confirmOut) onSignOut();
            else setConfirmOut(true);
          }}
        >
          {confirmOut ? t("provider.confirmSignOut") : t("provider.signOut")}
        </Button>
      </div>
      {revealed !== null && (
        <div className="mt-2 space-y-1.5">
          <SecretTokenRow label={t("provider.accessTokenLabel")} value={revealed.accessToken} />
          <SecretTokenRow label={t("provider.refreshTokenLabel")} value={revealed.refreshToken} />
        </div>
      )}
      {/*
        ★★ 一个凭证槽装两种凭证,所以「退出登录」在这些家会把**密钥一起删掉**
        (走的是 `removeCredential`,整条 ref 删干净)。等他点完确认才发现
        key 也没了 —— 那一步是不可逆的,提示必须在确认之前。
      */}
      {clearsSlot && confirmOut && (
        <p className="mt-1.5 flex items-start gap-1.5 text-[11.5px] leading-[1.6] text-danger">
          <AlertTriangle size={12} className="mt-[2px] shrink-0" />
          <span className="min-w-0">{t("provider.signOutClearsSlot")}</span>
        </p>
      )}
    </>
  );
}

function SecretValueRow({
  value,
  masked,
  revealing,
  onToggle,
}: {
  value: string | null;
  masked: string;
  revealing: boolean;
  onToggle: () => void;
}): ReactNode {
  const { t } = useI18n();
  return (
    <div
      className={cn(
        "flex min-h-8 min-w-0 flex-1 items-center gap-2 rounded-[8px] border border-border",
        "bg-surface-field px-2.5 py-1",
      )}
    >
      <span
        className={cn(
          "selectable min-w-0 flex-1 break-all text-[13px] text-fg",
          value === null && "truncate tracking-[0.18em] text-fg-muted",
        )}
      >
        {value ?? masked}
      </span>
      {value === null && (
        <span className="flex shrink-0 items-center gap-1 text-[11px] text-accent">
          <Check size={11} />
          {t("provider.configured")}
        </span>
      )}
      {value !== null && (
        <RowAction
          label={t("provider.copyKey")}
          disabled={false}
          onClick={() => void navigator.clipboard?.writeText(value).catch(() => undefined)}
        >
          <Copy size={12} />
        </RowAction>
      )}
      <RowAction
        label={value === null ? t("provider.viewKey") : t("provider.hideKey")}
        disabled={revealing}
        onClick={onToggle}
      >
        {revealing ? <Spinner size="xs" /> : value === null ? <Eye size={12} /> : <EyeOff size={12} />}
      </RowAction>
    </div>
  );
}

function SecretTokenRow({ label, value }: { label: string; value: string }): ReactNode {
  const { t } = useI18n();
  return (
    <div className="flex min-w-0 items-start gap-2 rounded-[8px] border border-border bg-surface-field px-2.5 py-1.5">
      <span className="shrink-0 text-[11px] text-fg-faint">{label}</span>
      <span className="selectable min-w-0 flex-1 break-all font-mono text-[11px] leading-[1.5] text-fg">
        {value}
      </span>
      <RowAction
        label={t("common.copy")}
        disabled={false}
        onClick={() => void navigator.clipboard?.writeText(value).catch(() => undefined)}
      >
        <Copy size={12} />
      </RowAction>
    </div>
  );
}

function RowAction({
  label,
  children,
  onClick,
  disabled,
  danger = false,
}: {
  label: string;
  children: ReactNode;
  onClick: () => void;
  disabled: boolean;
  danger?: boolean;
}): ReactNode {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      disabled={disabled}
      onClick={onClick}
      className={cn(
        "app-no-drag flex size-6 shrink-0 items-center justify-center rounded-[6px] transition-colors disabled:opacity-35",
        danger
          ? "text-icon hover:bg-danger/10 hover:text-danger"
          : "text-icon hover:bg-tint hover:text-fg",
      )}
    >
      {children}
    </button>
  );
}

const modelDialogInputClass =
  "apple-field h-8 w-full text-[12px]";

function ModelEditDialog({
  model,
  provider,
  busy,
  onClose,
  onSave,
}: {
  model: ModelAlias;
  provider: UpstreamProvider;
  busy: boolean;
  onClose: () => void;
  onSave: (
    draft: Pick<
      ModelAlias,
      "alias" | "displayName" | "contextWindow" | "maxOutputTokens" | "capabilities"
    > & { protocolOverride: UpstreamProtocol | undefined },
  ) => void;
}): ReactNode {
  const { t } = useI18n();
  const [alias, setAlias] = useState(model.alias);
  const [displayName, setDisplayName] = useState(model.displayName ?? "");
  const [contextWindow, setContextWindow] = useState(String(model.contextWindow));
  const [maxOutputTokens, setMaxOutputTokens] = useState(
    String(model.maxOutputTokens),
  );
  const [protocol, setProtocol] = useState<UpstreamProtocol | "">(
    model.protocolOverride ?? "",
  );
  const [capabilities, setCapabilities] = useState({
    tools: model.capabilities.tools,
    vision: model.capabilities.vision,
    caching: model.capabilities.caching,
  });
  const [invalid, setInvalid] = useState(false);

  const effectiveProtocol = effectiveModelProtocol(provider, {
    protocolOverride: protocol === "" ? undefined : protocol,
  });

  const save = (): void => {
    const nextContextWindow = Number(contextWindow);
    const nextMaxOutputTokens = Number(maxOutputTokens);
    if (
      alias.trim() === "" ||
      !Number.isInteger(nextContextWindow) ||
      nextContextWindow <= 0 ||
      !Number.isInteger(nextMaxOutputTokens) ||
      nextMaxOutputTokens <= 0 ||
      nextMaxOutputTokens > nextContextWindow
    ) {
      setInvalid(true);
      return;
    }
    onSave({
      alias: alias.trim(),
      displayName: displayName.trim() || undefined,
      contextWindow: nextContextWindow,
      maxOutputTokens: nextMaxOutputTokens,
      capabilities: { ...model.capabilities, ...capabilities },
      protocolOverride: protocol === "" ? undefined : protocol,
    });
  };

  return (
    <Dialog
      title={t("provider.editModel")}
      description={t("provider.editModelDescription")}
      open
      onClose={onClose}
      width={460}
      footer={
        <>
          <Button type="button" variant="secondary" disabled={busy} onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button type="button" variant="primary" disabled={busy} onClick={save}>
            {t("common.save")}
          </Button>
        </>
      }
    >
      <div className="space-y-3">
        {invalid && (
          <p className="rounded-[8px] border border-danger/30 bg-danger/5 px-2.5 py-2 text-[11.5px] text-danger">
            {t("models.modelInvalid")}
          </p>
        )}
        <div className="grid grid-cols-2 gap-2.5">
          <DialogField label={t("provider.modelAlias")}>
            <input
              value={alias}
              onChange={(event) => setAlias(event.target.value)}
              className={modelDialogInputClass}
            />
          </DialogField>
          <DialogField label={t("provider.modelDisplayName")}>
            <input
              value={displayName}
              onChange={(event) => setDisplayName(event.target.value)}
              className={modelDialogInputClass}
            />
          </DialogField>
          <DialogField label={t("models.fieldContextWindow")}>
            <input
              type="number"
              min={1}
              value={contextWindow}
              onChange={(event) => setContextWindow(event.target.value)}
              className={modelDialogInputClass}
            />
          </DialogField>
          <DialogField label={t("models.fieldMaxOutputTokens")}>
            <input
              type="number"
              min={1}
              value={maxOutputTokens}
              onChange={(event) => setMaxOutputTokens(event.target.value)}
              className={modelDialogInputClass}
            />
          </DialogField>
        </div>
        <DialogField label={t("models.protocol")}>
          <Select
            ariaLabel={t("models.protocol")}
            inModal
            value={protocol}
            onValueChange={(next) => setProtocol(next as UpstreamProtocol | "")}
            options={[
              { value: "", label: t("models.protocolFollowProvider") },
              ...PROTOCOLS.map((item) => ({
                value: item,
                label: protocolLabel(item, t),
              })),
            ]}
          />
          <p className="mt-1 text-[10.5px] text-fg-faint">
            {t("models.protocolEffective", {
              protocol: protocolLabel(effectiveProtocol, t),
              provider: provider.name,
            })}
          </p>
        </DialogField>
        <div className="rounded-[8px] border border-border px-3 py-2.5">
          <p className="text-[11.5px] text-fg-muted">
            {t("models.capabilities")}
          </p>
          <div className="mt-2 space-y-2">
            <DialogToggle
              label={t("models.capabilityTools")}
              checked={capabilities.tools}
              onChange={(tools) =>
                setCapabilities((current) => ({ ...current, tools }))
              }
            />
            <DialogToggle
              label={t("models.capabilityVision")}
              checked={capabilities.vision}
              onChange={(vision) =>
                setCapabilities((current) => ({ ...current, vision }))
              }
            />
            <DialogToggle
              label={t("models.capabilityCaching")}
              checked={capabilities.caching}
              onChange={(caching) =>
                setCapabilities((current) => ({ ...current, caching }))
              }
            />
          </div>
        </div>
      </div>
    </Dialog>
  );
}

function ThinkingDialog({
  model,
  busy,
  onClose,
  onSave,
}: {
  model: ModelAlias;
  busy: boolean;
  onClose: () => void;
  onSave: (
    thinkingConfig: ThinkingConfig,
    reasoningEfforts: readonly ReasoningEffort[] | undefined,
  ) => void;
}): ReactNode {
  const { t } = useI18n();
  const original = model.thinkingConfig;
  const [mode, setMode] = useState<ThinkingMode>(
    original?.mode ?? (model.capabilities.thinking ? "toggle" : "unsupported"),
  );
  const [defaultEnabled, setDefaultEnabled] = useState(
    original?.defaultEnabled ?? false,
  );
  const [parameterPath, setParameterPath] = useState(
    original?.parameterPath ?? "thinking",
  );
  const [defaultEffort, setDefaultEffort] = useState<ReasoningEffort>(
    original?.defaultEffort ?? "medium",
  );
  const [efforts, setEfforts] = useState<readonly ReasoningEffort[]>(model.reasoningEfforts ?? REASONING_EFFORTS);
  const [budget, setBudget] = useState(
    original?.defaultBudgetTokens === undefined
      ? ""
      : String(original.defaultBudgetTokens),
  );
  const [invalid, setInvalid] = useState(false);

  const save = (): void => {
    const nextBudget = budget === "" ? undefined : Number(budget);
    if (
      (nextBudget !== undefined && (!Number.isInteger(nextBudget) || nextBudget < 0)) ||
      (mode === 'effort' && (!efforts.includes(defaultEffort) ||
        (original?.anthropicAdaptive === true && parameterPath.trim() === 'output_config.effort' &&
          !defaultEnabled && !efforts.includes('none'))))
    ) {
      setInvalid(true);
      return;
    }
    onSave({
      ...(original ?? {}),
      mode,
      defaultEnabled:
        mode === "unsupported" ? false : mode === "always" || defaultEnabled,
      ...(mode === "effort" ? { defaultEffort } : { defaultEffort: undefined }),
      ...(mode === "effort" && parameterPath.trim() === "output_config.effort"
        ? {}
        : { anthropicAdaptive: undefined }),
      ...(mode === "toggle" || mode === "budget" || mode === "effort"
        ? { parameterPath: parameterPath.trim() || undefined }
        : { parameterPath: undefined }),
      ...(mode === "toggle" || mode === "budget"
        ? { defaultBudgetTokens: nextBudget }
        : { defaultBudgetTokens: undefined }),
    }, mode === 'effort' ? efforts : model.reasoningEfforts);
  };

  return (
    <Dialog
      title={t("provider.configureReasoning")}
      description={t("provider.configureReasoningDescription")}
      open
      onClose={onClose}
      width={440}
      footer={
        <>
          <Button type="button" variant="secondary" disabled={busy} onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button type="button" variant="primary" disabled={busy} onClick={save}>
            {t("common.save")}
          </Button>
        </>
      }
    >
      <div className="space-y-3">
        {invalid && (
          <p className="rounded-[8px] border border-danger/30 bg-danger/5 px-2.5 py-2 text-[11.5px] text-danger">
            {t("models.modelInvalid")}
          </p>
        )}
        <DialogField label={t("models.reasoning")}>
          <Select
            inModal
            value={mode}
            ariaLabel={t("models.reasoning")}
            className="h-8 text-[13px]"
            options={[
              { value: "unsupported", label: t("models.unsupported") },
              { value: "always", label: t("models.always") },
              { value: "toggle", label: t("models.toggle") },
              { value: "effort", label: t("models.effort") },
              { value: "budget", label: t("models.budget") },
            ]}
            onValueChange={(value) => {
              const nextMode = value as ThinkingMode;
              setMode(nextMode);
              if (nextMode !== mode) setParameterPath(nextMode === 'effort' ? 'reasoning_effort'
                : nextMode === 'budget' ? 'thinking.budget_tokens' : 'thinking.type');
            }}
          />
        </DialogField>
        {(mode === "toggle" || mode === "budget" || mode === "effort") && (
          <>
            <DialogField label={t("models.fieldParameterPath")}>
              <input
                value={parameterPath}
                onChange={(event) => setParameterPath(event.target.value)}
                className={modelDialogInputClass}
              />
            </DialogField>
            <DialogToggle
              label={t("models.fieldDefaultEnabled")}
              checked={defaultEnabled}
              onChange={setDefaultEnabled}
            />
          </>
        )}
        {mode === "effort" && (
          <>
          <DialogField label={t("models.fieldDefaultEffort")}>
            <Select
              inModal
              value={defaultEffort}
              ariaLabel={t("models.fieldDefaultEffort")}
              className="h-8 text-[13px]"
              options={efforts.map((effort) => ({ value: effort, label: effort }))}
              onValueChange={(value) => setDefaultEffort(value as ReasoningEffort)}
            />
          </DialogField>
          <fieldset className="space-y-2">
            <legend className="text-[11.5px] text-fg-muted">{t('models.supportedReasoningEfforts')}</legend>
            <div className="flex flex-wrap gap-3">
              {REASONING_EFFORTS.map((effort) => (
                <label key={effort} className="flex items-center gap-1.5 text-[11.5px] text-fg-muted">
                  <input type="checkbox" checked={efforts.includes(effort)} onChange={(event) => {
                    const next = event.target.checked ? [...efforts, effort] : efforts.filter((item) => item !== effort);
                    setEfforts(next);
                    if (!next.includes(defaultEffort) && next[0] !== undefined) setDefaultEffort(next[0]);
                  }} />
                  {effort}
                </label>
              ))}
            </div>
          </fieldset>
          </>
        )}
        {(mode === "toggle" || mode === "budget") && (
          <DialogField label={t("models.fieldDefaultBudgetTokens")}>
            <input
              type="number"
              min={0}
              value={budget}
              onChange={(event) => setBudget(event.target.value)}
              className={modelDialogInputClass}
            />
          </DialogField>
        )}
        <p className="text-[11.5px] leading-[1.6] text-fg-faint">
          {t("models.reasoningHint")}
        </p>
      </div>
    </Dialog>
  );
}

function DialogField({
  label,
  children,
}: {
  label: string;
  children: ReactNode;
}): ReactNode {
  return (
    <label className="block">
      <span className="mb-1 block text-[11.5px] text-fg-muted">{label}</span>
      {children}
    </label>
  );
}

function DialogToggle({
  label,
  checked,
  onChange,
}: {
  label: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
}): ReactNode {
  return (
    <div className="flex items-center justify-between gap-3 rounded-[8px] border border-border bg-surface-field px-2.5 py-1.5">
      <span className="text-[11.5px] text-fg-muted">{label}</span>
      <Switch checked={checked} onCheckedChange={onChange} aria-label={label} />
    </div>
  );
}
