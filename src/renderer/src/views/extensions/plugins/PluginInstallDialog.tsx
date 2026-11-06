/**
 * 装之前的那一屏 —— 市场条目的详情与确认。
 *
 * ## 为什么要多这一步
 *
 * 装一个插件是**把别人的代码放进自己机器**。一颗直接开装的按钮把这件事压成
 * 了一次误触的距离:用户在列表里扫过去,手比脑子快,装完才回头看它要什么。
 * 这一屏的作用只有一个 —— 在那句「装」之前,把决定所需的东西摊开一次。
 *
 * ## 但卡片上那份能力清单**不撤**
 *
 * 很容易把这个弹窗当成「详情都搬进来了,列表可以精简了」。不行:那样一来
 * 用户是**先决定(点了安装)再了解**,而那时他心里已经点过一次头了。
 * 弹窗是确认,不是第一次告知 —— 两处都列,同一份信息看两遍不是冗余,
 * 是让第二遍变成复核。
 *
 * ## 进度留在弹窗里,装完才关
 *
 * 点完「安装」立刻关掉弹窗、让用户回列表自己找哪张卡在转,是把一件他刚发起
 * 的事扔回给他去追踪。这里原地接上进度条,成功了才关 —— 失败则**留着不关**,
 * 因为下一步(重试还是放弃)只有他能定。
 */
import { Download, ShieldCheck } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import type { PluginMarketItem } from '../../../../../shared/plugin/market'
import { Button } from '../../../components/arc/button/button'
import { Dialog } from '../../../components/ui/Dialog'
import { ProgressBar } from '../../../components/ui/ProgressBar'
import { useI18n, type TranslationKey } from '../../../i18n'
import { cn } from '../../../lib/cn'
import { usePluginsStore } from '../../../stores/plugins'
import { PluginIcon } from './PluginIcon'
import { installLabel, installRatio } from '../../../lib/install-progress'

export function PluginInstallDialog({
  item,
  onClose
}: {
  /** `null` = 关着。关闭动画那 220ms 里内容仍要在,见下面的 `shown` */
  item: PluginMarketItem | null
  onClose: () => void
}): ReactNode {
  const { t } = useI18n()
  const installFromMarket = usePluginsStore((state) => state.installFromMarket)
  const catalog = usePluginsStore((state) => state.catalog)
  const progress = usePluginsStore((state) =>
    item === null ? undefined : state.installProgress[`market:${item.slug}`]
  )
  /*
    ★ 失败原因从 store 读,不是组件自己的 state —— 同市场列表那边的理由:
    这一条可能是**别的窗口**那次安装失败推过来的。
  */
  const error = usePluginsStore((state) =>
    item === null ? undefined : state.installError[`market:${item.slug}`]
  )

  /*
    ★★ 记住最后一个非空的条目。

    `Dialog` 是带退场动画的(`usePresence`,220ms):`item` 一变 `null`,面板还
    在淡出,而里面的内容已经没了 —— 用户看到的是一个空壳在缩回去。留一份
    上一次的值,让淡出的那几帧还是他刚才在看的那个插件。
  */
  const [shown, setShown] = useState(item)
  useEffect(() => {
    if (item !== null) setShown(item)
  }, [item])

  if (shown === null) return null

  const installed = catalog.plugins.some((plugin) => plugin.id === shown.pluginId)
  const busy = progress !== undefined

  return (
    <Dialog
      open={item !== null}
      title={t('plugins.install')}
      onClose={onClose}
      width={520}
      footer={
        <>
          <Button type="button" variant="secondary" onClick={onClose}>{t('common.cancel')}</Button>
          <Button
            type="button"
            variant="primary"
            disabled={installed || busy}
            onClick={() => {
              /*
                ★ 成功才 `onClose()`,失败**留在原地**:错误文案就在这一屏上,
                关掉它等于把刚发生的事藏起来,用户只剩「我刚才点了没反应」。
                `installFromMarket` 失败会重抛,这里的 rejection 已经由 store
                写进 `installError` 了,不必再接一次。
              */
              void installFromMarket(shown.slug).then(onClose, () => undefined)
            }}
          >
            <Download size={13} />
            {installed ? t('plugins.installed') : t('plugins.install')}
          </Button>
        </>
      }
    >
      <div className="flex items-start gap-3">
        {/* 图标块与已装详情页同一个形状 —— 同一个插件在两处要看起来像同一个东西 */}
        <PluginIcon iconUrl={shown.iconUrl} />
        <div className="min-w-0 flex-1">
          {/* ★ displayName / pluginId / publisher / version 都是**领域值**,不翻译 */}
          <h3 className="truncate text-[15px] font-medium text-fg">{shown.displayName}</h3>
          <p className="mt-0.5 truncate font-mono text-[11px] text-fg-faint">{shown.pluginId}</p>
        </div>
      </div>

      <p className="mt-3 text-[12.5px] leading-relaxed text-fg-muted">{shown.description}</p>

      <dl className="mt-3 grid grid-cols-2 gap-x-4 gap-y-1.5 text-[11.5px]">
        <MetaRow label={t('plugins.marketPublisher')} value={shown.author || shown.publisher} />
        {shown.version !== null && (
          <MetaRow label={t('plugins.marketVersion')} value={shown.version} />
        )}
        <MetaRow
          label={t('plugins.marketDownloadsLabel')}
          value={String(shown.downloadCount)}
          numeric
        />
        {shown.engines !== null && (
          <MetaRow label={t('plugins.marketEngines')} value={shown.engines} numeric />
        )}
      </dl>

      <h4 className="mt-4 text-[12px] font-medium text-fg">{t('plugins.permissions')}</h4>
      <p className="mt-1 text-[11px] leading-relaxed text-fg-faint">{t('plugins.permissionsHint')}</p>
      {shown.permissions.length === 0 ? (
        <p className="mt-2 text-[12px] text-fg-muted">{t('plugins.noPermissions')}</p>
      ) : (
        <ul className="mt-2 space-y-1">
          {shown.permissions.map((permission) => (
            <li key={permission} className="flex items-start gap-2.5 rounded-[8px] bg-tint px-2.5 py-2">
              <ShieldCheck size={13} className="mt-0.5 shrink-0 text-fg-faint" />
              <div className="min-w-0 flex-1">
                {/*
                  ★ **人话在上,id 在下** —— 同已装详情页那份授权清单。一个
                  `workspace.write` 回答不了「批了会发生什么」;认不出的能力
                  退回显示 id 本身,不吞掉。
                */}
                <div className="text-[12px] leading-relaxed text-fg">
                  {t(`plugins.perm.${permission}` as TranslationKey)}
                </div>
                <code className="mt-0.5 block font-mono text-[10.5px] text-fg-faint">{permission}</code>
              </div>
              {/* 市场条目里列的就是这一版的**必选**能力(见 PluginMarketItem.permissions) */}
              <span className="mt-0.5 shrink-0 rounded-[4px] bg-tint-strong px-1 text-[10px] text-fg-faint">
                {t('plugins.required')}
              </span>
            </li>
          ))}
        </ul>
      )}

      {/*
        ★ 自带的 Skill 紧跟在能力后面,而且**不画在「没有能力」那条分支里**。

        一个 `permissions: []` 的插件在这一屏上原本只有一句「不需要任何能力」,
        用户据此判断「它什么也做不了,装吧」—— 而一条 Skill 会进入他此后每一轮
        对话的上下文。这两件事都要摆在同一屏上,他才是在知情的前提下按下安装。

        名字是领域值,不翻译;等宽字体是这个界面里「这串字符是标识符」的既有信号。
      */}
      {shown.skills.length > 0 && (
        <>
          <h4 className="mt-4 text-[12px] font-medium text-fg">{t('plugins.marketSkills')}</h4>
          <p className="mt-1 text-[11px] leading-relaxed text-fg-faint">{t('plugins.marketSkillsHint')}</p>
          <ul className="mt-2 flex flex-wrap gap-1">
            {shown.skills.map((skill) => (
              <li
                key={skill}
                className="rounded-[4px] bg-accent/10 px-1.5 py-0.5 font-mono text-[11px] text-fg-muted"
              >
                {skill}
              </li>
            ))}
          </ul>
        </>
      )}

      {busy && (
        <div className="mt-4">
          <div className="text-[11.5px] text-fg-muted tabular-nums">{installLabel(progress, t)}</div>
          {/*
            ★ 同市场卡片那颗按钮:这里**不加** `aria-live`。进度每 120ms 一帧,
            播报出去就是每秒八次的刷屏;`ProgressBar` 自带的
            `role="progressbar"` + `aria-valuetext` 已经够了。
          */}
          <ProgressBar className="mt-1.5 h-1" value={installRatio(progress)} label={installLabel(progress, t)} />
        </div>
      )}

      {error !== undefined && <p className="mt-3 text-[12px] text-danger">{t(error)}</p>}
    </Dialog>
  )
}

function MetaRow({
  label,
  value,
  numeric = false
}: {
  label: string
  value: string
  numeric?: boolean
}): ReactNode {
  return (
    <div className="flex min-w-0 items-baseline gap-1.5">
      <dt className="shrink-0 text-fg-faint">{label}</dt>
      <dd className={cn('min-w-0 truncate text-fg-muted', numeric && 'tabular-nums')}>{value}</dd>
    </div>
  )
}
