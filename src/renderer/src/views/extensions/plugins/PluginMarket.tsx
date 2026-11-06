/**
 * 插件市场的浏览与安装。
 *
 * ## 这一页最要紧的一件事:**能力写在卡片里**
 *
 * 装一个插件这个决定的全部信息量,就是「它要哪几条能力」。把它藏到详情页
 * 或者安装弹窗里,等于让用户**先决定再了解** —— 而那时候他已经在心里点头了。
 * 所以每一条都在卡片上直接列出来,和名字、安装量同一屏。
 *
 * ★ 有了安装弹窗(`PluginInstallDialog`)之后这条**依然成立**。弹窗是确认,
 *   不是第一次告知;卡片上这份清单撤掉的话,用户就只能在点过「安装」之后
 *   才第一次看见这些能力。
 *
 * ## 卡片而不是长条
 *
 * 一行一个的长条把「图标 / 名字 / 说明 / 能力 / 装机量」压进一条横向的线,
 * 眼睛要横着扫过整屏才凑齐一个插件的信息,而右端那颗按钮又离名字最远 ——
 * 用户点的时候得回头确认自己点的是哪一个。卡片把一个插件的所有信息收进
 * 一个方框:一次跳视看完一个,按钮就在它自己那张卡的底边上。
 *
 * ## 空列表与拉不动是两件事
 *
 * 市场连不上时显示「还没有插件」是一种很具体的误导:用户会以为这个市场是空的,
 * 而不是去检查网络。两种状态在这里有两套文案和一颗重试按钮。
 *
 * ## 安装进度就画在那颗按钮上
 *
 * 包能到 20MB。以前这里只有一个 `busy` 开关:按钮变灰,然后是几十秒的沉默 ——
 * 用户分不清是在下载、卡住了、还是根本没点上。现在按钮**原地**变成一条进度条,
 * 下载那一段报真实百分比,授权和解压两段报阶段文案。
 *
 * ★ 发起安装的人此刻在看弹窗(进度那边也有一条)。卡片上这条是给**别的**
 *   路径的:别的窗口装的、以及弹窗关掉之后还没装完的那几秒。
 */
import { AlertTriangle, Download, Puzzle } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import type { PluginMarketItem } from '../../../../../shared/plugin/market'
import { Button } from '../../../components/arc/button/button'
import { EmptyState } from '../../../components/arc/empty-state/empty-state'
import { ProgressBar } from '../../../components/ui/ProgressBar'
import { TextInput } from '../../../components/ui/TextInput'
import { useI18n, type TranslationKey } from '../../../i18n'
import { usePluginsStore, type PluginInstallProgress } from '../../../stores/plugins'
import { PluginIcon } from './PluginIcon'
import { PluginInstallDialog } from './PluginInstallDialog'
import { installLabel, installRatio } from '../../../lib/install-progress'

export function PluginMarket(): ReactNode {
  const { t } = useI18n()
  const market = usePluginsStore((state) => state.market)
  const loading = usePluginsStore((state) => state.marketLoading)
  const marketError = usePluginsStore((state) => state.marketError)
  const marketPage = usePluginsStore((state) => state.marketPage)
  const marketHasNext = usePluginsStore((state) => state.marketHasNext)
  const loadMarket = usePluginsStore((state) => state.loadMarket)
  const installProgress = usePluginsStore((state) => state.installProgress)
  const installError = usePluginsStore((state) => state.installError)
  const catalog = usePluginsStore((state) => state.catalog)
  const [query, setQuery] = useState('')
  const loadPage = (page: number): void => {
    void loadMarket({ q: query, page })
  }
  /*
    ★ 存**整条**而不是 slug。存 slug 的话,一次「刷新」把某个条目从列表里换掉,
    弹窗会当场变空 —— 而用户正在读的就是那一屏。列表是可变的,他手上这一份不是。
  */
  const [detail, setDetail] = useState<PluginMarketItem | null>(null)

  const installed = new Set(catalog.plugins.map((plugin) => plugin.id))

  return (
    <div className="mt-4 flex min-h-0 flex-1 flex-col">
      <div className="flex shrink-0 items-center gap-2">
        <TextInput
          className="w-[260px]"
          value={query}
          onChange={setQuery}
          placeholder={t('plugins.marketSearch')}
          ariaLabel={t('plugins.marketSearch')}
        />
        <Button type="button" variant="secondary" size="sm" disabled={loading} onClick={() => { loadPage(1) }}>
          {t('common.refresh')}
        </Button>
      </div>

      {marketError !== null ? (
        <EmptyState
          className="my-auto"
          icon={<AlertTriangle size={26} />}
          title={t('plugins.marketUnavailable')}
          description={t('plugins.marketUnavailableHint')}
        />
      ) : loading ? (
        <div className="py-14 text-center text-[13px] text-fg-faint">{t('common.loading')}</div>
      ) : market.length === 0 ? (
        <EmptyState className="my-auto" icon={<Puzzle size={26} />} title={t('plugins.marketEmpty')} description="" />
      ) : (
        <div className="mt-2 min-h-0 flex-1 overflow-y-auto pr-1">
          {/*
            ★ 断点取 `lg` / `2xl` 而不是 `md`:这一栏外面还压着一条 280px 的
            侧栏和 `max-w-[1080px]`,按视口宽度提前分栏会把卡片挤到放不下
            那行能力标签,于是每张卡都在换行。

            ★★ `py-1.5` 是**给悬停那一抬留的位置**,不是装饰性留白。卡片
            `hover:-translate-y-0.5` 抬起 2px,而外面这层是 `overflow-y-auto` ——
            滚动容器会沿着自己的内边界裁切,于是第一排卡片一悬停,顶边连同
            那圈高亮描边就被削掉一条。最后一排同理(抬起时底边空出的一道)。
            上面的 `mt-3` 相应收成 `mt-2`,总间距不变。
          */}
          <div className="grid grid-cols-1 gap-3 py-1.5 lg:grid-cols-2 2xl:grid-cols-3">
            {market.map((item, index) => (
              <MarketCard
                key={item.pluginId}
                item={item}
                index={index}
                installed={installed.has(item.pluginId)}
                progress={installProgress[`market:${item.slug}`]}
                /*
                  ★ 失败原因也从 store 读,不是组件自己的 state:这一条可能是
                  **别的窗口**那次安装失败推过来的。主进程抛的是 key,
                  `pluginMessageKey` 收窄之后才 `t()` —— 直接显示的话界面上
                  会是一个 `plugins.xxx`。
                */
                error={installError[`market:${item.slug}`]}
                onOpen={() => setDetail(item)}
              />
            ))}
          </div>
        </div>
      )}

      {marketError === null && (marketPage > 1 || marketHasNext) && (
        <div className="mt-3 flex shrink-0 items-center justify-center gap-3 border-t border-hairline pt-3">
          <Button type="button" variant="secondary" size="sm" disabled={loading || marketPage <= 1} onClick={() => { loadPage(marketPage - 1) }}>
            {t('plugins.marketPreviousPage')}
          </Button>
          <span className="text-[12px] text-fg-muted tabular-nums">{t('plugins.marketPage', { page: marketPage })}</span>
          <Button type="button" variant="secondary" size="sm" disabled={loading || !marketHasNext} onClick={() => { loadPage(marketPage + 1) }}>
            {t('plugins.marketNextPage')}
          </Button>
        </div>
      )}

      {/*
        ★ 弹窗挂在**列表外面**,不在卡片里。挂在卡片里的话它的存活依赖那张卡
        还在渲染 —— 一次搜索把它筛掉,正开着的弹窗就连同卸载了。
      */}
      <PluginInstallDialog item={detail} onClose={() => setDetail(null)} />
    </div>
  )
}

function MarketCard({
  item,
  index,
  installed,
  progress,
  error,
  onOpen
}: {
  item: PluginMarketItem
  index: number
  installed: boolean
  progress: PluginInstallProgress | undefined
  error: TranslationKey | undefined
  onOpen: () => void
}): ReactNode {
  const { t } = useI18n()
  return (
    <article
      className="skills-card-enter flex flex-col rounded-[16px] border border-hairline bg-surface p-4 shadow-sm transition-[translate,box-shadow,border-color] duration-200 hover:-translate-y-0.5 hover:border-accent/30 hover:shadow-md motion-reduce:hover:translate-y-0"
      /* 逐张错开一点点入场。`min(index, 7)` 封顶 —— 第 40 张卡不该等上 1.4 秒 */
      style={{ animationDelay: `${String(Math.min(index, 7) * 35)}ms` }}
    >
      {/*
        ★ 整张卡的上半部分是**一颗按钮**,点哪儿都能打开详情 —— 用户对卡片的
        默认预期就是「点它看详情」,只有右下角那一小块可点等于把这个预期打掉。
        安装按钮留在下面那一行、在这颗按钮**外面**:`<button>` 套 `<button>`
        在 HTML 里是非法的,浏览器会把内层那颗拎出去,点击行为随之变得不可预测。
      */}
      <button type="button" className="block w-full text-left" onClick={onOpen}>
        <div className="flex items-start gap-3">
          <PluginIcon iconUrl={item.iconUrl} />
          <div className="min-w-0 flex-1">
            {/* ★ displayName / pluginId / author 都是**领域值**,不翻译 */}
            <h3 className="truncate text-[13.5px] font-medium text-fg">{item.displayName}</h3>
            <p className="mt-0.5 truncate font-mono text-[10.5px] text-fg-faint">{item.pluginId}</p>
            <p className="mt-1.5 line-clamp-2 text-[12px] leading-relaxed text-fg-muted">
              {item.description}
            </p>
          </div>
        </div>
      </button>

      {item.permissions.length > 0 && (
        <div className="mt-3 flex flex-wrap items-center gap-1">
          {/* ★ 能力就在卡片上 —— 装它的决定信息量全在这一行 */}
          <span className="text-[10.5px] text-fg-faint">{t('plugins.permissions')}</span>
          {item.permissions.map((permission) => (
            <span
              key={permission}
              className="rounded-[4px] bg-tint px-1.5 py-px font-mono text-[10.5px] text-fg-muted"
            >
              {permission}
            </span>
          ))}
        </div>
      )}

      {/*
        ★ 自带的 Skill 和能力并列画在卡片上,理由却不同。

        能力回答「它能对我做什么」,Skill 回答「它会往模型的目录里塞什么」——
        后者是插件里唯一一类装上之后**每一轮对话都在场**的贡献。藏到详情页去,
        等于让用户在不知情的情况下改变了自己每次提问的上下文。

        ★ 名字是**领域值,不翻译**(它就是模型看到的那个 token),所以和能力
        标签一样用等宽字体 —— 那是这个界面里「这串字符是标识符」的既有信号。
      */}
      {item.skills.length > 0 && (
        <div className="mt-1.5 flex flex-wrap items-center gap-1">
          <span className="text-[10.5px] text-fg-faint">{t('plugins.marketSkills')}</span>
          {item.skills.map((skill) => (
            <span
              key={skill}
              className="rounded-[4px] bg-accent/10 px-1.5 py-px font-mono text-[10.5px] text-fg-muted"
            >
              {skill}
            </span>
          ))}
        </div>
      )}

      {/* 失败原因贴在这张卡片上,不是页面顶部 —— 连点了几个之后,顶部那一条说不清是哪个失败了 */}
      {error !== undefined && <p className="mt-2 text-[11.5px] text-danger">{t(error)}</p>}

      {/*
        ★ `mt-auto` 而不是固定间距:同一行里几张卡的说明有长有短,底边这一条
        不对齐的话,几颗安装按钮会排成一道楼梯。
      */}
      <div className="mt-auto flex items-center justify-between gap-3 border-t border-hairline pt-3">
        <span className="min-w-0 truncate text-[11px] text-fg-faint tabular-nums">
          {t('plugins.marketDownloads', { count: item.downloadCount })}
          {item.version !== null && ` · ${item.version}`}
        </span>
        {/*
          ★ **动作槽宽度写死。** 按钮和进度条都 `w-full` 填进来,于是那一格在
          两种状态之间一像素不跳。去算按钮宽度是做不到的:进度条上的文字
          (「正在下载 63%」)比按钮上的长。

          120px 是按**英文**文案量的:「Install plugin」≈ 76 + 图标 13 + 间距 6
          + 左右 padding 24 ≈ 119。中文那一档只要 91,取长的那个。
          改文案的话这个数要重新量。
        */}
        <div className="w-[120px] shrink-0">
          {progress === undefined ? (
            <Button
              type="button"
              className="w-full"
              size="sm"
              variant={installed ? 'secondary' : 'primary'}
              disabled={installed}
              /* ★ 这颗按钮**不直接开装**,它打开确认弹窗 —— 真正的安装在那一屏上 */
              onClick={onOpen}
            >
              <Download size={13} />
              {installed ? t('plugins.installed') : t('plugins.install')}
            </Button>
          ) : (
            <InstallingButton progress={progress} />
          )}
        </div>
      </div>
    </article>
  )
}

/**
 * 安装中的那颗按钮。
 *
 * ★★ **仍然是一个 `<button>`**,不是 `<div>`。换成 div 的话,正在聚焦的那个
 * 元素会从 DOM 里消失、焦点掉回 `<body>` —— 键盘用户按下回车装插件,
 * 代价是丢失自己在列表里的位置。
 *
 * ★ 用 `aria-disabled` + `pointer-events-none` 而不是 `disabled`:后者会连带
 * 命中 Button 的 `disabled:opacity-40`,把进度条整个洗成灰的。
 */
function InstallingButton({ progress }: { progress: PluginInstallProgress }): ReactNode {
  const { t } = useI18n()
  const label = installLabel(progress, t)
  return (
    <button
      type="button"
      aria-disabled
      className="app-no-drag pointer-events-none flex h-7 w-full flex-col justify-center gap-1 rounded-pill bg-tint px-2.5"
    >
      {/*
        ★ 这里**不加** `aria-live`。进度每 120ms 更新一次,播报出去就是每秒
        八次「下载 61%…下载 62%…」—— 读屏用户没法在这种刷屏里做别的事。
        `ProgressBar` 的 `role="progressbar"` + `aria-valuetext` 已经够了:
        聚焦到它的时候读一次,而不是替用户盯着。
      */}
      <span className="truncate text-center text-[10.5px] leading-none text-fg-muted tabular-nums">{label}</span>
      <ProgressBar className="h-1" value={installRatio(progress)} label={label} />
    </button>
  )
}
