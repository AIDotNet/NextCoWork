/**
 * 「从会话提炼 Skill」的文案 —— 侧边栏菜单项、`/skillify` 命令副标题、提炼会话的标题与触发语、
 * 以及主进程拒绝时带回来的 `skills.extraction.*`。
 *
 * 单独一个文件而不是往 `index.tsx` 里塞 —— 照 `git.ts` / `compaction.ts` 的先例。
 * `composer.command.skillify` 沿用 composer 的命名空间(它和 `/compact`、`/goal` 是一组),
 * 但同样落在这个文件里 —— 前缀是命名空间,不是文件归属。
 *
 * ★ 触发语(`skillify.startPrompt`)会作为用户消息发给模型,并且留在对话里给用户看。
 * 它故意只有一句:真正的工作流指令挂在主进程的头块里(`kernel/skill/extraction.ts`),
 * 这里写长了,用户会在对话里看到一大段看不懂的模板。用户侧的解释由触发消息下方的
 * 说明卡承担(`skillify.banner.*`,渲染在 `SkillExtractionBanner`),不进对话记录;
 * 收起态只有两行,完整信息(源会话 ID / 消息数 / 创建时间 / 产出位置)点击展开后显示。
 */

export const skillifyZh = {
  'skillify.menu': '提炼为 Skill',
  'skillify.sessionTitle': '提炼 Skill · {title}',
  'skillify.startPrompt': '把会话「{title}」提炼成本项目可复用的 Skill。',
  'skillify.hintPrefix': '补充说明：{hint}',
  'composer.command.skillify': '把当前会话提炼成项目 Skill',
  'skillify.banner.title': 'Skill 提炼会话',
  'skillify.banner.source': '源会话：{title}',
  'skillify.banner.sourceLoading': '正在读取源会话…',
  'skillify.banner.sourceMissing': '源会话已删除或不可访问',
  'skillify.banner.body': '源会话的完整过程摘要与提炼指令已注入模型上下文（此界面不显示附件内容）。产出会写入 .next-cowork/skills/，写盘后自动启用，并出现在回复下方的改动卡片里。',
  'skillify.banner.detail.sourceId': '源会话 ID',
  'skillify.banner.detail.messages': '源会话消息',
  'skillify.banner.detail.messagesValue': '{count} 条',
  'skillify.banner.detail.created': '源会话创建时间',
  'skillify.banner.detail.output': '产出位置',
  'skills.extraction.sourceMissing': '找不到要提炼的会话，它可能已被删除',
  'skills.extraction.sourceEmpty': '这个会话还没有任何对话，没有可提炼的内容',
  'skills.extraction.sourceRunning': '请等这个会话的任务完成后再提炼 Skill',
  'skills.extraction.nested': '提炼 Skill 的会话不能再次提炼'
}

export const skillifyEn = {
  'skillify.menu': 'Extract as Skill',
  'skillify.sessionTitle': 'Skill extraction · {title}',
  'skillify.startPrompt': 'Turn the conversation "{title}" into a reusable project Skill.',
  'skillify.hintPrefix': 'Additional notes: {hint}',
  'composer.command.skillify': 'Extract this conversation into a project Skill',
  'skillify.banner.title': 'Skill extraction',
  'skillify.banner.source': 'Source conversation: {title}',
  'skillify.banner.sourceLoading': 'Loading the source conversation…',
  'skillify.banner.sourceMissing': 'The source conversation has been deleted or is inaccessible.',
  'skillify.banner.body': 'The full digest of the source conversation and the extraction instructions are injected into the model context (not shown in this view). The Skill is written to .next-cowork/skills/, enabled automatically, and appears in the change card below the reply.',
  'skillify.banner.detail.sourceId': 'Source session ID',
  'skillify.banner.detail.messages': 'Source messages',
  'skillify.banner.detail.messagesValue': '{count}',
  'skillify.banner.detail.created': 'Source created',
  'skillify.banner.detail.output': 'Output location',
  'skills.extraction.sourceMissing': 'The conversation to extract from could not be found. It may have been deleted.',
  'skills.extraction.sourceEmpty': 'This conversation has nothing to extract yet.',
  'skills.extraction.sourceRunning': 'Wait for this conversation to finish before extracting a Skill.',
  'skills.extraction.nested': 'A Skill extraction conversation cannot be extracted again.'
}
