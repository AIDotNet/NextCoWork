import {
  Check,
  Cpu,
  File,
  Globe,
  Image,
  Pencil,
  Plus,
  RefreshCw,
  RotateCcw,
  Search,
  Trash2,
  Video,
  Wrench,
  Zap,
} from "lucide-react";
import { useEffect, useMemo, useState, type ReactNode } from "react";
import type {
  ModelAlias,
  ModelCapabilities,
  ModelModality,
  RequestAdapterConfig,
  ThinkingMode,
  UpstreamProvider,
} from "../../../../../shared/domain/provider";
import {
  validateRequestPatches,
  type RequestPatchValidationCode,
} from "../../../../../shared/domain/request-patch";
import { usePricingTable } from "../../../services/pricing";
import {
  mergeModelCatalog,
  type ModelCatalogDefinition,
  type ModelCatalogEntry,
  type ModelCatalogVerificationStatus,
} from "../../../../../shared/domain/model-catalog";
import {
  BUILTIN_MODEL_CATALOG,
  MODEL_MANUFACTURERS,
  type ReasoningEffort,
} from "../../../../../shared/domain/model-catalog-inventory";
import { EmptyState } from "../../../components/arc/empty-state/empty-state";
import { Dialog } from "../../../components/ui/Dialog";
import { Select, type SelectOption } from "../../../components/ui/Select";
import { TextInput } from "../../../components/ui/TextInput";
import { Switch } from "../../../components/arc/switch/switch";
import { cn } from "../../../lib/cn";
import {
  listModels,
  setProviderAliases,
  updateModel,
} from "../../../services/provider";
import {
  listUserModelCatalog,
  onUserModelCatalogChanged,
  removeUserModelCatalog,
  upsertUserModelCatalog
} from "../../../services/model-catalog";
import { useModelsStore } from "../../../stores/models";
import type { SettingsPageProps } from "../../props";
import { ProviderCatalog } from "./ProviderCatalog";
import { ModelCatalogSyncDialog } from "./ModelCatalogSyncDialog";
import { StubModalityPage } from "./StubModalityPage";
import { EnabledModelList } from "./EnabledModelList";
import { ProviderPanel } from "./ProviderPanel";
import {
  providerEntries,
} from "./enabled-models";
import { formatRate, selectCatalogPricing } from "./pricing-table";
import { parseModelTab } from "./tabs";
import { useI18n, type TranslationKey } from "../../../i18n";
import { ModelProtocolEditor, modelProtocolSummary } from "./ModelProtocol";
import { ImageModelPage } from "./ImageModelPage";
import { VideoModelPage } from "./VideoModelPage";

export function ModelPage({ settings, sub, patch }: SettingsPageProps): ReactNode {
  const tab = parseModelTab(sub);
  if (tab === "management") return <ModelConsole />;
  // 图片页要 settings/patch:页脚那行「对话生图使用的模型」是全局设置(见 ImageGenModelRow)
  if (tab === "image") return <ImageModelPage settings={settings} patch={patch} />;
  // 视频页同理 —— 它有自己的开关(`videoGenerationEnabled`)与点名模型(`videoModel`),
  // 而两者的语义与生图那条**不同**(关掉只挡新建,见 settings.ts 上那段)。
  if (tab === "video") return <VideoModelPage settings={settings} patch={patch} />;
  if (tab !== "text") return <StubModalityPage modality={tab} />;
  return <LegacyTextTab settings={settings} />;
}

function LegacyTextTab({
  settings,
}: Pick<SettingsPageProps, "settings">): ReactNode {
  const { t } = useI18n();
  const providers = useModelsStore((s) => s.providers),
    models = useModelsStore((s) => s.models),
    loaded = useModelsStore((s) => s.loaded),
    load = useModelsStore((s) => s.load);
  const [selectedId, setSelectedId] = useState<string | null>(null),
    [catalogOpen, setCatalogOpen] = useState(false);
  useEffect(() => {
    void load();
  }, [load]);
  const entries = useMemo(
    () => providerEntries(providers, models, settings.defaultModel, settings.defaultModelProviderId),
    [providers, models, settings.defaultModel, settings.defaultModelProviderId],
  );
  const selected =
    entries.find((e) => e.provider.id === selectedId) ?? entries[0] ?? null;
  return (
    <>
      {/*
        ★ 「默认模型/默认子代理」的选择器搬去了「通用 → Agent」(`GeneralPage.tsx`
        的 `sub === 'agent'` 分支)—— 用户来这里配权限/审核模型/上下文管理,
        「Agent 用哪个模型」该跟它们同屏,不该单独留在模型设置页里跟供应商列表
        抢一条窄列。这里的 `entries`/`isDefault` 徽章原样保留:它是「库存」视图,
        展示哪家供应商在提供当前默认模型,跟「去哪改默认模型」是两件事。
      */}
      <div className="flex items-start gap-4">
        <EnabledModelList
          entries={entries}
          loaded={loaded}
          selectedId={catalogOpen ? null : (selected?.provider.id ?? null)}
          onSelect={(id) => {
            // ★ 目录不是模态,左列在它开着时照样可点 —— 点了就是「不加了,看这家」
            setSelectedId(id);
            setCatalogOpen(false);
          }}
          onAdd={() => setCatalogOpen(true)}
        />
        {/*
          ★★ **目录占的是供应商面板的位置,不再是一个浮层。** 于是右边这一栏
          有三态:目录 / 选中的那一家 / 一家都没有。顺序不能换 —— 目录必须排在
          「一家都没有」前面,否则第一次进来的用户点了加号,看到的还是那句
          「点左侧加号添加供应商」。
        */}
        {catalogOpen ? (
          <ProviderCatalog
            onClose={() => setCatalogOpen(false)}
            onAdded={(id) => {
              setSelectedId(id);
              setCatalogOpen(false);
            }}
          />
        ) : selected ? (
          <ProviderPanel
            entry={selected}
            /* 账号列表用它算「下一次请求会用哪个账号」——主进程是设置的唯一权威(§9),
               所以一路从 props 传下来,不在下面镜像一份 */
            accountRotation={settings.providerAccountRotation}
          />
        ) : (
          <EmptyState
            className="min-w-0 flex-1 py-16"
            title={t("models.noProvider")}
            description={t("models.addProviderHint")}
          />
        )}
      </div>
    </>
  );
}

type CatalogModel = ModelCatalogEntry;
type CatalogDraft = ModelCatalogDefinition;
type CatalogEditorTarget =
  { model: CatalogModel; isNew: false } | { model: CatalogDraft; isNew: true };

const REASONING_EFFORTS: readonly ReasoningEffort[] = [
  "none",
  "minimal",
  "low",
  "medium",
  "high",
  "xhigh",
  "max",
];

const PATCH_VALIDATION_MESSAGES: Record<
  RequestPatchValidationCode,
  TranslationKey
> = {
  invalid_json: "models.patchInvalidJson",
  not_array: "models.patchNotArray",
  invalid_rule: "models.patchInvalidRule",
  unsupported_operation: "models.patchUnsupportedOperation",
  invalid_path: "models.patchInvalidPath",
  missing_value: "models.patchMissingValue",
  invalid_value: "models.patchInvalidValue",
};

function blankCatalogModel(): CatalogDraft {
  const manufacturer =
    MODEL_MANUFACTURERS.find((item) => item.id === "other") ??
    MODEL_MANUFACTURERS[0];
  return {
    id: "",
    manufacturerId: manufacturer?.id ?? "other",
    manufacturerLabel: manufacturer?.label ?? "other",
    displayName: "",
    modality: "text",
    capabilities: {
      // 和目录的默认值保持一致：标错成 false 不会报错，只会让 Agent 静默失去全部工具
      // （见 shared/domain/model-catalog-inventory.ts 的 textCapabilities）
      tools: true,
      vision: false,
      thinking: false,
      caching: false,
      textInput: true,
      fileInput: false,
      videoInput: false,
      audioInput: false,
      textOutput: true,
      imageOutput: false,
      videoOutput: false,
      audioOutput: false,
      webSearch: false,
      structuredOutput: false,
      streaming: false,
      batch: false,
    },
    contextWindow: 128_000,
    maxOutputTokens: 8_192,
    thinkingConfig: { mode: "unsupported", defaultEnabled: false },
    requestAdapter: { preset: "auto", patches: [] },
    verificationStatus: "unverified",
  };
}

function ModelConsole(): ReactNode {
  const { t } = useI18n();
  const providers = useModelsStore((s) => s.providers),
    models = useModelsStore((s) => s.models),
    loaded = useModelsStore((s) => s.loaded),
    load = useModelsStore((s) => s.load);
  const [manufacturerId, setManufacturerId] = useState<string | null>(null),
    [selectedKey, setSelectedKey] = useState<string | null>(null),
    [syncOpen, setSyncOpen] = useState(false),
    [editingModel, setEditingModel] = useState<CatalogEditorTarget | null>(
      null,
    ),
    [customModels, setCustomModels] = useState<ModelCatalogDefinition[]>([]),
    [catalogLoaded, setCatalogLoaded] = useState(false),
    [catalogError, setCatalogError] = useState<string | null>(null),
    [query, setQuery] = useState(""),
    [capability, setCapability] = useState("all"),
    [modalityFilter, setModalityFilter] = useState("all");

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    let active = true;
    let unsubscribe: (() => void) | undefined;
    try {
      unsubscribe = onUserModelCatalogChanged((next) => {
        if (active) setCustomModels(next);
      });
    } catch (error) {
      console.error("Failed to subscribe to model catalog changes", error);
      setCatalogError(t("models.catalogLoadFailed"));
    }
    void listUserModelCatalog()
      .then((next) => {
        if (!active) return;
        setCustomModels(next);
        setCatalogError(null);
      })
      .catch((error: unknown) => {
        console.error("Failed to load user model catalog", error);
        if (active) setCatalogError(t("models.catalogLoadFailed"));
      })
      .finally(() => {
        if (active) setCatalogLoaded(true);
      });
    return () => {
      active = false;
      unsubscribe?.();
    };
  }, [t]);

  const catalog = useMemo(
    () =>
      mergeModelCatalog({
        builtin: BUILTIN_MODEL_CATALOG,
        custom: customModels,
        bindings: models,
      }),
    [customModels, models],
  );
  const manufacturerList = useMemo(() => {
    const present = new Set(catalog.map((model) => model.manufacturerId));
    return MODEL_MANUFACTURERS.filter((item) => present.has(item.id));
  }, [catalog]);
  const filtered = useMemo(
    () =>
      catalog.filter((m) => {
        if (manufacturerId !== null && m.manufacturerId !== manufacturerId)
          return false;
        const q = query.trim().toLowerCase();
        const connectedProviders = m.providerIds
          .flatMap((providerId) => {
            const provider = providers.find((item) => item.id === providerId);
            return provider === undefined
              ? [providerId]
              : [providerId, provider.name];
          })
          .join(" ");
        if (
          q &&
          !`${m.id} ${m.displayName} ${m.manufacturerLabel} ${(m.aliases ?? []).join(" ")} ${connectedProviders}`
            .toLowerCase()
            .includes(q)
        )
          return false;
        if (
          modalityFilter !== "all" &&
          (m.modality ?? "text") !== modalityFilter
        )
          return false;
        if (
          capability === "vision" &&
          !m.capabilities.vision &&
          !m.capabilities.visionInput
        )
          return false;
        if (capability === "file" && !m.capabilities.fileInput) return false;
        if (capability === "video" && !m.capabilities.videoInput) return false;
        if (capability === "audio" && !m.capabilities.audioInput) return false;
        if (capability === "web" && !m.capabilities.webSearch) return false;
        if (capability === "tools" && !m.capabilities.tools) return false;
        if (capability === "think" && !m.capabilities.thinking) return false;
        if (capability === "image-output" && !m.capabilities.imageOutput)
          return false;
        if (capability === "video-output" && !m.capabilities.videoOutput)
          return false;
        if (capability === "audio-output" && !m.capabilities.audioOutput)
          return false;
        if (
          capability === "structured" &&
          !m.capabilities.structuredOutput
        )
          return false;
        if (capability === "streaming" && !m.capabilities.streaming)
          return false;
        if (capability === "batch" && !m.capabilities.batch) return false;
        if (capability === "caching" && !m.capabilities.caching) return false;
        return true;
      }),
    [catalog, providers, manufacturerId, query, capability, modalityFilter],
  );

  const persistCustom = (saved: ModelCatalogDefinition): void => {
    setCustomModels((current) => [
      ...current.filter(
        (item) => item.id.toLowerCase() !== saved.id.toLowerCase(),
      ),
      saved,
    ]);
    setManufacturerId(saved.manufacturerId);
    setEditingModel(null);
  };

  const removeCustom = (id: string): void => {
    setCustomModels((current) =>
      current.filter((item) => item.id.toLowerCase() !== id.toLowerCase()),
    );
    setEditingModel(null);
  };
  return (
    <>
      <ModelCatalogSyncDialog open={syncOpen} onClose={() => setSyncOpen(false)} />
      <div className="flex min-h-[520px] min-w-0 gap-3">
        <aside className="flex max-h-[542px] w-[190px] shrink-0 flex-col border-r border-hairline pr-3">
          <div className="mb-2 flex items-center justify-between">
            <span className="text-[13px] text-fg">
              {t("models.manufacturer")}
            </span>
            <button
              type="button"
              aria-label={t("models.addModel")}
              title={t("models.addModel")}
              onClick={() =>
                setEditingModel({ model: blankCatalogModel(), isNew: true })
              }
              className="rounded-[6px] p-1 text-icon hover:bg-tint-hover hover:text-fg"
            >
              <Plus size={14} />
            </button>
          </div>
          <button
            type="button"
            onClick={() => setManufacturerId(null)}
            className={cn(
              "mb-1 flex w-full items-center justify-between rounded-[8px] px-2 py-2 text-left text-[12px]",
              manufacturerId === null
                ? "bg-tint text-fg"
                : "text-fg-muted hover:bg-tint-hover",
            )}
          >
            <span>{t("models.all")}</span>
            <span className="text-[11px] text-fg-faint">{catalog.length}</span>
          </button>
          <ul className="scroll-thin min-h-0 flex-1 space-y-0.5 overflow-y-auto pr-1">
            {manufacturerList.map((item) => (
              <li key={item.id}>
                <button
                  type="button"
                  onClick={() => setManufacturerId(item.id)}
                  className={cn(
                    "flex w-full items-center justify-between rounded-[8px] px-2 py-2 text-left text-[12px]",
                    manufacturerId === item.id
                      ? "bg-tint text-fg"
                      : "text-fg-muted hover:bg-tint-hover",
                  )}
                >
                  <span className="min-w-0 truncate">
                    {item.id === "other" ? t("models.otherVendor") : item.label}
                  </span>
                  <span className="text-[11px] text-fg-faint">
                    {catalog.filter((m) => m.manufacturerId === item.id).length}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        </aside>
        <section className="min-w-0 flex-1">
          {catalogError !== null && (
            <div className="mb-2 rounded-[8px] border border-danger/30 bg-danger/5 px-2.5 py-2 text-[11px] text-danger">
              {t("chat.status.error")}: {catalogError}
            </div>
          )}
          <div className="mb-2 flex flex-wrap items-center gap-2">
            <div className="min-w-0 flex-1">
              <TextInput
                size="sm"
                value={query}
                onChange={setQuery}
                placeholder={t("models.search")}
                ariaLabel={t("models.searchLabel")}
                icon={<Search size={13} />}
              />
            </div>
            <div className="w-[116px]">
              <EditorSelect
                value={modalityFilter}
                ariaLabel={t("models.allTypes")}
                onValueChange={setModalityFilter}
                options={[
                  { value: "all", label: t("models.allTypes") },
                  { value: "text", label: t("models.text") },
                  { value: "image", label: t("models.image") },
                  { value: "video", label: t("models.video") },
                  { value: "speech", label: t("models.speech") },
                  { value: "transcription", label: t("models.transcription") },
                ]}
              />
            </div>
            <div className="w-[136px]">
              <EditorSelect
                value={capability}
                ariaLabel={t("models.allCapabilities")}
                onValueChange={setCapability}
                options={[
                  { value: "all", label: t("models.allCapabilities") },
                  { value: "vision", label: t("models.capabilityVision") },
                  { value: "file", label: t("models.capabilityFile") },
                  { value: "video", label: t("models.capabilityVideo") },
                  { value: "audio", label: t("models.capabilityAudioInput") },
                  { value: "web", label: t("models.webSearch") },
                  { value: "tools", label: t("models.capabilityTools") },
                  { value: "think", label: t("models.reasoning") },
                  { value: "image-output", label: t("models.imageGeneration") },
                  { value: "video-output", label: t("models.capabilityVideoOutput") },
                  { value: "audio-output", label: t("models.capabilityAudioOutput") },
                  { value: "structured", label: t("models.capabilityStructuredOutput") },
                  { value: "streaming", label: t("models.capabilityStreaming") },
                  { value: "batch", label: t("models.capabilityBatch") },
                  { value: "caching", label: t("models.capabilityCaching") },
                ]}
              />
            </div>
            <button
              type="button"
              onClick={() => setSyncOpen(true)}
              className="inline-flex h-8 items-center gap-1 rounded-[8px] border border-border px-2.5 text-[11.5px] text-fg hover:bg-tint-hover"
            >
              <RefreshCw size={13} />
              {t("models.syncButton")}
            </button>
            <button
              type="button"
              onClick={() =>
                setEditingModel({ model: blankCatalogModel(), isNew: true })
              }
              className="inline-flex h-8 items-center gap-1 rounded-[8px] bg-accent px-2.5 text-[11.5px] text-accent-fg"
            >
              <Plus size={13} />
              {t("models.addModel")}
            </button>
          </div>
          <div className="scroll-thin max-h-[502px] overflow-auto rounded-[10px] border border-border">
            <table className="w-full min-w-[1000px] border-collapse text-[11.5px]">
              <thead className="sticky top-0 z-10 bg-surface">
                <tr className="border-b border-hairline text-fg-faint">
                  <th className="w-7 py-2"></th>
                  <th className="py-2 text-left font-normal">
                    {t("models.columns.model")}
                  </th>
                  <th className="py-2 text-left font-normal">
                    {t("models.columns.type")}
                  </th>
                  <th className="py-2 text-left font-normal">
                    {t("models.protocol")}
                  </th>
                  <th className="py-2 text-left font-normal">
                    {t("models.columns.capabilities")}
                  </th>
                  <th className="py-2 text-left font-normal">
                    {t("models.columns.input")}
                  </th>
                  <th className="py-2 text-left font-normal">
                    {t("models.columns.output")}
                  </th>
                  <th className="py-2 text-left font-normal">
                    {t("models.columns.pricing")}
                  </th>
                  <th className="py-2 text-left font-normal">
                    {t("models.columns.status")}
                  </th>
                  <th className="sticky right-0 w-16 bg-surface py-2 pr-2 text-right font-normal shadow-[-6px_0_8px_-8px_rgba(0,0,0,0.35)]">
                    {t("models.actions")}
                  </th>
                </tr>
              </thead>
              <tbody>
                {filtered.map((m) => (
                  <ModelRow
                    key={m.id}
                    model={m}
                    providers={providers}
                    selected={selectedKey === m.id}
                    onSelect={() => setSelectedKey(m.id)}
                    onEdit={() => setEditingModel({ model: m, isNew: false })}
                  />
                ))}
              </tbody>
            </table>
            {filtered.length === 0 && (
              <EmptyState
                className="py-12"
                icon={<Cpu size={20} />}
                title={t("models.noMatch")}
                description={
                  loaded && catalogLoaded
                    ? t("models.adjustFilters")
                    : t("models.readingCatalog")
                }
              />
            )}
          </div>
        </section>
      </div>
      <Dialog
        open={editingModel !== null}
        onClose={() => setEditingModel(null)}
        title={editingModel?.isNew ? t("models.addModel") : t("models.edit")}
        description={
          editingModel === null
            ? undefined
            : editingModel.model.displayName || editingModel.model.id
        }
        width={640}
      >
        {editingModel !== null && (
          <CatalogModelEditor
            key={`${editingModel.isNew ? "new" : "edit"}:${editingModel.model.id}`}
            initial={editingModel.model}
            isNew={editingModel.isNew}
            builtin={!editingModel.isNew && editingModel.model.builtin === true}
            overridden={
              !editingModel.isNew && editingModel.model.overridden === true
            }
            providers={providers}
            initialProviderId={
              editingModel.isNew
                ? undefined
                : editingModel.model.bindings[0]?.providerId
            }
            existingIds={catalog.flatMap((model) => [
              model.id,
              ...(model.aliases ?? []),
            ])}
            bindings={editingModel.isNew ? [] : catalog.find((model) => model.id === editingModel.model.id)?.bindings ?? []}
            onSaved={persistCustom}
            onRemoved={removeCustom}
          />
        )}
      </Dialog>
    </>
  );
}

function ModelRow({
  model: m,
  providers,
  selected,
  onSelect,
  onEdit,
}: {
  model: CatalogModel;
  providers: readonly UpstreamProvider[];
  selected: boolean;
  onSelect: () => void;
  onEdit: () => void;
}): ReactNode {
  const { t } = useI18n();
  const pricingTable = usePricingTable();
  const caps = [
    (m.capabilities.vision || m.capabilities.visionInput) &&
      t("models.badgeVision"),
    m.capabilities.fileInput && t("models.badgeFile"),
    m.capabilities.videoInput && t("models.badgeVideoInput"),
    m.capabilities.audioInput && t("models.badgeAudioInput"),
    m.capabilities.imageOutput && t("models.badgeImageOutput"),
    m.capabilities.videoOutput && t("models.badgeVideoOutput"),
    m.capabilities.audioOutput && t("models.badgeAudioOutput"),
    m.capabilities.webSearch && t("models.badgeWeb"),
    m.capabilities.tools && t("models.badgeTools"),
    m.capabilities.thinking && t("models.badgeThink"),
  ].filter(Boolean) as string[];
  const price = selectCatalogPricing(pricingTable, m, Date.now());
  const rate = price?.tiers[0]?.rate;
  const currency = price?.currency ?? "USD";
  const money = (value: number | undefined): string =>
    formatRate(value, currency);
  const modalityLabel =
    {
      text: t("models.text"),
      image: t("models.image"),
      video: t("models.video"),
      speech: t("models.speech"),
      transcription: t("models.transcription"),
    }[m.modality] ?? t("models.text");
  const verificationLabel =
    m.verificationStatus === "official-api"
      ? t("models.verificationOfficialApi")
      : m.verificationStatus === "official-model-card"
        ? t("models.verificationOfficialCard")
        : m.verificationStatus === "aggregator-reference"
          ? t("models.verificationAggregator")
          : t("models.verificationUnverified");
  return (
    <tr
      onClick={onSelect}
      className={cn(
        "group cursor-pointer border-b border-hairline last:border-0 hover:bg-tint-hover/50",
        selected && "bg-tint",
      )}
    >
      <td className="py-2 pl-2 text-center">
        {selected && <Check size={13} className="text-accent" />}
      </td>
      <td className="max-w-[150px] py-2">
        <div className="truncate text-fg">{m.displayName}</div>
        <div className="truncate font-mono text-[10px] text-fg-faint">
          {m.id}
        </div>
        <span className="text-[9px] text-fg-faint">
          {m.builtin ? t("models.catalogModel") : t("models.local")}
        </span>
      </td>
      <td className="py-2 text-fg-muted">{modalityLabel}</td>
      <td className="max-w-[200px] py-2 pr-3 text-[10px] text-fg-muted">
        {m.bindings.length === 0 ? t("models.protocolNoBindingShort") : m.bindings.map((binding) => (
          <div key={`${binding.providerId}:${binding.alias}`} className="py-0.5">
            <span className="text-fg-faint">
              {providers.find((provider) => provider.id === binding.providerId)?.name ?? binding.providerId}
              {m.bindings.length > 1 ? ` · ${binding.alias}` : ""}
            </span>
            <div>{modelProtocolSummary(binding, providers, t)}</div>
          </div>
        ))}
      </td>
      <td className="py-2">
        <div className="flex max-w-[135px] flex-wrap gap-1">
          {caps.slice(0, 4).map((c) => (
            <span
              key={c}
              className="rounded-[4px] bg-surface-sunken px-1 text-[10px] text-fg-muted"
            >
              {c}
            </span>
          ))}
          {caps.length > 4 && (
            <span className="text-[10px] text-fg-faint">
              +{caps.length - 4}
            </span>
          )}
        </div>
      </td>
      <td className="py-2 tabular-nums text-fg-muted">
        <div>{money(rate?.input)}</div>
        {currency !== "USD" && rate?.input !== undefined && (
          <div
            title={t("models.currencyOriginal")}
            className="text-[9px] text-fg-faint"
          >
            {t("models.officialOriginalCurrency")}
          </div>
        )}
      </td>
      <td className="py-2 tabular-nums text-fg-muted">
        <div>{money(rate?.output)}</div>
        {currency !== "USD" && rate?.output !== undefined && (
          <div
            title={t("models.currencyOriginal")}
            className="text-[9px] text-fg-faint"
          >
            {t("models.officialOriginalCurrency")}
          </div>
        )}
      </td>
      <td className="py-2 text-fg-muted">
        {price ? (
          <span>
            {price.tiers.length > 1
              ? t("models.tiers", { count: price.tiers.length })
              : t("models.singleTier")}
            {price.windows?.length
              ? t("models.windows", { count: price.windows.length })
              : ""}
          </span>
        ) : (
          t("models.pendingValidation")
        )}
      </td>
      <td className="py-2 pr-2 text-right">
        <div
          className={cn(
            "text-[10px]",
            m.verificationStatus === undefined ||
              m.verificationStatus === "unverified"
              ? "text-fg-faint"
              : "text-accent",
          )}
        >
          {verificationLabel}
        </div>
        <div className="mt-0.5 text-[10px]">
          {!m.configured ? (
            <span className="text-fg-faint">{t("models.unconfigured")}</span>
          ) : m.enabled === false ? (
            <span className="text-fg-faint">{t("models.disabled")}</span>
          ) : (
            <span className="text-accent">{t("models.enabled")}</span>
          )}
        </div>
      </td>
      <td
        className={cn(
          "sticky right-0 py-2 pr-2 text-right shadow-[-6px_0_8px_-8px_rgba(0,0,0,0.35)]",
          selected
            ? "bg-tint"
            : "bg-canvas group-hover:bg-tint-hover",
        )}
      >
        <button
          type="button"
          aria-label={`${t("models.edit")} ${m.displayName}`}
          onClick={(event) => {
            event.stopPropagation();
            onEdit();
          }}
          className="inline-flex items-center gap-1 rounded-[5px] px-1.5 py-1 text-[10.5px] text-fg-muted hover:bg-tint-hover hover:text-fg"
        >
          <Pencil size={12} />
          {t("models.edit")}
        </button>
      </td>
    </tr>
  );
}

/** 检查器里的下拉：共享 `Select`（Radix），避免原生 select 露出系统控件样式。 */
function EditorSelect({
  value,
  options,
  onValueChange,
  ariaLabel,
  disabled = false,
  className,
}: {
  value: string;
  options: readonly SelectOption[];
  onValueChange: (value: string) => void;
  ariaLabel: string;
  disabled?: boolean;
  className?: string;
}): ReactNode {
  return (
    <Select
      inModal
      value={value}
      options={options}
      onValueChange={onValueChange}
      ariaLabel={ariaLabel}
      disabled={disabled}
      className={cn("h-8", disabled && "opacity-60", className)}
    />
  );
}

const editorInputClass =
  "apple-field h-8 w-full text-[12px]";

function cloneCatalogDraft(model: ModelCatalogDefinition): CatalogDraft {
  return {
    id: model.id,
    manufacturerId: model.manufacturerId,
    manufacturerLabel: model.manufacturerLabel,
    displayName: model.displayName,
    modality: model.modality,
    capabilities: { ...model.capabilities },
    contextWindow: model.contextWindow,
    maxOutputTokens: model.maxOutputTokens,
    thinkingConfig: { ...model.thinkingConfig },
    ...(model.requestAdapter === undefined
      ? {}
      : {
          requestAdapter: {
            ...model.requestAdapter,
            patches: model.requestAdapter.patches.map((patch) => ({
              ...patch,
            })),
          },
        }),
    ...(model.source === undefined ? {} : { source: { ...model.source } }),
    ...(model.verificationStatus === undefined
      ? {}
      : { verificationStatus: model.verificationStatus }),
    ...(model.pricingModelId === undefined
      ? {}
      : { pricingModelId: model.pricingModelId }),
    ...(model.aliases === undefined ? {} : { aliases: [...model.aliases] }),
    ...(model.reasoningEfforts === undefined
      ? {}
      : { reasoningEfforts: [...model.reasoningEfforts] }),
    ...(model.overrideBuiltin === undefined
      ? {}
      : { overrideBuiltin: model.overrideBuiltin }),
  };
}

function CatalogModelEditor({
  initial,
  isNew,
  builtin,
  overridden,
  providers,
  initialProviderId,
  existingIds,
  bindings,
  onSaved,
  onRemoved,
}: {
  initial: ModelCatalogDefinition;
  isNew: boolean;
  builtin: boolean;
  overridden: boolean;
  providers: readonly UpstreamProvider[];
  initialProviderId: string | undefined;
  existingIds: readonly string[];
  bindings: readonly ModelAlias[];
  onSaved: (model: ModelCatalogDefinition) => void;
  onRemoved: (id: string) => void;
}): ReactNode {
  const { t } = useI18n();
  const [draft, setDraft] = useState(() => cloneCatalogDraft(initial));
  const [patchText, setPatchText] = useState(() =>
    JSON.stringify(initial.requestAdapter?.patches ?? [], null, 2),
  );
  const [bindingProviderId, setBindingProviderId] = useState(
    initialProviderId ?? providers[0]?.id ?? "",
  );
  const [busy, setBusy] = useState<"save" | "bind" | "remove" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [confirmingRemove, setConfirmingRemove] = useState(false);

  const setCapability = (
    key: keyof ModelCapabilities,
    value: boolean,
  ): void => {
    setDraft((current) => ({
      ...current,
      capabilities:
        key === "vision" || key === "visionInput"
          ? {
              ...current.capabilities,
              vision: value,
              visionInput: value,
            }
          : { ...current.capabilities, [key]: value },
    }));
  };

  const buildDefinition = (): ModelCatalogDefinition | null => {
    const id = draft.id.trim();
    const displayName = draft.displayName.trim();
    if (
      id === "" ||
      displayName === "" ||
      !Number.isInteger(draft.contextWindow) ||
      draft.contextWindow <= 0 ||
      !Number.isInteger(draft.maxOutputTokens) ||
      draft.maxOutputTokens <= 0 ||
      draft.maxOutputTokens > draft.contextWindow ||
      (draft.thinkingConfig.defaultBudgetTokens !== undefined &&
        (!Number.isInteger(draft.thinkingConfig.defaultBudgetTokens) ||
          draft.thinkingConfig.defaultBudgetTokens < 0)) ||
      (draft.thinkingConfig.anthropicAdaptive === true &&
        draft.thinkingConfig.mode === "effort" &&
        draft.thinkingConfig.parameterPath?.trim() === "output_config.effort" &&
        !draft.thinkingConfig.defaultEnabled &&
        !draft.reasoningEfforts?.includes("none")) ||
      (draft.thinkingConfig.mode === "effort" &&
        (draft.thinkingConfig.defaultEffort === undefined ||
          draft.reasoningEfforts === undefined ||
          !draft.reasoningEfforts.includes(
            draft.thinkingConfig.defaultEffort,
          )))
    ) {
      setError(t("models.modelInvalid"));
      return null;
    }
    if (
      isNew &&
      existingIds.some(
        (existing) => existing.toLowerCase() === id.toLowerCase(),
      )
    ) {
      setError(t("models.modelDuplicate", { id }));
      return null;
    }
    const patchValidation = validateRequestPatches(patchText);
    if (!patchValidation.ok) {
      const firstIssue = patchValidation.issues[0];
      setError(
        firstIssue === undefined
          ? t("models.patchInvalid")
          : t(PATCH_VALIDATION_MESSAGES[firstIssue.code], {
              index: (firstIssue.index ?? 0) + 1,
            }),
      );
      return null;
    }
    const manufacturer =
      MODEL_MANUFACTURERS.find((item) => item.id === draft.manufacturerId) ??
      MODEL_MANUFACTURERS.find((item) => item.id === "other");
    const sourceUrl = draft.source?.url.trim() ?? "";
    const visionInput = Boolean(
      draft.capabilities.vision || draft.capabilities.visionInput,
    );
    return {
      ...draft,
      id,
      displayName,
      manufacturerId: manufacturer?.id ?? "other",
      manufacturerLabel: manufacturer?.label ?? draft.manufacturerLabel,
      contextWindow: Math.max(0, Math.trunc(draft.contextWindow)),
      maxOutputTokens: Math.max(0, Math.trunc(draft.maxOutputTokens)),
      capabilities: {
        ...draft.capabilities,
        vision: visionInput,
        visionInput,
        thinking: draft.thinkingConfig.mode !== "unsupported",
      },
      thinkingConfig: {
        ...draft.thinkingConfig,
        ...(draft.thinkingConfig.mode === "effort" &&
        draft.thinkingConfig.parameterPath?.trim() === "output_config.effort"
          ? {}
          : { anthropicAdaptive: undefined }),
        ...(draft.thinkingConfig.defaultBudgetTokens === undefined
          ? {}
          : {
              defaultBudgetTokens: Math.max(
                0,
                Math.trunc(draft.thinkingConfig.defaultBudgetTokens),
              ),
            }),
      },
      requestAdapter: {
        preset: draft.requestAdapter?.preset ?? "auto",
        patches: patchValidation.patches,
      },
      ...(sourceUrl === ""
        ? { source: undefined }
        : {
            source: {
              url: sourceUrl,
              fetchedAt:
                draft.source?.fetchedAt.trim() ||
                new Date().toISOString().slice(0, 10),
            },
          }),
      ...(builtin ? { overrideBuiltin: true } : { overrideBuiltin: false }),
    };
  };

  const save = async (): Promise<void> => {
    const model = buildDefinition();
    if (model === null || busy !== null) return;
    setBusy("save");
    setError(null);
    try {
      onSaved(await upsertUserModelCatalog(model));
    } catch (saveError) {
      console.error("Failed to save model catalog entry", saveError);
      setError(t("models.modelSaveFailed"));
    } finally {
      setBusy(null);
    }
  };

  const bind = async (): Promise<void> => {
    const model = buildDefinition();
    if (model === null || bindingProviderId === "" || busy !== null) return;
    setBusy("bind");
    setError(null);
    try {
      const saved = await upsertUserModelCatalog(model);
      const current = await listModels(bindingProviderId);
      const names = [
        ...new Set([...current.map((item) => item.upstreamModel), saved.id]),
      ];
      const aliases = await setProviderAliases(bindingProviderId, names);
      const created = aliases.find(
        (item) => item.upstreamModel.toLowerCase() === saved.id.toLowerCase(),
      );
      if (created === undefined) {
        setError(t("models.modelBindFailed"));
        return;
      }
      await updateModel({
        ...created,
        enabled: true,
      });
      onSaved(saved);
    } catch (bindError) {
      console.error("Failed to bind model catalog entry", bindError);
      setError(t("models.modelBindFailed"));
    } finally {
      setBusy(null);
    }
  };

  const remove = async (): Promise<void> => {
    if (isNew || busy !== null || (builtin && !overridden)) return;
    setBusy("remove");
    setError(null);
    try {
      await removeUserModelCatalog(initial.id);
      setConfirmingRemove(false);
      onRemoved(initial.id);
    } catch (removeError) {
      console.error("Failed to remove model catalog entry", removeError);
      setError(
        t(builtin ? "models.modelRestoreFailed" : "models.modelDeleteFailed"),
      );
    } finally {
      setBusy(null);
    }
  };

  const thinking = draft.thinkingConfig;
  const efforts = draft.reasoningEfforts ?? [];
  const adapter: RequestAdapterConfig = draft.requestAdapter ?? {
    preset: "auto",
    patches: [],
  };

  return (
    <div className="space-y-4 pb-2">
      <div className="flex items-center justify-between gap-2">
        <span className="rounded-full bg-surface-sunken px-2 py-0.5 text-[10px] text-fg-faint">
          {builtin ? t("models.catalogModel") : t("models.local")}
        </span>
        {!isNew && (!builtin || overridden) && (
          <button
            type="button"
            aria-label={
              builtin
                ? t("models.restoreBuiltin")
                : confirmingRemove
                  ? t("common.confirmDelete")
                  : t("models.delete")
            }
            title={
              builtin
                ? t("models.restoreBuiltinHint")
                : confirmingRemove
                  ? t("common.confirmDelete")
                  : undefined
            }
            onClick={() => {
              if (builtin || confirmingRemove) void remove();
              else setConfirmingRemove(true);
            }}
            disabled={busy !== null}
            className={cn(
              "inline-flex items-center gap-1 rounded-[6px] px-2 py-1 text-[10.5px] disabled:opacity-50",
              builtin
                ? "text-fg-muted hover:bg-tint-hover"
                : "text-danger hover:bg-danger/10",
            )}
          >
            {builtin ? <RotateCcw size={12} /> : <Trash2 size={12} />}
            {builtin
              ? t("models.restoreBuiltin")
              : confirmingRemove
                ? t("common.confirmDelete")
                : t("models.delete")}
          </button>
        )}
      </div>

      {error !== null && (
        <div className="rounded-[8px] border border-danger/30 bg-danger/5 px-2.5 py-2 text-[11px] text-danger">
          {t("chat.status.error")}: {error}
        </div>
      )}

      <InspectorSection title={t("models.columns.model")}>
        <div className="grid grid-cols-2 gap-2.5">
          <EditorField label={t("models.fieldModelId")}>
            <input
              value={draft.id}
              disabled={!isNew}
              onChange={(event) =>
                setDraft((current) => ({ ...current, id: event.target.value }))
              }
              className={cn(editorInputClass, !isNew && "opacity-60")}
            />
          </EditorField>
          <EditorField label={t("models.columns.model")}>
            <input
              value={draft.displayName}
              onChange={(event) =>
                setDraft((current) => ({
                  ...current,
                  displayName: event.target.value,
                }))
              }
              className={editorInputClass}
            />
          </EditorField>
          <EditorField label={t("models.manufacturer")}>
            <EditorSelect
              value={draft.manufacturerId}
              disabled={builtin}
              ariaLabel={t("models.manufacturer")}
              options={MODEL_MANUFACTURERS.map((manufacturer) => ({
                value: manufacturer.id,
                label:
                  manufacturer.id === "other"
                    ? t("models.otherVendor")
                    : manufacturer.label,
              }))}
              onValueChange={(value) => {
                const manufacturer = MODEL_MANUFACTURERS.find(
                  (item) => item.id === value,
                );
                setDraft((current) => ({
                  ...current,
                  manufacturerId: value,
                  manufacturerLabel:
                    manufacturer?.label ?? current.manufacturerLabel,
                }));
              }}
            />
          </EditorField>
          <EditorField label={t("models.columns.type")}>
            <EditorSelect
              value={draft.modality}
              ariaLabel={t("models.columns.type")}
              options={[
                { value: "text", label: t("models.text") },
                { value: "image", label: t("models.image") },
                { value: "video", label: t("models.video") },
                { value: "speech", label: t("models.speech") },
                { value: "transcription", label: t("models.transcription") },
              ]}
              onValueChange={(value) =>
                setDraft((current) => ({
                  ...current,
                  modality: value as ModelModality,
                }))
              }
            />
          </EditorField>
          <EditorField label={t("models.verificationStatus")}>
            <EditorSelect
              value={draft.verificationStatus ?? "unverified"}
              disabled={builtin}
              ariaLabel={t("models.verificationStatus")}
              options={[
                { value: "official-api", label: t("models.verificationOfficialApi") },
                { value: "official-model-card", label: t("models.verificationOfficialCard") },
                { value: "aggregator-reference", label: t("models.verificationAggregator") },
                { value: "unverified", label: t("models.verificationUnverified") },
              ]}
              onValueChange={(value) =>
                setDraft((current) => ({
                  ...current,
                  verificationStatus: value as ModelCatalogVerificationStatus,
                }))
              }
            />
          </EditorField>
          <EditorField label={t("models.fieldContextWindow")}>
            <input
              type="number"
              min={1}
              value={draft.contextWindow}
              onChange={(event) =>
                setDraft((current) => ({
                  ...current,
                  contextWindow: Number(event.target.value),
                }))
              }
              className={editorInputClass}
            />
          </EditorField>
          <EditorField label={t("models.fieldMaxOutputTokens")}>
            <input
              type="number"
              min={1}
              value={draft.maxOutputTokens}
              onChange={(event) =>
                setDraft((current) => ({
                  ...current,
                  maxOutputTokens: Number(event.target.value),
                }))
              }
              className={editorInputClass}
            />
          </EditorField>
          <EditorField label={t("models.fieldPricingModelId")}>
            <input
              value={draft.pricingModelId ?? ""}
              onChange={(event) =>
                setDraft((current) => ({
                  ...current,
                  pricingModelId: event.target.value,
                }))
              }
              className={editorInputClass}
            />
          </EditorField>
        </div>
      </InspectorSection>

      <InspectorSection title={t("models.capabilities")}>
        <div className="grid grid-cols-2 gap-x-5">
          <Capability
            icon={<Cpu size={13} />}
            label={t("models.capabilityTextInput")}
            value={Boolean(draft.capabilities.textInput)}
            onChange={(value) => setCapability("textInput", value)}
          />
          <Capability
            icon={<Image size={13} />}
            label={t("models.capabilityVision")}
            value={Boolean(
              draft.capabilities.vision || draft.capabilities.visionInput,
            )}
            onChange={(value) => setCapability("vision", value)}
          />
          <Capability
            icon={<File size={13} />}
            label={t("models.capabilityFile")}
            value={Boolean(draft.capabilities.fileInput)}
            onChange={(value) => setCapability("fileInput", value)}
          />
          <Capability
            icon={<Video size={13} />}
            label={t("models.capabilityVideo")}
            value={Boolean(draft.capabilities.videoInput)}
            onChange={(value) => setCapability("videoInput", value)}
          />
          <Capability
            icon={<Zap size={13} />}
            label={t("models.capabilityAudioInput")}
            value={Boolean(draft.capabilities.audioInput)}
            onChange={(value) => setCapability("audioInput", value)}
          />
          <Capability
            icon={<Cpu size={13} />}
            label={t("models.capabilityTextOutput")}
            value={Boolean(draft.capabilities.textOutput)}
            onChange={(value) => setCapability("textOutput", value)}
          />
          <Capability
            icon={<Image size={13} />}
            label={t("models.imageGeneration")}
            value={Boolean(draft.capabilities.imageOutput)}
            onChange={(value) => setCapability("imageOutput", value)}
          />
          <Capability
            icon={<Video size={13} />}
            label={t("models.capabilityVideoOutput")}
            value={Boolean(draft.capabilities.videoOutput)}
            onChange={(value) => setCapability("videoOutput", value)}
          />
          <Capability
            icon={<Zap size={13} />}
            label={t("models.capabilityAudioOutput")}
            value={Boolean(draft.capabilities.audioOutput)}
            onChange={(value) => setCapability("audioOutput", value)}
          />
          <Capability
            icon={<Globe size={13} />}
            label={t("models.webSearch")}
            value={Boolean(draft.capabilities.webSearch)}
            onChange={(value) => setCapability("webSearch", value)}
          />
          <Capability
            icon={<Wrench size={13} />}
            label={t("models.capabilityTools")}
            value={draft.capabilities.tools}
            onChange={(value) => setCapability("tools", value)}
          />
          <Capability
            icon={<Cpu size={13} />}
            label={t("models.capabilityStructuredOutput")}
            value={Boolean(draft.capabilities.structuredOutput)}
            onChange={(value) => setCapability("structuredOutput", value)}
          />
          <Capability
            icon={<Zap size={13} />}
            label={t("models.capabilityStreaming")}
            value={Boolean(draft.capabilities.streaming)}
            onChange={(value) => setCapability("streaming", value)}
          />
          <Capability
            icon={<Cpu size={13} />}
            label={t("models.capabilityBatch")}
            value={Boolean(draft.capabilities.batch)}
            onChange={(value) => setCapability("batch", value)}
          />
          <Capability
            icon={<Cpu size={13} />}
            label={t("models.capabilityCaching")}
            value={draft.capabilities.caching}
            onChange={(value) => setCapability("caching", value)}
          />
        </div>
      </InspectorSection>

      <InspectorSection title={t("models.reasoning")}>
        <div className="grid grid-cols-2 gap-2.5">
          <EditorField label={t("models.reasoning")}>
            <EditorSelect
              value={thinking.mode}
              ariaLabel={t("models.reasoning")}
              options={[
                { value: "unsupported", label: t("models.unsupported") },
                { value: "always", label: t("models.always") },
                { value: "toggle", label: t("models.toggle") },
                { value: "effort", label: t("models.effort") },
                { value: "budget", label: t("models.budget") },
              ]}
              onValueChange={(value) => {
                const mode = value as ThinkingMode;
                setDraft((current) => ({
                  ...current,
                  capabilities: {
                    ...current.capabilities,
                    thinking: mode !== "unsupported",
                  },
                  thinkingConfig: {
                    ...current.thinkingConfig,
                    mode,
                    defaultEnabled:
                      mode === "unsupported"
                        ? false
                        : mode === "always"
                          ? true
                        : current.thinkingConfig.defaultEnabled,
                    ...(mode === "effort" &&
                    current.thinkingConfig.defaultEffort === undefined
                      ? { defaultEffort: "medium" as const }
                      : {}),
                  },
                  ...(mode === "effort" &&
                  (current.reasoningEfforts === undefined ||
                    current.reasoningEfforts.length === 0)
                    ? { reasoningEfforts: ["medium"] as const }
                    : {}),
                }));
              }}
            />
          </EditorField>
          <EditorField label={t("models.fieldParameterPath")}>
            <input
              value={thinking.parameterPath ?? ""}
              disabled={
                thinking.mode === "unsupported" || thinking.mode === "always"
              }
              onChange={(event) =>
                setDraft((current) => ({
                  ...current,
                  thinkingConfig: {
                    ...current.thinkingConfig,
                    parameterPath: event.target.value,
                  },
                }))
              }
              className={cn(
                editorInputClass,
                (thinking.mode === "unsupported" ||
                  thinking.mode === "always") &&
                  "opacity-60",
              )}
            />
          </EditorField>
          <EditorField label={t("models.fieldDefaultEnabled")}>
            <div
              aria-disabled={
                thinking.mode === "unsupported" || thinking.mode === "always"
              }
              className={cn(
                "flex h-8 items-center justify-end rounded-[8px] border border-border bg-surface-field px-2",
                (thinking.mode === "unsupported" ||
                  thinking.mode === "always") &&
                  "opacity-60",
              )}
            >
              <Switch
                checked={
                  thinking.mode === "unsupported"
                    ? false
                    : thinking.mode === "always"
                      ? true
                    : thinking.defaultEnabled
                }
                onCheckedChange={(value) => {
                  if (
                    thinking.mode === "unsupported" ||
                    thinking.mode === "always"
                  )
                    return;
                  setDraft((current) => ({
                    ...current,
                    thinkingConfig: {
                      ...current.thinkingConfig,
                      defaultEnabled: value,
                    },
                  }));
                }}
                aria-label={t("models.fieldDefaultEnabled")}
              />
            </div>
          </EditorField>
          <EditorField label={t("models.fieldDefaultEffort")}>
            <EditorSelect
              value={thinking.defaultEffort ?? ""}
              disabled={thinking.mode !== "effort"}
              ariaLabel={t("models.fieldDefaultEffort")}
              options={[
                { value: "", label: t("models.defaultOption") },
                ...(efforts.length > 0 ? efforts : REASONING_EFFORTS).map(
                  (effort) => ({ value: effort, label: effort }),
                ),
              ]}
              onValueChange={(value) => {
                const defaultEffort =
                  value === ""
                    ? undefined
                    : (value as ReasoningEffort);
                setDraft((current) => ({
                  ...current,
                  thinkingConfig: {
                    ...current.thinkingConfig,
                    defaultEffort,
                  },
                  ...(defaultEffort === undefined ||
                  current.reasoningEfforts?.includes(defaultEffort)
                    ? {}
                    : {
                        reasoningEfforts: [
                          ...(current.reasoningEfforts ?? []),
                          defaultEffort,
                        ],
                      }),
                }));
              }}
            />
          </EditorField>
          <EditorField label={t("models.fieldDefaultBudgetTokens")}>
            <input
              type="number"
              min={0}
              value={thinking.defaultBudgetTokens ?? ""}
              disabled={
                thinking.mode !== "budget" && thinking.mode !== "toggle"
              }
              onChange={(event) =>
                setDraft((current) => ({
                  ...current,
                  thinkingConfig: {
                    ...current.thinkingConfig,
                    defaultBudgetTokens:
                      event.target.value === ""
                        ? undefined
                        : Number(event.target.value),
                  },
                }))
              }
              className={cn(
                editorInputClass,
                thinking.mode !== "budget" &&
                  thinking.mode !== "toggle" &&
                  "opacity-60",
              )}
            />
          </EditorField>
        </div>
        <div className="mt-3 text-[10.5px] text-fg-faint">
          {t("models.effort")}
        </div>
        <div className="mt-1 flex flex-wrap gap-2">
          {REASONING_EFFORTS.map((effort) => (
            <label
              key={effort}
              className={cn(
                "flex items-center gap-1 text-[10.5px] text-fg-muted",
                thinking.mode !== "effort" && "opacity-50",
              )}
            >
              <input
                type="checkbox"
                disabled={thinking.mode !== "effort"}
                checked={efforts.includes(effort)}
                onChange={(event) =>
                  setDraft((current) => {
                    const reasoningEfforts = event.target.checked
                      ? [...(current.reasoningEfforts ?? []), effort]
                      : (current.reasoningEfforts ?? []).filter(
                          (item) => item !== effort,
                        );
                    const defaultEffort =
                      current.thinkingConfig.defaultEffort;
                    return {
                      ...current,
                      reasoningEfforts,
                      thinkingConfig: {
                        ...current.thinkingConfig,
                        ...(defaultEffort === undefined ||
                        reasoningEfforts.includes(defaultEffort)
                          ? {}
                          : { defaultEffort: reasoningEfforts[0] }),
                      },
                    };
                  })
                }
              />
              {effort}
            </label>
          ))}
        </div>
        <p className="mt-2 text-[10.5px] leading-[1.45] text-fg-faint">
          {t("models.reasoningHint")}
        </p>
      </InspectorSection>

      <InspectorSection title={t("models.requestAdapter")}>
        <EditorSelect
          value={adapter.preset}
          ariaLabel={t("models.requestAdapter")}
          options={[
            { value: "auto", label: t("models.autoDetect") },
            { value: "anthropic", label: "Anthropic" },
            { value: "openai-chat", label: "OpenAI Chat" },
            { value: "openai-responses", label: "OpenAI Responses" },
            { value: "custom", label: t("models.customPatch") },
          ]}
          onValueChange={(value) =>
            setDraft((current) => ({
              ...current,
              requestAdapter: {
                preset: value as RequestAdapterConfig["preset"],
                patches: current.requestAdapter?.patches ?? [],
              },
            }))
          }
        />
        <EditorField label="JSON Patch">
          <textarea
            value={patchText}
            onChange={(event) => setPatchText(event.target.value)}
            spellCheck={false}
            className="apple-field selectable mt-2 min-h-[112px] w-full resize-y rounded-[8px] border border-border bg-surface-field p-2 font-mono text-[10.5px] text-fg outline-none focus:border-fg-faint"
          />
        </EditorField>
        <p className="mt-1 text-[10.5px] leading-[1.45] text-fg-faint">
          {t("models.adapterHint")}
        </p>
      </InspectorSection>

      <InspectorSection title={t("models.officialSource")}>
        <div className="grid grid-cols-[1fr_150px] gap-2.5">
          <EditorField label="URL">
            <input
              value={draft.source?.url ?? ""}
              disabled={builtin}
              onChange={(event) =>
                setDraft((current) => ({
                  ...current,
                  source: {
                    url: event.target.value,
                    fetchedAt: current.source?.fetchedAt ?? "",
                  },
                }))
              }
              className={cn(editorInputClass, builtin && "opacity-60")}
            />
          </EditorField>
          <EditorField label={t("models.fieldFetchedAt")}>
            <input
              type="date"
              value={draft.source?.fetchedAt ?? ""}
              disabled={builtin}
              onChange={(event) =>
                setDraft((current) => ({
                  ...current,
                  source: {
                    url: current.source?.url ?? "",
                    fetchedAt: event.target.value,
                  },
                }))
              }
              className={cn(editorInputClass, builtin && "opacity-60")}
            />
          </EditorField>
        </div>
      </InspectorSection>

      {providers.length > 0 && (
        <InspectorSection title={t("models.protocolSettings")}>
          <ModelProtocolEditor bindings={bindings} providers={providers} />
        </InspectorSection>
      )}

      {providers.length > 0 && (
        <InspectorSection title={t("models.saveToConnection")}>
          <div className="flex gap-2">
            <div className="min-w-0 flex-1">
              <EditorSelect
                value={bindingProviderId}
                ariaLabel={t("models.saveToConnection")}
                options={providers.map((provider) => ({
                  value: provider.id,
                  label: provider.name,
                }))}
                onValueChange={setBindingProviderId}
              />
            </div>
            <button
              type="button"
              onClick={() => void bind()}
              disabled={busy !== null || bindingProviderId === ""}
              className="rounded-[8px] border border-border px-3 text-[11px] text-fg-muted hover:bg-tint-hover disabled:opacity-50"
            >
              {busy === "bind" ? t("models.binding") : t("models.bindAndSave")}
            </button>
          </div>
          <p className="mt-1 text-[10.5px] text-fg-faint">
            {t("models.unboundHint")}
          </p>
        </InspectorSection>
      )}

      <div className="flex justify-end border-t border-hairline pt-3">
        <button
          type="button"
          onClick={() => void save()}
          disabled={busy !== null}
          className="rounded-[8px] bg-accent px-4 py-1.5 text-[11.5px] text-accent-fg disabled:opacity-50"
        >
          {busy === "save" ? t("models.binding") : t("models.saveCatalog")}
        </button>
      </div>
    </div>
  );
}

function EditorField({
  label,
  children,
}: {
  label: string;
  children: ReactNode;
}): ReactNode {
  return (
    <label className="block min-w-0">
      <span className="mb-1 block text-[10.5px] text-fg-faint">{label}</span>
      {children}
    </label>
  );
}

function InspectorSection({
  title,
  children,
}: {
  title: string;
  children: ReactNode;
}): ReactNode {
  return (
    <section className="border-t border-hairline pt-3">
      <h4 className="mb-2 text-[11.5px] text-fg-faint">{title}</h4>
      {children}
    </section>
  );
}
function Capability({
  icon,
  label,
  value,
  onChange,
}: {
  icon: ReactNode;
  label: string;
  value: boolean;
  onChange: (v: boolean) => void;
}): ReactNode {
  return (
    <div className="flex items-center gap-2 py-1">
      <span className="text-icon">{icon}</span>
      <span className="min-w-0 flex-1 text-[11.5px] text-fg-muted">
        {label}
      </span>
      <Switch checked={value} onCheckedChange={onChange} aria-label={label} />
    </div>
  );
}
