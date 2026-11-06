/**
 * 左列「启用的模型」的判断逻辑。
 *
 * ★ **参考图那一列的每一行是一个「供应商」,不是一个模型** —— 行标题是供应商名
 * (RoutinAI / NewMax Gateway),副标题才是它当前的主模型(`claude-fable-5-1` /
 * `doubao-seed-2.1-pro`)。右侧面板那张「模型优先级」列表才是这家底下的模型们。
 * 照字面把它做成模型列表,右侧面板就没有主语了。
 */
import { isChatModelAlias, type ModelAlias, type UpstreamProvider } from '../../../../../shared/domain/provider'
import {
  modelSelectionKey,
  selectModelBinding
} from '../../../../../shared/domain/model-selection'

export interface ProviderEntry {
  provider: UpstreamProvider
  /** 这家提供的别名，保持 store 的优先级顺序。 */
  aliases: readonly ModelAlias[]
  /** 副标题显示的那个。没有别名时为 null —— 显示成「未配置模型」而不是空一行 */
  primaryAlias: string | null
  /**
   * ★ 语义是「**这家能提供当前的默认模型**」,不是「它排在第一位」。
   *
   * 两种情形:
   * - 用户**锁定了供应商**(`defaultModelProviderId` 有值)→ 只有那一家挂徽章。
   *   请求只会发给它,别家挂徽章就是假话。
   * - 没锁定 → **每一家都挂**。参考图把「默认」做成首行的徽章,是因为那边一个
   *   别名只有一个来源;我们的故障切换轴恰恰是同一别名多个供应商
   *   (`router.ts` 的候选链),这时只给第一家挂,等于宣称另外几家跟这次请求
   *   无关,而它们其实随时会接手。
   */
  isDefault: boolean
}

export function providerEntries(
  providers: readonly UpstreamProvider[],
  models: readonly ModelAlias[],
  defaultModel: string,
  defaultModelProviderId?: string
): ProviderEntry[] {
  return providers.map((provider) => {
    const aliases = models.filter((m) => m.providerId === provider.id)
    return {
      provider,
      aliases,
      primaryAlias: aliases[0]?.alias ?? null,
      // 空串 = 「跟随对话」,那时没有任何一家是默认的
      isDefault: defaultModel !== '' && aliases.some((m) => m.alias === defaultModel)
        && (defaultModelProviderId === undefined || provider.id === defaultModelProviderId)
    }
  })
}

/**
 * 「默认模型」/「默认子代理」/「审核模型」几个下拉框的选项。
 *
 * ★★ **一条绑定一个选项,不再按别名去重。** 别名的主键是 `(provider_id, alias)`,
 * 同一个别名可以挂在多家上 —— 而这几个设置项现在存的是**别名 + 供应商**一对,
 * 「哪一家」正是用户要选的东西(他选了 Codex 就不该被路由到 RoutinAI)。
 *
 * ★ 曾经必须按别名去重,是因为那时 `value` 就是裸别名:两个 `value` 相同的
 * `Select.Item` 都会认为自己被选中,各自把文本 portal 进触发器的 value 节点,
 * 于是那一格显示成 `gpt-6-astragpt-6-astra`(实测)。现在 `value` 是
 * `providerId/alias`,天然唯一,重复项本身不存在了 —— **不要把去重加回来**,
 * 加回来等于又把「选哪一家」这个选项从界面上抹掉。
 *
 * 别名只有一家提供时标签保持裸别名(不制造视觉噪音);多家撞名时才加供应商后缀,
 * 否则用户面对两条一模一样的文本仍然没法分辨。
 *
 * 保序:`listResolvedModels()` 已经按「供应商顺序 → priority → 别名」排好,这里不重排。
 */
export function modelOptions(
  models: readonly ModelAlias[],
  providers: readonly UpstreamProvider[] = []
): { value: string; label: string }[] {
  /*
    ★ 需求:这个构建器的调用方**全是对话语境**(默认模型/子代理/审核/目标判定/
    工作区默认模型/Hooks),选择器一律只列文本模型 —— 图片模型选中了也发不出
    对话请求,它出现在这个下拉里就是一个选了就废的选项。判据收口在
    `isChatModelAlias`,别在调用点再写一份。
  */
  const chatModels = models.filter(isChatModelAlias)
  const shared = new Set<string>()
  const seen = new Set<string>()
  for (const model of chatModels) {
    if (seen.has(model.alias)) shared.add(model.alias)
    seen.add(model.alias)
  }
  return chatModels.map((model) => {
    const providerName = providers.find((p) => p.id === model.providerId)?.name ?? model.providerId
    return {
      value: modelSelectionKey(model.providerId, model.alias),
      label: shared.has(model.alias) ? `${model.alias} · ${providerName}` : model.alias
    }
  })
}

/**
 * 头像里那个字。
 *
 * ★ 用 `[...name]` 而不是 `name[0]` —— emoji 和一部分汉字是代理对,
 * 按 UTF-16 取第 0 位会切出半个字符,渲染成一个 ▯。
 */
export function avatarInitial(name: string): string {
  const first = [...name.trim()][0]
  if (first === undefined) return '?'
  return first.toUpperCase()
}

// ══════════════════════════════════════════════════════════════
// 「默认模型」/「默认子代理」那两栏 —— 两级下拉:先供应商,再它的模型
// ══════════════════════════════════════════════════════════════

/*
  ★★ 为什么是两级,而不是一个「别名 · 供应商」的合并下拉。

  合并下拉的标签在只有一家提供该别名时是**裸别名**(见 `modelOptions`),于是
  绝大多数情况下那一格根本不显示供应商 —— 而这两栏存的偏偏是 `(别名, 供应商)`
  一对,「哪一家」是它的一半内容。用户看到 `deepseek-v4-pro` 无从知道自己钉的是
  按量那条线还是订阅那条线,而两者**计费不同**。

  拆成两级之后:第一级永远把那一半摆在明面上,第二级的候选天然被压到那一家
  提供的别名 —— 于是「A 家的别名 + B 家的锁」这个形状在界面上根本拼不出来。
*/

/**
 * 第一级的候选。
 *
 * ★ 过滤条件必须和 `modelBindingsFor` 一致(启用的供应商 + 至少一条启用的别名):
 * 列出一家挑不出模型的供应商,用户选中它之后第二级是空的,那一步就成了死路。
 *
 * @param keep 当前已经存着的那一家。**即使它已经不可选也要留在列表里** ——
 *   供应商被停用/别名被删之后,把它从下拉里抹掉会让那一格显示成空白,
 *   用户看见的是「我没配过」,而设置里其实还钉着它。留着才有得改。
 */
export function selectableProviders(
  models: readonly ModelAlias[],
  providers: readonly UpstreamProvider[],
  keep: string
): UpstreamProvider[] {
  return providers.filter(
    (p) =>
      p.id === keep ||
      (p.enabled && models.some((m) => m.providerId === p.id && m.enabled !== false))
  )
}

/**
 * 第二级的候选:这一家提供的别名。
 *
 * ★ `value` 是**裸别名**,不是 `providerId/alias` —— 供应商已经由第一级钉死了,
 * 再编一次码只会让两级之间多一处要对齐的约定。同一家里别名不重复
 * (`(provider_id, alias)` 是主键),所以裸别名在这一级天然唯一。
 */
export function providerAliasOptions(
  models: readonly ModelAlias[],
  providerId: string
): { value: string; label: string }[] {
  if (providerId === '') return []
  return models
    .filter((m) => m.providerId === providerId && m.enabled !== false)
    .map((m) => ({ value: m.alias, label: m.alias }))
}

/**
 * 存着的那一对 `(别名, 供应商)` 在两级下拉里各显示成什么。
 *
 * ★ `modelProviderId` 缺席(老数据、或者用户就是没钉过)时**显示解析出来的那一家**,
 * 而不是显示空白:`selectModelBinding` 和路由器 `candidates()` 是同一段代码,
 * 所以那一家就是此刻真正会收到请求的那一家 —— 显示它是**实话**。
 *
 * ★★ 但**只显示、不回写**。回写等于替用户把这一对钉死,而钉死会关掉故障切换
 * (`modelBindingsFor` 在钉住时不做任何回退)—— 那是个用户没要求过的行为改变,
 * 而且只有在第一家挂掉时才会以「怎么不切了」的形式暴露出来。用户下次动这两个
 * 下拉时自然就钉上了,那才是他自己的选择。
 */
export function roleModelChoice(
  models: readonly ModelAlias[],
  providers: readonly UpstreamProvider[],
  model: string,
  modelProviderId: string | undefined
): { providerId: string; alias: string } {
  if (model.trim() === '') return { providerId: '', alias: '' }
  const binding = selectModelBinding(models, providers, model, modelProviderId)
  // 查不到 = 这一对悬空了(那家被删了/别名改名了)。原样显示,别装作没配过 ——
  // 主进程 `repairModelSelection` 会在下一次供应商写入时修好它,在那之前
  // 用户至少看得见自己钉的是什么。
  return { providerId: binding?.providerId ?? modelProviderId ?? '', alias: model }
}
