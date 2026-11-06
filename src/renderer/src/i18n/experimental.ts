/**
 * 设置 › 实验 那一页的文案。
 *
 * 单独成文件的理由同 `widget.ts`:`i18n/index.tsx` 已经三千多行,新域一律搬出来。
 * 页名 `settings.page.experimental` 也放这里 —— 加页时它最容易漏(见 `nav.test.ts`)。
 */

export const experimentalZh = {
  'settings.page.experimental': '实验',
  'settings.experimental.intro': '这些功能仍在开发中，行为和界面都可能变化，也可能被移除。默认全部关闭。',
  'settings.experimental.workflow.title': 'Workflow 工作流',
  'settings.experimental.workflow.desc':
    '允许 Agent 把任务编排成一张依赖图，按依赖并行派出多个子代理执行，并在回复里以画布实时展示进度。会同时消耗多个子代理的 Token。',
  'settings.experimental.workflowConfirm.title': '运行前确认',
  'settings.experimental.workflowConfirm.desc': '工作流开跑之前先展示编排好的图，由你决定运行、要求修改或取消。关掉后直接运行。'
}

export const experimentalEn: Record<keyof typeof experimentalZh, string> = {
  'settings.page.experimental': 'Experimental',
  'settings.experimental.intro':
    'These features are still in development. Their behaviour and UI may change, or they may be removed. All are off by default.',
  'settings.experimental.workflow.title': 'Workflow',
  'settings.experimental.workflow.desc':
    'Lets the agent plan a task as a dependency graph, run several subagents in parallel along it, and show live progress as a canvas in the reply. Uses tokens for every subagent it runs.',
  'settings.experimental.workflowConfirm.title': 'Confirm before running',
  'settings.experimental.workflowConfirm.desc': 'Show the planned graph before a workflow starts, and let you run it, ask for changes, or cancel. When off, workflows start right away.'
}
