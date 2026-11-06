/**
 * 圆环菜单里「压缩模型 / 压缩思考强度」那两项的取值编解码。
 *
 * 需求:工作区这一层是**三态**(跟随全局设置 / 显式跟随会话模型 / 指定一个模型),
 * 而落盘的形状是 `compactModel?: string | null`(`shared/domain/workspace.ts` 那三态)。
 * 下拉的 value 只能是字符串,所以必须有一层编解码 —— 抽到这里是为了它能被单测
 * (Composer.tsx 已 2000+ 行,往里塞的每一行纯逻辑都等于多一条测不到的分支)。
 *
 * ★ `FOLLOW_SESSION` 这个哨兵**只活在下拉的 value 里**,一个字都不会落盘 ——
 *   与 `modelSelectionKey` 文件头那条「只用在下拉和 Map 键,不落盘」的约定一致。
 *   落盘的是 `null`,因为别名是用户可写的任意字符串,任何哨兵串都可能真的撞上一个别名。
 */
import {
  modelSelectionKey,
  parseModelSelectionKey
} from '../../../../shared/domain/model-selection'
import type { WorkspaceCompactionOverride } from '../../../../shared/domain/compaction-model'
import { isSubagentThinking, type SubagentThinking } from '../../../../shared/domain/subagent-thinking'

/** 「跟随全局设置」= 这个工作区没表过态。 */
export const FOLLOW_GLOBAL = ''
/**
 * 「显式跟随会话模型」—— 用来反盖全局配的压缩模型。
 *
 * ★ 带 `@` 前缀是因为 `modelSelectionKey` 的输出形如 `providerId/alias`,
 *   而 providerId 是 slug(`ipc/provider.ts` 的 `upsertProvider` 拒收带 `/` 的 id),
 *   不可能以 `@` 开头 —— 于是这个值和任何一条真实绑定都不会撞。
 */
export const FOLLOW_SESSION = '@session'

/** 工作区覆盖 → 下拉的 value。 */
export function compactModelValue(override: WorkspaceCompactionOverride | undefined): string {
  const model = override?.compactModel
  // ★ `null` 和 `undefined` 是两个不同的答案,所以这里不能写 `??` 或真值判断。
  if (model === null) return FOLLOW_SESSION
  if (model === undefined || model === '') return FOLLOW_GLOBAL
  return modelSelectionKey(override?.compactModelProviderId, model)
}

/**
 * 下拉的 value → 写回工作区设置的那一对。
 *
 * ★ 「跟随全局」写的是**空串**而不是 `undefined`。两个理由:
 *   1. 工作区设置的合并是 `{ ...cur.settings, ...patch }`(`ipc/workspace.ts`),
 *      **省略一个键清不掉旧值** —— 用户从「指定模型」改回「跟随全局」会毫无反应,
 *      而且零报错;
 *   2. 送一个 explicit `undefined` 过 IPC 能不能保住这个键,取决于结构化克隆的细节 ——
 *      让一条用户可见的行为依赖那个,是在赌。空串在领域侧和缺席同义
 *      (`compactModelSelection` 对空白串走的就是「没配」那一支)。
 *
 * ★ 两个字段**一起返回**,哪怕 providerId 是 `undefined`:少给一个,旧的 providerId
 * 会留下来,于是拼出「新别名 + 旧供应商」—— 候选集为空,然后报一条指着一家跟这次
 * 压缩无关的供应商的错(`model-selection.ts` 的成对规则)。
 */
export function compactModelPatch(value: string): {
  compactModel: string | null
  compactModelProviderId: string | undefined
} {
  if (value === FOLLOW_SESSION) return { compactModel: null, compactModelProviderId: undefined }
  if (value === FOLLOW_GLOBAL) return { compactModel: '', compactModelProviderId: undefined }
  const { alias, modelProviderId } = parseModelSelectionKey(value)
  return { compactModel: alias, compactModelProviderId: modelProviderId }
}

/**
 * 菜单里可选的模型列表 —— 一条**绑定**一项,不是一个别名一项。
 *
 * ★ 同一个别名可以挂在多家上(`model-selection.ts` 文件头),而「用哪一家压缩」
 * 正是用户要选的东西:一家是订阅制、另一家按量计费时,两者的账单完全不同。
 * 停用的绑定不列 —— 画出来的每个控件都是一次会失败的承诺(§5)。
 */
export function compactModelOptions(
  models: readonly { alias: string; providerId: string; enabled?: boolean }[],
  providers: readonly { id: string; name: string; enabled?: boolean }[]
): { value: string; label: string }[] {
  const byId = new Map(providers.map((p) => [p.id, p]))
  return models
    .filter((m) => m.enabled !== false && byId.get(m.providerId)?.enabled !== false)
    .map((m) => ({
      value: modelSelectionKey(m.providerId, m.alias),
      // 别名和供应商名都是领域值,不翻译(§6.5)。
      label: `${m.alias} · ${byId.get(m.providerId)?.name ?? m.providerId}`
    }))
}

/** 工作区覆盖 → 档位下拉的 value。`null`(改回跟随全局)和缺席同义。 */
export function compactThinkingValue(override: WorkspaceCompactionOverride | undefined): string {
  const level = override?.compactThinking
  return level === undefined || level === null ? FOLLOW_GLOBAL : level
}

/**
 * 档位下拉的 value → 写回工作区设置的那一项。
 *
 * ★ 「跟随全局」写 `null` 而不是省略这个键 —— 浅合并省略等于什么都没改
 * (理由同 `compactModelPatch`)。这里不能像模型那样用空串:`SubagentThinking`
 * 是个枚举,空串不是它的成员,写进去会让 `isSubagentThinking` 在下一次导入时判假。
 */
export function compactThinkingPatch(value: string): { compactThinking: SubagentThinking | null } {
  if (value === FOLLOW_GLOBAL) return { compactThinking: null }
  // 认不出的值一律当成「跟随全局」,绝不原样落库(同设置页那几行枚举校验)。
  return { compactThinking: isSubagentThinking(value) ? value : null }
}
