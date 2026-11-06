/**
 * 「用户选中的那个模型」到底落在哪一条 `(providerId, alias)` 绑定上。
 *
 * ★★ **这个模块存在的唯一理由:药丸上显示的那家,必须和路由器实际发请求的那家,
 * 由同一段代码算出来。** 在它之前,渲染层是 `models.find((m) => m.alias === x)`
 * (取数组里第一条),而 `upstream/router.ts` 的 `candidates()` 是按
 * `provider.priority` 升序取第一条 —— 两者只是**碰巧**一致(别名表本来就大致按
 * provider 顺序排)。一旦不一致,界面说 A、请求发给 B,而且不报任何错。
 *
 * 别名的主键是 `(provider_id, alias)`(`db/repo.ts` 那条 `PRIMARY KEY`),
 * **同一个别名可以挂在多家上** —— 那正是故障切换的轴。所以「alias 字符串」
 * 从来就不足以定位一条绑定,缺的那一半必须一路带着走。
 */
import type { ModelAlias, UpstreamProvider } from './provider'
import { isChatModelAlias } from './provider'

/**
 * ★ 第四个参数是**必填**的(值可以是 `undefined`)。
 *
 * 写成 `modelProviderId?: string` 的话,新加的调用点漏传不会报错,而漏传的后果
 * 恰好是「退回按优先级猜一家」—— 也就是这个模块要消灭的那个 bug,悄无声息地
 * 复活一次。必填参数是编译期哨兵:你可以传 `undefined`,但你必须**写出来**,
 * 于是你会想一下为什么这里没有 providerId。
 *
 * @param modelProviderId 用户显式选定的供应商。`undefined` = 没指定过
 *   (历史数据、子代理 frontmatter 里的裸别名),此时按优先级择优 —— 这是本模块
 *   引入之前的全局行为,必须逐字保留,否则每个老用户的每一次发送都会改变落点。
 */
export function modelBindingsFor(
  models: readonly ModelAlias[],
  providers: readonly UpstreamProvider[],
  alias: string,
  modelProviderId: string | undefined
): ModelAlias[] {
  const byId = new Map(providers.map((p) => [p.id, p]))
  const list: { model: ModelAlias; priority: number }[] = []
  for (const model of models) {
    if (model.alias !== alias || model.enabled === false) continue
    // ★ 钉住时**不做任何回退**:那家没了就是空数组,而不是「换一家给你」。
    //   静默换家正是用户报的这个 bug —— 他选了 Codex,请求发给了 RoutinAI。
    if (modelProviderId !== undefined && model.providerId !== modelProviderId) continue
    const provider = byId.get(model.providerId)
    if (provider?.enabled !== true) continue
    list.push({ model, priority: provider.priority })
  }
  // 与 `upstream/router.ts` 的 `candidates()` 逐字一致:只按 priority 升序,
  // 同分时靠 sort 的稳定性保留输入顺序。**任何一边改了排序,两边就又分家了。**
  list.sort((a, b) => a.priority - b.priority)
  return list.map((x) => x.model)
}

/** 实际生效的那一条。查不到 = 用户选的那家已经不提供这个别名了。 */
export function selectModelBinding(
  models: readonly ModelAlias[],
  providers: readonly UpstreamProvider[],
  alias: string,
  modelProviderId: string | undefined
): ModelAlias | undefined {
  return modelBindingsFor(models, providers, alias, modelProviderId)[0]
}

/**
 * 下拉框的 `value` / Map 的键。**只用在这两处**,不落盘、不进 IPC。
 * 空别名代表未指定模型，下拉框必须与值为 `''` 的选项一致；否则会显示空白。
 *
 * ★ 分隔符是 `/`,而且**只切第一个** —— 顺序必须是 `providerId/alias` 而不是
 * 反过来。别名里真的会有斜杠(`openrouter/claude-sonnet-4`),而 providerId
 * 是 slug 且 `ipc/provider.ts` 的 `upsertProvider` 明确拒收带 `/` 的 id。
 * 于是「第一个斜杠」永远是分隔符,别名后面有几个斜杠都原样保留。
 *
 * 不用 `data.ts` / `model-catalog.ts` 那套 NUL 分隔符:那两处是纯内存 Map 键,
 * 而这个值要进 DOM 的 `Select.value`。裸 NUL 进属性不可靠,还会让 grep 把
 * 整个源文件判成 binary(于是搜不到、也不报错)。`providerId/alias` 也正是
 * `Composer.tsx` 那个列表 key 已经在用的写法。
 */
export function modelSelectionKey(modelProviderId: string | undefined, alias: string): string {
  // 需求：未配置与「跟随对话」共用空值；编码成 '/' 会让 Select 找不到选项，触发器显示空白。
  if (alias === '') return ''
  return `${modelProviderId ?? ''}/${alias}`
}

export function parseModelSelectionKey(key: string): {
  modelProviderId: string | undefined
  alias: string
} {
  const at = key.indexOf('/')
  // 没有分隔符 = 一个裸别名(旧的持久化值,或调用方直接塞了字符串),按「没指定」处理。
  if (at < 0) return { modelProviderId: undefined, alias: key }
  const providerId = key.slice(0, at)
  return { modelProviderId: providerId === '' ? undefined : providerId, alias: key.slice(at + 1) }
}

/**
 * 子代理该用哪个「别名 + 供应商」。
 *
 * ★★ 抽成一个函数,是因为这条规则**必须成对决定**,而写成两条独立的 `??`
 * (`def.model ?? parent.model` 和 `def.providerId ?? parent.providerId`)
 * 看起来同样自然、却会拼出**「A 家的别名 + B 家的锁」**:候选集返回空,
 * 然后报一条指着 B 的错,而 B 跟这次调用根本没关系,用户无从排查。
 *
 * 三档来源,**越具体的越优先**:
 *
 * 1. `declared` —— 这个子代理自己声明的。它是对某一个代理的明确安排,
 *    盖过任何全局默认。
 * 2. `configured` —— 设置页那个「默认子代理」。★ 它排在父亲**前面**才是这一栏
 *    存在的理由:子代理是拿来跑量的(`perSessionLimit` 默认 4),用户配它就是
 *    为了让那几路别跟着主力模型走。排在父亲后面的话,只有「主力模型也没配」
 *    时才轮得到它 —— 也就是几乎永远不生效。
 * 3. `parent` —— 都没配时沿用父 run,连同它锁的那家。
 *
 * @param declared `agents/<name>.md` frontmatter 里的那一对。`modelProviderId`
 *   缺席(从 Claude Code 粘过来的文件里**总是**缺席)= 只认别名,按优先级择优,
 *   而**不是**沿用父亲锁的那家 —— 父亲那家未必提供这个别名,拼出来的就是
 *   上面说的「A 家的别名 + B 家的锁」。
 * @param configured `settings.subagent`。空别名 = 这一栏是「跟随对话」,当作没配。
 *   ★ 调用方有责任先确认它**当前还指得到一条绑定**(供应商被删/别名被改名的
 *   窗口期里它会悬空),否则子代理会拿着一个查不到的别名去发请求。
 */
export function subagentModelSelection(
  declared: { model?: string; modelProviderId?: string },
  configured: { model: string; modelProviderId?: string },
  parent: { model: string; modelProviderId?: string }
): { model: string; modelProviderId: string | undefined } {
  if (declared.model !== undefined) {
    return { model: declared.model, modelProviderId: declared.modelProviderId }
  }
  if (configured.model.trim() !== '') {
    return { model: configured.model, modelProviderId: configured.modelProviderId }
  }
  return { model: parent.model, modelProviderId: parent.modelProviderId }
}

/**
 * 校正兜底:第一个能当对话模型的绑定。
 *
 * 需求:对话模型选择器(输入框、通用页、工作区、Hooks)只列文本模型;存量配置里
 * 已经选中图片模型的,**打开选择器时**落到这一个,并 toast 提示用户(不静默改)。
 * 不这样做的症状是选择器里勾着一个列表里根本不存在的项,用户一打开就以为坏了。
 *
 * ★ 沿用输入数组的顺序(`listResolvedModels()` 已按「供应商顺序 → priority → 别名」
 * 排好,正是选择器的显示顺序)——「第一个」必须和用户在列表里看到的第一个一致。
 * ★ `enabled === false` 的别名/供应商都不算:落到一个发不出请求的模型比不校正更糟。
 */
export function firstChatModelAlias(
  models: readonly ModelAlias[],
  providers: readonly UpstreamProvider[]
): ModelAlias | null {
  const enabled = new Set(providers.filter((p) => p.enabled === true).map((p) => p.id))
  for (const model of models) {
    if (model.enabled === false) continue
    if (!enabled.has(model.providerId)) continue
    if (!isChatModelAlias(model)) continue
    return model
  }
  return null
}

/**
 * 打开对话模型选择器时的校正:当前选中的是**不能对话**的模型(图片/纯非文本输出)
 * 就换成 `firstChatModelAlias`,否则 `null`(不用动)。
 *
 * 需求:决定 4 —— 不动存量配置,**打开选择器时**才就地校正并提示;
 * 不静默改写是因为用户可能正打算去换回来,而提示让他知道发生了什么。
 *
 * ★ 悬空(alias 解析不到)**不归这里管**:那是 `repairModelSelection` 的活,
 * 混进来会让「换模型」和「修数据」两件事挤在同一个副作用里。
 * ★ 别名为空串(「跟随对话」这类空档)同样不动:它本来就不是模型。
 */
export function chatModelCorrection(
  models: readonly ModelAlias[],
  providers: readonly UpstreamProvider[],
  alias: string,
  modelProviderId: string | undefined
): { alias: string; modelProviderId: string } | null {
  if (alias === '') return null
  const binding = selectModelBinding(models, providers, alias, modelProviderId)
  if (binding === undefined || isChatModelAlias(binding)) return null
  const fallback = firstChatModelAlias(models, providers)
  return fallback === null ? null : { alias: fallback.alias, modelProviderId: fallback.providerId }
}
