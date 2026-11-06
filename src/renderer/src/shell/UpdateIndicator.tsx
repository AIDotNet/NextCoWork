/**
 * 标题栏右端的更新指示器。
 *
 * 取代原先那条 `UpdateBanner` —— 它 `fixed left-2 right-2 top-10`,横跨整个窗口
 * 压在 Tab 条下面,把内容区最上面一整行盖掉。「有个新版本」这件事既不紧急、也
 * 跟用户当下在做的事无关,不值得占一条通栏;而它唯一的出路是「稍后」,点掉之后
 * 到下次检查为止就再也找不回来了。
 *
 * 改成一颗只在真的有更新时才出现的图标,悬停展开详情、更新说明和按钮。
 *
 * ★ **浮层不能用 `Tooltip`。** 那个组件整体 `pointer-events-none` —— 是故意的,
 * 提示不该挡住底下的东西 —— 鼠标进不去,里面的按钮点不着。这里要的是 hover
 * card:内容可交互。代价是得自己处理「鼠标从触发器走到卡片」这段路。
 *
 * ★ **关闭要延迟,开启不用 —— 和 `Tooltip` 正好反过来。** 触发器和卡片之间隔着
 * GAP 的空隙,鼠标穿过时会先收到触发器的 `pointerleave`、下一帧才收到卡片的
 * `pointerenter`。立刻关就会在半路把卡片收掉,用户看到的是「卡片一靠近就跑」。
 * 反过来开启不必延迟:这颗图标是孤立的,不存在「鼠标扫过一排」要过滤的情况。
 *
 * ★ **`error` 也要显示。** 旧 Banner 只认 `available` / `downloaded`,于是点了
 * 「下载」之后不管成功失败,提示都当场消失 —— 下载失败是彻底静默的。这里把最后
 * 一次已知的 `UpdateInfo` 记在 `info` 里,错误态照样挂着图标,卡片里给「重试」。
 */
import { AnimatePresence, motion } from 'motion/react'
import { Download, RotateCw } from 'lucide-react'
import {
  useCallback,
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type ReactNode
} from 'react'
import { createPortal } from 'react-dom'
import type { UpdateInfo, UpdateState } from '../../../shared/domain/update'
import { Button } from '../components/arc/button/button'
import { IconButton } from '../components/ui/IconButton'
import { Spinner } from '../components/ui/Spinner'
import { useI18n } from '../i18n'
import { cn } from '../lib/cn'
import { updateErrorKey } from '../lib/update-error'
import { updateDownload, updateGetState, updateInstall } from '../services/app'
import { on } from '../services/ipc'
import { motionScale, useMotionLevel } from '../theme/useMotionLevel'

/** 卡片与触发器之间的空隙 */
const GAP = 8
/** 离视口边缘至少留这么多 */
const EDGE = 12
const CLOSE_DELAY_MS = 160
const CARD_WIDTH = 320

/** 挂着图标的那几档 —— 其余(idle / checking / up-to-date / disabled)都不占位置 */
function isActive(state: UpdateState | null): state is UpdateState {
  if (state === null) return false
  return (
    state.state === 'available' ||
    state.state === 'downloading' ||
    state.state === 'downloaded' ||
    state.state === 'installing' ||
    state.state === 'error'
  )
}

export function UpdateIndicator(): ReactNode {
  const { t } = useI18n()
  const id = `update-card-${useId()}`
  const scale = motionScale(useMotionLevel())
  const anchorRef = useRef<HTMLSpanElement>(null)
  const cardRef = useRef<HTMLDivElement>(null)
  const timer = useRef<number | undefined>(undefined)
  /** 已经自动弹过的强制更新版本号 —— 同一个版本只打扰一次 */
  const announced = useRef<string | null>(null)

  const [state, setState] = useState<UpdateState | null>(null)
  const [info, setInfo] = useState<UpdateInfo | null>(null)
  const [busy, setBusy] = useState(false)
  const [open, setOpen] = useState(false)
  const [pos, setPos] = useState<{ left: number; top: number } | null>(null)

  useEffect(() => {
    const apply = (next: UpdateState): void => {
      setState(next)
      // `error` 不带 update 字段,所以这里只在有的时候覆盖,让它保住上一次的版本信息。
      // 反过来回到「已是最新」说明这一轮更新已经不存在了(装完重启后就是这样),得清掉。
      if ('update' in next) setInfo(next.update)
      else if (next.state === 'up-to-date' || next.state === 'disabled') setInfo(null)
    }
    void updateGetState().then(apply).catch(() => undefined)
    return on('app:updateChanged', apply)
  }, [])

  const show = useCallback(() => {
    window.clearTimeout(timer.current)
    setOpen(true)
  }, [])

  const scheduleHide = useCallback(() => {
    window.clearTimeout(timer.current)
    timer.current = window.setTimeout(() => setOpen(false), CLOSE_DELAY_MS)
  }, [])

  /*
    强制更新自己弹一次。悬停才看得见的东西对「必需」这个语义是不够的 ——
    用户完全可能一整天都不把鼠标挪到那个角上。弹一次之后交回给 hover:
    鼠标移进来再移出去就正常收掉,不做成关不掉的东西。
  */
  useEffect(() => {
    if (info === null || !info.mandatory || announced.current === info.version) return
    announced.current = info.version
    setOpen(true)
  }, [info])

  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent): void => {
      if (e.key === 'Escape') setOpen(false)
    }
    /*
      ★ 收起靠的是「点到别处」,**不是触发器的 `blur`**。
      卡片 portal 在 body 上,不是触发器的后代 —— 焦点一进到卡片里的按钮,
      触发器就收到 `blur`,于是「鼠标明明还停在卡片上,卡片却自己收了」。
      同理也不能只靠 `pointerleave`:点在卡片里那段更新说明上(不可聚焦)时,
      指针根本没离开过。所以判据是**这次按下落在哪**。
    */
    const onDown = (e: PointerEvent): void => {
      const target = e.target as Node | null
      if (target === null) return
      if (anchorRef.current?.contains(target) === true) return
      if (cardRef.current?.contains(target) === true) return
      setOpen(false)
    }
    window.addEventListener('keydown', onKey)
    window.addEventListener('pointerdown', onDown, true)
    return () => {
      window.removeEventListener('keydown', onKey)
      window.removeEventListener('pointerdown', onDown, true)
    }
  }, [open])

  useEffect(() => () => window.clearTimeout(timer.current), [])

  /*
    位置算在 `useLayoutEffect` 里,理由和 `Tooltip` 那边一字不差:绘制前同步量完,
    卡片的第一帧就在对的地方。不同的是这里不做上下翻转 —— 触发器钉在标题栏上,
    底下永远有地方,只需要把左缘夹回视口(窗口窄到 320px 以下时右边宁可溢出)。
  */
  useLayoutEffect(() => {
    if (!open) return setPos(null)
    const anchor = anchorRef.current?.getBoundingClientRect()
    const card = cardRef.current?.getBoundingClientRect()
    if (!anchor || !card) return
    const left = Math.max(EDGE, Math.min(anchor.right - card.width, window.innerWidth - card.width - EDGE))
    setPos({ left, top: anchor.bottom + GAP })
  }, [open])

  if (info === null || !isActive(state)) return null

  const downloading = state.state === 'downloading'
  const installing = state.state === 'installing'
  const failed = state.state === 'error'
  const ready = state.state === 'downloaded'
  const percent = state.state === 'downloading' ? Math.round(state.progress?.percent ?? 0) : 0

  const run = (action: () => Promise<unknown>) => async (): Promise<void> => {
    setBusy(true)
    try {
      await action()
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <span
        ref={anchorRef}
        className="app-no-drag relative flex shrink-0 self-center"
        onPointerEnter={show}
        onPointerLeave={scheduleHide}
        onFocus={show}
        aria-describedby={open ? id : undefined}
      >
        <IconButton
          label={t('about.updates.indicator', { version: info.version })}
          size={28}
          width={38}
          active={open}
          onClick={() => (open ? setOpen(false) : show())}
          // 38×28 + `rounded-pill` —— 和右边那两颗面板开关是同一种控件,见
          // OuterTabBar 里量几何的那段注释。笔画同样是 1.5 不是 lucide 默认的 2。
          className="rounded-pill"
        >
          {downloading || installing ? (
            <Spinner size="sm" />
          ) : ready ? (
            <RotateCw size={16} strokeWidth={1.5} />
          ) : (
            <Download size={16} strokeWidth={1.5} />
          )}
        </IconButton>
        {/*
          小红点。`pointer-events-none` 是必需的:它盖在按钮右上角,不挡掉指针的话
          点在那一小块上什么都不会发生。下载中不点 —— 转圈本身已经在说话了。
        */}
        {!downloading && !installing && (
          <span
            aria-hidden
            className={cn(
              'pointer-events-none absolute right-[6px] top-[4px] size-[6px] rounded-full ring-2 ring-chrome',
              failed ? 'bg-danger' : info.mandatory ? 'bg-danger' : 'bg-accent'
            )}
          />
        )}
      </span>

      {createPortal(
        <AnimatePresence>
          {open && (
            <motion.div
              id={id}
              ref={cardRef}
              aria-label={t('about.updates.indicator', { version: info.version })}
              initial={{ opacity: 0, y: -4, scale: 0.98 }}
              animate={{ opacity: 1, y: 0, scale: 1 }}
              exit={{ opacity: 0, scale: 0.98 }}
              transition={{ duration: 0.14 * scale, ease: [0.32, 0.72, 0, 1] }}
              onPointerEnter={show}
              onPointerLeave={scheduleHide}
              style={{ left: pos?.left ?? -9999, top: pos?.top ?? -9999, width: CARD_WIDTH }}
              // z-150:portal 到 body 的浮层那一档,见 theme.css 末尾 z 轴那段。
              // 和 `Tooltip` 同档 —— 它俩不会同时出现在同一个位置上。
              className="app-no-drag fixed z-[150] rounded-[14px] bg-surface-menu p-3 text-fg shadow-menu"
            >
              <div className="text-[12.5px] font-medium">
                {t('about.updates.banner', { version: info.version })}
              </div>
              <div className="mt-0.5 text-[11px] text-fg-muted">
                {t('about.updates.currentVersion', { version: state.currentVersion })}
              </div>

              {info.mandatory && (
                <div className="mt-2 text-[11px] text-danger">{t('about.updates.mandatory')}</div>
              )}

              <div className="mt-2.5 border-t border-line pt-2.5">
                <div className="text-[11px] font-medium text-fg-muted">
                  {t('about.updates.releaseNotes')}
                </div>
                {/*
                  更新说明是**要被读的整段文字**,不是标签 —— 全局 `user-select: none`
                  在这里得 opt-in,理由和 AboutPage 里那串版本号一样(想复制去贴 issue)。
                */}
                <div className="selectable mt-1 max-h-[180px] overflow-y-auto whitespace-pre-wrap text-[11.5px] leading-[1.6] text-fg-muted">
                  {info.releaseNotes !== undefined && info.releaseNotes.length > 0
                    ? info.releaseNotes
                    : t('about.updates.noReleaseNotes')}
                </div>
              </div>

              {downloading && (
                <div className="mt-2.5">
                  <div className="mb-1 text-[11px] text-fg-muted">
                    {t('about.updates.downloading', { percent })}
                  </div>
                  <div className="h-1 overflow-hidden rounded-pill bg-tint">
                    <div
                      className="h-full rounded-pill bg-accent transition-[width] duration-200 motion-reduce:transition-none"
                      style={{ width: `${percent}%` }}
                    />
                  </div>
                </div>
              )}

              {installing && (
                <div className="mt-2.5 text-[11px] text-fg-muted">{t('about.updates.installing')}</div>
              )}

              {failed && state.state === 'error' && (
                <div className="mt-2.5 text-[11px] text-danger">{t(updateErrorKey(state.code))}</div>
              )}

              {!downloading && !installing && (
                <div className="mt-3 flex justify-end">
                  {ready ? (
                    <Button type="button" size="sm" variant="primary" disabled={busy} onClick={() => void run(updateInstall)()}>
                      {t('about.updates.restartAndInstall')}
                    </Button>
                  ) : (
                    <Button type="button" size="sm" variant="primary" disabled={busy} onClick={() => void run(updateDownload)()}>
                      {failed
                        ? t('about.updates.retry')
                        : t('about.updates.download', { version: info.version })}
                    </Button>
                  )}
                </div>
              )}
            </motion.div>
          )}
        </AnimatePresence>,
        document.body
      )}
    </>
  )
}
