/**
 * 渲染层这一侧的插件状态。
 *
 * ★ **只读投影,没有任何句柄。** 这里存的是一份 `PluginCatalog`(可结构化克隆
 * 的纯数据),所有写操作都是一次 IPC —— 主进程返回新的 catalog,这里整份换掉。
 *
 * 为什么不做增量:插件数量是个位数,而增量协议要多一套「我这份过期了吗」的
 * 判断。`mcp:changed` 当初就是那么走弯路的 —— 推了一条没有内容的通知,
 * 渲染层还得自己再拉一次,于是两份状态之间永远差着一个往返。
 */
import { create } from 'zustand'
import { isRunnable, type InstalledPlugin, type PluginActivity, type PluginCatalog } from '../../../shared/plugin/state'
import type { PluginMarketItem, PluginUpdate } from '../../../shared/plugin/market'
import type { PluginPermission } from '../../../shared/plugin/permission'
import {
  evaluateWhen,
  normalizeMenuIcon,
  parseMenuGroup,
  type TabMenuItem,
  type WhenContext
} from '../../../shared/plugin/contribution'
import { registerPluginMessages, translate, unregisterPluginMessages, type TranslationKey } from '../i18n'
import { registerPluginPresenters, type ToolPresenter } from '../../../shared/domain/tool-presenter'
import { invoke, on } from '../services/ipc'
import type { InstallProgress } from '../lib/install-progress'
import { pluginErrorKey, pluginMessageKey } from '../views/extensions/plugins/plugin-error'
import { toast } from './toast'

/**
 * 一次安装正在进行到哪一步。装完 / 失败之后这条就从表里消失。
 *
 * ★ 类型本体搬去了 `lib/install-progress.ts` —— Skill 那边的安装推的是**同一个
 * 形状**,两处各写一份的话,主进程某天加个字段只会在其中一处露出来。这里留一个
 * 别名是为了不动插件那一侧十几处 `PluginInstallProgress` 的引用。
 */
export type PluginInstallProgress = InstallProgress

interface PluginsState {
  catalog: PluginCatalog
  activity: PluginActivity[]
  loading: boolean
  /** 市场列表。**和已装列表分开存** —— 它们的生命周期完全不同 */
  market: PluginMarketItem[]
  marketLoading: boolean
  marketError: string | null
  marketPage: number
  marketHasNext: boolean
  /**
   * 正在装的那些,按 `market:<slug>` / `local:<路径>` 索引。
   *
   * ★ 放 store 不放组件:这条事件是**全局广播**的(别的窗口发起的安装,
   * 这个窗口的市场页也要看到那颗按钮在跑),而组件里的 useState 一关页面
   * 就没了。
   */
  installProgress: Record<string, PluginInstallProgress>
  /** 最后一次失败的原因,同一套 key。用户再点一次安装时清掉 */
  installError: Record<string, TranslationKey>
  updates: PluginUpdate[]
  checkingUpdates: boolean
  updatingAll: boolean
  load: () => Promise<void>
  setEnabled: (pluginId: string, enabled: boolean) => Promise<void>
  uninstall: (pluginId: string) => Promise<void>
  installFromPicker: () => Promise<void>
  grant: (pluginId: string, permissions: PluginPermission[]) => Promise<void>
  revoke: (pluginId: string, permissions: PluginPermission[]) => Promise<void>
  loadActivity: (pluginId?: string) => Promise<void>
  runCommand: (pluginId: string, commandId: string, args?: { workspaceId: string }) => Promise<void>
  loadMarket: (query?: { q?: string; category?: string; page?: number }) => Promise<void>
  installFromMarket: (slug: string, version?: string) => Promise<void>
  checkUpdates: (force?: boolean) => Promise<void>
  updateAll: () => Promise<void>
  /** 某个菜单挂载点上的插件贡献项,已归一成 `TabMenuItem` */
  menuItems: (menuId: string, context: WhenContext) => TabMenuItem[]
}

const EMPTY: PluginCatalog = { plugins: [], hostVersion: '' }

/**
 * 一次安装最多挂多久。
 *
 * ★ 没有这个兜底的话,主进程崩掉 / 那一帧终态事件丢了,进度条就**永远**留在
 * 界面上,而那颗按钮同时也永远点不动 —— 用户唯一的出路是重启应用。
 * 3 分钟是按 20MB 上限在很慢的网上算的。
 */
const PROGRESS_STALE_MS = 3 * 60 * 1000

/**
 * `done` 之后延迟多久再把进度条撤掉。
 *
 * ★ 不能立刻撤:主进程是先 `plugins:changed` 后 `done`,而渲染层收到
 * changed 还要再打一次 `plugins:list` 才拿到新 catalog。中间那几十毫秒里
 * 撤掉进度条,按钮会闪回「安装插件」再跳到「已安装」。
 *
 * ★ **发起安装的那个窗口走不到这条路** —— 它在 invoke resolve 时把 catalog
 * 和进度**放在同一个 `set()` 里**换掉,一帧都不闪。这个延迟是给其他窗口的,
 * 它们手上只有事件,没有那个返回值。
 */
const DONE_LINGER_MS = 400

let subscribed = false
/** 这个会话查过一次更新了吗 —— 见 `load()` 里为什么需要这个闸 */
let updatesEverChecked = false

/**
 * ★ 订阅装在第一次 `load()` 里,不在模块顶层、也不在组件的 useEffect 里 ——
 * 同 `stores/mcp.ts` 的取向。顶层 `on(...)` 会在 import 那一刻去碰
 * `window.nextcowork`,任何 import 到这个 store 的单测都会在 import 阶段炸;
 * 放进组件的 useEffect 则是页面一关就收不到了,而装插件这件事在用户离开
 * 插件页之后还在继续。
 *
 * `App.tsx` 开机就调 `load()`,所以这条订阅在启动时就装好了。
 */
function subscribeOnce(): void {
  if (subscribed) return
  subscribed = true
  on('plugins:installProgress', (event) => {
    const { key, phase } = event
    if (phase === 'failed') {
      usePluginsStore.setState((state) => ({
        installProgress: without(state.installProgress, key),
        installError: { ...state.installError, [key]: pluginMessageKey(event.messageKey ?? '') }
      }))
      return
    }
    if (phase === 'done') {
      setTimeout(() => {
        usePluginsStore.setState((state) => ({ installProgress: without(state.installProgress, key) }))
      }, DONE_LINGER_MS)
      return
    }
    usePluginsStore.setState((state) => ({
      installProgress: {
        ...sweepStale(state.installProgress),
        [key]: {
          phase,
          ...(event.received === undefined ? {} : { received: event.received }),
          ...(event.total === undefined ? {} : { total: event.total }),
          startedAt: state.installProgress[key]?.startedAt ?? Date.now()
        }
      }
    }))
  })
}

function without<T>(record: Record<string, T>, key: string): Record<string, T> {
  if (!(key in record)) return record
  const next = { ...record }
  delete next[key]
  return next
}

/** 顺手扫掉挂太久的 —— 挂在每一帧进度上,不另起定时器 */
function sweepStale(record: Record<string, PluginInstallProgress>): Record<string, PluginInstallProgress> {
  const now = Date.now()
  const stale = Object.keys(record).filter((key) => now - record[key]!.startedAt > PROGRESS_STALE_MS)
  if (stale.length === 0) return record
  const next = { ...record }
  for (const key of stale) delete next[key]
  return next
}

export const usePluginsStore = create<PluginsState>((set, get) => ({
  catalog: EMPTY,
  activity: [],
  loading: false,
  market: [],
  marketLoading: false,
  marketError: null,
  marketPage: 1,
  marketHasNext: false,
  installProgress: {},
  installError: {},
  updates: [],
  checkingUpdates: false,
  updatingAll: false,

  async load() {
    subscribeOnce()
    set({ loading: true })
    try {
      const catalog = await invoke('plugins:list', undefined)
      applyCatalog(catalog)
      set({ catalog, loading: false })
      /*
        ★★ **catalog 变了,更新表就得跟着重算。**

        `PluginUpdate.escalatedPermissions` 是拿 `approvedRequired` 算出来的
        一份快照,而用户在详情页点一下「批准」,那份快照立刻就过期了 ——
        界面上半截还挂着「这一版要新增能力:workspace.write」,下半截同一条
        能力已经是绿的、写着「撤销」。同屏自相矛盾比信息滞后更糟:用户
        没法判断该信哪一个。

        挂在 `load()` 上而不是各个写操作里,是因为改 catalog 的来源不止一处:
        本窗口的批/撤、**别的窗口**装了个插件、主进程自己把某个插件标成
        error —— 它们最后都汇到 `plugins:changed` → 这里。逐个写操作去加的话,
        漏一个就是一处对不上。

        ★ `everChecked` 这个闸是必要的:没打开过插件页的会话不该为一件
        它没要求过的事多打一次网络。第一次 `checkUpdates()` 之后才跟。
        (跟上之后也基本不打网络 —— 主进程压着 10 分钟的市场列表缓存,
        这里只是拿新的批准状态把那张表重新 join 一遍。)
      */
      if (updatesEverChecked) void get().checkUpdates()
    } catch {
      set({ loading: false })
    }
  },

  async setEnabled(pluginId, enabled) {
    const catalog = await invoke('plugins:setEnabled', { pluginId, enabled })
    applyCatalog(catalog)
    set({ catalog })
  },

  async uninstall(pluginId) {
    const catalog = await invoke('plugins:uninstall', { pluginId })
    unregisterPluginMessages(pluginId)
    applyCatalog(catalog)
    set({ catalog })
  },

  async installFromPicker() {
    const picked = await invoke('plugins:pickPackage', undefined)
    if (picked === null) return
    const key = `local:${picked.path}`
    set((state) => ({ installError: without(state.installError, key) }))
    try {
      const catalog = await invoke('plugins:installPackage', { path: picked.path })
      applyCatalog(catalog)
      // ★ 同一个 `set()` 里换 catalog + 撤进度 —— 分两次的话中间会闪一帧旧状态
      set((state) => ({ catalog, installProgress: without(state.installProgress, key) }))
    } catch (error) {
      set((state) => ({
        installProgress: without(state.installProgress, key),
        installError: { ...state.installError, [key]: pluginErrorKey(error) }
      }))
      throw error
    }
  },

  async grant(pluginId, permissions) {
    const catalog = await invoke('plugins:grantPermissions', { pluginId, permissions })
    applyCatalog(catalog)
    set({ catalog })
    // 更新表的重算不在这儿 —— 主进程会播 `plugins:changed`,统一由 `load()` 接(见那里)
  },

  async revoke(pluginId, permissions) {
    const catalog = await invoke('plugins:revokePermissions', { pluginId, permissions })
    applyCatalog(catalog)
    set({ catalog })
  },

  async loadActivity(pluginId) {
    const activity = await invoke('plugins:activity', { ...(pluginId === undefined ? {} : { pluginId }) })
    set({ activity })
  },

  async runCommand(pluginId: string, commandId: string, args?: { workspaceId: string }): Promise<void> {
    /*
      ★ **失败必须说出来。**

      调用点(`shell/Dock.tsx`、`shell/commands.ts`)都是 `void runCommand(...)` ——
      即发即忘。以前这里直接把 rejection 抛出去,于是一次失败的点击表现为
      「点了没反应」:没有提示、没有日志、控制台干净。而插件命令会失败的
      地方很多(没激活、能力没批、路径被拒、插件自己的代码抛了),
      用户唯一能做的就是反复点。

      收口在这一层而不是每个调用点:漏一个调用点就漏一种静默失败。
    */
    try {
      await invoke('plugins:runCommand', { pluginId, commandId, ...(args === undefined ? {} : { args }) })
    } catch (error) {
      /*
        面向用户的是一句人话;原始错误进 console 供排查 —— 主进程那边抛出来的
        是英文诊断句(比如 `plugin acme.excalidraw could not be activated`),
        它不该出现在界面上,但排查时又不能没有。`[plugins]` 前缀同 App.tsx。

        ★ `remote-unsupported` 是宿主与自家 CLI 插件(claude-code / codex)的
        约定标记:插件在 `tabs.openTerminal` 拿到 `reason: 'remote'` 后抛这个
        词。对它给一句**说得到点上**的话,其余失败维持通用文案。
      */
      console.error(`[plugins] ${pluginId} 的命令 ${commandId} 执行失败`, error)
      if (error instanceof Error && error.message.includes('remote-unsupported')) {
        toast.error(translate('plugins.terminalRemote'), `plugin-command-${commandId}`)
        return
      }
      toast.error(translate('plugins.commandFailed', { plugin: pluginId }), `plugin-command-${commandId}`)
    }
  },

  async loadMarket(query = {}) {
    set({ marketLoading: true, marketError: null })
    try {
      const result = await invoke('plugins:marketList', query)
      set({ market: result.items, marketPage: result.page, marketHasNext: result.hasNext, marketLoading: false })
    } catch (error) {
      /*
        ★ 市场拉不动**不是空列表**。给空列表的话,界面上写着「还没有插件」,
        而真相是「连不上市场」—— 用户会以为这个市场是空的,而不是去检查网络。
      */
      set({ marketLoading: false, marketError: error instanceof Error ? error.message : String(error) })
    }
  },

  async installFromMarket(slug, version) {
    const key = `market:${slug}`
    set((state) => ({ installError: without(state.installError, key) }))
    try {
      const catalog = await invoke('plugins:installMarket', {
        slug,
        ...(version === undefined ? {} : { version })
      })
      applyCatalog(catalog)
      /*
        ★ catalog 和进度在**同一个 `set()`** 里换掉。

        分成两次的话,中间会有一帧「新 catalog 还没到、进度已经撤了」——
        那一帧按钮显示的是「安装插件」,然后才跳成「已安装」。一次成功的
        安装在结尾闪一下"没装上",比慢一点更让人怀疑。
      */
      set((state) => ({ catalog, installProgress: without(state.installProgress, key) }))
      // 装完顺手把更新表重算一遍 —— 刚更新掉的那条要立刻从横幅里消失
      void get().checkUpdates()
    } catch (error) {
      set((state) => ({
        installProgress: without(state.installProgress, key),
        installError: { ...state.installError, [key]: pluginErrorKey(error) }
      }))
      throw error
    }
  },

  async checkUpdates(force = false) {
    updatesEverChecked = true
    set({ checkingUpdates: true })
    try {
      set({ updates: await invoke('plugins:checkUpdates', { force }), checkingUpdates: false })
    } catch {
      /*
        ★ 查不到更新**不清空已有的那张表**,也不弹错。

        这是一件后台的事:进插件页时自动跑一次,用户没要求过它。连不上市场
        就安静地维持现状 —— 为它弹一条错误提示,等于把一次用户没发起的网络
        失败变成一件他必须处理的事。
      */
      set({ checkingUpdates: false })
    }
  },

  async updateAll() {
    set({ updatingAll: true })
    try {
      const result = await invoke('plugins:updateAll', undefined)
      const catalog = await invoke('plugins:list', undefined)
      applyCatalog(catalog)
      set({ catalog, updatingAll: false })
      await get().checkUpdates(true)
      // ★ 失败的那几个要说出来。串行更新里前面成功的已经落盘了,
      //   只报「完成」的话,用户会以为全都更新好了。
      if (result.failed.length > 0) {
        toast.error(
          translate('plugins.updateSummary', { updated: result.updated.length, failed: result.failed.length }),
          'plugin-update-all'
        )
      }
    } catch (error) {
      set({ updatingAll: false })
      toast.error(translate(pluginErrorKey(error)), 'plugin-update-all')
    }
  },

  menuItems(menuId, context) {
    const out: TabMenuItem[] = []
    for (const plugin of get().catalog.plugins) {
      // ★ 禁用的插件,菜单项必须消失 —— 否则点了之后是一条静默失败。
      if (!isRunnable(plugin)) continue
      const contributions = plugin.manifest.contributes.menus[menuId] ?? []
      for (const contribution of contributions) {
        if (!evaluateWhen(contribution.when, context)) continue
        const command = plugin.manifest.contributes.commands.find((c) => c.command === contribution.command)
        if (command === undefined) continue
        const { group, order } = parseMenuGroup(contribution.group)
        out.push({
          id: `${plugin.id}:${contribution.command}`,
          // 清单里写的是 `%cmd.new%`,注册进 i18n 的是 `plugin.<id>.cmd.new`。
          titleKey: `plugin.${plugin.id}.${command.title.slice(1, -1)}`,
          icon: normalizeMenuIcon(command.icon),
          // 品牌图标(iconFile)主进程已转成 data URL 随 catalog 下来;没有就走名字闭集。
          ...(plugin.commandIcons?.[contribution.command] !== undefined
            ? { iconUrl: plugin.commandIcons[contribution.command] }
            : {}),
          group,
          order,
          pluginId: plugin.id,
          action: { kind: 'command', commandId: contribution.command }
        })
      }
    }
    return out
  }
}))

/**
 * catalog 到手之后把插件文案注册进 i18n。
 *
 * ★ 在**这里**做而不是在设置页里做:菜单项、命令名在插件被激活之前就要显示,
 * 而菜单本身就是激活事件的来源。等到打开设置页才注册的话,没开过设置页的
 * 用户看到的是一排 key。
 */
function applyCatalog(catalog: PluginCatalog): void {
  for (const plugin of catalog.plugins) {
    /*
      ★ **包内的 l10n 优先,清单兜底。** 顺序反过来的话,包里明明写了
      「新建绘图」,菜单上却一直是 `excalidraw.new` —— 而兜底那一份
      看起来「有值」,所以不会有任何地方报错。
    */
    const bundled = plugin.messages ?? {}
    const fallback = pluginMessageFallback(plugin)
    for (const locale of ['zh-CN', 'en-US'] as const) {
      const dict = { ...fallback[locale], ...bundled[locale] }
      if (Object.keys(dict).length === 0) continue
      try {
        registerPluginMessages(plugin.id, locale, dict)
      } catch {
        // 注册被拒(前缀不对/超配额)只影响这一个插件的文案,不该打断别的。
      }
    }
  }
  // ★ 文案注册完之后再注入 presenter:title 闭包在渲染时才 translate,此刻只需
  // 保证 externalName → presenter 的映射就位(全量替换,清掉已卸载插件的条目)。
  registerPluginPresenters(buildPluginPresenters(catalog))
}

/**
 * 由工具 externalName + viewType 反查 frame 卡片的挂载目标(iframe origin 用的 pluginId
 * + 包内 HTML 路径)。渲染层拿它把聊天里的 tool_result 卡片指回是哪个插件的哪张卡。
 *
 * ★ 纯函数(接 catalog 传入),不做 hook —— 调用方用 `usePluginsStore((s) => s.catalog)`
 * 选出稳定的 catalog 引用后再 `useMemo`,避免选择器每次返回新对象引发重渲循环。
 * 返回 undefined = 插件已卸载/禁用后清出 catalog,或该 viewType 未声明。
 */
export function frameCardTarget(
  catalog: PluginCatalog,
  externalName: string,
  viewType: string
): { pluginId: string; path: string } | undefined {
  for (const plugin of catalog.plugins) {
    if (!(plugin.tools ?? []).some((t) => t.externalName === externalName)) continue
    const view = plugin.manifest.contributes.cardViews.find((v) => v.viewType === viewType)
    return view === undefined ? undefined : { pluginId: plugin.id, path: view.path }
  }
  return undefined
}

/**
 * 由工具 externalName 反查它属于哪个插件 —— 实时卡片的按钮动作要按 pluginId 回传。
 * 纯函数(接 catalog),同 `frameCardTarget` 的理由。
 */
export function pluginIdForTool(catalog: PluginCatalog, externalName: string): string | undefined {
  for (const plugin of catalog.plugins) {
    if ((plugin.tools ?? []).some((t) => t.externalName === externalName)) return plugin.id
  }
  return undefined
}

/** 有未闭合 `{param}` 的模板视为「参数没齐」——回退静态标题,绝不把花括号漏给用户。 */
const UNFILLED_PARAM_RE = /\{[A-Za-z0-9_]+\}/

/** 只有对象里的字符串/数字字段能当插值参数;流式态只使用当前已经解析出的字段。 */
function asTemplateParams(input: unknown): Record<string, string | number> {
  if (input === null || typeof input !== 'object' || Array.isArray(input)) return {}
  const out: Record<string, string | number> = {}
  for (const [key, value] of Object.entries(input as Record<string, unknown>)) {
    if (typeof value === 'string' || typeof value === 'number') out[key] = value
  }
  return out
}

/** `%ref%` → 已注册的插件 i18n key(与 `PluginConfiguration.tsx` 同一转换)。 */
function pluginTranslationKey(pluginId: string, ref: string): TranslationKey {
  return `plugin.${pluginId}.${ref.replace(/^%|%$/g, '')}` as TranslationKey
}

/**
 * 把 catalog 里每个插件工具的 shape / card 模板构建成 `ToolPresenter`,注入 `tool-presenter`。
 *
 * ★ 在渲染层构建(而非 shared 模块内)的原因:presenter 依赖 i18n 的 `translate` 与当前
 * locale,而 `tool-presenter` 是纯 shared 模块不碰 store。`line` 闭包在**渲染时**才
 * `translate`,所以 locale 切换自动生效,不必因换语言重建。
 *
 * ★ 半截 JSON 容错(硬约束):流式态 `input` 只有当前已经解析出的参数;字段尚未到达时
 * card.title 渲染出的串会残留 `{param}` —— 检测到就回退**无参静态标题**
 * (manifest 的 tool.title),绝不把花括号漏到界面上。
 */
function buildPluginPresenters(catalog: PluginCatalog): Array<[string, ToolPresenter]> {
  const out: Array<[string, ToolPresenter]> = []
  for (const plugin of catalog.plugins) {
    const externalOf = new Map((plugin.tools ?? []).map((t) => [t.name, t.externalName]))
    for (const tool of plugin.manifest.contributes.tools) {
      const externalName = externalOf.get(tool.name)
      if (externalName === undefined) continue
      const staticTitleKey = pluginTranslationKey(plugin.id, tool.title)
      const cardTitleKey = tool.card?.title !== undefined ? pluginTranslationKey(plugin.id, tool.card.title) : undefined
      const cardSummaryKey = tool.card?.summary !== undefined ? pluginTranslationKey(plugin.id, tool.card.summary) : undefined
      const staticTitle = (): string => {
        const rendered = translate(staticTitleKey)
        return rendered === '' ? tool.name : rendered
      }
      out.push([
        externalName,
        {
          shape: tool.shape ?? 'external',
          /*
            插件只声明**一个**标题模板,所以它整条落在行的 `label` 那一格。
            内置工具那种「标签 / 目标 / 目录」三段拆分要求编译期知道字段名
            (`file_path`、`command`),而插件工具的入参形状不可知 ——
            硬拆会在某个插件上把参数放错格子,而那不会报错,只会看起来很怪。
          */
          line: (input) => {
            if (cardTitleKey === undefined) return { label: staticTitle() }
            const rendered = translate(cardTitleKey, asTemplateParams(input))
            if (rendered === '' || UNFILLED_PARAM_RE.test(rendered)) return { label: staticTitle() }
            return { label: rendered }
          },
          ...(cardSummaryKey === undefined
            ? {}
            : {
                summary: (input: unknown): string | undefined => {
                  const rendered = translate(cardSummaryKey, asTemplateParams(input))
                  return rendered === '' || UNFILLED_PARAM_RE.test(rendered) ? undefined : rendered
                }
              })
        }
      ])
    }
  }
  return out
}

/**
 * 包没带 l10n 时的**兜底**文案。
 *
 * ★ 兜底值是 `displayName`,不是 `command.command` —— 后者是**协议标识符**
 * (`excalidraw.new`),把它显示在菜单上等于把内部标识漏给用户,而且看起来
 * 像一条没翻译的 key,没人分得清它是「缺翻译」还是「本来就长这样」。
 * 显示名至少是作者自己起的、给人看的字。
 */
function pluginMessageFallback(plugin: InstalledPlugin): Record<'zh-CN' | 'en-US', Record<string, string>> {
  const dict: Record<string, string> = {}
  for (const command of plugin.manifest.contributes.commands) {
    dict[`plugin.${plugin.id}.${command.title.slice(1, -1)}`] = plugin.manifest.displayName
  }
  /*
    ★ 工具的 title / card 模板也兜底,理由同命令:没兜底时 `translate` 对未注册的
    key 会**原样返回 key**(`plugin.acme.demo.tool.x`),那串会直接显示成工具卡片标题。
    带 `{param}` 的 card 模板兜到 displayName(无花括号)后也不会漏出占位符。
  */
  for (const tool of plugin.manifest.contributes.tools) {
    dict[`plugin.${plugin.id}.${tool.title.slice(1, -1)}`] = plugin.manifest.displayName
    if (tool.card?.title !== undefined) dict[`plugin.${plugin.id}.${tool.card.title.slice(1, -1)}`] = plugin.manifest.displayName
    if (tool.card?.summary !== undefined) dict[`plugin.${plugin.id}.${tool.card.summary.slice(1, -1)}`] = plugin.manifest.displayName
  }
  return { 'zh-CN': dict, 'en-US': dict }
}
