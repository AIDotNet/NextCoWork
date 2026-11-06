/**
 * 插件管理页 —— 装 / 启 / 禁 / 卸,加上能力批准、诊断与活动日志。
 *
 * ★ 落在**扩展页**里当第五类资源,而不是另开一个顶层入口:扩展页装的正是
 * 「用户装上来的东西」,Skill / 命令 / 子代理 / 模式 / 钩子已经在那儿了。
 * 插件和它们的区别是带代码,但对用户来说它们回答的是同一个问题:
 * 「我这台机器上多装了什么」。
 *
 * ★ 这一页**不渲染任何插件提供的 HTML**。它显示的全是清单里的声明值
 * (名字、作者、版本、能力、贡献点)和宿主自己的记录(诊断、活动)——
 * 插件的 UI 只出现在受控 webview 里,那是另一期的事。
 */
import { AlertTriangle, ArrowUpCircle, BookOpen, Package, Puzzle, RefreshCw, ShieldCheck, Trash2 } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import type { InstalledPlugin, PluginStatus } from '../../../../../shared/plugin/state'
import type { PluginUpdate } from '../../../../../shared/plugin/market'
import type { PluginPermission } from '../../../../../shared/plugin/permission'
import { Button } from '../../../components/arc/button/button'
import { EmptyState } from '../../../components/arc/empty-state/empty-state'
import SegmentedControl from '../../../components/arc/segmented-control/segmented-control'
import { Switch } from '../../../components/arc/switch/switch'
import { useI18n, type TranslationKey } from '../../../i18n'
import { cn } from '../../../lib/cn'
import { usePluginsStore } from '../../../stores/plugins'
import { useSkillsStore } from '../../../stores/skills'
import { useWindowStore } from '../../../stores/window'
import { PluginConfiguration } from './PluginConfiguration'
import { PluginMarket } from './PluginMarket'
import { pluginErrorKey } from './plugin-error'
import { missingPluginSkills, pluginSkillRows } from './plugin-skills'

const STATUS_KEY: Record<PluginStatus, TranslationKey> = {
  idle: 'plugins.status.idle',
  activating: 'plugins.status.activating',
  active: 'plugins.status.active',
  asleep: 'plugins.status.asleep',
  disabled: 'plugins.status.disabled',
  'pending-approval': 'plugins.status.pendingApproval',
  error: 'plugins.status.error'
}

/**
 * 状态的颜色。
 *
 * ★ 只有**三种**颜色,不是七种:运行中(绿)、需要你处理(琥珀)、其余(faint)。
 *   七种状态各给一个颜色的结果是没有一种颜色有意义 —— 用户没法从颜色读出
 *   「我现在要不要做点什么」,而那正是他扫这一列时唯一想知道的事。
 *
 * ★ 待批准用琥珀而不是红:它不是错误,是一件**等着你点头**的事。
 *   红色会让用户以为插件坏了,从而躲开一次本来正常的授权。
 */
const STATUS_TONE: Record<PluginStatus, string> = {
  idle: 'bg-fg-faint/50',
  activating: 'bg-accent animate-pulse',
  active: 'bg-accent',
  asleep: 'bg-fg-faint/50',
  disabled: 'bg-fg-faint/30',
  'pending-approval': 'bg-warning',
  error: 'bg-danger'
}

export function PluginsPanel(): ReactNode {
  const { t } = useI18n()
  const catalog = usePluginsStore((state) => state.catalog)
  const install = usePluginsStore((state) => state.installFromPicker)
  const loadMarket = usePluginsStore((state) => state.loadMarket)
  const updates = usePluginsStore((state) => state.updates)
  const checkUpdates = usePluginsStore((state) => state.checkUpdates)
  const checkingUpdates = usePluginsStore((state) => state.checkingUpdates)
  const updatingAll = usePluginsStore((state) => state.updatingAll)
  const updateAll = usePluginsStore((state) => state.updateAll)
  const [error, setError] = useState<string | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  /*
    ★ 默认落在**市场**。进这一页的动机绝大多数是「去找一个装上」,而不是
    「看看我装了什么」—— 后者是已经知道自己要找谁才走的路。默认停在已安装
    那一栏,等于让最常走的那条路每次都多点一下;而首次使用的人打开看到的
    还是一句「还没有安装任何插件」,这是这一页能给出的最没用的一屏。
  */
  const [view, setView] = useState<'installed' | 'market'>('market')

  useEffect(() => {
    // 市场列表**按需拉**:没打开那一栏的用户不该为它付一次网络往返。
    if (view === 'market') void loadMarket()
  }, [view, loadMarket])

  useEffect(() => {
    /*
      ★ 进这一页顺手查一次更新。**不是每次都打网络** —— 主进程那边压着
      10 分钟的列表缓存,反复进出只有第一次真的联网。

      ★ 不放到 `App.tsx` 开机查:更新这件事只在用户看得见它的时候才有意义,
      而开机时多打一次市场请求的收益是零。
    */
    void checkUpdates()
  }, [checkUpdates])

  /*
    ★ 这里**不再**拉 catalog、也不再订阅 `plugins:changed` —— 两者都搬去了
    `App.tsx`。搬家的原因见那边的注释(菜单和自定义编辑器不能等到这一页
    被打开才有数据);留一份在这里的后果是每条 `plugins:changed` 触发两次
    重取,而这一页恰恰是装/卸插件的地方,事件最密。
  */

  const selected = catalog.plugins.find((plugin) => plugin.id === selectedId) ?? catalog.plugins[0]
  const updateById = new Map(updates.map((update) => [update.pluginId, update]))
  // ★ 只数市场来源的:本地包装的那些照常能单独更新,但不该被一颗「全部更新」
  //   悄悄换成市场版(见 `PluginUpdate.fromMarket`)。
  const batchable = updates.filter((update) => update.fromMarket)

  return (
    <div className="flex min-h-0 flex-1 flex-col overflow-hidden px-6 py-5">
      <div className="mx-auto flex min-h-0 w-full max-w-[1080px] flex-1 flex-col">
        <div className="flex shrink-0 items-center gap-3">
          <div className="min-w-0">
            <h2 className="text-[14px] font-medium text-fg">{t('plugins.title')}</h2>
            <p className="mt-1 text-[12px] leading-relaxed text-fg-faint">{t('plugins.description')}</p>
          </div>
          <SegmentedControl
            className="ml-3"
            value={view}
            onValueChange={(value) => setView(value as 'installed' | 'market')}
            label={t('plugins.title')}
            /* 顺序跟着默认值走 —— 让「第一个」和「进来看到的」是同一个 */
            options={[
              { value: 'market', label: t('plugins.tab.market') },
              { value: 'installed', label: t('plugins.tab.installed') }
            ]}
          />
          <Button
            type="button"
            className="ml-auto"
            size="sm"
            variant="secondary"
            disabled={checkingUpdates}
            onClick={() => { void checkUpdates(true) }}
          >
            <RefreshCw size={13} />
            {t('plugins.checkUpdates')}
          </Button>
          <Button
            type="button"
            size="sm"
            variant="secondary"
            onClick={() => {
              setError(null)
              void install().catch((cause: unknown) => {
                setError(t('plugins.installFailed', { error: t(pluginErrorKey(cause)) }))
              })
            }}
          >
            <Package size={13} />
            {t('plugins.install')}
          </Button>
        </div>

        {error !== null && (
          <div className="mt-4 shrink-0 rounded-[10px] border border-danger/30 bg-danger/5 px-3 py-2 text-[12px] text-danger">
            {error}
          </div>
        )}

        {/*
          可更新横幅。形状同详情页那块 `pending-approval` 提示,只换配色 ——
          「有新版可以装」是一件好事,用 accent;琥珀留给「等着你处理」。

          ★ **两栏都挂**,不再只挂在「已安装」那一栏。默认落地页改成市场之后,
            只挂一栏的后果是这条横幅默认永远不出现 —— 用户得先主动切到已安装
            才知道自己有东西要更新,而他本来就不知道才需要被告知。
        */}
        {batchable.length > 0 && (
          <div className="mt-4 flex shrink-0 items-center gap-3 rounded-[10px] border border-accent/40 bg-accent/5 px-3 py-2 text-[12px] text-fg">
            <ArrowUpCircle size={14} className="shrink-0 text-accent" aria-hidden />
            <span className="min-w-0 flex-1">{t('plugins.updatesAvailable', { count: batchable.length })}</span>
            <Button type="button" variant="primary" size="sm" disabled={updatingAll} onClick={() => { void updateAll() }}>
              {updatingAll ? t('plugins.updating') : t('plugins.updateAll')}
            </Button>
          </div>
        )}

        {view === 'market' ? (
          <PluginMarket />
        ) : catalog.plugins.length === 0 ? (
          <EmptyState
            className="my-auto"
            icon={<Puzzle size={26} />}
            title={t('plugins.empty')}
            description={t('plugins.emptyHint')}
          />
        ) : (
          <div className="mt-4 flex min-h-0 flex-1 gap-4">
            <div className="w-[280px] shrink-0 space-y-1 overflow-y-auto pr-1">
              {catalog.plugins.map((plugin) => (
                <button
                  key={plugin.id}
                  type="button"
                  onClick={() => setSelectedId(plugin.id)}
                  className={cn(
                    'flex w-full flex-col gap-0.5 rounded-[8px] px-2.5 py-2 text-left transition-colors',
                    plugin.id === selected?.id ? 'bg-tint-strong text-fg' : 'text-fg-muted hover:bg-tint-hover hover:text-fg'
                  )}
                >
                  <span className="flex min-w-0 items-center gap-1.5 text-[13px]">
                    {/* 状态点在名字**前面**:扫一列的时候眼睛走的是左边那条竖线 */}
                    <span aria-hidden className={cn('size-1.5 shrink-0 rounded-full', STATUS_TONE[plugin.status])} />
                    <span className="min-w-0 truncate">{plugin.manifest.displayName}</span>
                    {plugin.status === 'error' && <AlertTriangle size={12} className="shrink-0 text-danger" />}
                    {/*
                      ★ 徽标打在**所有**有新版的插件上,本地装的那些也打 ——
                      「有新版」是事实,和「要不要一键帮你换掉」是两件事。
                      后者才看 `fromMarket`。
                    */}
                    {updateById.has(plugin.id) && (
                      <span className="ml-auto shrink-0 rounded-[4px] bg-accent/15 px-1 text-[10px] text-accent">
                        ↑ {updateById.get(plugin.id)?.latestVersion}
                      </span>
                    )}
                  </span>
                  <span className="pl-3 text-[11px] text-fg-faint">{t(STATUS_KEY[plugin.status])}</span>
                </button>
              ))}
            </div>
            {selected !== undefined && <PluginDetail plugin={selected} update={updateById.get(selected.id)} />}
          </div>
        )}
      </div>
    </div>
  )
}

function PluginDetail({ plugin, update }: { plugin: InstalledPlugin; update: PluginUpdate | undefined }): ReactNode {
  const { t } = useI18n()
  const setEnabled = usePluginsStore((state) => state.setEnabled)
  const uninstall = usePluginsStore((state) => state.uninstall)
  const grant = usePluginsStore((state) => state.grant)
  const revoke = usePluginsStore((state) => state.revoke)
  const activity = usePluginsStore((state) => state.activity)
  const loadActivity = usePluginsStore((state) => state.loadActivity)
  const installFromMarket = usePluginsStore((state) => state.installFromMarket)
  const installProgress = usePluginsStore((state) => state.installProgress)
  const [tab, setTab] = useState<'overview' | 'activity'>('overview')

  useEffect(() => {
    if (tab === 'activity') void loadActivity(plugin.id)
  }, [tab, plugin.id, loadActivity])

  const declared = [...plugin.permissions.required, ...plugin.permissions.optional]
  const granted = new Set(plugin.permissions.granted)
  const updating = update !== undefined && installProgress[`market:${update.slug}`] !== undefined

  return (
    <div className="min-w-0 flex-1 overflow-y-auto rounded-[12px] border border-hairline p-4">
      <div className="flex items-start gap-3">
        {/* 图标块与 web 详情页同一个形状 —— 同一个插件在两处看起来要像同一个东西 */}
        <div className="grid size-11 shrink-0 place-items-center rounded-[12px] border border-hairline bg-tint text-fg-faint">
          <Puzzle size={20} />
        </div>
        <div className="min-w-0 flex-1">
          {/* ★ displayName / publisher / version 是**领域值,不翻译** —— 同主题名的处理 */}
          <h3 className="truncate text-[15px] font-medium text-fg">{plugin.manifest.displayName}</h3>
          <p className="mt-0.5 text-[12px] text-fg-faint">
            {t('plugins.publisher', { publisher: plugin.manifest.publisher })} ·{' '}
            {t('plugins.version', { version: plugin.manifest.version })}
          </p>
          <p className="mt-2 text-[12.5px] leading-relaxed text-fg-muted">{plugin.manifest.description}</p>
        </div>
        <Switch
          checked={plugin.enabled}
          onCheckedChange={() => { void setEnabled(plugin.id, !plugin.enabled) }}
          aria-label={plugin.enabled ? t('plugins.disable') : t('plugins.enable')}
        />
      </div>

      {update !== undefined && (
        <div className="mt-3 rounded-[10px] border border-accent/40 bg-accent/5 px-3 py-2">
          <div className="flex items-center gap-2.5">
            <ArrowUpCircle size={14} className="shrink-0 text-accent" aria-hidden />
            {/* 版本号是领域值,不翻译 */}
            <span className="min-w-0 flex-1 text-[12px] text-fg">
              {t('plugins.updateTo', { version: update.latestVersion })}
            </span>
            <Button
              type="button"
              size="sm"
              variant="primary"
              disabled={updating}
              onClick={() => { void installFromMarket(update.slug, update.latestVersion).catch(() => undefined) }}
            >
              {updating ? t('plugins.updating') : t('plugins.update')}
            </Button>
          </div>
          {/*
            ★★ **扩权要在点下去之前说,而且要逐条说。**

            新版本多要的必选能力会让插件装完停在「待批准」—— 那一刻用户看到
            的是一个自己刚更新完、却不转了的插件。事后再解释,他已经在怀疑
            这次更新是不是把东西弄坏了。「权限有变化」这种话也不够:他要
            知道的是变成了什么。
          */}
          {update.escalatedPermissions.length > 0 && (
            <p className="mt-1.5 pl-[24px] text-[11.5px] leading-relaxed text-warning">
              {t('plugins.updateEscalatesPermissions', {
                permissions: update.escalatedPermissions.join('、')
              })}
            </p>
          )}
        </div>
      )}

      {plugin.status === 'pending-approval' && (
        <div className="mt-3 rounded-[10px] border border-warning/40 bg-warning/5 px-3 py-2 text-[12px] text-warning">
          {t('plugins.pendingApprovalHint')}
        </div>
      )}

      <SegmentedControl
        className="mt-4"
        value={tab}
        onValueChange={(value) => setTab(value as 'overview' | 'activity')}
        label={t('plugins.title')}
        options={[
          { value: 'overview', label: t('plugins.permissions') },
          { value: 'activity', label: t('plugins.activity') }
        ]}
      />

      {tab === 'overview' ? (
        <>
          <p className="mt-3 text-[11px] text-fg-faint">{t('plugins.permissionsHint')}</p>
          <div className="mt-2 space-y-1">
            {declared.map((permission) => (
              <PermissionRow
                key={permission}
                permission={permission}
                required={plugin.permissions.required.includes(permission)}
                granted={granted.has(permission)}
                onGrant={() => { void grant(plugin.id, [permission]) }}
                onRevoke={() => { void revoke(plugin.id, [permission]) }}
              />
            ))}
          </div>

          <PluginConfiguration plugin={plugin} />

          <PluginSkills plugin={plugin} />

          {plugin.diagnostics.length > 0 && (
            <>
              <h4 className="mt-4 text-[12px] font-medium text-fg">{t('plugins.diagnostics')}</h4>
              <ul className="mt-1 space-y-1">
                {plugin.diagnostics.map((diagnostic, index) => (
                  <li
                    key={`${diagnostic.path}:${String(index)}`}
                    className={cn('rounded-[8px] px-2 py-1.5 text-[11.5px]', diagnostic.level === 'error' ? 'bg-danger/5 text-danger' : 'bg-tint text-fg-muted')}
                  >
                    {/* 诊断内容来自清单与包内文件,是**领域值**,不进翻译表 */}
                    <span className="font-mono">{diagnostic.path}</span>: {diagnostic.message}
                  </li>
                ))}
              </ul>
            </>
          )}

          <Button
            type="button"
            className="mt-4"
            size="sm"
            variant="danger"
            onClick={() => { void uninstall(plugin.id) }}
          >
            <Trash2 size={13} />
            {t('plugins.uninstall')}
          </Button>
        </>
      ) : activity.length === 0 ? (
        <p className="mt-4 text-[12px] text-fg-faint">{t('plugins.activityEmpty')}</p>
      ) : (
        <ul className="mt-3 space-y-1">
          {activity.map((entry, index) => (
            <li key={`${String(entry.ts)}:${String(index)}`} className="flex items-baseline gap-2 rounded-[8px] px-2 py-1 text-[11.5px]">
              <span className={cn('shrink-0 font-mono', entry.verdict === 'ok' ? 'text-fg-faint' : 'text-danger')}>{entry.method}</span>
              <span className="min-w-0 flex-1 truncate text-fg-muted">{entry.summary}</span>
              <span className="shrink-0 tabular-nums text-fg-faint">{entry.durationMs}ms</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

function PermissionRow({
  permission,
  required,
  granted,
  onGrant,
  onRevoke
}: {
  permission: PluginPermission
  required: boolean
  granted: boolean
  onGrant: () => void
  onRevoke: () => void
}): ReactNode {
  const { t } = useI18n()
  return (
    <div className="flex items-start gap-2.5 rounded-[8px] bg-tint px-2.5 py-2">
      <ShieldCheck size={13} className={cn('mt-0.5 shrink-0', granted ? 'text-accent' : 'text-fg-faint')} />
      <div className="min-w-0 flex-1">
        {/*
          ★ **人话在上,id 在下。**

          原来这一行只有 `workspace.write` 这个 id —— 而用户就是在这里点
          「批准」的。一个 id 回答不了「批了会发生什么」,于是这个决定其实是
          闭着眼做的。网页详情页反倒有说明,那是本末倒置:**做决定的地方是这里**。

          认不出的能力退回显示 id 本身,不吞掉 —— 宁可显示一个陌生字符串,
          也不要让一项能力从这份清单里消失。
        */}
        <div className="text-[12px] leading-relaxed text-fg">
          {t(`plugins.perm.${permission}` as TranslationKey)}
        </div>
        <div className="mt-0.5 flex items-center gap-1.5">
          <code className="font-mono text-[10.5px] text-fg-faint">{permission}</code>
          {required && (
            <span className="rounded-[4px] bg-tint-strong px-1 text-[10px] text-fg-faint">{t('plugins.required')}</span>
          )}
        </div>
      </div>
      <Button type="button" variant="secondary" size="sm" onClick={granted ? onRevoke : onGrant}>
        {granted ? t('plugins.revoke') : t('plugins.grant')}
      </Button>
    </div>
  )
}

/**
 * 这个插件贡献的 Skill。
 *
 * ## 为什么读的是 Skill 列表,不是插件自己的清单
 *
 * 清单说「我声明了这几个目录」,这一屏要回答的是「模型现在看得见哪几条」。
 * 两者不同的情形不少(缺 description、撞名、被用户同名的 skill 覆盖),
 * 判定规则全在 `plugin-skills.ts` 的注释里。照清单画的话,这一页会列出
 * 一条模型根本看不见的 skill,而作者没有任何线索。
 *
 * ## 为什么在这里触发加载
 *
 * 用户完全可能没开过 Skill 页就来看插件详情,那时 store 是空的 —— 空的时候
 * 这一屏会显示「声明了 N 条、一条都没生效」,一句彻底的误报。
 *
 * ★ 只在**空且不在加载中**时拉一次,而不是每次挂载都拉:Skill 页可能正开着,
 * 而 `load()` 会把 items 清空再填 —— 那会让另一个页面的列表闪一下。
 */
function PluginSkills({ plugin }: { plugin: InstalledPlugin }): ReactNode {
  const { t } = useI18n()
  const workspaceId = useWindowStore((state) => state.activeWorkspaceId)
  const items = useSkillsStore((state) => state.items)
  const load = useSkillsStore((state) => state.load)

  const declaredCount = plugin.manifest.contributes.skills.length
  useEffect(() => {
    if (declaredCount === 0) return
    /*
      ★ `items` / `loading` 在这里**用 getState 读快照**,不作为 effect 依赖。

      作为依赖的话,一台真的扫不出任何 skill 的机器会在「拉完 → 还是空 → 再拉」
      之间转个不停。而用 `eslint-disable` 把依赖检查关掉是不行的 ——
      本仓库渲染层的 disable 计数是 0,这条规则见 AGENTS.md §3。
      读快照两个问题一起解决:依赖列表是完整的,行为也是「只试一次」。
    */
    const state = useSkillsStore.getState()
    if (state.items.length > 0 || state.loading) return
    void load(workspaceId)
  }, [workspaceId, declaredCount, load])

  if (declaredCount === 0) return null

  const declared = plugin.manifest.contributes.skills
  const rows = pluginSkillRows(items, plugin.id)
  const missing = missingPluginSkills(declared.map((entry) => entry.path), rows)

  return (
    <>
      <h4 className="mt-4 text-[12px] font-medium text-fg">{t('plugins.skills')}</h4>
      <p className="mt-1 text-[11px] leading-relaxed text-fg-faint">{t('plugins.skillsHint')}</p>
      <ul className="mt-2 space-y-1">
        {rows.map((row) => (
          <li key={row.id} className="flex items-start gap-2.5 rounded-[8px] bg-tint px-2.5 py-2">
            <BookOpen size={13} className={cn('mt-0.5 shrink-0', row.active ? 'text-accent' : 'text-fg-faint')} />
            <div className="min-w-0 flex-1">
              {/* ★ skill 名是**领域值**(模型看到的就是它),不翻译、等宽 */}
              <code className="font-mono text-[11.5px] text-fg">{row.name}</code>
              <div className="mt-0.5 line-clamp-2 text-[11px] leading-relaxed text-fg-muted">{row.description}</div>
            </div>
            {!row.active && (
              /*
                ★ 「已关闭」要说出来。用户在这一页看到插件是启用的,而它的 skill
                可能被 Skill 页上的开关单独关掉了 —— 不说的话,他只会发现模型
                从来不用它,而原因在另一个页面上。
              */
              <span className="mt-0.5 shrink-0 rounded-[4px] bg-tint-strong px-1 text-[10px] text-fg-faint">
                {t('plugins.skillInactive')}
              </span>
            )}
          </li>
        ))}
        {missing.map((name) => (
          /*
            ★ 声明了却没出现的,**单独列出来并说明去向**。这是这一屏存在的主要
            理由:在此之前,一条加载失败的插件 skill 在任何界面上都不留痕迹。
            具体原因(缺 description / 撞名 / 被覆盖)在 Skill 页的诊断里。
          */
          <li key={`missing:${name}`} className="flex items-start gap-2.5 rounded-[8px] bg-warning/5 px-2.5 py-2">
            <AlertTriangle size={13} className="mt-0.5 shrink-0 text-warning" />
            <div className="min-w-0 flex-1">
              <code className="font-mono text-[11.5px] text-fg">{name}</code>
              <div className="mt-0.5 text-[11px] leading-relaxed text-fg-muted">{t('plugins.skillMissing')}</div>
            </div>
          </li>
        ))}
      </ul>
    </>
  )
}
