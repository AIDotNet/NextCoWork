/**
 * 输入框补全(灰字 + Tab 接受)的文案 —— 占位符、灰字提示、设置页那几栏。
 * 设计见 `docs/composer-autocomplete.md`。
 *
 * ★ 打字续写那一栏的说明必须写清「每次停顿都会把草稿发给所选模型」:
 *   那是用户打开它之前唯一能看到的隐私与费用提示。
 */

/** 带参数的文案的入参类型(理由见 `compaction.ts` 同名类型) */
type Params = Record<string, string | number>

export const composerCompletionZh = {
  'composer.placeholder.fresh': '给 NextCoWork 派个活… 输入 @ 引用文件，/ 使用命令',
  'composer.ghost.accept': '接受建议',
  'composer.ghost.announce': ({ text }: Params) => `建议：${String(text)}，按 Tab 接受`,

  'settings.completion.suggestions': '回合结束后建议下一步',
  'settings.completion.suggestionsHint': '回复结束后，在空输入框里用灰字给出你可能要说的下一句。按 Tab 填入，再按 Enter 发送。每轮最多一次请求。',
  'settings.completion.model': '建议所用模型',
  'settings.completion.modelHint': '默认跟随输入框当前的模型，不会换到别的供应商。可以指定一个更快、更便宜的模型。',
  'settings.completion.local': '历史与文件补全',
  'settings.completion.localHint': '输入时用你发过的指令和改动过的文件名补全，按 Tab 接受。只在本机进行，不发送任何请求。',
  'settings.completion.inline': '打字时续写',
  'settings.completion.inlineHint': '停顿时让所选模型续写这一句。每次停顿都会把草稿发给该模型的供应商并产生费用。',
  'settings.completion.inlineModel': '续写所用模型',
  'settings.completion.inlineModelHint': '必须单独选择。建议用一个快速、不带推理的小模型，否则灰字到的时候你已经打过去了。',
  'settings.completion.inlineModelNone': '未选择（不生效）',
  'settings.completion.inlineModelThinking': '这个模型关不掉思考，续写不会生效。请换一个能关闭思考的模型。'
}

export const composerCompletionEn: Record<keyof typeof composerCompletionZh, string | ((params: Params) => string)> = {
  'composer.placeholder.fresh': 'Give NextCoWork a task… Type @ to reference files, / for commands',
  'composer.ghost.accept': 'to accept',
  'composer.ghost.announce': ({ text }: Params) => `Suggestion: ${String(text)}. Press Tab to accept`,

  'settings.completion.suggestions': 'Suggest the next step',
  'settings.completion.suggestionsHint': 'After a reply finishes, show the message you are likely to send next as grey text in the empty input. Press Tab to fill it in, then Enter to send. At most one request per turn.',
  'settings.completion.model': 'Suggestion model',
  'settings.completion.modelHint': 'Follows the model selected in the input by default and never switches provider. Pick a faster, cheaper model if you like.',
  'settings.completion.local': 'History and file completion',
  'settings.completion.localHint': 'Complete from messages you have sent and files you have changed as you type. Press Tab to accept. Runs locally and sends no requests.',
  'settings.completion.inline': 'Complete while typing',
  'settings.completion.inlineHint': 'When you pause, the chosen model continues your sentence. Every pause sends your draft to that model’s provider and is billed.',
  'settings.completion.inlineModel': 'Completion model',
  'settings.completion.inlineModelHint': 'Must be chosen explicitly. Use a fast model without reasoning, or the suggestion arrives after you have already typed on.',
  'settings.completion.inlineModelNone': 'Not selected (off)',
  'settings.completion.inlineModelThinking': 'This model cannot turn reasoning off, so completion will not run. Choose a model that can.'
}
