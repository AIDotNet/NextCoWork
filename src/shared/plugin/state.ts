/**
 * 一个已安装插件在**界面上**的样子 —— 主进程与渲染层共用的只读投影。
 *
 * ★ 这里**没有任何句柄**:没有 iframe 引用、没有 MessagePort、没有 Disposable。
 * 渲染层拿到的永远是一份可结构化克隆的数据。把运行期对象混进来会让
 * 「插件详情页」这种纯展示的东西变成必须跑在主进程里的东西。
 */
import type { PluginManifest } from './manifest'
import type { PluginPermission, PluginPermissionState } from './permission'

export type PluginStatus =
  /** 装上了、启用着,但还没被任何激活事件叫醒 */
  | 'idle'
  /** 正在跑 activate() */
  | 'activating'
  | 'active'
  /** 空闲 5 分钟后休眠。下一次激活事件会把它叫回来 */
  | 'asleep'
  /** 用户手动禁用 */
  | 'disabled'
  /**
   * 升级后必选能力变大,**用户不批就不激活**。
   *
   * ★ 这个状态是「装完之后自动更新悄悄扩权」那条路上的闸门,不是一个提示。
   */
  | 'pending-approval'
  /** 装载失败(清单读不懂、engines 不匹配、入口文件缺失) */
  | 'error'

export interface PluginDiagnostic {
  /** 出问题的地方,给插件作者看的:字段路径或包内文件路径 */
  path: string
  message: string
  level: 'info' | 'warn' | 'error'
}

/** 活动日志的一条。环形缓冲,照抄 `main/hooks.ts` 的 `recentFailures`。 */
export interface PluginActivity {
  ts: number
  pluginId: string
  method: string
  /** 人话摘要,**不含参数原文** —— 参数里可能有文件内容 */
  summary: string
  verdict: 'ok' | 'denied' | 'error' | 'timeout'
  durationMs: number
}

/**
 * 插件挂在状态栏上的一格。
 *
 * ★ `textKey` 是 **l10n key**,不是文案。状态栏是全应用**最显眼**的一块
 * 常驻文字 —— 让插件往这里塞任意字符串,等于让它在用户每一秒都看得见的地方
 * 写字,而且切语言之后那行字不会变。
 */
export interface PluginStatusBarItem {
  id: string
  pluginId: string
  textKey: string
  tooltipKey?: string
  /** 点一下执行哪条命令。必须是这个插件自己贡献过的 */
  command?: string
}

/**
 * 插件贡献的一个工具在**界面上**的身份投影 —— 渲染层拿它把聊天里的
 * `tool_call.name`(经 ToolNamer 消毒/哈希后的 externalName)映回是哪个插件的哪个工具。
 *
 * ★ 为什么 catalog 里非它不可:模型回传的、转录里存的都是 externalName,
 * 而它是 `plugin__<pub>_<name>__<tool>` 经截断 + 去重哈希算出来的,渲染层**算不出来**。
 * 主进程用 `ToolRegistry.reserveName`(与将来真正注册时同一个记忆化 namer)预留权威值,
 * 随 catalog 一起下来;这样即便插件还没被激活,它的工具卡片也能画出自定义标题。
 */
export interface PluginToolProjection {
  /** 插件清单里声明的工具名(`contributes.tools[].name`) */
  name: string
  /** ToolNamer 分配的稳定 externalName —— 模型与转录里出现的就是它 */
  externalName: string
}

export interface InstalledPlugin {
  id: string
  manifest: PluginManifest
  status: PluginStatus
  /** 装在哪 —— 全局(userData)还是这个工作区 */
  scope: 'global' | 'workspace'
  /** 包目录的绝对路径。渲染层只用来「在访达里显示」 */
  path: string
  enabled: boolean
  permissions: PluginPermissionState
  diagnostics: PluginDiagnostic[]
  /** 这一版不实现的贡献点,逐条列给作者看 */
  unsupported: string[]
  installedAt: number
  updatedAt: number
  /** 升级时新增的必选能力 —— 授权界面逐条列,不是只说「权限有变化」 */
  pendingPermissions?: PluginPermission[]
  /** 这一刻它挂在状态栏上的那几格。禁用时必须清空 */
  statusBar: PluginStatusBarItem[]
  /**
   * 这个插件贡献的工具的 externalName 投影,按清单顺序。
   *
   * ★ 渲染层用它把 `tool_call.name` 映回 `(pluginId, toolName)`,再 join
   * `manifest.contributes.tools` 拿到 shape / card 模板去画卡片。见 `PluginToolProjection`。
   * 没有贡献工具时省略。
   */
  tools?: PluginToolProjection[]
  /**
   * 命令声明的品牌图标(`contributes.commands[].iconFile`)经主进程读出后的
   * data URL,按 commandId 索引。装载时读一次、随 catalog 下来 —— 菜单在
   * 插件激活之前就要画出图标,和 `messages` 同一条「激活前就得有」的理由。
   * 没有声明 iconFile(或文件读不出来)时省略整个字段。
   */
  commandIcons?: Record<string, string>
  /*
    ★ 这里**故意没有** `skills` 字段,尽管详情页要显示「这个插件提供了哪几条 Skill」。

    想加的话,manager 得知道「扫描器最后认下了哪几条」—— 而那个判定发生在
    `kernel/skill/load.ts` 里(缺 description 的会被作废),结果要回灌进 manager。
    那条回灌路径必然会与真源分叉:插件禁用后 catalog 先更新、skill 重扫在下一次
    发送前才发生,中间这段时间详情页列着几条已经不在目录里的 skill。

    真源是 skill 注册表,而它**已经有一条到渲染层的路**(`skills:list`,
    条目上带 `pluginId`)。详情页按 `pluginId` 过滤那份列表即可 ——
    零新增状态,且按构造就是准的。
  */
  /**
   * 包内 `l10n/` 的词条,按语言分。
   *
   * ★ **必须跟着 catalog 一起下来**,不能另走一条消息:菜单项在插件被激活
   * 之前就要画出来,而菜单本身就是激活事件的来源。分两条路送的话,
   * 菜单会先以 key 的样子出现一帧(甚至一直保持那样,如果第二条消息先到),
   * 再变成文字 —— 见 `renderer/stores/plugins.ts` 的注册处。
   *
   * 值是 `plugin.<pluginId>.<key>` → 文案。
   */
  messages?: Record<string, Record<string, string>>
}

/** 插件系统整体状态,一次 IPC 全量取回。 */
export interface PluginCatalog {
  plugins: InstalledPlugin[]
  /** 宿主这一版的版本号,用于 `engines` 过滤与市场列表 */
  hostVersion: string
  /**
   * 这个宿主实现的**插件 API 版本** —— `engines.nextcowork` 比的是它,不是 `hostVersion`。
   *
   * ★ 两者分开的理由见 `shared/plugin/api-version.ts`:应用版本回答「用户装的是哪一版
   * NextCoWork」(市场灰度按它走),API 版本回答「`nextcowork` 模块的形状是哪一版」。
   * 合成一个的后果是按官方模板写的插件全部被判装载失败。
   *
   * 可选:旧的持久化/旧窗口拿不到这个字段时不显示,而不是显示成 `undefined`。
   */
  apiVersion?: string
}

export function isRunnable(plugin: InstalledPlugin): boolean {
  return plugin.enabled && plugin.status !== 'error' && plugin.status !== 'pending-approval'
}
