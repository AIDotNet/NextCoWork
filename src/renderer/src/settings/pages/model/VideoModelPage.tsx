/**
 * 「设置 › 模型 › 视频生成」—— 本轮接进来的视频供应商与型号。
 *
 * ## 与图片页的三处**刻意不同**
 *
 * 1. **左列列出所有已收录的供应商,而不只是已配置的。** 图片页只列配置过的
 *    (它的左列语义是"哪家在提供图片模型")。视频这边用户问的是"我到底能用什么",
 *    所以未配置的也要看得见 —— 否则他无从知道我们支持哪家,而"支持但没配"
 *    与"根本不支持"在界面上就没有区别。
 *
 * 2. **型号行带"可调用 / 待核对"两态。** 一个型号可能在目录里(我们知道它存在),
 *    但我们**还没核对过它的请求形状**(见 `video-profiles.ts` 的 `verification`)。
 *    那种条目照列但不可选,并写明原因 —— 这是「不猜」在界面上的样子。
 *
 * 3. **页脚那两句的说明不同。** 生图关掉 = 工具没了;视频关掉 = 不能**新建**,
 *    已提交的任务继续收。那张卡片上的取消才是"停某一条"的地方
 *    (理由全文见 `shared/domain/settings.ts` 的 `videoGenerationEnabled`)。
 *
 * ★ 供应商记录与聊天那条**分开**(id 带 `video-` 前缀):视频接口既不是
 * openai-chat 也不是 anthropic,而且地址常不同(见 `video-provider-presets.ts`
 * 文件头)。所以这里新建的是另一条 provider,不会覆盖用户在聊天页配的那条。
 */
import { ChevronDown, Info, Loader2, Plus, Search, Settings2, Trash2, Video } from 'lucide-react'
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import type { CredentialInfo, ModelAlias, UpstreamProvider } from '../../../../../shared/domain/provider'
import { isVideoModelAlias } from '../../../../../shared/domain/provider'
import { selectModelBinding } from '../../../../../shared/domain/model-selection'
import type { AppSettings } from '../../../../../shared/domain/settings'
import type { VideoAdapterId, VideoProfile } from '../../../../../shared/domain/video-generation'
import { isCallableProfile, VIDEO_PROFILES, videoProfile } from '../../../../../shared/domain/video-profiles'
import { VIDEO_PROVIDER_PRESETS, type VideoProviderPreset } from '../../../../../shared/domain/video-provider-presets'
import { BUILTIN_MODEL_CATALOG } from '../../../../../shared/domain/model-catalog-inventory'
import { Button } from '../../../components/arc/button/button'
import { EmptyState } from '../../../components/arc/empty-state/empty-state'
import { Select } from '../../../components/ui/Select'
import { TextInput } from '../../../components/ui/TextInput'
import { Switch } from '../../../components/arc/switch/switch'
import { ProviderModelMenu, type ProviderModelMenuRow } from '../../../components/ProviderModelMenu'
import { cn } from '../../../lib/cn'
import { useI18n, type TranslationKey } from '../../../i18n'
import { useModelsStore } from '../../../stores/models'
import { getCredentialInfo, removeProvider, setCredential, setProviderAliases, updateModel, upsertProvider } from '../../../services/provider'
import type { SettingsPageProps } from '../../props'
import { ProviderAvatar } from './ProviderAvatar'
import { customProviderId } from './custom-provider'
import { roleModelChoice, selectableProviders, providerAliasOptions } from './enabled-models'

// 自定义连接复用已实现的 API Key 协议,不把尚未接通或需要签名的接口当作兼容格式。
const CUSTOM_VIDEO_PRESETS = VIDEO_PROVIDER_PRESETS.filter((preset) =>
  preset.credential === 'api-key' && preset.models.some((entry) => {
    const profile = videoProfile(entry.profileId)
    return isCallableProfile(profile) && profile.adapter === preset.adapter
  })
)

interface VideoModelDraft {
  key: string
  model: string
  profileId: string
  endpointId: string
}

/** 一个左侧条目的来源:出厂预设,还是用户自己建的一条 provider。 */
interface VideoProviderRow {
  id: string
  name: string
  preset?: VideoProviderPreset
  provider?: UpstreamProvider
}

export function VideoModelPage({ settings, patch }: Pick<SettingsPageProps, 'settings' | 'patch'>): ReactNode {
  const { t } = useI18n()
  const providers = useModelsStore((s) => s.providers)
  const models = useModelsStore((s) => s.models)
  const loaded = useModelsStore((s) => s.loaded)
  const load = useModelsStore((s) => s.load)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [catalogOpen, setCatalogOpen] = useState(false)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { void load() }, [load])

  /*
    ★ 左列 = **预设 ∪ 已配置的视频 provider**,按 id 去重。
    预设在前(它们是我们核对过的),用户自建的追加在后面 —— 顺序稳定,
    而"打开这一页默认选中第一个"因此不会跳到一条他没见过的东西上。
  */
  const rows = useMemo<VideoProviderRow[]>(() => {
    const out: VideoProviderRow[] = []
    const seen = new Set<string>()
    for (const preset of VIDEO_PROVIDER_PRESETS) {
      const configured = providers.find((p) => p.id === preset.id)
      out.push({ id: preset.id, name: configured?.name ?? preset.name, preset, ...(configured === undefined ? {} : { provider: configured }) })
      seen.add(preset.id)
    }
    for (const provider of providers) {
      if (seen.has(provider.id) || provider.videoGeneration === undefined) continue
      // 用户自建、或从别处导入的视频连接 —— 也列出来(否则它无法管理)
      out.push({ id: provider.id, name: provider.name, provider })
    }
    return out
  }, [providers])

  const selected = rows.find((row) => row.id === selectedId) ?? rows[0] ?? null

  const addProvider = async (preset: VideoProviderPreset): Promise<void> => {
    if (preset.models.length === 0 && preset.verification === 'unverified') {
      /*
        ★ 型号表为空的预设(硅基流动)**不建连接** —— 一条没有可调用型号的连接
        在聊天页、在视频页都只是噪音。它仍然在目录里可见(见 `catalogOpen` 那段),
        这是"看得见但可选性为零"的正确形态。
      */
      setError(t('videoGen.notWired'))
      return
    }
    setBusy(preset.id)
    setError(null)
    try {
      await upsertProvider({
        id: preset.id,
        name: preset.name,
        // ★ 聊天协议字段仍然要有一个合法值(它是必填),但视频走的是
        //   `videoGeneration.adapter`,这个字段对视频连接没有意义。
        protocol: 'openai-chat',
        baseUrl: preset.baseUrl,
        credentialRef: `provider:${preset.id}`,
        priority: 60,
        enabled: true,
        videoGeneration: {
          adapter: preset.adapter,
          baseUrl: preset.baseUrl,
          ...(preset.region === undefined ? {} : { region: preset.region }),
          ...(preset.s3 === undefined ? {} : { s3: { bucket: '', prefix: 'ncw-video', region: preset.s3.regionHint } })
        }
      })
      // 把这家的型号落成别名 —— 视频页与 `selectModelBinding` 都按别名找它。
      const currentIds = models.filter((m) => m.providerId === preset.id).map((m) => m.upstreamModel)
      const wanted = [...new Set([...currentIds, ...preset.models.map((entry) => entry.model)])]
      const aliases = await setProviderAliases(preset.id, wanted)
      // ★ 标成视频模态并把 profile 写在绑定上 —— 模型列表端点不提供这两样。
      await Promise.all(aliases
        .filter((alias) => preset.models.some((entry) => entry.model === alias.upstreamModel))
        .map((alias) => {
          const entry = preset.models.find((item) => item.model === alias.upstreamModel)
          return updateModel({
            ...alias,
            modality: 'video',
            capabilities: { ...alias.capabilities, textInput: false, textOutput: false, videoOutput: true },
            ...(entry === undefined ? {} : { video: { profileId: entry.profileId, ...(entry.endpointId === undefined ? {} : { endpointId: entry.endpointId }) } })
          })
        }))
      setSelectedId(preset.id)
      setCatalogOpen(false)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setBusy(null)
    }
  }

  return (
    <div className="flex min-h-0 min-w-0 flex-1 flex-col gap-3">
      <div className="flex min-h-0 min-w-0 flex-1 gap-4">
        <ProviderList rows={rows} models={models} disabled={busy !== null} selectedId={catalogOpen ? null : (selected?.id ?? null)} onAdd={() => setCatalogOpen(true)} onSelect={(id) => { setSelectedId(id); setCatalogOpen(false) }} />
        {catalogOpen ? (
          <ProviderCatalog
            rows={rows}
            busy={busy}
            error={error}
            onClose={() => setCatalogOpen(false)}
            onAdd={addProvider}
            onBusyChange={(active) => setBusy(active ? 'custom' : null)}
            onAdded={(id) => { setSelectedId(id); setCatalogOpen(false) }}
          />
        ) : selected === null ? (
          <div className="min-w-0 flex-1 rounded-[12px] border border-border bg-canvas px-3 py-3">
            <div className="mb-3 flex items-start gap-2 rounded-[8px] bg-tint px-3 py-2.5 text-[11.5px] leading-[1.6] text-fg-muted">
              <Info size={14} className="mt-0.5 shrink-0 text-icon" />
              <span>{t('videoGen.catalogHint')}</span>
            </div>
            <EmptyState icon={<Video size={22} />} title={t('videoGen.noVideoModels')} description={t('videoGen.addProvider')} />
          </div>
        ) : (
          <ProviderDetail
            key={selected.id}
            row={selected}
            models={models}
            busy={busy}
            error={error}
            onAdd={() => setCatalogOpen(true)}
            onBusyChange={(active) => setBusy(active ? selected.id : null)}
          />
        )}
      </div>
      <VideoGenToggleRow settings={settings} patch={patch} />
      <VideoGenModelRow settings={settings} patch={patch} models={models} providers={providers} loaded={loaded} />
    </div>
  )
}

/** 左列。与图片页同款版式(缩略图 + 供应商名 + 当前主型号)。 */
function ProviderList({ rows, models, disabled, selectedId, onAdd, onSelect }: { rows: readonly VideoProviderRow[]; models: readonly ModelAlias[]; disabled: boolean; selectedId: string | null; onAdd: () => void; onSelect: (id: string) => void }): ReactNode {
  const { t } = useI18n()
  return (
    <div className="flex w-[216px] shrink-0 flex-col">
      <div className="flex items-start gap-2 px-1 pb-2">
        <div className="min-w-0 flex-1">
          <p className="text-[13px] text-fg">{t('videoGen.providers')}</p>
          <p className="mt-0.5 text-[11.5px] leading-[1.5] text-fg-faint">{t('videoGen.catalogHint')}</p>
        </div>
        <button type="button" aria-label={t('videoGen.addProvider')} disabled={disabled} onClick={onAdd} className="mt-0.5 flex size-6 items-center justify-center rounded-[8px] text-icon hover:bg-tint disabled:opacity-40">
          <Plus size={15} />
        </button>
      </div>
      <ul className="scroll-thin min-h-0 flex-1 space-y-0.5 overflow-y-auto pr-1">
        {rows.map((row) => (
          <li key={row.id}>
            <button
              type="button"
              disabled={disabled}
              onClick={() => onSelect(row.id)}
              className={cn('flex w-full items-center gap-2 rounded-[8px] px-1.5 py-2 text-left disabled:opacity-40', row.id === selectedId ? 'bg-tint' : 'hover:bg-tint/60')}
            >
              <ProviderAvatar name={row.name} id={row.id} />
              <span className="min-w-0 flex-1">
                <span className="block truncate text-[12.5px] text-fg">{row.name}</span>
                <span className="mt-0.5 block truncate text-[11.5px] text-fg-faint">
                  {row.provider === undefined ? t('videoGen.notConfigured') : (models.find((model) => model.providerId === row.id && isVideoModelAlias(model))?.alias ?? t('videoGen.modelsEmpty'))}
                </span>
              </span>
            </button>
          </li>
        ))}
      </ul>
    </div>
  )
}

/** 右侧:这一家的型号表 + 连接状态。 */
function ProviderDetail({ row, models, busy, error, onAdd, onBusyChange }: { row: VideoProviderRow; models: readonly ModelAlias[]; busy: string | null; error: string | null; onAdd: () => void; onBusyChange: (active: boolean) => void }): ReactNode {
  const { t } = useI18n()
  const [editing, setEditing] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [localError, setLocalError] = useState<string | null>(null)
  const [credentialInfo, setCredentialInfo] = useState<CredentialInfo | null>(null)
  const bound = models.filter((model) => model.providerId === row.id && isVideoModelAlias(model))
  const adapter = row.provider?.videoGeneration?.adapter ?? row.preset?.adapter
  const canConfigure = CUSTOM_VIDEO_PRESETS.some((preset) => preset.adapter === adapter)
  const entries = row.provider === undefined
    ? row.preset?.models ?? []
    : bound.map((model) => ({ model: model.upstreamModel, profileId: model.video?.profileId ?? '' }))

  useEffect(() => {
    if (row.provider === undefined || editing) return
    let alive = true
    void getCredentialInfo(row.id)
      .then((info) => { if (alive) setCredentialInfo(info) })
      .catch((cause: unknown) => { if (alive) setLocalError(cause instanceof Error ? cause.message : String(cause)) })
    return () => { alive = false }
  }, [row.provider?.id, editing])

  const remove = async (): Promise<void> => {
    if (busy !== null) return
    onBusyChange(true)
    setLocalError(null)
    try {
      await removeProvider(row.id)
    } catch (cause) {
      setLocalError(cause instanceof Error ? cause.message : String(cause))
      setConfirmDelete(false)
    } finally {
      onBusyChange(false)
    }
  }

  if (editing && row.provider !== undefined) {
    return (
      <div className="flex min-h-0 min-w-0 flex-1 flex-col overflow-hidden rounded-[12px] border border-border bg-canvas">
        <div className="border-b border-hairline px-4 py-3 text-[13px] text-fg">{t('videoGen.configureProvider')}</div>
        <div className="scroll-thin min-h-0 flex-1 overflow-y-auto">
          <VideoProviderForm
            provider={row.provider}
            preset={row.preset}
            models={models}
            onBusyChange={onBusyChange}
            onCancel={() => setEditing(false)}
            onSaved={() => { setEditing(false); setLocalError(null) }}
          />
        </div>
      </div>
    )
  }

  const shownError = localError ?? error
  return (
    <div className="flex min-h-0 min-w-0 flex-1 flex-col overflow-hidden rounded-[12px] border border-border bg-canvas">
      <div className="flex items-center gap-2 border-b border-hairline px-4 py-3">
        <ProviderAvatar name={row.name} id={row.id} />
        <span className="min-w-0 flex-1 truncate text-[13px] text-fg">{row.name}</span>
        {busy === row.id && <Loader2 size={13} className="animate-spin text-fg-faint" />}
        {row.provider === undefined ? (
          <Button type="button" size="sm" variant="secondary" disabled={busy !== null} onClick={onAdd}>
            <Plus size={12} />
            {t('videoGen.addProvider')}
          </Button>
        ) : canConfigure && (
          <Button type="button" size="sm" variant="secondary" disabled={busy !== null} onClick={() => setEditing(true)}>
            <Settings2 size={12} />
            {t('videoGen.configureProvider')}
          </Button>
        )}
        {row.preset?.verification === 'unverified' && (
          <span className="rounded-full bg-tint px-2 py-0.5 text-[10.5px] text-fg-muted">{t('videoGen.notWired')}</span>
        )}
      </div>
      {row.preset?.notes !== undefined && (
        <p className="border-b border-hairline px-4 py-2 text-[11.5px] leading-[1.6] text-fg-faint">{row.preset.notes}</p>
      )}
      {shownError !== null && <p role="alert" className="border-b border-hairline bg-danger/10 px-4 py-2 text-[12px] text-danger">{shownError}</p>}

      <div className="scroll-thin min-h-0 flex-1 overflow-y-auto px-4 py-3">
        {row.provider !== undefined && (
          <div className="mb-3 space-y-1 text-[11.5px] text-fg-muted">
            <p className="truncate">{t('provider.apiAddress')}: {row.provider.videoGeneration?.baseUrl ?? row.preset?.baseUrl}</p>
            <p>{t('provider.apiKey')}: {credentialInfo === null ? '…' : credentialInfo.hasKey ? t('provider.configured') : t('videoGen.notConfigured')}</p>
          </div>
        )}
        <p className="mb-2 text-[12px] text-fg-muted">{t('videoGen.modelsTitle')}</p>
        {entries.length === 0 ? (
          <EmptyState className="py-10" icon={<Video size={20} />} title={t('videoGen.modelsEmpty')} description="" />
        ) : (
          <table className="w-full border-collapse text-[11.5px]">
            <thead>
              <tr className="border-b border-hairline text-fg-faint">
                <th className="py-2 text-left font-normal">{t('models.columns.model')}</th>
                <th className="py-2 text-left font-normal">{t('videoGen.capabilities')}</th>
                <th className="py-2 text-left font-normal">{t('models.columns.status')}</th>
              </tr>
            </thead>
            <tbody>
              {entries.map((entry) => {
                const profile = entry.profileId === '' ? undefined : videoProfile(entry.profileId)
                const callable = isCallableProfile(profile) && profile.adapter === adapter
                return (
                  <tr key={entry.model} className="border-b border-hairline last:border-0">
                    <td className="max-w-[240px] py-2">
                      <div className="truncate text-fg">{BUILTIN_MODEL_CATALOG.find((c) => c.id === entry.model)?.displayName ?? entry.model}</div>
                      <div className="truncate font-mono text-[10px] text-fg-faint">{entry.model}</div>
                    </td>
                    <td className="py-2"><CapabilityBadges profile={profile} /></td>
                    <td className="py-2">
                      <span className={cn('text-[10px]', callable ? 'text-accent' : 'text-fg-faint')}>
                        {callable ? t('models.enabled') : t('videoGen.notWired')}
                      </span>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        )}
        {row.preset !== undefined && row.preset.models.length > 0 && (
          <p className="mt-2 text-[11px] leading-[1.6] text-fg-faint">{row.preset.docsUrl}</p>
        )}
      </div>
      {row.preset === undefined && row.provider !== undefined && (
        <div className="flex items-center gap-2 border-t border-hairline px-4 py-3">
          <p className="min-w-0 flex-1 text-[11.5px] text-fg-faint">{t('provider.deleteHint')}</p>
          {confirmDelete && <Button type="button" variant="secondary" size="sm" disabled={busy !== null} onClick={() => setConfirmDelete(false)}>{t('common.cancel')}</Button>}
          <Button type="button" size="sm" variant={confirmDelete ? 'danger' : 'secondary'} disabled={busy !== null} onClick={() => { if (confirmDelete) void remove(); else setConfirmDelete(true) }}>
            <Trash2 size={12} />
            {confirmDelete ? t('common.confirmDelete') : t('provider.delete')}
          </Button>
        </div>
      )}
    </div>
  )
}

/** 能力徽章。**按 profile 的 capabilities 画**(不是按"我们实现没实现")。 */
function CapabilityBadges({ profile }: { profile: VideoProfile | undefined }): ReactNode {
  const { t } = useI18n()
  if (profile === undefined) return <span className="text-fg-faint">—</span>
  const caps: Array<TranslationKey> = []
  if (profile.capabilities.textToVideo) caps.push('videoGen.cap.t2v')
  if (profile.capabilities.imageToVideo) caps.push('videoGen.cap.i2v')
  if (profile.capabilities.firstLastFrame) caps.push('videoGen.cap.frames')
  if (profile.capabilities.editVideo) caps.push('videoGen.cap.edit')
  if (profile.capabilities.extendVideo) caps.push('videoGen.cap.extend')
  return (
    <div className="flex max-w-[200px] flex-wrap gap-1">
      {caps.map((key) => (
        <span key={key} className="rounded-[4px] bg-surface-sunken px-1 text-[10px] text-fg-muted">{t(key)}</span>
      ))}
    </div>
  )
}

/** 供应商目录(右侧的"添加"面板)。 */
function ProviderCatalog({ rows, busy, error, onClose, onAdd, onAdded, onBusyChange }: { rows: readonly VideoProviderRow[]; busy: string | null; error: string | null; onClose: () => void; onAdd: (preset: VideoProviderPreset) => Promise<void>; onAdded: (id: string) => void; onBusyChange: (active: boolean) => void }): ReactNode {
  const { t } = useI18n()
  const models = useModelsStore((s) => s.models)
  const [query, setQuery] = useState('')
  const [custom, setCustom] = useState(false)
  const list = VIDEO_PROVIDER_PRESETS.filter((preset) => `${preset.name} ${preset.notes ?? ''}`.toLowerCase().includes(query.trim().toLowerCase()))
  return (
    <div className="flex min-h-0 min-w-0 flex-1 flex-col overflow-hidden rounded-[12px] border border-border bg-canvas">
      <div className="flex items-center gap-2 border-b border-hairline px-4 py-3">
        <span className="flex-1 text-[13px] text-fg">{t(custom ? 'videoGen.customProvider' : 'videoGen.addProvider')}</span>
        <Button type="button" variant="secondary" size="sm" disabled={busy !== null} onClick={onClose}>{t('common.close')}</Button>
      </div>
      {custom ? (
        <div className="scroll-thin min-h-0 flex-1 overflow-y-auto">
          <VideoProviderForm models={models} onBusyChange={onBusyChange} onCancel={() => setCustom(false)} onSaved={onAdded} />
        </div>
      ) : (
      <div className="scroll-thin min-h-0 flex-1 overflow-y-auto px-3 py-3">
        <div className="mb-3 flex items-start gap-2 rounded-[8px] bg-tint px-3 py-2.5 text-[11.5px] leading-[1.6] text-fg-muted">
          <Info size={14} className="mt-0.5 shrink-0 text-icon" />
          <span>{t('videoGen.catalogHint')}</span>
        </div>
        {error !== null && <p className="mb-3 rounded-[8px] bg-danger/10 px-3 py-2 text-[11.5px] text-danger">{error}</p>}
        <div className="flex items-center gap-2">
          <div className="min-w-0 flex-1">
            <TextInput size="sm" value={query} onChange={setQuery} placeholder={t('videoGen.searchProvider')} ariaLabel={t('videoGen.searchProvider')} icon={<Search size={13} />} />
          </div>
          <Button type="button" size="sm" variant="secondary" disabled={busy !== null} onClick={() => setCustom(true)}>
            <Settings2 size={13} />
            {t('videoGen.customProvider')}
          </Button>
        </div>
        <div className="mt-3 grid grid-cols-2 gap-2">
          {list.map((preset) => {
            const configured = rows.find((row) => row.id === preset.id)?.provider !== undefined
            const empty = preset.models.length === 0
            return (
              <button
                key={preset.id}
                type="button"
                disabled={configured || busy !== null || empty}
                onClick={() => void onAdd(preset)}
                className={cn('min-w-0 rounded-[12px] border border-border px-3 py-2.5 text-left transition-colors', configured || empty ? 'bg-tint/60 opacity-70' : 'hover:bg-tint')}
              >
                <div className="flex items-center gap-2">
                  <ProviderAvatar name={preset.name} id={preset.id} size="sm" />
                  <span className="min-w-0 flex-1 truncate text-[12.5px] text-fg">{preset.name}</span>
                  {preset.verification === 'unverified' && (
                    <span className="rounded-full bg-tint px-1.5 py-0.5 text-[10px] text-fg-muted">{t('videoGen.notWired')}</span>
                  )}
                </div>
                <p className="mt-1 truncate text-[11px] text-fg-muted">{preset.models.map((entry) => entry.model).join(' / ') || '—'}</p>
                <p className="mt-1 truncate text-[10.5px] text-fg-faint">{configured ? t('models.added') : (preset.notes ?? '')}</p>
              </button>
            )
          })}
        </div>
      </div>
      )}
    </div>
  )
}

/** 创建与编辑共用一份表单;密钥只通过凭据频道提交,不进入 provider/model 记录。 */
function VideoProviderForm({ provider, preset, models, onCancel, onSaved, onBusyChange }: { provider?: UpstreamProvider; preset?: VideoProviderPreset; models: readonly ModelAlias[]; onCancel: () => void; onSaved: (id: string) => void; onBusyChange: (active: boolean) => void }): ReactNode {
  const { t } = useI18n()
  const initialAdapter = provider?.videoGeneration?.adapter ?? preset?.adapter ?? 'xai-video'
  const [name, setName] = useState(provider?.name ?? '')
  const [baseUrl, setBaseUrl] = useState(provider?.videoGeneration?.baseUrl ?? preset?.baseUrl ?? provider?.baseUrl ?? '')
  const [adapter, setAdapter] = useState<VideoAdapterId>(initialAdapter)
  const [region, setRegion] = useState(provider?.videoGeneration?.region ?? preset?.region ?? '')
  const [keyDraft, setKeyDraft] = useState('')
  const [credentialInfo, setCredentialInfo] = useState<CredentialInfo | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const requestInFlight = useRef(false)
  const createdId = useRef<string | null>(null)
  // 分步写入中新增但尚未标成视频的别名,在失败重试时仍归这份表单管理。
  const stagedModels = useRef(new Set<string>())
  const nextRow = useRef(0)
  const editableModels = preset === undefined
  const compatiblePreset = CUSTOM_VIDEO_PRESETS.find((item) => item.adapter === adapter)
  const profiles = VIDEO_PROFILES.filter((profile) => profile.adapter === adapter && isCallableProfile(profile))
  const endpointModel = adapter === 'fal-queue' || adapter === 'replicate-predictions'
  const [modelDrafts, setModelDrafts] = useState<VideoModelDraft[]>(() => {
    const bound = provider === undefined ? [] : models.filter((model) => model.providerId === provider.id && isVideoModelAlias(model))
    return bound.length > 0
      ? bound.map((model) => ({ key: `existing:${model.alias}`, model: model.upstreamModel, profileId: model.video?.profileId ?? '', endpointId: model.video?.endpointId ?? '' }))
      : [{ key: 'initial', model: '', profileId: VIDEO_PROFILES.find((profile) => profile.adapter === initialAdapter && isCallableProfile(profile))?.id ?? '', endpointId: '' }]
  })

  useEffect(() => {
    if (provider === undefined) return
    let alive = true
    void getCredentialInfo(provider.id)
      .then((info) => { if (alive) setCredentialInfo(info) })
      .catch((cause: unknown) => { if (alive) setError(cause instanceof Error ? cause.message : String(cause)) })
    return () => { alive = false }
  }, [provider?.id])

  const changeAdapter = (value: string): void => {
    const next = CUSTOM_VIDEO_PRESETS.find((item) => item.adapter === value)
    if (next === undefined) return
    setAdapter(next.adapter)
    setRegion(next.region ?? '')
    const profileId = VIDEO_PROFILES.find((profile) => profile.adapter === next.adapter && isCallableProfile(profile))?.id ?? ''
    // 保留用户填写的地址与模型 ID;只同步请求模板,不悄悄切回官方服务。
    setModelDrafts((current) => current.map((entry) => ({ ...entry, profileId, endpointId: '' })))
  }

  const updateDraft = (key: string, patch: Partial<VideoModelDraft>): void => {
    setModelDrafts((current) => current.map((entry) => entry.key === key ? { ...entry, ...patch } : entry))
  }

  const submit = async (): Promise<void> => {
    if (requestInFlight.current) return
    setError(null)
    if (name.trim() === '') { setError(t('models.customNameRequired')); return }
    const address = baseUrl.trim().replace(/\/+$/u, '')
    try {
      const url = new URL(address)
      if (url.protocol !== 'https:' && url.protocol !== 'http:') throw new Error('invalid protocol')
    } catch {
      setError(t('videoGen.invalidBaseUrl'))
      return
    }
    const wanted = modelDrafts.map((entry) => ({ ...entry, model: entry.model.trim(), endpointId: entry.endpointId.trim() }))
    if (editableModels) {
      if (wanted.length === 0 || wanted.some((entry) => entry.model === '')) {
        setError(t('videoGen.modelsRequired'))
        return
      }
      if (new Set(wanted.map((entry) => entry.model)).size !== wanted.length) {
        setError(t('provider.modelAlreadyAdded'))
        return
      }
      if (wanted.some((entry) => {
        const profile = videoProfile(entry.profileId)
        return !isCallableProfile(profile) || profile.adapter !== adapter
      })) {
        setError(t('videoGen.invalidProfile'))
        return
      }
    }
    const existingProviders = useModelsStore.getState().providers
    const id = provider?.id ?? createdId.current ?? `video-${customProviderId(name, existingProviders.filter((item) => item.id.startsWith('video-')).map((item) => item.id.slice(6)))}`
    const currentModels = useModelsStore.getState().models.filter((model) => model.providerId === id)
    const preserved = currentModels.filter((model) => !isVideoModelAlias(model) && !stagedModels.current.has(model.upstreamModel))
    if (editableModels && wanted.some((entry) => preserved.some((model) => model.upstreamModel === entry.model))) {
      setError(t('provider.modelAlreadyAdded'))
      return
    }

    requestInFlight.current = true
    setSaving(true)
    onBusyChange(true)
    try {
      await upsertProvider({
        ...(provider ?? {}),
        id,
        name: name.trim(),
        protocol: provider?.protocol ?? 'openai-chat',
        // 混合供应商的聊天地址不跟着视频地址改变。
        baseUrl: provider?.baseUrl ?? address,
        credentialRef: provider?.credentialRef ?? `provider:${id}`,
        priority: provider?.priority ?? 60,
        enabled: provider?.enabled ?? true,
        videoGeneration: { adapter, baseUrl: address, ...(region.trim() === '' ? {} : { region: region.trim() }) }
      })
      createdId.current = id
      setBaseUrl(address)
      if (keyDraft.trim() !== '') {
        setCredentialInfo(await setCredential(id, keyDraft.trim()))
        setKeyDraft('')
      }
      if (editableModels) {
        for (const entry of wanted) {
          if (!currentModels.some((model) => model.upstreamModel === entry.model)) stagedModels.current.add(entry.model)
        }
        const aliases = await setProviderAliases(id, [...preserved.map((model) => model.upstreamModel), ...wanted.map((entry) => entry.model)])
        await Promise.all(wanted.map((entry) => {
          const alias = aliases.find((model) => model.upstreamModel === entry.model)!
          const profile = videoProfile(entry.profileId)!
          return updateModel({
            ...alias,
            modality: 'video',
            capabilities: {
              ...alias.capabilities,
              tools: false,
              vision: profile.capabilities.imageToVideo || profile.capabilities.firstLastFrame,
              thinking: false,
              caching: false,
              textInput: true,
              textOutput: false,
              imageOutput: false,
              videoOutput: true,
              visionInput: profile.capabilities.imageToVideo || profile.capabilities.firstLastFrame,
              videoInput: profile.capabilities.editVideo || profile.capabilities.extendVideo
            },
            video: { profileId: entry.profileId, ...(entry.endpointId === '' ? {} : { endpointId: entry.endpointId }) }
          })
        }))
      }
      onSaved(id)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      requestInFlight.current = false
      setSaving(false)
      onBusyChange(false)
    }
  }

  return (
    <div className="space-y-4 px-4 py-4">
      <p className="text-[11.5px] leading-[1.6] text-fg-muted">{t('videoGen.customHint')}</p>
      {error !== null && <p role="alert" className="rounded-[8px] bg-danger/10 px-3 py-2 text-[11.5px] text-danger">{error}</p>}
      <label className="block space-y-1.5 text-[12px] text-fg-muted">
        <span>{t('provider.name')}</span>
        <TextInput value={name} onChange={setName} ariaLabel={t('provider.name')} placeholder={t('models.customNamePlaceholder')} disabled={saving} />
      </label>
      <div className="space-y-1.5 text-[12px] text-fg-muted">
        <p>{t('videoGen.adapter')}</p>
        <Select value={adapter} options={CUSTOM_VIDEO_PRESETS.map((item) => ({ value: item.adapter, label: item.name }))} onValueChange={changeAdapter} ariaLabel={t('videoGen.adapter')} disabled={saving || !editableModels} inModal />
      </div>
      <label className="block space-y-1.5 text-[12px] text-fg-muted">
        <span>{t('provider.apiAddress')}</span>
        <TextInput value={baseUrl} onChange={setBaseUrl} ariaLabel={t('provider.apiAddress')} inputMode="url" placeholder={compatiblePreset?.baseUrl} disabled={saving} />
        <p className="text-[11px] leading-[1.6] text-fg-faint">{t('videoGen.addressHint', { url: compatiblePreset?.baseUrl ?? '' })}</p>
      </label>
      {adapter === 'dashscope-video' && (
        <label className="block space-y-1.5 text-[12px] text-fg-muted">
          <span>{t('videoGen.region')}</span>
          <TextInput value={region} onChange={setRegion} ariaLabel={t('videoGen.region')} placeholder="cn-beijing" disabled={saving} />
        </label>
      )}
      <label className="block space-y-1.5 text-[12px] text-fg-muted">
        <span>{t('provider.apiKey')}{credentialInfo?.hasKey && <span className="ml-2 text-[11px] text-accent">{t('provider.configured')}</span>}</span>
        <TextInput value={keyDraft} onChange={setKeyDraft} ariaLabel={t('provider.apiKey')} type="password" placeholder={t('provider.pasteKey')} disabled={saving} />
        <p className="text-[11px] leading-[1.6] text-fg-faint">{t(credentialInfo?.hasKey ? 'videoGen.keepKeyHint' : 'videoGen.keyHint')}</p>
      </label>
      {editableModels && (
        <div className="space-y-2">
          <div className="flex items-center justify-between gap-2">
            <p className="text-[12px] text-fg-muted">{t('videoGen.modelsTitle')}</p>
            <Button type="button" size="sm" variant="secondary" disabled={saving} onClick={() => {
              nextRow.current += 1
              setModelDrafts((current) => [...current, { key: `new-${String(nextRow.current)}`, model: '', profileId: profiles[0]?.id ?? '', endpointId: '' }])
            }}>
              <Plus size={12} />
              {t('provider.addModel')}
            </Button>
          </div>
          {modelDrafts.map((entry, index) => (
            <div key={entry.key} className="space-y-2 rounded-[10px] border border-border p-3">
              <div className="flex items-center gap-2">
                <div className="min-w-0 flex-1">
                  <TextInput value={entry.model} onChange={(model) => updateDraft(entry.key, { model })} ariaLabel={`${t('provider.modelIdLabel')} ${String(index + 1)}`} placeholder={compatiblePreset?.models.find((model) => model.profileId === entry.profileId)?.model ?? 'model-id'} disabled={saving} />
                </div>
                <button type="button" aria-label={`${t('provider.deleteModel')} ${String(index + 1)}`} disabled={saving} onClick={() => setModelDrafts((current) => current.filter((model) => model.key !== entry.key))} className="flex size-7 shrink-0 items-center justify-center rounded-[8px] text-fg-faint hover:bg-danger/10 hover:text-danger disabled:opacity-40"><Trash2 size={13} /></button>
              </div>
              <div className="space-y-1 text-[11.5px] text-fg-muted">
                <p>{t('videoGen.profile')}</p>
                <Select
                  value={entry.profileId}
                  options={[
                    ...profiles.map((profile) => ({ value: profile.id, label: profile.label })),
                    ...(entry.profileId !== '' && !profiles.some((profile) => profile.id === entry.profileId) ? [{ value: entry.profileId, label: `${entry.profileId} · ${t('videoGen.notWired')}` }] : [])
                  ]}
                  onValueChange={(profileId) => updateDraft(entry.key, { profileId })}
                  ariaLabel={`${t('videoGen.profile')} ${String(index + 1)}`}
                  disabled={saving}
                  inModal
                />
              </div>
              {endpointModel && <TextInput value={entry.endpointId} onChange={(endpointId) => updateDraft(entry.key, { endpointId })} ariaLabel={`${t('videoGen.endpointId')} ${String(index + 1)}`} placeholder={entry.model || t('videoGen.endpointId')} disabled={saving} />}
            </div>
          ))}
          <p className="text-[11px] leading-[1.6] text-fg-faint">{t(endpointModel ? 'videoGen.endpointHint' : 'videoGen.profileHint')}</p>
        </div>
      )}
      <div className="flex justify-end gap-2">
        <Button type="button" variant="secondary" size="sm" disabled={saving} onClick={onCancel}>{t('common.cancel')}</Button>
        <Button type="button" size="sm" variant="primary" disabled={saving} onClick={() => void submit()}>
          {saving ? <Loader2 size={12} className="animate-spin" /> : undefined}
          {t('common.save')}
        </Button>
      </div>
    </div>
  )
}

/**
 * 页脚「对话视频生成」开关。
 *
 * ★★ **说明行必须写清"只挡新建"** —— 这是与生图那一条唯一的语义差别,
 * 而它正好是用户最容易误解的地方("我关了它,之前那条是不是白花钱了?")。
 * 文案在 `videoGen.enabledHint` 里,这里不再重复。
 */
function VideoGenToggleRow({ settings, patch }: { settings: AppSettings; patch: SettingsPageProps['patch'] }): ReactNode {
  const { t } = useI18n()
  return (
    <div className="flex shrink-0 items-center gap-3 border-t border-hairline px-1 pt-3">
      <div className="min-w-0 flex-1">
        <p className="text-[12.5px] text-fg">{t('videoGen.enabled')}</p>
        <p className="mt-0.5 text-[11px] text-fg-faint">{t('videoGen.enabledHint')}</p>
      </div>
      <Switch aria-label={t('videoGen.enabled')} checked={settings.videoGenerationEnabled} onCheckedChange={(videoGenerationEnabled) => patch({ videoGenerationEnabled })} />
    </div>
  )
}

/**
 * 页脚「对话视频生成使用的模型」。
 *
 * ★ 与生图那条**同构**:只列视频模型,类型是"选了就固定用它"(不设自动档)。
 * ★ 但可选集合**更窄**:只有"存在已核对 profile"的绑定才进列表 ——
 * 一条 404 的选项在生图上只是失败,在视频上要等几分钟才知道错了。
 */
function VideoGenModelRow({ settings, patch, models, providers, loaded }: { settings: AppSettings; patch: SettingsPageProps['patch']; models: readonly ModelAlias[]; providers: readonly UpstreamProvider[]; loaded: boolean }): ReactNode {
  const { t } = useI18n()
  const videoModels = models.filter((model) => {
    const profile = model.video?.profileId === undefined ? undefined : videoProfile(model.video.profileId)
    const generation = providers.find((provider) => provider.id === model.providerId)?.videoGeneration
    return isVideoModelAlias(model) && isCallableProfile(profile) && generation?.adapter === profile.adapter
  })
  const binding = settings.videoModel === '' ? undefined : selectModelBinding(videoModels, providers, settings.videoModel, settings.videoModelProviderId)
  const missing = settings.videoModel !== '' && binding === undefined
  const choice = roleModelChoice(videoModels, providers, settings.videoModel, settings.videoModelProviderId)
  const providerName = providers.find((p) => p.id === choice.providerId)?.name
  const triggerLabel = choice.alias === ''
    ? t('videoGen.modelPick')
    : providerName === undefined ? choice.alias : `${choice.alias} · ${providerName}`
  const rows: ProviderModelMenuRow[] = selectableProviders(videoModels, providers, choice.providerId).map((p) => {
    const aliasOptions = providerAliasOptions(videoModels, p.id)
    return {
      id: p.id,
      label: p.name,
      description: t('chat.availableModels', { count: aliasOptions.length }),
      selected: p.id === choice.providerId,
      models: aliasOptions.map((option) => ({ value: option.value, label: option.label, selected: p.id === choice.providerId && option.value === choice.alias }))
    }
  })
  return (
    <div className="flex shrink-0 items-center gap-3 border-t border-hairline px-1 pt-3">
      <div className="min-w-0 flex-1">
        <p className="text-[12.5px] text-fg">{t('videoGen.model')}</p>
        <p className="mt-0.5 text-[11px] text-fg-faint">
          {settings.videoModel === '' ? t('videoGen.modelUnset') : missing ? t('videoGen.modelMissing') : t('videoGen.modelHint')}
        </p>
      </div>
      <div className="w-[240px] shrink-0">
        <ProviderModelMenu
          trigger={<><span className="min-w-0 flex-1 truncate">{triggerLabel}</span><ChevronDown size={12} className="shrink-0 text-fg-faint" /></>}
          triggerClassName={cn('apple-field group flex h-7 w-full items-center gap-1.5 rounded-[8px] border border-border', 'bg-surface-field px-2 text-left text-[11.5px] text-fg outline-none', 'transition-[background-color,border-color,box-shadow] duration-150', 'hover:bg-tint focus-visible:border-fg-faint focus-visible:ring-2 focus-visible:ring-fg-faint/15')}
          className="w-full"
          align="end"
          width={280}
          ariaLabel={t('videoGen.model')}
          menuLabel={t('videoGen.modelPick')}
          loaded={loaded}
          loadingLabel={t('common.loading')}
          emptyLabel={t('videoGen.noVideoModels')}
          rows={rows}
          // ★ patch 成对无条件写:只给别名会留下"新别名 + 旧供应商"的脏配对
          onSelectModel={(providerId, alias) => patch({ videoModel: alias, videoModelProviderId: providerId })}
        />
      </div>
    </div>
  )
}
