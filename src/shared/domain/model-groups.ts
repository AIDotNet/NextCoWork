/**
 * 「供应商 → 它的模型」分组 —— 设置页供应商列表与所有模型选择器的**唯一数据源**。
 *
 * ★ 需求:之前同一份 `providers` / `models` 被五处各自过滤一遍 ——
 *   - 设置页左列(`providerEntries`):全部供应商、全部别名;
 *   - 设置页三个角色下拉(`selectableProviders` + `providerAliasOptions`):
 *     启用的供应商 + 启用的别名;
 *   - 输入框模型药丸:**只按模态过滤,不看 `enabled`** —— 于是在设置页停用的供应商 /
 *     别名仍然出现在输入框里,选了也会被路由器拒掉;
 *   - 图片 / 视频页:各自再抄一遍。
 *   规则写了五份就会漂成五种。现在全部从这里取,差异只剩显式的选项。
 *
 * ★ 排序不在这里做:`provider:list` 已经是供应商顺序,`listResolvedModels()`
 *   已经按「供应商顺序 → priority → 别名」排好。这里只保序地分组。
 */
import type { ModelAlias, UpstreamProvider } from './provider'

/**
 * ★★ 「这条绑定现在能不能发请求」的**唯一判据**:供应商存在且启用,别名没被停用。
 *
 * 之前散在十来处,写法有三种:`m.enabled !== false`(漏了供应商)、
 * `provider?.enabled !== false`(供应商被删时当作可用)、`provider?.enabled === true`。
 * 前两种都会在下拉里摆出一个选了必然失败的选项。路由器(`router.listModels`)、
 * `modelBindingsFor`、各处选择器现在都走这一条。
 */
export function isBindingAvailable(
  model: Pick<ModelAlias, 'enabled'>,
  provider: Pick<UpstreamProvider, 'enabled'> | undefined
): boolean {
  return provider?.enabled === true && model.enabled !== false
}

/** 扁平的可用绑定列表,保持 `models` 的顺序。`filter` 是模态过滤 */
export function availableBindings(
  providers: readonly UpstreamProvider[],
  models: readonly ModelAlias[],
  filter?: (model: ModelAlias) => boolean
): ModelAlias[] {
  const byId = new Map(providers.map((p) => [p.id, p]))
  return models.filter((m) => isBindingAvailable(m, byId.get(m.providerId)) && (filter === undefined || filter(m)))
}

export interface ProviderModelGroup {
  provider: UpstreamProvider
  /** 这家在当前过滤条件下的别名,保持 store 顺序 */
  models: ModelAlias[]
  /**
   * 现在能不能从这家选出一个模型:供应商启用,且至少一条启用的别名。
   * 设置页用它把「停用 / 空」的那几家画成弱化态;选择器只列 `available` 的。
   */
  available: boolean
}

export interface ProviderModelGroupOptions {
  /** 模态过滤(对话 / 图片 / 视频)。缺省 = 不过滤 */
  filter?: (model: ModelAlias) => boolean
  /**
   * 列出所有供应商(包括停用的、一个模型都没有的),每家下面也保留停用的别名。
   * 设置页要的是这一份 —— 停用的东西也得留在那里才改得回来。
   */
  includeUnavailable?: boolean
  /**
   * 选择器专用:当前存着的那一家**即使已经不可选也留在列表里**。
   * 供应商被停用 / 别名被删之后把它抹掉,触发器上就是一个找不到出处的名字。
   */
  keepProviderId?: string
}

export function providerModelGroups(
  providers: readonly UpstreamProvider[],
  models: readonly ModelAlias[],
  options: ProviderModelGroupOptions = {}
): ProviderModelGroup[] {
  const { filter, includeUnavailable = false, keepProviderId } = options
  const out: ProviderModelGroup[] = []
  for (const provider of providers) {
    const own = models.filter((m) => m.providerId === provider.id && (filter === undefined || filter(m)))
    const enabled = own.filter((m) => isBindingAvailable(m, provider))
    const available = enabled.length > 0
    if (includeUnavailable) {
      out.push({ provider, models: own, available })
    } else if (available || provider.id === keepProviderId) {
      out.push({ provider, models: enabled, available })
    }
  }
  return out
}

/** 选择器上显示的名字:目录里有展示名就用展示名,否则是别名本身 */
export function modelDisplayName(model: Pick<ModelAlias, 'alias' | 'displayName'>): string {
  const name = model.displayName?.trim()
  return name === undefined || name === '' ? model.alias : name
}

/**
 * 单层下拉(`Select`)的选项 —— 一条绑定一项,`value` 是 `providerId/alias`。
 *
 * 需求:审核模型、目标判定、工作区默认模型、Hooks、子代理、定时任务、压缩模型
 * 这些下拉之前各自拼选项,过滤规则和标签格式各不相同。现在统一在这里:
 * - 只列可用绑定(`isBindingAvailable`) + 模态过滤;
 * - `keepKey`:当前存着的那一对**即使不可用也留着**,否则下拉显示空白,
 *   用户会以为没配过;
 * - 标签一律 `展示名 · 供应商`。`providerLabel: 'onConflict'` 时只有同一个别名
 *   挂在多家上才带供应商(审核 / 目标判定那几栏不想要视觉噪音)。
 */
export function modelBindingOptions(
  providers: readonly UpstreamProvider[],
  models: readonly ModelAlias[],
  options: {
    filter?: (model: ModelAlias) => boolean
    keepKey?: string
    providerLabel?: 'always' | 'onConflict'
  } = {}
): { value: string; label: string }[] {
  const { filter, keepKey, providerLabel = 'always' } = options
  const byId = new Map(providers.map((p) => [p.id, p]))
  const picked = models.filter((m) =>
    (filter === undefined || filter(m)) &&
    (isBindingAvailable(m, byId.get(m.providerId)) || (keepKey !== undefined && keepKey !== '' && bindingKey(m) === keepKey))
  )
  const seen = new Set<string>()
  const shared = new Set<string>()
  for (const m of picked) {
    if (seen.has(m.alias)) shared.add(m.alias)
    seen.add(m.alias)
  }
  return picked.map((m) => {
    const name = modelDisplayName(m)
    const providerName = byId.get(m.providerId)?.name ?? m.providerId
    return {
      value: bindingKey(m),
      label: providerLabel === 'always' || shared.has(m.alias) ? `${name} · ${providerName}` : name
    }
  })
}

/** 与 `model-selection.ts` 的 `modelSelectionKey` 同一编码;这里不 import 以免成环 */
function bindingKey(m: Pick<ModelAlias, 'providerId' | 'alias'>): string {
  return `${m.providerId}/${m.alias}`
}

/** 搜索:展示名 / 别名 / 上游名 / 供应商名任一命中。空查询全部命中 */
export function matchesModelQuery(model: ModelAlias, provider: UpstreamProvider, query: string): boolean {
  const q = query.trim().toLowerCase()
  if (q === '') return true
  return [modelDisplayName(model), model.alias, model.upstreamModel, provider.name]
    .some((s) => s.toLowerCase().includes(q))
}
