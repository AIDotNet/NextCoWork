import {
  ArrowLeft,
  Check,
  ExternalLink,
  PackageOpen,
  Plus,
  Search,
  Shuffle,
  SlidersHorizontal,
  X } from "lucide-react";
import { useEffect, useMemo, useState, type ReactNode } from "react";
import {
  PROVIDER_PRESETS,
  type ProviderPreset,
} from "../../../../../shared/domain/presets";
import { baseUrlWarnings, previewUrl } from "../../../../../shared/domain/baseurl";
import {
  joinProtocol,
  PROTOCOL_LABEL,
  type ProtocolFamily,
} from "../../../../../shared/domain/provider";
import type { ImportSourceKind } from "../../../../../shared/domain/import";
import type { ImportableProvider } from "../../../../../shared/domain/provider-import";
import { Button } from "../../../components/arc/button/button";
import { EmptyState } from "../../../components/arc/empty-state/empty-state";
import SegmentedControl from "../../../components/arc/segmented-control/segmented-control";
import { TextInput } from "../../../components/ui/TextInput";
import { Switch } from "../../../components/arc/switch/switch";
import { cn } from "../../../lib/cn";
import { openExternal } from "../../../services/app";
import { upsertProvider, setProviderAliases, listImportableProviders } from "../../../services/provider";
import { useModelsStore } from "../../../stores/models";
import { ProviderAvatar } from "./ProviderAvatar";
import { isPresetAdded, providerFromPreset, seedModelsForPreset } from "./provider-edit";
import {
  customProviderDraft,
  validateCustomProvider,
  type CustomProviderIssue,
} from "./custom-provider";
import { useI18n, type TranslationKey } from "../../../i18n";
import { Spinner } from '../../../components/ui/Spinner'
import {
  CATALOG_TABS,
  divergentCount,
  endpointRows,
  hasDivergentBaseUrls,
  LIST_ACCESS_LABEL,
  matchPresets,
  presetsForTab,
  tabCount,
  VERIFICATION_LABEL,
  type CatalogTab,
  type EndpointRow,
} from "./provider-catalog";

/**
 * 「添加供应商」的预设目录 —— 参考图那个五分类卡片网格。
 *
 * ★★ **这曾经是一个 `Dialog`,现在是右侧那一栏的一个视图。** 换掉的理由不是
 * 好看:这个目录是**一栏能滚很久的内容**(42 家 × 每家两三行地址),塞进一个
 * 定宽浮层里,它自己带一层滚动、底下还压着设置页本身的滚动 —— 而且弹窗一开,
 * 左边那列「已启用的模型」就被蒙层盖住了,用户没法一边看着自己已经有哪几家、
 * 一边挑下一家(「我是不是已经加过 OpenRouter 了」正是这时候要问的)。
 * 现在它长在 `ProviderPanel` 的位置上,左列始终可点,点任意一条就回到那一家。
 *
 * ★ 因此**没有蒙层、没有焦点陷阱、Escape 不关它**。这几样是 `Dialog` 的东西,
 * 而这里不是模态:关掉它的方式是右上角那个叉,或者在左列点任意一家 ——
 * 后者由 `ModelPage` 负责(选中即关),不在这个组件里。
 *
 * ★ 但**只有卡片右下角那颗按钮是按钮,卡片本身仍然不是** —— 卡片上还有
 * 「接入文档」这个会打开浏览器的动作,整卡可点的话,想看文档的人会先建出
 * 一个供应商。
 *
 * ★ 已经建过的显示「已添加」并置灰,不是重复建一条:`upsertProvider` 按 id 覆盖,
 * 再点一次会把用户改过的地址和名字冲回预设的初值 —— 而 key 还留着,于是变成
 * 「密钥没动、地址被换走」,这种配置错到报错为止都看不出来。
 *
 * 卡片里每个字都是实测来的:
 * `presets.ts` 那些条目的判据是 `curl` 探针 + **同前缀假路径对照**
 * (DeepSeek / 火山 / 星火 / 智谱这几家对任意路径都返回 401,不做对照全是假阳性)。
 * 用户手上有一个域名想知道是哪家、或者配到一半 401 想查是不是走错了鉴权域,
 * 这本册子直接答得上来 —— 这些正是调研报告里最贵的那部分,不该停在 md 文件里。
 *
 * ★ 每张卡片显示的是 `previewUrl()` 算出的**最终请求地址**而不是 baseUrl:
 * OpenAI 族的版本段在 base 里、Anthropic 族不带,只看 base 会以为数据录错了。
 */
/** 右侧那一栏的三个视图:预设目录 / 自定义表单 / 从其他应用导入。 */
type CatalogView = "catalog" | "custom" | "import";

export function ProviderCatalog({
  onClose,
  onAdded,
}: {
  onClose: () => void;
  /** 建好之后把左列选到它。不给的话用户建完还得自己去找刚加的那一条 */
  onAdded?: (providerId: string) => void;
}): ReactNode {
  const { t } = useI18n();
  const [tab, setTab] = useState<CatalogTab>("recommended");
  const [query, setQuery] = useState("");
  /** 目录 ↔ 自定义表单 ↔ 从其他应用导入。同一栏里换视图,不再叠一层浮层 */
  const [view, setView] = useState<CatalogView>("catalog");
  const providers = useModelsStore((s) => s.providers);

  // 搜索时跨全表找 —— 用户打「openrouter」不该还要先猜它在哪个分类
  const list = useMemo(
    () =>
      query.trim() === ""
        ? presetsForTab(tab)
        : matchPresets(PROVIDER_PRESETS, query),
    [tab, query],
  );

  /*
    ★ 搜索时也要能搜到「自定义」那张卡。用户手上有一个预设表里没有的地址,
    他的第一个动作往往是把域名粘进搜索框 —— 结果一家都不命中。那一刻正是
    最需要这条路的时候,而它如果只挂在「不搜索」的状态下,就恰好在这时消失了。
  */
  const q = query.trim().toLowerCase();
  const showCustom =
    q === "" ||
    list.length === 0 ||
    "custom".includes(q) ||
    t("models.customProvider").toLowerCase().includes(q);

  return (
    <div className="min-w-0 flex-1 rounded-[12px] border border-border bg-canvas">
      <div className="flex items-center gap-2 border-b border-hairline px-4 py-3">
        {view !== "catalog" && (
          <button
            type="button"
            aria-label={t("models.backToPresets")}
            onClick={() => setView("catalog")}
            className={cn(
              "app-no-drag flex size-6 shrink-0 items-center justify-center",
              "rounded-[8px] text-icon transition-colors hover:bg-tint hover:text-fg",
            )}
          >
            <ArrowLeft size={14} />
          </button>
        )}
        <span className="min-w-0 flex-1 truncate text-[13px] text-fg">
          {view === "custom"
            ? t("models.customProviderTitle")
            : view === "import"
              ? t("models.importFromApps")
              : t("models.addProvider")}
        </span>
        <button
          type="button"
          aria-label={t("common.close")}
          onClick={onClose}
          className={cn(
            "app-no-drag flex size-6 shrink-0 items-center justify-center",
            "rounded-[8px] text-icon transition-colors hover:bg-tint hover:text-fg",
          )}
        >
          <X size={14} />
        </button>
      </div>

      {view === "custom" ? (
        <CustomProviderForm onAdded={onAdded} />
      ) : view === "import" ? (
        <ImportProvidersForm onAdded={onAdded} />
      ) : (
        <div className="px-4 py-3.5">
          <p className="pb-3 text-[11.5px] leading-[1.6] text-fg-faint">
            {t("models.catalogHint", { count: PROVIDER_PRESETS.length })}
          </p>

          <div className="flex items-center gap-3 pb-3">
            <SegmentedControl
              label={t("models.providerCategory")}
              value={tab}
              onValueChange={(v) => {
                setTab(v as CatalogTab);
                setQuery("");
              }}
              options={CATALOG_TABS.map((t) => ({
                value: t.id,
                label: `${t.label} ${String(tabCount(t.id))}`,
              }))}
            />
            <div className="min-w-0 flex-1">
              <TextInput
                size="sm"
                value={query}
                onChange={setQuery}
                placeholder={t("models.searchProvider")}
                ariaLabel={t("models.searchProviderLabel")}
                icon={<Search size={13} className="text-icon" />}
              />
            </div>
          </div>

          {query.trim() !== "" && (
            <p className="pb-2 text-[11.5px] text-fg-faint">
              {t("models.searchAllHint", { total: PROVIDER_PRESETS.length, count: list.length })}
            </p>
          )}

          {list.length === 0 && !showCustom ? (
            <EmptyState
              icon={<Search size={20} />}
              title={t("models.noProviderMatch")}
              description={t("models.providerSearchHint")}
              className="py-10"
            />
          ) : (
            <div className="grid grid-cols-2 gap-2">
              {showCustom && <CustomProviderCard onOpen={() => setView("custom")} />}
              {showCustom && <ImportFromAppsCard onOpen={() => setView("import")} />}
              {list.map((p) => (
                <PresetCard
                  key={p.id}
                  preset={p}
                  added={isPresetAdded(p, providers)}
                  onAdded={onAdded}
                />
              ))}
            </div>
          )}

          <p className="mt-4 border-t border-hairline pt-3 text-[11.5px] leading-[1.6] text-fg-faint">
            {t("models.protocolAddressHint", { total: PROVIDER_PRESETS.length, divergent: divergentCount() })}
          </p>
        </div>
      )}
    </div>
  );
}

/**
 * 「自定义供应商」那张卡。
 *
 * ★ **这张是整卡可点的**,和预设卡不一样 —— 它上面没有第二个动作
 * (没有「接入文档」可开),所以整卡可点不会误触到别的东西。
 *
 * ★ 它排在网格**第一个**,而且五个分类里都在。预设表是一张快照:自建中转、
 * 私有部署、昨天刚上线的网关一家都不在里面,把这条路藏到列表末尾,等于让
 * 这些用户翻完 42 家才发现自己本来就不该翻。
 */
function CustomProviderCard({ onOpen }: { onOpen: () => void }): ReactNode {
  const { t } = useI18n();
  return (
    <button
      type="button"
      onClick={onOpen}
      className={cn(
        "app-no-drag min-w-0 rounded-[8px] border border-border bg-canvas px-2.5 py-2 text-left",
        "transition-colors hover:bg-tint/60",
      )}
    >
      <div className="flex items-center gap-1.5">
        <span
          className={cn(
            "flex size-5 shrink-0 items-center justify-center rounded-[6px]",
            "bg-tint text-icon",
          )}
          aria-hidden
        >
          <SlidersHorizontal size={12} />
        </span>
        <span className="min-w-0 flex-1 truncate text-[12px] text-fg">
          {t("models.customProvider")}
        </span>
        <Plus size={12} className="shrink-0 text-icon" aria-hidden />
      </div>
      {/* ★ 高度和预设卡对齐(地址行 + 说明行),否则它在网格第一格里矮一截,
          整排卡看着像是没对齐 */}
      <p className="mt-1 truncate text-[10.5px] text-fg-muted">
        {t("models.customProviderHint")}
      </p>
      <p className="mt-0.5 truncate text-[10.5px] text-fg-faint">
        {t("models.customProviderCardHint")}
      </p>
    </button>
  );
}

/**
 * 「从其他应用导入」那张卡。紧挨着「自定义」卡 —— 两者都是预设表覆盖不到的
 * 逃生门:一个给手上有地址的人,一个给手上有 Claude Code / Codex / OpenCode
 * 的人(那些配置里往往已经有一个可用的中转端点,不该让他再手抄一遍)。
 */
function ImportFromAppsCard({ onOpen }: { onOpen: () => void }): ReactNode {
  const { t } = useI18n();
  return (
    <button
      type="button"
      onClick={onOpen}
      className={cn(
        "app-no-drag min-w-0 rounded-[8px] border border-border bg-canvas px-2.5 py-2 text-left",
        "transition-colors hover:bg-tint/60",
      )}
    >
      <div className="flex items-center gap-1.5">
        <span
          className={cn(
            "flex size-5 shrink-0 items-center justify-center rounded-[6px]",
            "bg-tint text-icon",
          )}
          aria-hidden
        >
          <PackageOpen size={12} />
        </span>
        <span className="min-w-0 flex-1 truncate text-[12px] text-fg">
          {t("models.importFromApps")}
        </span>
        <Plus size={12} className="shrink-0 text-icon" aria-hidden />
      </div>
      <p className="mt-1 truncate text-[10.5px] text-fg-muted">
        {t("models.importFromAppsHint")}
      </p>
      <p className="mt-0.5 truncate text-[10.5px] text-fg-faint">
        {t("models.importFromAppsCardHint")}
      </p>
    </button>
  );
}

const IMPORT_SOURCES: ReadonlyArray<{ kind: ImportSourceKind; labelKey: TranslationKey }> = [
  { kind: "claude-code", labelKey: "import.sourceClaude" },
  { kind: "codex", labelKey: "import.sourceCodex" },
  { kind: "opencode", labelKey: "import.sourceOpencode" },
  { kind: "opencowork", labelKey: "import.sourceOpencowork" },
];

/**
 * 从 Claude Code / Codex / OpenCode 解析提供商 → 勾选审核 → 生成自定义提供商。
 *
 * ★ 解析全在主进程只读完成,这里拿到的**已经没有任何密钥值**(`hasLocalKey`
 * 只是个布尔提示)。落地走的是和「自定义」表单**同一条路**:`customProviderDraft`
 * + `upsertProvider` 生成 `custom-` 前缀的提供商,用户随后到右侧填密钥、拉模型。
 *
 * ★ **不自动种模型** —— 理由同 `CustomProviderForm`:我们对这个端点一无所知,
 * 种什么都是猜。源侧的模型名只作展示,导入后一点「从服务商拉取」就有真的那份。
 *
 * ★ 缺 base URL 的项**禁选**:生成一个连不上的提供商比不生成更糟。
 */
function ImportProvidersForm({
  onAdded,
}: {
  onAdded?: (providerId: string) => void;
}): ReactNode {
  const { t } = useI18n();
  const providers = useModelsStore((s) => s.providers);
  const [sourceKind, setSourceKind] = useState<ImportSourceKind>("codex");
  const [items, setItems] = useState<ImportableProvider[] | null>(null);
  const [available, setAvailable] = useState(true);
  const [loading, setLoading] = useState(false);
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    setSelected(new Set());
    void listImportableProviders(sourceKind)
      .then((res) => {
        if (cancelled) return;
        setAvailable(res.available);
        setItems(res.providers);
        // 默认勾选所有可导入(有 base URL)的项。
        setSelected(new Set(res.providers.filter((p) => p.baseUrl !== "").map((p) => p.sourceKey)));
      })
      .catch((e: unknown) => {
        if (cancelled) return;
        setError(e instanceof Error ? e.message : String(e));
        setItems([]);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [sourceKind]);

  const toggle = (key: string): void => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  };

  const chosen = (items ?? []).filter((p) => selected.has(p.sourceKey) && p.baseUrl !== "");

  const runImport = (): void => {
    if (chosen.length === 0) return;
    setBusy(true);
    setError(null);
    void (async () => {
      // ★ existingIds 边导边累积:两条同名的源提供商不会撞成同一个 id。
      const existing = providers.map((p) => p.id);
      let last = "";
      for (const p of chosen) {
        const draft = customProviderDraft(
          { name: p.name, baseUrl: p.baseUrl, protocol: p.protocol },
          existing,
        );
        await upsertProvider(draft);
        existing.push(draft.id);
        last = draft.id;
      }
      if (last !== "") onAdded?.(last);
    })()
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setBusy(false));
  };

  return (
    <div className="space-y-3 px-4 py-4">
      <p className="text-[11.5px] leading-[1.6] text-fg-faint">
        {t("models.importProvidersHint")}
      </p>

      <SegmentedControl
        label={t("import.fromOtherApps")}
        value={sourceKind}
        onValueChange={(v) => setSourceKind(v as ImportSourceKind)}
        options={IMPORT_SOURCES.map((s) => ({ value: s.kind, label: t(s.labelKey) }))}
      />

      {error !== null && (
        <p className="rounded-[8px] bg-danger/10 px-3 py-2 text-[12px] leading-[1.6] text-danger">
          {error}
        </p>
      )}

      {loading ? (
        <div className="flex items-center gap-2 py-8 text-[12px] text-fg-faint">
          <Spinner size="sm" />
          {t("common.loading")}
        </div>
      ) : !available ? (
        <EmptyState
          icon={<PackageOpen size={20} />}
          title={t("models.importSourceNotFound")}
          description={t("models.importSourceNotFoundHint")}
          className="py-10"
        />
      ) : (items ?? []).length === 0 ? (
        <EmptyState
          icon={<PackageOpen size={20} />}
          title={t("models.importNoProviders")}
          description={t("models.importNoProvidersHint")}
          className="py-10"
        />
      ) : (
        <div className="space-y-1.5">
          {(items ?? []).map((p) => {
            const selectable = p.baseUrl !== "";
            const active = selected.has(p.sourceKey);
            return (
              <button
                key={p.sourceKey}
                type="button"
                disabled={!selectable}
                onClick={() => toggle(p.sourceKey)}
                className={cn(
                  "app-no-drag flex w-full min-w-0 items-start gap-2.5 rounded-[8px] border px-2.5 py-2 text-left transition-colors",
                  selectable ? "border-border hover:bg-tint/60" : "border-hairline opacity-60",
                  active && selectable && "border-accent bg-accent/5",
                )}
              >
                <span
                  className={cn(
                    "mt-0.5 flex size-4 shrink-0 items-center justify-center rounded-[5px] border",
                    active && selectable ? "border-accent bg-accent text-accent-fg" : "border-border text-transparent",
                  )}
                  aria-hidden
                >
                  <Check size={11} />
                </span>
                <span className="min-w-0 flex-1">
                  <span className="flex items-center gap-1.5">
                    <span className="min-w-0 flex-1 truncate text-[12px] text-fg">{p.name}</span>
                    <span className="shrink-0 rounded-[5px] bg-tint px-1.5 py-0.5 text-[10px] text-fg-muted">
                      {PROTOCOL_LABEL[p.protocol]}
                    </span>
                  </span>
                  <span className="mt-0.5 block truncate font-mono text-[10.5px] text-fg-faint" title={p.baseUrl}>
                    {p.baseUrl || t("models.importNoBaseUrl")}
                  </span>
                  <span className="mt-0.5 block text-[10.5px] text-fg-muted">
                    {p.models.length > 0 && t("models.importModelCount", { count: p.models.length })}
                    {p.hasLocalKey && (
                      <span className="text-warning">
                        {p.models.length > 0 ? " · " : ""}
                        {t("models.importHasKey")}
                      </span>
                    )}
                  </span>
                </span>
              </button>
            );
          })}
        </div>
      )}

      <div className="flex items-center justify-end gap-2 border-t border-hairline pt-3">
        <Button size="sm"
          type="button"
          variant="primary"
          disabled={busy || loading || chosen.length === 0}
          onClick={runImport}
        >
          {busy ? <Spinner size="sm" /> : undefined}
          {t("models.importSelected", { count: chosen.length })}
        </Button>
      </div>
    </div>
  );
}

const ISSUE_MESSAGE: Readonly<Record<CustomProviderIssue, TranslationKey>> = {
  "name-required": "models.customNameRequired",
  "url-required": "models.customUrlRequired",
  "url-invalid": "models.customUrlInvalid",
};

/**
 * 自定义供应商的表单。
 *
 * ★ **只问三件事:名称、地址、API 格式。** 密钥不在这里问 —— 建完就选到
 * 右侧那张面板,密钥、模型列表、推理参数都在那儿,而那套控件已经存在。
 * 在这里再做一遍输入框,等于同一件事有两个入口、两份校验。
 *
 * ★ 地址下面照样跑 `baseUrlWarnings` 和「实际会请求」那一行。手填地址正是
 * 这两条最该出现的地方:预设填进来的地址都是带版本段的,**缺版本段、
 * Anthropic 端多带 `/v1`** 这些只有手填才可能发生。
 *
 * ★ 校验只在**提交时**报,不逐键描红:用户打到 `http://` 的中间态必然不合法,
 * 一边打字一边跳红字是在骂一个还没写完的输入。
 */
function CustomProviderForm({
  onAdded,
}: {
  onAdded?: (providerId: string) => void;
}): ReactNode {
  const { t } = useI18n();
  const providers = useModelsStore((s) => s.providers);
  const [name, setName] = useState("");
  const [baseUrl, setBaseUrl] = useState("");
  const [family, setFamily] = useState<ProtocolFamily>("openai");
  const [responses, setResponses] = useState(false);
  const [busy, setBusy] = useState(false);
  const [issue, setIssue] = useState<CustomProviderIssue | null>(null);
  const [error, setError] = useState<string | null>(null);

  const protocol = joinProtocol(family, responses);
  const warnings = baseUrlWarnings(baseUrl, protocol);

  const submit = (): void => {
    const found = validateCustomProvider({ name, baseUrl });
    setIssue(found);
    if (found !== null) return;
    const draft = customProviderDraft(
      { name, baseUrl, protocol },
      providers.map((p) => p.id),
    );
    setBusy(true);
    setError(null);
    void upsertProvider(draft)
      .then(async () => {
        /*
          ★ 自定义供应商**不种模型**(预设那条路会种,见 `seedModelsForPreset`)。
          我们对这个地址一无所知,种什么都是猜 —— 而猜错的表现是用户面前
          凭空多出两个请求必然 404 的模型名。他到右侧点一下「从服务商拉取」
          就有了真的那份;拉不动就手动加,两条路都通。
        */
        onAdded?.(draft.id);
      })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setBusy(false));
  };

  return (
    <div className="space-y-4 px-4 py-4">
      {error !== null && (
        <p className="rounded-[8px] bg-danger/10 px-3 py-2 text-[12px] leading-[1.6] text-danger">
          {error}
        </p>
      )}

      <Field label={t("provider.name")}>
        <TextInput
          value={name}
          onChange={(v) => {
            setName(v);
            setIssue(null);
          }}
          placeholder={t("models.customNamePlaceholder")}
          ariaLabel={t("provider.name")}
          invalid={issue === "name-required"}
          disabled={busy}
        />
      </Field>

      <Field label={t("provider.apiAddress")} hint={t("provider.apiAddressHint")}>
        <TextInput
          value={baseUrl}
          onChange={(v) => {
            setBaseUrl(v);
            setIssue(null);
          }}
          onCommit={() => setIssue(null)}
          placeholder={t("models.customUrlPlaceholder")}
          ariaLabel={t("provider.apiAddress")}
          inputMode="url"
          invalid={issue === "url-required" || issue === "url-invalid"}
          disabled={busy}
        />
        {baseUrl.trim() !== "" && (
          <p className="mt-1.5 truncate font-mono text-[11px] text-fg-faint">
            {t("provider.actualRequest")} {previewUrl(baseUrl, protocol)}
          </p>
        )}
        {warnings.map((w) => (
          <p key={w.kind} className="mt-1.5 text-[11.5px] leading-[1.6] text-fg-muted">
            {w.message}
          </p>
        ))}
      </Field>

      <Field label={t("provider.apiFormat")}>
        <fieldset disabled={busy} className="contents">
          <SegmentedControl
            label={t("provider.apiFormat")}
            value={family}
            onValueChange={(v) => setFamily(v as ProtocolFamily)}
            options={[
              { value: "openai", label: t("provider.openaiFormat") },
              { value: "anthropic", label: t("provider.anthropicFormat") },
            ]}
          />
        </fieldset>
        {/* ★ Responses 只在 OpenAI 族下出现,状态**留着** —— 用户切回来希望它还是原样
            (`joinProtocol` 在 anthropic 下会忽略它,那是正常交互的中间态,不是非法值) */}
        {family === "openai" && (
          <label className="mt-2.5 flex items-center justify-between gap-3">
            <span className="min-w-0 text-[12.5px] text-fg-muted">
              {t("provider.responsesApi")}
            </span>
            <Switch
              checked={responses}
              onCheckedChange={setResponses}
              aria-label={t("provider.responsesApi")}
              disabled={busy}
            />
          </label>
        )}
      </Field>

      {issue !== null && (
        <p className="text-[11.5px] leading-[1.6] text-danger">{t(ISSUE_MESSAGE[issue])}</p>
      )}

      <div className="flex items-center gap-2 border-t border-hairline pt-3">
        <p className="min-w-0 flex-1 text-[11.5px] leading-[1.6] text-fg-faint">
          {t("models.customProviderKeyHint")}
        </p>
        <Button size="sm"
          type="button"
          variant="primary"
          disabled={busy}
          onClick={submit}
        >
          {busy ? <Spinner size="xs" /> : <Plus size={12} />}
          {t("common.add")}
        </Button>
      </div>
    </div>
  );
}

function Field({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: ReactNode;
}): ReactNode {
  return (
    <div>
      <p className="pb-1.5 text-[12px] text-fg-muted">{label}</p>
      {children}
      {hint !== undefined && (
        <p className="mt-1.5 text-[11.5px] leading-[1.6] text-fg-faint">{hint}</p>
      )}
    </div>
  );
}

function PresetCard({
  preset: p,
  added,
  onAdded,
}: {
  preset: ProviderPreset;
  added: boolean;
  onAdded?: (providerId: string) => void;
}): ReactNode {
  const { t } = useI18n();
  const rows = endpointRows(p);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const add = (): void => {
    const draft = providerFromPreset(p);
    // endpoints 非空是预设表的结构约束(presets.test.ts 守着),这里兜底不报错
    if (draft === null) return;
    setBusy(true);
    setError(null);
    void upsertProvider(draft)
      .then(async () => {
        /*
          ★ 拉不动模型列表的那几家,顺手把建议模型种进去 —— 否则用户添加完
          得到的是一家零模型的供应商。「从服务商拉取」按钮现在**一律能点**
          (置灰那条禁令已删,见 `import-models.ts`),但这几家实测拉不到,
          种子是为了让他不至于零模型起步。判据在 `seedModelsForPreset` 里
          (是 supportsModelList,不是哪一家)。

          ★ 种失败**不阻断**:供应商已经建出来了,模型还能手动加。
          在这里把整个「添加」判失败,用户会以为没建成而再点一次。
        */
        const seeds = seedModelsForPreset(p, draft.protocol);
        if (seeds.length > 0) await setProviderAliases(draft.id, seeds).catch(() => {});
        onAdded?.(draft.id);
      })
      .catch((e: unknown) =>
        setError(e instanceof Error ? e.message : String(e)),
      )
      .finally(() => setBusy(false));
  };

  return (
    <div
      title={detailTooltip(p, rows)}
      className="min-w-0 rounded-[8px] border border-border bg-canvas px-2.5 py-2"
    >
      <div className="flex items-center gap-1.5">
        <ProviderAvatar name={p.name} id={p.id} size="sm" />
        <span className="min-w-0 flex-1 truncate text-[12px] text-fg">{p.name}</span>
        {/* ★ **只有「未核实」还留在卡面上。** 「探针实测」「文档提取」那两个角标
            每张卡都有,等于没有信息;而未核实的那几家必须当场看得见 ——
            配失败时用户要知道该去查文档,而不是怀疑自己填错了。
            (完整核实来源仍在整卡的 title 里。) */}
        {p.verification === "unverified" && <Tag danger>{t("models.unverified")}</Tag>}
        <span className="shrink-0" />
        <button
          type="button"
          onClick={() => void openExternal(p.docsUrl)}
          title={p.docsUrl}
          aria-label={t("models.docs")}
          className={cn(
            "app-no-drag flex size-5 shrink-0 items-center justify-center rounded-[5px]",
            "text-icon transition-colors hover:bg-tint hover:text-fg",
          )}
        >
          <ExternalLink size={11} />
        </button>
        {added ? (
          <span
            className="flex size-5 shrink-0 items-center justify-center text-accent"
            title={t("models.added")}
          >
            <Check size={12} />
          </span>
        ) : (
          <button
            type="button"
            disabled={busy}
            onClick={add}
            aria-label={t("common.add")}
            className={cn(
              "app-no-drag flex size-5 shrink-0 items-center justify-center rounded-[5px]",
              "bg-tint text-fg transition-colors hover:bg-tint-strong disabled:opacity-40",
            )}
          >
            {busy ? <Spinner size="xs" /> : <Plus size={11} />}
          </button>
        )}
      </div>

      {/*
        ★★ **只显示主端点那一条地址。** 以前每个协议一行、每行还带「列表可拉性」,
        一张卡八行高,两列排下来一屏放不下四家 —— 而这本册子的用处恰恰是**扫**,
        扫不动就等于没有。第二个协议的地址、列表可拉性、核实来源、建议模型
        全部进了整卡的 `title`:它们是**查**的时候要的(「api.moonshot.cn 是哪家」
        「我这个 401 是不是走错了鉴权域」),而查的时候用户已经停在某一张卡上了。

        ★ 显示的仍然是 `previewUrl()` 算出的**最终请求地址**而不是 baseUrl:
        OpenAI 族的版本段在 base 里、Anthropic 族不带,只看 base 会以为数据录错了。
      */}
      <div className="mt-1 flex items-center gap-1">
        <code className="min-w-0 flex-1 truncate font-mono text-[10.5px] text-fg-faint">
          {rows[0]?.requestUrl ?? ""}
        </code>
        {hasDivergentBaseUrls(p) && (
          <Shuffle
            size={9}
            className="shrink-0 text-fg-faint"
            aria-label={t("models.protocolChangesAddress")}
          />
        )}
      </div>

      {p.notes !== undefined && (
        <p className="mt-0.5 truncate text-[10.5px] text-fg-muted">{p.notes}</p>
      )}

      {/* ★ 原样显示主进程回的那句话。这里最可能出现的是 baseUrl 被拒
          (`normalizeBaseUrl` 只放行 http/https),概括成「添加失败」就没了线索。
          ★ 这一条**不截断**:它是唯一一句需要读完的文字 */}
      {error !== null && (
        <p className="mt-1 text-[10.5px] leading-[1.5] text-danger">{error}</p>
      )}
    </div>
  );
}

/**
 * 整卡 hover 时那段详情 —— 卡面压掉的东西都在这里。
 *
 * 用 `title` 而不是自绘浮层:这段是**查**的时候才要的,而系统 tooltip
 * 不抢焦点、不遮住旁边的卡,也不需要为它维护一套定位逻辑。
 */
function detailTooltip(p: ProviderPreset, rows: readonly EndpointRow[]): string {
  const lines = [p.name];
  for (const r of rows) lines.push(`${r.label} · ${LIST_ACCESS_LABEL[r.list]}\n  ${r.requestUrl}`);
  lines.push(VERIFICATION_LABEL[p.verification]);
  if (p.notes !== undefined) lines.push(p.notes);
  if (p.suggestedModels.length > 0) lines.push(p.suggestedModels.join(" · "));
  return lines.join("\n");
}

function Tag({
  children,
  danger = false,
}: {
  children: ReactNode;
  danger?: boolean;
}): ReactNode {
  return (
    <span
      className={cn(
        "shrink-0 rounded-[5px] px-1.5 py-0.5 text-[10px]",
        danger ? "bg-danger/10 text-danger" : "bg-tint text-fg-faint",
      )}
    >
      {children}
    </span>
  );
}
