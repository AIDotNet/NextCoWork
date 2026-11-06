/**
 * 「打开方式」—— 用本机已装的文件管理器 / IDE / 终端打开一个工作区文件。
 *
 * 这个文件只放**两端都要认的形状**:目标 id、图标 id、探测结果的类型。
 * 「这台机器上装了哪些 IDE」是主进程的事(见 `main/system/open-with.ts`),
 * 渲染层只负责把它画出来 —— 它拿不到、也不需要拿到任何绝对路径
 * (`workspace-file.ts` 那条「不向渲染层返回磁盘绝对路径」的约定在这里继续成立,
 * 所以「复制绝对路径」也由主进程直接写剪贴板,而不是把路径递过来让渲染层自己写)。
 *
 * ★ `label` 是**产品名,不翻译**("VS Code"、"Rider"、"Ghostty")—— 和模型名、
 *   供应商名同一条规矩。通用目标(文件管理器 / 终端 / 默认应用)的 label 是空串,
 *   文案由渲染层按 i18n 出:系统文件管理器叫什么各平台不同
 *   (访达 / 文件资源管理器 / 文件),那不是我们的产品名,不该跟着应用语言走。
 */

/**
 * 图标 id。
 *
 * ★ 这是一个**闭合联合**,`components/brand/EditorIcon.tsx` 里那张表按它
 *   `Record<…>` 展开 —— 加一个 IDE 却忘了配字形,编译期就挂,而不是界面上
 *   出现一颗空白图标(和 `ProviderIcon` 的 `MARK` 同一条规矩)。
 */
export type OpenTargetIcon =
  | 'file-manager'
  | 'default-app'
  | 'terminal'
  | 'vscode'
  | 'cursor'
  | 'windsurf'
  | 'zed'
  | 'sublime'
  | 'intellij'
  | 'pycharm'
  | 'webstorm'
  | 'goland'
  | 'clion'
  | 'rider'
  | 'phpstorm'
  | 'rubymine'
  | 'androidstudio'
  | 'xcode'

/** 系统文件管理器。走 `shell.showItemInFolder`,不需要任何可执行文件,所以恒可用。 */
export const REVEAL_TARGET_ID = 'reveal'
/** 终端。**打开的是文件所在目录**,不是文件本身。 */
export const TERMINAL_TARGET_ID = 'terminal'
/**
 * 系统默认应用(`shell.openPath`)—— 参考截图「打开方式」子菜单里的 "Default app"。
 *
 * 需求:`.png` / `.pdf` / `.docx` 这类文件用户要的是「系统平时用什么开就用什么开」,
 * 而上面那张 IDE 表里没有预览/办公软件。
 *
 * ★ 这**不是**去问系统「这个类型关联了谁」再把答案列成一项(`main/system/editors.ts`
 *   头上第 1 条排除的正是那件事):它是一个和「文件管理器」同级的**通用目标**,
 *   名字由渲染层按 i18n 出,关联交给系统在打开那一刻自己解析。
 */
export const DEFAULT_APP_TARGET_ID = 'default-app'

/** 三个通用目标:恒在(终端除外,按探测),label 为空串、名字由渲染层出。 */
export function isGenericTarget(id: string): boolean {
  return id === REVEAL_TARGET_ID || id === TERMINAL_TARGET_ID || id === DEFAULT_APP_TARGET_ID
}

/**
 * 某一项(文件 / 目录)上能列出的打开方式。
 *
 * ★ 目录上只留「文件管理器 / 终端」—— 编辑器 `code <目录>` 会**把编辑器的工作区根换掉**
 *   (理由全文见 `components/OpenWithMenu.tsx` 的 `directory` 参数);默认应用对目录
 *   等于再开一次文件管理器,同一个动作列两行只会让人以为它们不一样。
 */
export function targetsForEntry(targets: readonly OpenTarget[], directory: boolean): OpenTarget[] {
  if (!directory) return [...targets]
  return targets.filter((target) => target.id === REVEAL_TARGET_ID || target.id === TERMINAL_TARGET_ID)
}

/**
 * 右键菜单第一行「在 X 中打开」用哪一个。
 *
 * 需求:默认程序由用户在「设置 › 通用」里指定(`AppSettings.defaultOpenTarget`);
 * 没指定、或者指定的那个已经卸载 / 对这一项不可用(目录上选了编辑器)时要**有一个
 * 合理的落点**,而不是让第一行消失 —— 那一行是这份菜单最常点的一项。
 *
 * 落点顺序:用户指定 → 第一个探测到的编辑器(文件)→ 系统默认应用(文件)→ 文件管理器。
 * 一个都没有返回 null(探测失败的空表),调用方据此不画这一行。
 */
export function pickPrimaryTarget(
  targets: readonly OpenTarget[],
  preferred: string,
  directory: boolean
): OpenTarget | null {
  const usable = targetsForEntry(targets, directory)
  const chosen = preferred === '' ? undefined : usable.find((target) => target.id === preferred)
  if (chosen !== undefined) return chosen
  return (
    usable.find((target) => !isGenericTarget(target.id)) ??
    usable.find((target) => target.id === DEFAULT_APP_TARGET_ID) ??
    usable.find((target) => target.id === REVEAL_TARGET_ID) ??
    usable[0] ??
    null
  )
}

/**
 * 「打开方式 ›」子菜单的顺序:当前默认的那一个打头,然后是三个通用目标,最后是其余编辑器。
 *
 * ★ 顺序照参考截图(VS Code / Default app / Finder / Terminal / Xcode / Rider)。
 *   主进程 `buildTargets` 的顺序是「探测表」的顺序,两者不必相同 —— 这里是展示决定,
 *   所以放在调用方而不是去改主进程那张表(它的顺序有自己的测试)。
 */
export function submenuTargets(targets: readonly OpenTarget[], primary: OpenTarget | null): OpenTarget[] {
  const rest = targets.filter((target) => target.id !== primary?.id)
  const generic = [DEFAULT_APP_TARGET_ID, REVEAL_TARGET_ID, TERMINAL_TARGET_ID]
    .map((id) => rest.find((target) => target.id === id))
    .filter((target): target is OpenTarget => target !== undefined)
  const editors = rest.filter((target) => !isGenericTarget(target.id))
  return [...(primary === null ? [] : [primary]), ...generic, ...editors]
}

/** 默认打开方式的设置值上限。id 都是主进程表里的短串,64 已经远超任何一个。 */
const OPEN_TARGET_ID_MAX = 64

/**
 * `AppSettings.defaultOpenTarget` 的合法形状:空串(= 自动)或一个 id 形状的短串。
 *
 * ★ 这里**不**校验「这个 id 在本机探测表里」:设置是跨启动持久的,而 IDE 可能暂时
 *   卸载、或者设置从另一台机器导入 —— 那时 `pickPrimaryTarget` 自己会退回落点。
 *   这一道只挡「形状都不对」的坏值(对象、超长串、带路径分隔符的串)。
 */
export function isOpenTargetPreference(value: unknown): value is string {
  return typeof value === 'string' && value.length <= OPEN_TARGET_ID_MAX && /^[a-z0-9-]*$/.test(value)
}

/**
 * 一个可用的打开方式。
 *
 * `id` 是稳定标识(主进程侧那张表的键),渲染层把它原样回传 —— 它不携带任何
 * 路径或命令,主进程收到之后只拿它**查表**,查不到就拒。
 */
export interface OpenTarget {
  id: string
  /** 产品名,不翻译;通用目标(见 `isGenericTarget`)为空串(见文件头) */
  label: string
  icon: OpenTargetIcon
}

/** 「复制路径」的两档。`relative` 是**工作区相对**写法(工作区外的文件就是它的绝对路径)。 */
export type WorkspacePathKind = 'absolute' | 'relative'
