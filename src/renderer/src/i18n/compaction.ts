/**
 * 上下文压缩相关的文案 —— 设置页那两栏、圆环菜单里的两项、分隔线上的「谁写的」。
 *
 * 需求:压缩模型和思考档位可以配置了(设置 › 通用 › Agent,工作区在圆环菜单里覆盖),
 * 这些选项和它们的后果都得说清楚 —— 尤其是「跟随会话模型」「跟随全局设置」这两种
 * **缺省态**:它们在下拉里长得像一个普通选项,但含义是「我没表态」。
 *
 * 单独一个文件而不是往 `index.tsx` 那四千行里塞 —— 照 `git.ts` / `ssh.ts` / `usage.ts` 的先例。
 * 分隔线那两条键沿用既有的 `chat.compaction.` 前缀(它们和那一组是一件事),
 * 但同样落在这个文件里 —— 前缀是命名空间,不是文件归属。
 */

/**
 * 带参数的文案那一个入参类型。★ 必须显式标出来:这个文件没有 `Messages` 的上下文
 * (它在 `index.tsx` 里),不标的话参数会被推断成 implicit any,spread 进 `ZH` 时
 * 整张表都不再匹配 `Messages`。
 */
type Params = Record<string, string | number>

export const compactionZh = {
  'compaction.model': '压缩上下文模型',
  'compaction.modelHint': '压缩是一次长输入、短输出的机械活,可以交给更便宜的模型。默认跟随会话模型。',
  'compaction.thinking': '压缩思考强度',
  'compaction.thinkingHint': '压缩模型不支持所选强度时自动降到最接近的可用档,不会让压缩失败。',
  'compaction.followSession': '跟随会话模型',
  'compaction.followGlobal': '跟随全局设置',
  'compaction.thinkingInherit': '跟随会话强度',
  'compaction.back': '返回',

  'chat.compaction.writtenBy': ({ model, level }: Params) => `由 ${String(model)} 写 · 思考 ${String(level)}`,
  'chat.compaction.fellBack': '配置的压缩模型当时不可用,已改用会话模型'
}

export const compactionEn = {
  'compaction.model': 'Compaction model',
  'compaction.modelHint': 'Compaction is a long-input, short-output chore — a cheaper model can do it. Follows the conversation model by default.',
  'compaction.thinking': 'Compaction thinking effort',
  'compaction.thinkingHint': 'If the compaction model does not accept the chosen effort, the closest available one is used instead — compaction never fails over it.',
  'compaction.followSession': 'Follow the conversation model',
  'compaction.followGlobal': 'Follow the global setting',
  'compaction.thinkingInherit': 'Follow the conversation',
  'compaction.back': 'Back',

  'chat.compaction.writtenBy': ({ model, level }: Params) => `Written by ${String(model)} · thinking ${String(level)}`,
  'chat.compaction.fellBack': 'The configured compaction model was unavailable; the conversation model was used instead'
}
