/**
 * 应用级设置。工作区级的在 workspace.ts。
 */
import type { PermissionMode } from '../agent/permission'
import { DEFAULT_MAX_OUTPUT_TOKENS } from '../agent/run-request'
import { isOpenTargetPreference } from './open-target'
import type { ProxySettings } from './proxy'
import { DEFAULT_PROXY, migrateLegacyProxy } from './proxy'
import { INHERIT_THINKING, isSubagentThinking, type SubagentThinking } from './subagent-thinking'
import {
  DEFAULT_COLOR_THEME_ID,
  DEFAULT_CUSTOM_SEED,
  type ColorThemeChoice,
  type ImageRender,
  type ThemeToken
} from './theme'

/** 三态,默认跟随系统(方案 §8:深浅两套对等)。 */
export type ThemePreference = 'system' | 'light' | 'dark'
/** 实际生效的那个,由主进程 nativeTheme 解析后下发 */
export type ResolvedTheme = 'light' | 'dark'

/** 本机数据备份频率。云同步不属于本地数据设置的一部分。 */
export type BackupFrequency = 'manual' | 'daily' | 'weekly'

/**
 * 本地命令 / 命令钩子 / 新建终端用哪个 shell。
 *
 * ★ `'system'` 是唯一需要运行时解析的取值(由主进程按平台挑),其余七个是
 * **域名词** —— 它们就是可执行文件的名字,界面上不翻译。
 */
export const SHELL_PREFERENCES = [
  'system',
  'cmd',
  'powershell',
  'pwsh',
  'zsh',
  'bash',
  'fish',
  'sh'
] as const
export type ShellPreference = (typeof SHELL_PREFERENCES)[number]

/** 只认枚举。坏值不许落库,也不许当成 `'system'` 悄悄生效。 */
export function isShellPreference(value: unknown): value is ShellPreference {
  return typeof value === 'string' && SHELL_PREFERENCES.includes(value as ShellPreference)
}

/** 系统原生预设，自动选择排最前。Windows PowerShell 仅限 Windows，pwsh 跨平台。 */
export function shellPreferencesForPlatform(platform: string): readonly ShellPreference[] {
  switch (platform) {
    case 'win32':
      return ['system', 'cmd', 'powershell', 'pwsh']
    case 'darwin':
      return ['system', 'zsh', 'bash', 'fish', 'sh', 'pwsh']
    case 'linux':
      return ['system', 'bash', 'zsh', 'fish', 'sh', 'pwsh']
    default:
      return ['system', 'sh']
  }
}

export interface DataSettings {
  /** 用户选择的本机备份目录；null 表示尚未选择。 */
  backupDirectory: string | null
  backupFrequency: BackupFrequency
}

/**
 * Agent 上下文整理偏好。
 *
 * ★ 原先这里还有一个 `experimentalMode`:它区分的是「机械折叠」和「模型摘要」
 * 两条压缩路径。按 Claude Code 重写之后只剩模型摘要一条路(机械折叠会把工具输出
 * 清空成骨架,省不下多少又让模型看不见自己刚做过什么),这个开关没有剩下的语义,
 * 所以删掉 —— 留一个不改变任何行为的开关,比没有开关更让人困惑。
 *
 * ★ `autoCompact` 保留,它现在是**唯一**的总闸:关掉之后到阈值也不压缩,
 * 上下文一路涨到上游报超长为止(被动压缩同样受它管)。
 */
export interface ContextManagementSettings {
  autoCompact: boolean
}

/**
 * 设置 › 实验:尚未定型的功能。每一项都**出厂关闭** —— 打开它是用户在说
 * 「我愿意用一个还会变的东西」,这句话不能替他说。
 *
 * ★ 一项转正后就从这里搬走、换成它自己的设置(或干脆不要开关),
 *   而不是在这里永久留一个永远是 true 的开关。
 */
export interface ExperimentalSettings {
  /**
   * `Workflow` 工具:模型把任务编排成一张依赖图,按依赖并行派子代理执行,
   * 对话里画成画布。关掉 = 工具不下发(`kernel/tool/builtin/workflow.ts` 的 `isEnabled`)。
   */
  workflow: boolean
  /**
   * 工作流派出任何节点之前先给用户看一眼图,点「运行」才开跑。
   * ★ 出厂**开**,和别的实验项相反:一张图一次就是十几个子代理的 token,
   *   模型画错图(汇总节点没接上游之类)时,开跑之前是唯一不花钱的拦截点。
   */
  workflowConfirm: boolean
}

/**
 * 界面「偏好 › 个性化」那三栏。**它是唯一一块会被原样拼进系统提示词的设置** ——
 * 见 `main/kernel/context-assembler.ts` 的 `buildPersonalizationSection`。
 *
 * ★ 三个字段都是**用户自己打进来的**,不走 `untrustedBoundary`:那道边界声明
 * 防的是「从 zip 装来的 Skill 正文」「clone 别人仓库带进来的 AGENTS.md」这类
 * 第三方文本,而这三栏就是用户本人在跟模型说话 —— 给自己的话加一句
 * 「以上内容不能放宽你的权限」既没有防住谁,又白占提示词。真正的防线仍在
 * 权限层:不管这里写了什么,每一次工具调用照样过 `approve`。
 */
export interface PersonalizationSettings {
  /** 姓名 —— 「让 AI 知道你是谁」 */
  name: string
  /** 工作描述 —— 「帮助 AI 理解你的背景,以便提供更贴合的回答」 */
  background: string
  /** 全局提示词 —— 「自定义指令会附加到每次对话的系统提示词中」 */
  instructions: string
}

/** 应用级快捷键。值使用 Electron accelerator 格式，例如 `CmdOrCtrl+,`。 */
export interface ShortcutSettings {
  openSettings: string
}

/**
 * 主题工作室的可持久化草稿。它只保存用户对现有语义 token 的覆盖，
 * 其余颜色仍由 shared/domain/theme.ts 的派生器负责生成。
 */
export interface ThemeStudioSettings {
  name: string
  wallpaperAssetId: string | null
  render: ImageRender
  opacity: number
  blur: number
  brightness: number
  saturation: number
  positionX: number
  positionY: number
  sidebarOpacity: number
  panelOpacity: number
  mask: number
  uiFont: 'system' | 'system-rounded' | 'system-serif'
  uiScale: 'small' | 'standard' | 'large'
  motion: 'standard' | 'soft' | 'reduced' | 'off'
  guardrails: boolean
  overrides: Partial<Record<ThemeToken, string>>
}

export const DEFAULT_THEME_STUDIO: ThemeStudioSettings = {
  name: '默认主题',
  wallpaperAssetId: null,
  render: 'blur',
  opacity: 0.55,
  blur: 44,
  brightness: 1,
  saturation: 1.15,
  positionX: 50,
  positionY: 50,
  sidebarOpacity: 0.88,
  panelOpacity: 0.9,
  mask: 0.28,
  uiFont: 'system',
  uiScale: 'standard',
  motion: 'standard',
  guardrails: true,
  overrides: {}
}

/**
 * 三栏各自的字符上限。
 *
 * ★ 闸门在**这里**(落库前)而不只在输入框上:`settings:update` 是个 IPC,
 * 输入框的 `maxLength` 拦不住任何一个绕过界面的调用,而这一块的去处是
 * **系统提示词的稳定前缀** —— 一段 200KB 的「全局提示词」不是显示得难看,
 * 是每一轮、每一个子代理都白烧一遍那 200KB。
 *
 * 数值本身是「够用就好」:名字给 64(比任何真名都长),背景 2000
 * (一段自我介绍),指令 8000(相当于一份不算短的 AGENTS.md)。
 * 提示词那侧还会再截一次 —— 那是防旧库里已经躺着超长值的,两道不冲突。
 */
export const PERSONALIZATION_MAX = {
  name: 64,
  background: 2000,
  instructions: 8000
} as const

export const BACKUP_FREQUENCIES: readonly BackupFrequency[] = ['manual', 'daily', 'weekly']

/**
 * 上游空闲超时(秒)的默认值与合法范围。管的是「供应商连续多少秒没吐出任何
 * 有效内容就判超时」(`UpstreamRouter` 的 idle timeout),不是单次请求总时长。
 *
 * ★ 常量放这里而不是 router.ts:渲染层(网络页的输入框提示)和主进程
 * (路由器)都要读它,放两边会分叉。60s 下限拦的是「误触改成 0 把所有慢模型
 * 秒杀」;3600s 上限拦的是「挂着一条永远不会到的流占住会话」。
 */
export const DEFAULT_UPSTREAM_IDLE_TIMEOUT_SECONDS = 600
export const UPSTREAM_IDLE_TIMEOUT_BOUNDS = { min: 60, max: 3600 } as const

/**
 * 每轮请求下发给模型的最大输出 Token(设置 › 通用 › Agent)。
 *
 * ★ 常量放这里、默认值仍在 `shared/agent/run-request.ts`:那边是请求侧的语义源头
 * (`resolveMaxOutputTokens` 在没有设置值时仍要有个数可用),这边只负责「设置项
 * 合法区间」。两处分叉的话,设置页能存进去的值和请求侧认的值会对不上。
 *
 * 1024 下限是 thinking 预算留的余量(见 `model-runtime.ts` 的 `MIN_OUTPUT_HEADROOM`,
 * 再小连最低档思考都放不下);200K 上限拦的是「多打一个 0」——真实模型的输出上限
 * 今天最大在 128K 量级,再大只会让上下文压力读数整屏泛红。
 */
export const MAX_OUTPUT_TOKENS_BOUNDS = { min: 1024, max: 200_000 } as const

/** 只认范围内的整数;坏值由调用点退回当前值(见 `mergeSettings`)。 */
export function isMaxOutputTokens(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) &&
    value >= MAX_OUTPUT_TOKENS_BOUNDS.min && value <= MAX_OUTPUT_TOKENS_BOUNDS.max
}

/** 只认范围内的整数秒;坏值由调用点退回当前值(见 `mergeSettings`)。 */
export function isUpstreamIdleTimeoutSeconds(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) &&
    value >= UPSTREAM_IDLE_TIMEOUT_BOUNDS.min && value <= UPSTREAM_IDLE_TIMEOUT_BOUNDS.max
}

function isBackupFrequency(value: unknown): value is BackupFrequency {
  return typeof value === 'string' && BACKUP_FREQUENCIES.includes(value as BackupFrequency)
}

/**
 * 旧设置文件是用户可编辑/可迁移的输入，不能因为一个坏字段让整个设置页
 * 变成 undefined。只接受这一块已知的两个字段，其余一律回到默认值。
 */
function mergeDataSettings(current: DataSettings, patch: unknown): DataSettings {
  if (typeof patch !== 'object' || patch === null || Array.isArray(patch)) return { ...current }
  const value = patch as Record<string, unknown>
  const backupDirectory =
    value.backupDirectory === null || typeof value.backupDirectory === 'string'
      ? value.backupDirectory
      : current.backupDirectory
  const backupFrequency = isBackupFrequency(value.backupFrequency)
    ? value.backupFrequency
    : current.backupFrequency
  return { backupDirectory, backupFrequency }
}

/**
 * 同 `mergeDataSettings`:只认这三个字符串字段,坏值原样退回当前值。
 * 顺手截到上限 —— 见 `PERSONALIZATION_MAX` 那段说明。
 */
function mergePersonalization(
  current: PersonalizationSettings,
  patch: unknown
): PersonalizationSettings {
  if (typeof patch !== 'object' || patch === null || Array.isArray(patch)) return { ...current }
  const value = patch as Record<string, unknown>
  const take = (key: keyof PersonalizationSettings): string => {
    const next = value[key]
    if (typeof next !== 'string') return current[key]
    return next.slice(0, PERSONALIZATION_MAX[key])
  }
  return { name: take('name'), background: take('background'), instructions: take('instructions') }
}

/**
 * 模型自提目标的三档。★ 它管的是「要不要弹审批」,所以坏值不许落库:
 * 落到 `'disabled'` 会让用户凭空失去一个功能,落到 `'auto'` 则是**静默放宽同意**。
 */
export const MODEL_PROPOSED_GOALS = ['auto', 'alwaysAsk', 'disabled'] as const
export type ModelProposedGoals = (typeof MODEL_PROPOSED_GOALS)[number]

/** 只认枚举。坏值由调用点退回上一个有效值(见 `mergeSettings`)。
 *  ★ 与 `defaultPermissionMode` 一样,**只从全局设置读** —— 仓库里的
 *  project/local settings 说不动它。 */
export function isModelProposedGoals(value: unknown): value is ModelProposedGoals {
  return typeof value === 'string' && MODEL_PROPOSED_GOALS.includes(value as ModelProposedGoals)
}

export interface AppSettings {
  /**
   * 界面「主题」页第一栏「外观模式」。**它只决定深浅,不决定颜色** ——
   * 颜色在下面 `colorTheme` / `imageTheme` 两栏,三者一起喂给 `tokensOf`。
   */
  theme: ThemePreference
  activeThemeProfileId: string | null
  locale: 'zh-CN' | 'en-US'

  /**
   * 「颜色主题」栏(随机 / 墨绿 / 霁青 / 极简 / Claude / 奢华 / 自定义)。
   *
   * ★ 形状(`ColorThemeChoice`)归 `theme.ts` 所有 —— 认识这三个字段各归谁用的是
   * `resolveColorTheme`,不是设置层。这里存的就是那一栏的全部状态。
   *
   * `id` 认不出来时 `resolveColorTheme` 落回默认而不抛 —— 它是从磁盘读回来的,
   * 而磁盘上可能存着一套后来被删掉的主题。
   *
   * `seed`(给「随机」,重掷一次换一个)和 `custom`(给「自定义」,用户挑的那个
   * 强调色)**都必须落盘**:两套主题都是由一个种子经 `specFromSeed` 现算的,
   * 不落盘就回不到用户挑中的那一套。
   */
  colorTheme: ColorThemeChoice

  /**
   * 「图片主题」栏。选了图就**盖过** `colorTheme`(见 theme.ts 的 `tokensOf`)。
   *
   * ★ 「没选图」写成 `id: null`,而不是把整块写成 `imageTheme: {…} | null`。
   * 后者会让这一块在 `AppSettingsPatch` 里退化成「整个给」—— `null` 不是 object,
   * 走不进那个 `extends object` 的真分支。于是「点一张卡片」和「点覆盖色药丸」
   * 这两个紧挨着的控件又回到互相覆盖的老路上,而那正是 `AppSettingsPatch`
   * 整个存在的理由。顺带还白捡一条:取消选图再选回来,用户挑的渲染方式还在。
   */
  imageTheme: {
    /** `IMAGE_THEMES` 里的 id,或上传图片的 assetId;`null` = 没选图 */
    id: string | null
    render: ImageRender
  }

  /** 新会话的默认档位 */
  defaultPermissionMode: PermissionMode
  /** “为我批准”档位使用的专用审核模型；空字符串表示未配置，自动回退人工审批。 */
  permissionReviewerModel: string
  /** 与 `permissionReviewerModel` 成对,见 `defaultModelProviderId` */
  permissionReviewerModelProviderId?: string
  /** 目标判定模型；空字符串表示未配置，回落到本次 run 的模型。 */
  goalEvaluatorModel: string
  /** 与 `goalEvaluatorModel` 成对,见 `defaultModelProviderId` */
  goalEvaluatorModelProviderId?: string
  /**
   * 上下文压缩用哪个模型；空字符串 = 跟随会话模型（出厂默认）。
   *
   * 需求：压缩是一次长输入、短输出的机械活，用户有理由把它交给另一个更便宜的模型；
   * 而它读的是同一段对话，所以**默认必须仍然跟随会话模型**（换一家既换口径也换账单）。
   * 三档来源（工作区 > 这一栏 > 会话模型）在 `domain/compaction-model.ts`。
   */
  compactModel: string
  /** 与 `compactModel` 成对,见 `defaultModelProviderId` */
  compactModelProviderId?: string
  /**
   * 压缩请求的思考强度。`'inherit'` = 跟随会话本轮档位（出厂默认）。
   *
   * ★ 这一栏不只是省钱开关，它是**正确性开关**：原先压缩硬发 `'off'`，而 effort 模型
   *   （`gpt-6-*`）的 `reasoningEfforts` 不含 `'none'`，于是每次压缩都被 `thinking-adapter`
   *   拒掉、三次后熔断，上下文再也压不下去。实际下发前还会按压缩模型归一化一次
   *   （`auxiliaryThinkingLevel`），所以这里选什么都不会让请求失败。
   */
  compactThinking: SubagentThinking
  /** 模型自提目标：默认放开，按用户原话直接设立。 */
  modelProposedGoals: ModelProposedGoals
  /**
   * 输入框「回合结束后建议下一步」(灰字,Tab 填入)。设计见 `docs/composer-autocomplete.md`。
   *
   * ★ 出厂**开启**:每轮最多一次请求、只发裁剪过的上下文(最近几条用户消息 + 回复末尾),
   *   而它是输入框「提醒」里最有用的那一层。不想要的用户在设置里一键关掉。
   */
  promptSuggestions: boolean
  /**
   * 建议下一步用哪个模型;空字符串 = 跟随输入框此刻的模型(出厂默认)。
   * ★ 跟随时不换家:同 `session-title.ts`「标题请求必须跟着正文走同一家」那条理由。
   */
  completionModel: string
  /** 与 `completionModel` 成对,见 `defaultModelProviderId` */
  completionModelProviderId?: string
  /** 本地补全:历史指令前缀 + 改动过的文件名。零请求、零成本,出厂开启。 */
  localCompletion: boolean
  /**
   * 打字中由模型续写。★ 出厂**关闭**:每次停顿都会把草稿发给所选模型 ——
   * 这是隐私和钱包两方面的决定,只能由用户自己打开。
   */
  inlineCompletion: boolean
  /**
   * 打字续写用的模型。★ **没有「跟随会话」这一档**:会话模型多半是大模型,
   * 首 token 一两秒,灰字到的时候用户早打过去了。空字符串 = 未选择,续写不生效。
   */
  inlineCompletionModel: string
  /** 与 `inlineCompletionModel` 成对,见 `defaultModelProviderId` */
  inlineCompletionModelProviderId?: string
  defaultModel: string
  /**
   * 与 `defaultModel` 成对:同一个别名可以挂在多家上,光凭别名定不下发给谁。
   * 缺席 = 没指定过,按 `provider.priority` 择优(引入这个字段之前的行为)。
   *
   * ★ 改 `defaultModel` 的 patch **必须**同时给出这个字段(哪怕是 `undefined`)——
   * `mergeSettings` 按这条规则成对写入,否则会留下「新别名 + 旧供应商」的脏配对。
   */
  defaultModelProviderId?: string

  /**
   * 「对话生图使用的模型」(设置 › 模型 › 图片生成)。**必选、不设自动档**:
   * 生成和改图共用这一个选择,只用点名的那个绑定,失败即失败(不按 priority
   * 跨家兜底 —— 跨家兜底的症状是「我明明点名了 A,画出来的却是 B 家的」)。
   *
   * ★ 空字符串 = **没选过**,`generate_image` 工具整体不下发(`image-gen.ts` 的
   * `available()`):不画一个注定失败的承诺。升级上来的库里这项就是空,
   * 用户要自己去图片页点一次 —— 这是「必选」决定的直接后果,不是遗漏。
   *
   * 与 `defaultModel` 成对,见那边 `defaultModelProviderId` 的说明:
   * 改 `imageModel` 的 patch 必须同时给 `imageModelProviderId`。
   */
  imageModel: string
  /** 与 `imageModel` 成对,见 `defaultModelProviderId` */
  imageModelProviderId?: string
  /**
   * 「对话生图」开关(设置 › 模型 › 图片生成)。关掉 = `generate_image` 不下发、调用被拒。
   *
   * 需求:生图有**自己的**开关,不再挂在输入框「联网搜索」那颗药丸上 —— 那颗现在
   * 只管网页搜索与抓取(`permission-gate.ts` 的 `NETWORK_SWITCH_TOOLS`)。原先生图跟着
   * 它一起消失,症状是「选好了生图模型,对话里却找不到工具」,且无从查起。
   *
   * ★ 出厂**开启**:选了生图模型本身就是「我要在对话里生图」的意思;默认关的话,
   *   用户还得去找第二个开关,正是这次要消灭的那种「配好了却不生效」。
   * ★ 与 `imageModel` 分开是因为两者回答不同问题:模型是「用谁画」,开关是「要不要画」——
   *   临时关掉生图不该让用户丢掉自己挑好的模型。
   */
  imageGenerationEnabled: boolean

  /**
   * 「对话视频生成使用的模型」(设置 › 模型 › 视频生成)。
   *
   * ★ 与 `imageModel` **同构、同一条规矩**:必选、不设自动档,只用点名的那个绑定,
   *   失败就是失败(不跨家兜底)。理由是同一句话,只是代价更大 —— 视频按秒/分辨率
   *   计费,「我点名了 A、出片的却是 B」既更贵,又更难发现(要等好几分钟)。
   *
   * ★ 空字符串 = **没选过**,`generate_video` 的**新建**动作整体不下发
   *   (`kernel/video-gen.ts`)。已提交任务的查询/取消**不看它** ——
   *   那是另一条路径,见下面 `videoGenerationEnabled` 那段。
   *
   * 与 `defaultModel` 成对,见那边 `defaultModelProviderId` 的说明:
   * 改 `videoModel` 的 patch 必须同时给 `videoModelProviderId`。
   */
  videoModel: string
  /** 与 `videoModel` 成对,见 `defaultModelProviderId` */
  videoModelProviderId?: string
  /**
   * 「对话视频生成」开关(设置 › 模型 › 视频生成)。关掉 = **不新建**任务。
   *
   * ★★ **这一项和 `imageGenerationEnabled` 有一处关键不同,写在字段上免得被后人
   *    "顺手统一":** 关掉生图 = `generate_image` 整体不下发、调用被拒,因为生图是
   *    同步的,不存在"已经提交出去了"这回事。关掉视频生成**只禁止新建** ——
   *    已经提交到云端的任务照常查询、照常收取成品。
   *
   *    理由:一个视频任务在云端可能已经跑了五分钟、**已经计过费**;关掉本地开关
   *    不会让它停下来,也不会退款。这时把它从界面上藏起来,只会让用户永远不知道
   *    结果。要停某一个任务,用那张结果卡上**独立**的取消按钮。
   *
   * ★ 出厂开启:与生图同一条理由 —— 选好模型本身就是"我要生成视频"的意思。
   * ★ 与 `videoModel` 分开,理由同为"模型答'用谁生成'、开关答'要不要新建'"。
   */
  videoGenerationEnabled: boolean

  /** 上下文管理：默认只开自动压缩，智能窗口模式要用户自己打开。 */
  contextManagement: ContextManagementSettings

  /** 设置 › 实验。见 `ExperimentalSettings` */
  experimental: ExperimentalSettings

  /**
   * 执行本地命令 / 命令钩子 / 新建终端用哪个 shell。
   *
   * ★ **机器本地**的选择:它描述的是这台机器上装了哪些 shell,所以既不该由
   * 云同步搬到另一台机器上,也不作用于 SSH —— SSH 那侧跑的是远端自己的 shell。
   * `'system'` 由主进程按平台解析,这一层只负责存取。
   */
  shell: ShellPreference

  /**
   * 设置 › 通用:文件树右键菜单第一行「在 X 中打开」用哪个程序。值是
   * `workspace:listOpenTargets` 给出的 id;空串 = 自动(见 `pickPrimaryTarget` 的落点顺序)。
   *
   * ★ 和 `shell` 同类,是**机器本地**的选择 —— 它描述的是这台机器上装了什么,
   *   换配置档不该把它换掉(见 `config-profile.ts` 的 `machineLevelPatch`)。
   */
  defaultOpenTarget: string

  /** 子代理(方案 §4.9 / 界面「Agent 资源调度」) */
  subagent: {
    /** 界面:「默认子代理模型」 */
    model: string
    /** 与 `model` 成对,见 `defaultModelProviderId` */
    modelProviderId?: string
    /**
     * 界面:「子代理思考深度」。`'inherit'` = 跟随本轮对话(父 run 这一轮的档位)。
     *
     * ★ 和 `model` 是**两个独立的选择**:换一个便宜模型跑量的子代理,照样可能
     *   需要在难题上想深一点。三档来源(子代理文件 > 这一栏 > 父 run)连同
     *   「为什么还要按子代理自己的模型归一化一次」都在 `subagent-thinking.ts`。
     */
    thinking: SubagentThinking
    /** 界面:「单对话子代理上限(推荐 4)」 */
    perSessionLimit: number
    /** 全局子代理池,对应界面「并发上限 0–10」 */
    globalLimit: number
  }

  /** 本地模型网关 —— ★ 默认关闭(方案 §5.4 第 1 条) */
  gateway: {
    enabled: boolean
    /** 需求：远程服务可显式启用 LAN 监听；旧设置缺席时仍只绑定 loopback。 */
    allowRemote?: boolean
    /** 19836 被占时动态选;这里存的是**期望**端口 */
    preferredPort: number
    failover: boolean
  }

  /**
   * 设置 › 模型:同一家供应商的**多个登录账号**之间自动轮换(schema 第 24 条)。
   *
   * ★★ **和 `gateway.failover` 是两件事,所以是两个开关。**
   * 那一个管的是「这家挂了换**另一家**供应商」,出厂关闭,且用户在模型药丸里
   * 钉死某家时会被刻意绕过(`router.ts` 的 `candidates`)。这一个管的是
   * 「Codex 的 A 号被限流了换 Codex 的 B 号」—— 钉死 Codex 的用户**正需要**
   * 后者继续工作。共用一个开关的表现是:用户钉死了 Codex,于是他的第二个
   * Codex 账号永远不会被用到,而界面上那两个账号都显示"可用"。
   *
   * ★ 出厂**开启**:配了第二个账号这件事本身就是"限流了请用它"的意思。
   *   关掉之后只用当前账号,失败也不切(见 `selectAccount`)。
   */
  providerAccountRotation: boolean

  notifications: {
    /** 三类音效,对应 InteractionKind(方案 §4.6) */
    taskComplete: boolean
    permissionApproval: boolean
    planApproval: boolean
  }

  /** 设置 › 连接 › 网络:上游空闲超时(秒)。生效点在 `UpstreamRouter`,实时读取,改完即生效。 */
  upstreamIdleTimeoutSeconds: number

  /**
   * 设置 › 通用 › Agent:每轮请求给模型的最大输出 Token。
   *
   * ★ 这是全应用**唯一**决定输出额度的地方 —— 模型目录里那条 `maxOutputTokens`
   * 只参与模型自身的校验与导入导出,不再压住这个值(理由写在
   * `shared/agent/run-request.ts` 的 `resolveMaxOutputTokens` 上)。
   * run 开始时取一次快照,和「默认权限档位」同一个口径:改完下一次新回复生效。
   */
  maxOutputTokens: number

  /**
   * 界面「连接 › 网络」那一页。★ 它**真的作用于全应用的出站请求** ——
   * `main/net/proxy.ts` 把它翻译成 `session.defaultSession.setProxy`,
   * 于是模型请求、MCP 的 HTTP 传输、搜索适配器一并跟着走(它们都经 `net.fetch`)。
   *
   * 形状与全部纯函数在 `proxy.ts`,那边的文件头解释了为什么拆成三段。
   */
  proxy: ProxySettings

  /**
   * 设置 › 连接 › 搜索 底部那一小节:免 Key 的内置搜索兜底。
   *
   * 需求:一个搜索服务都没配(或配了但全挂)时,`web_search` 仍然应该真的搜一次,
   * 而不是直接回一句「去配 Key」。内置链路自己带公共 SearxNG 实例,
   * 这里存的是**用户自建的那一个实例地址**,填了就优先用它。
   *
   * ★ 空串 = 没填,不是 `null` —— 与 `defaultModel` / `permissionReviewerModel`
   *   这一类「空串表示未配置」的既有写法一致,免得 patch 里多一种 `null` 语义。
   *
   * ★ 这是一个**机器本地**的选择(自建实例通常是 `localhost:8080`),
   *   语义上和 `shell` 同类。目前 `shared/domain/config-sync.ts` 不搬运 `AppSettings`,
   *   所以不需要额外标记;哪天配置同步开始覆盖设置,这一项必须留在本机。
   */
  builtinSearch: {
    /** 自建 SearxNG 实例地址,空串 = 未填。校验与放宽规则见 `main/search/builtin/instances.ts` */
    searxngUrl: string
  }

  /** 设置 › 数据：只保存本机备份偏好，不包含任何云端开关。 */
  data: DataSettings

  /**
   * 设置 › 偏好 › 个性化。★ 这一块**会进系统提示词** —— 它是这张表里
   * 唯一一个不只影响界面、而是直接改变模型看到什么的字段。
   */
  personalization: PersonalizationSettings

  /** 偏好 › 快捷键。 */
  shortcuts: ShortcutSettings

  /** 主题工作室设置；旧数据库缺席时由 mergeSettings 铺默认值。 */
  themeStudio: ThemeStudioSettings
}

export const DEFAULT_SETTINGS: AppSettings = {
  theme: 'system',
  activeThemeProfileId: null,
  locale: 'zh-CN',
  colorTheme: { id: DEFAULT_COLOR_THEME_ID, seed: 0, custom: DEFAULT_CUSTOM_SEED },
  imageTheme: { id: null, render: 'blur' },
  // 安全默认：任何会改变文件或执行命令的敏感操作都先询问用户。
  defaultPermissionMode: 'ask',
  permissionReviewerModel: '',
  goalEvaluatorModel: '',
  modelProposedGoals: 'auto',
  // 压缩出厂「跟随会话模型 + 跟随会话档位」—— 除了档位不再硬编码 off,
  // 这就是引入这两栏之前的行为,老用户升级后压缩发给谁一个字都不变。
  compactModel: '',
  compactThinking: INHERIT_THINKING,
  // 输入框补全:建议下一步与本地补全出厂开,打字续写出厂关(理由见 AppSettings 上的字段说明)
  promptSuggestions: true,
  completionModel: '',
  localCompletion: true,
  inlineCompletion: false,
  inlineCompletionModel: '',
  defaultModel: '',
  // 空 = 没选过生图模型,`generate_image` 不下发(见 AppSettings.imageModel 的说明)
  imageModel: '',
  // 生图开关出厂开启 —— 理由见 AppSettings.imageGenerationEnabled
  imageGenerationEnabled: true,
  // 空 = 没选过视频模型,`generate_video` 的**新建**动作不下发(见 AppSettings.videoModel)
  videoModel: '',
  // 视频生成开关出厂开启 —— ★ 它只管"要不要新建",已提交任务照常收取,
  // 理由写在 AppSettings.videoGenerationEnabled 那段。
  videoGenerationEnabled: true,
  contextManagement: { autoCompact: true },
  // 实验功能一律出厂关闭 —— 理由见 ExperimentalSettings
  experimental: { workflow: false, workflowConfirm: true },
  upstreamIdleTimeoutSeconds: DEFAULT_UPSTREAM_IDLE_TIMEOUT_SECONDS,
  maxOutputTokens: DEFAULT_MAX_OUTPUT_TOKENS,
  shell: 'system',
  defaultOpenTarget: '',
  // 思考档位出厂「跟随本轮对话」—— 这正是引入这一栏之前的行为,老用户升级后
  // 子代理的档位一个字都不变(见 `subagent-thinking.ts`)。
  subagent: { model: '', thinking: INHERIT_THINKING, perSessionLimit: 4, globalLimit: 4 },
  gateway: { enabled: false, preferredPort: 19836, failover: false },
  providerAccountRotation: true,
  notifications: { taskComplete: true, permissionApproval: true, planApproval: true },
  proxy: structuredClone(DEFAULT_PROXY),
  builtinSearch: { searxngUrl: '' },
  data: { backupDirectory: null, backupFrequency: 'manual' },
  personalization: { name: '', background: '', instructions: '' },
  shortcuts: { openSettings: 'CmdOrCtrl+,' },
  themeStudio: structuredClone(DEFAULT_THEME_STUDIO)
}

/**
 * `settings:update` 的入参 —— 顶层标量整给,**嵌套块可以只给要改的那一个属性**。
 *
 * ★ 这个类型存在的唯一理由是消灭一整类竞态,不是为了少打几个字。
 * 原来的契约是 `Partial<AppSettings>` + 主进程浅合并,于是「改一个属性」在
 * 渲染层只能写成 `{ gateway: { ...settings.gateway, failover: true } }` ——
 * 而 `settings` 是 prop,**在一次 IPC 往返回来之前它是旧的**。连点两个属于
 * 同一块的开关(通知页那三个音效是紧挨着的三行,网关页也是),第二次写就把
 * 第一次写的值原样覆盖回去。表现是「我明明打开了,它自己关了」,而且只在手快时出现。
 *
 * 深合并之后调用点写 `{ gateway: { failover: true } }` 就够了,兄弟属性
 * 根本不经过渲染层,也就无从被旧值覆盖。
 */
export type AppSettingsPatch = {
  [K in keyof AppSettings]?: AppSettings[K] extends object
    ? Partial<AppSettings[K]>
    : AppSettings[K]
}

/**
 * 纯函数,不改 `current`。主进程 `store.updateSettings` 是唯一调用点,
 * 放在 shared 是为了让它能在 node 环境的 vitest 里被直接测(见同目录 __tests__)。
 *
 * 逐字段展开而不是遍历 `Object.entries`:后者要么丢类型要么满地 cast,
 * 而这里一共就十个字段。下面那张 `PATCHABLE_KEYS` 表是**编译期哨兵** ——
 * 给 `AppSettings` 加字段却忘了在这里合并,类型检查当场就红。
 */
export function mergeSettings(current: AppSettings, patch: AppSettingsPatch): AppSettings {
  const next: AppSettings = structuredClone(current)

  if (patch.theme !== undefined) next.theme = patch.theme
  if (patch.activeThemeProfileId !== undefined) next.activeThemeProfileId = patch.activeThemeProfileId
  if (patch.locale !== undefined) next.locale = patch.locale
  if (patch.defaultPermissionMode !== undefined) {
    next.defaultPermissionMode = patch.defaultPermissionMode
  }
  // ★★ 八个「模型别名 + 供应商」配对一律**成对写**:只要 patch 给了别名,
  //    供应商就跟着 patch 走,哪怕 patch 里它是 `undefined`(那是「取消锁定」,
  //    供应商被删时的降级路径就靠这个)。
  //    只写别名、让旧 providerId 留下来的话,就会得到「新别名 + 旧供应商」——
  //    正是 `model-selection.ts` 那一整个模块要消灭的那个 bug,在它自己的
  //    合并函数里复活一次。这八处一个都不能漏。
  if (patch.permissionReviewerModel !== undefined) {
    next.permissionReviewerModel = patch.permissionReviewerModel
    next.permissionReviewerModelProviderId = patch.permissionReviewerModelProviderId
  }
  if (patch.goalEvaluatorModel !== undefined) {
    next.goalEvaluatorModel = patch.goalEvaluatorModel
    next.goalEvaluatorModelProviderId = patch.goalEvaluatorModelProviderId
  }
  if (patch.compactModel !== undefined) {
    next.compactModel = patch.compactModel
    next.compactModelProviderId = patch.compactModelProviderId
  }
  /*
    ★ 坏值退回**当前值**,同 `subagent.thinking`(那一段写了完整理由):
    认不出的档位会一路流到 `THINKING_BUDGET[档位]` 变成 NaN 写进请求体,
    而界面上那一栏看起来完全正常;静默改成 `'inherit'` 则会让用户以为
    自己选的档位在生效。
  */
  if (patch.compactThinking !== undefined && isSubagentThinking(patch.compactThinking)) {
    next.compactThinking = patch.compactThinking
  }
  // ★ 三档白名单:坏值退回**当前值**(不是硬退回 `'auto'`)—— 这一项管的是
  //   「要不要弹审批」,静默放宽成 `'auto'` 是这一整类改动里最不该出现的失败形态。
  if (isModelProposedGoals(patch.modelProposedGoals)) {
    next.modelProposedGoals = patch.modelProposedGoals
  }
  // 输入框补全:三个开关只认布尔(同 imageGenerationEnabled),两个模型成对写
  if (typeof patch.promptSuggestions === 'boolean') next.promptSuggestions = patch.promptSuggestions
  if (patch.completionModel !== undefined) {
    next.completionModel = patch.completionModel
    next.completionModelProviderId = patch.completionModelProviderId
  }
  if (typeof patch.localCompletion === 'boolean') next.localCompletion = patch.localCompletion
  if (typeof patch.inlineCompletion === 'boolean') next.inlineCompletion = patch.inlineCompletion
  if (patch.inlineCompletionModel !== undefined) {
    next.inlineCompletionModel = patch.inlineCompletionModel
    next.inlineCompletionModelProviderId = patch.inlineCompletionModelProviderId
  }
  if (patch.defaultModel !== undefined) {
    next.defaultModel = patch.defaultModel
    next.defaultModelProviderId = patch.defaultModelProviderId
  }
  if (patch.imageModel !== undefined) {
    next.imageModel = patch.imageModel
    next.imageModelProviderId = patch.imageModelProviderId
  }
  // 只认布尔,坏值退回当前值:这一项会从磁盘/云同步读回,一个非布尔值落库后
  // 桥那边的 `=== true` 会把它静默读成「关」,症状又是「工具不见了」
  if (typeof patch.imageGenerationEnabled === 'boolean') {
    next.imageGenerationEnabled = patch.imageGenerationEnabled
  }
  // 视频:成对写 + 布尔白名单,与生图那两处逐字同构(理由见上面那两段)。
  if (patch.videoModel !== undefined) {
    next.videoModel = patch.videoModel
    next.videoModelProviderId = patch.videoModelProviderId
  }
  if (typeof patch.videoGenerationEnabled === 'boolean') {
    next.videoGenerationEnabled = patch.videoGenerationEnabled
  }
  if (patch.contextManagement !== undefined) {
    next.contextManagement = { ...next.contextManagement, ...patch.contextManagement }
  }
  // 只认布尔(同 imageGenerationEnabled):非布尔落库后 `=== true` 会把它静默读成「关」
  if (typeof patch.experimental?.workflow === 'boolean') {
    next.experimental = { ...next.experimental, workflow: patch.experimental.workflow }
  }
  if (typeof patch.experimental?.workflowConfirm === 'boolean') {
    next.experimental = { ...next.experimental, workflowConfirm: patch.experimental.workflowConfirm }
  }
  // ★ 范围外的坏值退回当前值而不是默认值 —— 这一项改小了会把慢模型全数
  //   秒杀,静默放宽/收紧都不是可接受的失败形态(shell 那条同理)。
  if (isUpstreamIdleTimeoutSeconds(patch.upstreamIdleTimeoutSeconds)) {
    next.upstreamIdleTimeoutSeconds = patch.upstreamIdleTimeoutSeconds
  }
  // ★ 同理退回当前值:这一项直接变成请求体里的 max_tokens,静默改成默认值
  //   会让用户以为自己设的额度生效了,而长回答照旧被截断。
  if (isMaxOutputTokens(patch.maxOutputTokens)) {
    next.maxOutputTokens = patch.maxOutputTokens
  }
  // ★ 只认枚举:盘上/导入进来的坏值一个都不许落库,也不许被当成 `'system'`
  //   悄悄生效(`next` 是 current 的克隆,拒绝就是保留用户当前那一个)。
  if (isShellPreference(patch.shell)) next.shell = patch.shell
  // 同理只认形状合法的 id;「这个 id 本机有没有」不在这里判,见 `isOpenTargetPreference`
  if (isOpenTargetPreference(patch.defaultOpenTarget)) next.defaultOpenTarget = patch.defaultOpenTarget

  // 六个嵌套块:深一层。再深就没有了 —— AppSettings 只有两层,
  // 通用深合并在这里是纯粹的负担(它还得决定数组怎么办)。
  if (patch.colorTheme !== undefined) next.colorTheme = { ...next.colorTheme, ...patch.colorTheme }
  if (patch.imageTheme !== undefined) next.imageTheme = { ...next.imageTheme, ...patch.imageTheme }
  // Legacy imageTheme selections are promoted into the studio on first read.
  // A missing themeStudio field means this is an old persisted settings blob;
  // explicit studio patches always win.
  if (patch.themeStudio === undefined && patch.imageTheme?.id !== undefined) {
    next.themeStudio = {
      ...next.themeStudio,
      wallpaperAssetId: patch.imageTheme.id,
      render: patch.imageTheme.render ?? next.themeStudio.render
    }
  }
  if (patch.subagent !== undefined) {
    next.subagent = { ...next.subagent, ...patch.subagent }
    // ★ 这一行是那条「成对写」规则在**浅合并**下的补丁:上面的 spread 只覆盖
    //   patch 里出现过的键,于是只给 `model` 时旧的 `modelProviderId` 会原样留下。
    if (patch.subagent.model !== undefined) next.subagent.modelProviderId = patch.subagent.modelProviderId
    /*
      ★ 思考档位只认枚举,坏值退回**当前值**(不是 `'inherit'`)—— 上面那个 spread
      已经把它写进去了,所以这里判的是合并之后的结果。

      不拦会怎样:一个认不出的档位会原样进 `RunRequest.thinking`,而它取
      `THINKING_BUDGET[档位]` 得到 undefined,再经 `clampBudget` 变成 **NaN**
      写进请求体的思考预算 —— 上游收到的是个 NaN,而界面上那一栏看起来完全正常。
      退回当前值而不是缺省值,是因为静默改成「跟随对话」会让用户以为自己选的档位
      在生效,而那正是这一栏唯一要说清的事。
    */
    if (!isSubagentThinking(next.subagent.thinking)) next.subagent.thinking = current.subagent.thinking
  }
  if (patch.gateway !== undefined) next.gateway = { ...next.gateway, ...patch.gateway }
  if (patch.providerAccountRotation !== undefined) {
    next.providerAccountRotation = patch.providerAccountRotation
  }
  if (patch.notifications !== undefined) {
    next.notifications = { ...next.notifications, ...patch.notifications }
  }
  // ★ 经一道迁移:旧库里这一块是 `{ enabled, url }`。`migrateLegacyProxy` 只在
  // 三段字段缺席时才把 url 拆开,所以它对正常的表单 patch 是恒等的 —— 理由在 proxy.ts
  if (patch.proxy !== undefined) {
    next.proxy = { ...next.proxy, ...migrateLegacyProxy(patch.proxy) }
  }
  if (patch.data !== undefined) next.data = mergeDataSettings(next.data, patch.data)
  // ★ 只认字符串:坏值(数字、对象)原样落库的话,内置搜索会拿着它去 `new URL()`,
  //   表现为搜索每次都在同一处抛,而设置页看上去一切正常。trim 在这里做掉,
  //   免得末尾一个空格让 `=== ''` 这条「没填」的判断失效。
  if (patch.builtinSearch !== undefined && typeof patch.builtinSearch.searxngUrl === 'string') {
    next.builtinSearch = { searxngUrl: patch.builtinSearch.searxngUrl.trim() }
  }
  if (patch.personalization !== undefined) {
    next.personalization = mergePersonalization(next.personalization, patch.personalization)
  }
  if (patch.shortcuts !== undefined) {
    const candidate = patch.shortcuts
    if (typeof candidate === 'object' && candidate !== null && !Array.isArray(candidate)) {
      const openSettings = candidate.openSettings
      if (typeof openSettings === 'string' && openSettings.trim().length > 0) {
        next.shortcuts = { openSettings: openSettings.trim() }
      }
    }
  }
  if (patch.themeStudio !== undefined) {
    next.themeStudio = {
      ...next.themeStudio,
      ...patch.themeStudio,
      overrides: patch.themeStudio.overrides ?? next.themeStudio.overrides
    }
  }

  return next
}

/** 见 `mergeSettings`:漏掉一个字段就编译不过。运行时不用它。 */
const PATCHABLE_KEYS: Record<keyof AppSettings, true> = {
  theme: true,
  activeThemeProfileId: true,
  locale: true,
  colorTheme: true,
  imageTheme: true,
  defaultPermissionMode: true,
  permissionReviewerModel: true,
  permissionReviewerModelProviderId: true,
  goalEvaluatorModel: true,
  goalEvaluatorModelProviderId: true,
  modelProposedGoals: true,
  compactModel: true,
  compactModelProviderId: true,
  compactThinking: true,
  promptSuggestions: true,
  completionModel: true,
  completionModelProviderId: true,
  localCompletion: true,
  inlineCompletion: true,
  inlineCompletionModel: true,
  inlineCompletionModelProviderId: true,
  defaultModel: true,
  defaultModelProviderId: true,
  imageModel: true,
  imageModelProviderId: true,
  imageGenerationEnabled: true,
  videoModel: true,
  videoModelProviderId: true,
  videoGenerationEnabled: true,
  contextManagement: true,
  experimental: true,
  upstreamIdleTimeoutSeconds: true,
  maxOutputTokens: true,
  shell: true,
  defaultOpenTarget: true,
  subagent: true,
  gateway: true,
  providerAccountRotation: true,
  notifications: true,
  proxy: true,
  builtinSearch: true,
  data: true,
  personalization: true,
  shortcuts: true,
  themeStudio: true
}
void PATCHABLE_KEYS

/** 界面「数据」页:数据库大小 / 对话数量 / 消息数量 / 优化存储(VACUUM)。 */
export interface StorageStats {
  dbBytes: number
  walBytes: number
  conversationBytes: number
  attachmentBytes: number
  conversationCount: number
  messageCount: number
  attachmentCount: number
  dataDirectory: string
  lastBackupAt: number | null
}
