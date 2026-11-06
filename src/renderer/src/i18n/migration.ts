/**
 * 启动迁移闸门的文案。
 *
 * ★ 这一份和别的域文件有个关键区别:**它在首屏之前就要用**。
 * 那一刻 `settings` 还没读到(App 的握手还没发生),所以 `I18nProvider` 用的是
 * 它自己的默认 locale。文案因此必须是**自足的** —— 不能引用任何设置项、
 * 不能假设用户已经选过语言。
 *
 * ★ 错误文案按 `code` 分档(`migration.error.disk-full` 之类),不是把主进程的
 * 原始错误翻一遍。主进程的 `detail` 只出现在「复制诊断信息」里,原样不翻 ——
 * 用户要拿它去搜、去贴给模型,翻成中文就再也搜不到了。
 */

/** 带参数文案的入参类型。★ 必须显式标出来,否则 spread 进 `ZH` 时整张表不匹配。 */
type Params = Record<string, string | number>

export const migrationZh = {
  'migration.startup.failedTitle': 'NextCoWork 启动失败',
  'migration.startup.failedBody':
    '本地服务未能完成初始化。数据没有被删除，请打开数据目录备份，并复制下方诊断信息。',
  'migration.startup.delayedTitle': '启动时间比预期长',
  'migration.startup.delayedBody':
    '仍在等待本地服务，应用会继续尝试。如果长时间没有恢复，请打开数据目录并复制诊断信息。',

  'migration.title': '正在整理本地数据',
  'migration.subtitle': '首次升级到新的数据目录，完成后会自动进入应用。',
  'migration.keepOpen': '请不要关闭窗口。',
  'migration.preparing': '正在准备…',
  'migration.overall': ({ percent }: Params) => `整体进度 ${percent}%`,

  'migration.step.collapse-flat-layout': '整理数据目录结构',
  'migration.step.merge-legacy-rows': '合并旧数据里的会话',
  'migration.step.copy-attachment-files': '复制附件文件',

  'migration.step.pending': '等待中',
  'migration.step.done': '已完成',
  'migration.step.failed': '失败',
  'migration.step.running': '进行中',
  'migration.step.count': ({ done, total }: Params) => `${done} / ${total}`,

  'migration.mergedSummary': ({ sessions, messages }: Params) =>
    `已合并 ${sessions} 段会话、${messages} 条消息`,

  'migration.error.title': '整理数据时出错',
  'migration.error.disk-full': '磁盘空间不足，请清理后重试。',
  'migration.error.permission': '无法读写数据目录，请检查文件权限，或先打开数据目录确认。',
  'migration.error.source-corrupt': '旧数据文件已损坏，无法读取其中的会话。',
  'migration.error.target-corrupt': '当前数据文件无法读取，可能是文件损坏。请先打开数据目录确认。',
  'migration.error.target-locked': '数据文件被其他程序占用，请关闭其他 NextCoWork 窗口后重试。',
  'migration.error.unknown': '发生了未知错误，可以重试，或先跳过并继续使用。',

  'migration.error.whatNow': '已完成的部分已经保存，可以安全地重试或跳过。',
  'migration.error.detailLabel': '诊断信息',
  'migration.error.copyDetail': '复制诊断信息',
  'migration.error.copied': '已复制',

  'migration.action.retry': '重试',
  'migration.action.skip': '跳过并继续',
  'migration.action.openDataDirectory': '打开数据目录',
  'migration.action.undoMerge': '撤销本次合并',

  'migration.skipped.title': '已跳过数据整理',
  'migration.skipped.body': '应用会继续启动。旧数据仍保留在原处，下次启动会再次尝试。',
  'migration.skipped.continue': '继续',

  'migration.undo.title': '撤销本次合并',
  'migration.undo.body': '只会删除本次合并写入的会话，之后新建的会话不受影响。',
  'migration.undo.done': '已撤销本次合并',
  'migration.undo.unavailable': '没有可撤销的合并记录',
  'migration.undo.actionFailed': '撤销失败，请查看诊断信息',

  'migration.actionFailed': '操作失败，请重试。'
}

export const migrationEn = {
  'migration.startup.failedTitle': 'NextCoWork could not start',
  'migration.startup.failedBody':
    'The local service could not finish initializing. Your data was not deleted. Open the data directory to back it up, then copy the diagnostics below.',
  'migration.startup.delayedTitle': 'Startup is taking longer than expected',
  'migration.startup.delayedBody':
    'The app is still waiting for the local service and will keep trying. If it does not recover, open the data directory and copy the diagnostics below.',

  'migration.title': 'Organizing local data',
  'migration.subtitle': 'Upgrading to the new data directory. The app opens by itself when this finishes.',
  'migration.keepOpen': 'Please keep this window open.',
  'migration.preparing': 'Preparing…',
  'migration.overall': ({ percent }: Params) => `Overall ${percent}%`,

  'migration.step.collapse-flat-layout': 'Reorganizing the data directory',
  'migration.step.merge-legacy-rows': 'Merging conversations from older data',
  'migration.step.copy-attachment-files': 'Copying attachment files',

  'migration.step.pending': 'Waiting',
  'migration.step.done': 'Done',
  'migration.step.failed': 'Failed',
  'migration.step.running': 'In progress',
  'migration.step.count': ({ done, total }: Params) => `${done} / ${total}`,

  'migration.mergedSummary': ({ sessions, messages }: Params) =>
    `Merged ${sessions} conversations and ${messages} messages`,

  'migration.error.title': 'Something went wrong while organizing data',
  'migration.error.disk-full': 'Not enough disk space. Free up space and try again.',
  'migration.error.permission':
    'Cannot read or write the data directory. Check its permissions, or open the data directory to look.',
  'migration.error.source-corrupt': 'The older data file is damaged and its conversations cannot be read.',
  'migration.error.target-corrupt':
    'The current data file cannot be read; it may be damaged. Open the data directory to look.',
  'migration.error.target-locked':
    'The data file is in use by another program. Close any other NextCoWork windows and try again.',
  'migration.error.unknown': 'An unknown error occurred. You can retry, or skip and keep using the app.',

  'migration.error.whatNow': 'What already finished has been saved. Retrying or skipping is safe.',
  'migration.error.detailLabel': 'Diagnostic details',
  'migration.error.copyDetail': 'Copy diagnostics',
  'migration.error.copied': 'Copied',

  'migration.action.retry': 'Retry',
  'migration.action.skip': 'Skip and continue',
  'migration.action.openDataDirectory': 'Open data directory',
  'migration.action.undoMerge': 'Undo this merge',

  'migration.skipped.title': 'Data organizing skipped',
  'migration.skipped.body':
    'The app will keep starting. The older data is still in place and will be tried again next launch.',
  'migration.skipped.continue': 'Continue',

  'migration.undo.title': 'Undo this merge',
  'migration.undo.body': 'Only conversations written by this merge are removed. Newer ones are untouched.',
  'migration.undo.done': 'Merge undone',
  'migration.undo.unavailable': 'Nothing to undo',
  'migration.undo.actionFailed': 'Undo failed. See the diagnostic details.',

  'migration.actionFailed': 'Action failed. Please try again.'
}
