/**
 * 「打开方式」的菜单内容与那枚 ▾ 触发器 —— 这台机器上**实际可用**的文件管理器 /
 * IDE / 终端,末尾两行复制路径。
 *
 * ## 为什么是一个组件而不是四处各写一遍
 *
 * 它出现在四个地方(改动审查卡的文件行、文件视图工具条、图片灯箱、文件树的行菜单),
 * 而其中三处会**同时存在多份实例**(一屏文件行就有十几颗)。四份各自的代价不只是
 * 抄四遍:探测结果会变成四个独立的请求、菜单项顺序会慢慢分叉,而
 * 「复制绝对路径」这种要落剪贴板的动作会有一处忘了错误处理。
 *
 * ## 两个出口:整份下拉,与「只出条目」
 *
 * - `OpenWithMenu` —— 一枚 ▾ 触发器 + 面板。给工具条与卡片用。
 * - `OpenWithItems` —— **只有条目**,由调用方决定把它放进哪个面板。
 * - `OpenTargetItem` —— 单独一行。文件树右键菜单自己排版(默认那一行 + 「打开方式 ›」
 *   子菜单),但每一行的图标 / 名字 / 点击后的错误处理必须和这里一致,所以只借这一行。
 *
 * ★ 文件树那一行必须是后者。那一行的菜单自己是一个 `Menu` 面板,而 `Menu` 的面板
 *   带着 `translate` / `scale`(入场动效)—— 那两条 CSS 属性会给后代建立
 *   **包含块**,于是套在里面的第二级 `fixed` 面板不再对齐视口,而是按外层面板的
 *   坐标摆放。表现是子菜单**飞到屏幕外**(`ProviderModelMenu` 为此专门走
 *   `createPortal` 到 body,那是它那一层需要的解法)。平铺就没有这个问题。
 *   (文件树那一行后来按参考截图改成了真正的二级菜单「打开方式 ›」,走的正是
 *   portal 那条路:`views/files/FileRowMenu.tsx` + `components/ui/Submenu.tsx`,
 *   只借 `OpenTargetItem` 这一行。上面这条理由对仍然嵌在 `Menu` 里的调用点照样成立。)
 *
 * ## 探测结果按会话缓存一次
 *
 * (缓存本体已挪到 `useOpenTargets.ts` —— 文件树右键菜单和设置页也要读同一份,
 *  下面两条理由随它一起搬过去了,这里留作索引。)
 *
 * ★ 十几颗按钮挂载时同时去问主进程,等于同一次扫描跑十几遍(macOS 上那是一串
 *   目录列举)。所以模块级缓存一份 promise,**失败不缓存** —— 失败被记住的话,
 *   用户装完 IDE 要重启应用才看得见。
 *
 * ★ 缓存**不随菜单开关失效**:装了新 IDE 的用户重启应用后自然会重新扫。
 *   做「每次打开都重扫」的代价是每一次点开菜单都要等一轮目录列举。
 *
 * ## 远端工作区不画
 *
 * 调用方负责判(`isLocalEnvironment`):SSH 工作区里那些文件**不在本机磁盘上**,
 * 用本机的 VS Code 打开只会打开一个不存在的路径。菜单自己拿不到工作区,
 * 也不该猜 —— 这一条写在 `services/open-with.ts` 的头上,调用点照做。
 */
import type { ReactNode } from 'react'
import { ChevronDown, Copy, FolderOpen, SquareTerminal } from 'lucide-react'
import {
  DEFAULT_APP_TARGET_ID,
  REVEAL_TARGET_ID,
  TERMINAL_TARGET_ID,
  isGenericTarget,
  targetsForEntry,
  type OpenTarget,
  type WorkspacePathKind
} from '../../../shared/domain/open-target'
import { copyWorkspacePath, openWithTarget } from '../services/open-with'
import { useI18n, type TranslationKey } from '../i18n'
import { toast } from '../stores/toast'
import { cn } from '../lib/cn'
import { EditorIcon } from './brand/EditorIcon'
import { Menu, MenuItem, MenuSeparator } from './ui/Menu'
import { useOpenTargets } from './useOpenTargets'

/** 一枚 ▾ 触发器 + 面板。工具条、卡片、灯箱用这个。 */
export function OpenWithMenu({
  workspaceId,
  path,
  trigger,
  triggerClassName,
  align = 'end',
  width = 240,
  className,
  directory = false
}: {
  workspaceId: string
  /** 工作区相对路径,或工作区外的绝对路径(工具卡片给出的那种) */
  path: string
  /** 触发器里那颗图标;各调用点外观不同(有的只有 ▾,有的是「打开」+ ▾) */
  trigger: ReactNode
  triggerClassName?: string
  align?: 'start' | 'end'
  width?: number
  className?: string
  /** 见 `OpenWithItems` */
  directory?: boolean
}): ReactNode {
  const { t } = useI18n()
  return (
    <Menu
      label={t('openWith.label')}
      align={align}
      width={width}
      className={className}
      triggerClassName={triggerClassName}
      trigger={trigger}
    >
      {(close) => (
        <OpenWithItems workspaceId={workspaceId} path={path} directory={directory} close={close} />
      )}
    </Menu>
  )
}

/**
 * 菜单条目本身 —— 供已经在一个面板里的调用点(文件树的行菜单)直接铺开。
 *
 * @param directory 目标是一个**目录**。
 *   ★ 目录上只留「文件管理器 / 终端 / 复制路径」,编辑器一个都不列。
 *   `code <目录>` 与 `code <文件>` 是两个不同的动作 —— 前者会**把工作区根换掉**,
 *   而这一行的语义是「看看这个目录」,不是「把我们的工作区切到这儿」。
 *   顺手接上的表现是:用户点了一下目录,整个 VS Code 窗口的工作区换了。
 * @param omitReveal 不再画「文件管理器」那一行。
 *   ★ 只有文件树的行菜单用:它上面已经有一条「在文件管理器中显示」,
 *   同一个动作在一个面板里出现两次,用户会以为它们不一样(一个开 Finder、
 *   一个开别的什么)。留哪一条是调用方的决定,这里只负责不重复。
 */
export function OpenWithItems({
  workspaceId,
  path,
  directory = false,
  omitReveal = false,
  close
}: {
  workspaceId: string
  path: string
  directory?: boolean
  omitReveal?: boolean
  /** 选中之后关掉外层面板;平铺时由调用方给 */
  close: () => void
}): ReactNode {
  const { t } = useI18n()
  // 条目只在面板打开时才挂载,所以「挂载即探测」正好是「打开菜单才探测」
  const found = useOpenTargets()

  const copy = (kind: WorkspacePathKind): void => {
    void copyWorkspacePath(workspaceId, path, kind)
      .then(() => toast.success(t('openWith.copied'), 'copy-path'))
      /*
        需求:剪贴板写失败要说一声。静默失败的表现是用户粘出来一段**上一次**
        复制的内容,而他会以为这次复制的是这个文件的路径 —— 排查时那两件事
        完全对不上。
      */
      .catch(() => toast.error(t('openWith.openFailed'), 'copy-path'))
  }

  // 还没探测回来:一行提示,而不是空面板(空面板看起来像坏了)
  if (found === null) return <MenuItem disabled onSelect={() => undefined}>{t('common.loading')}</MenuItem>

  const items = targetsForEntry(found, directory)
    .filter((target) => !omitReveal || target.id !== REVEAL_TARGET_ID)

  return (
    <>
      {items.map((target) => (
        <OpenTargetItem key={target.id} workspaceId={workspaceId} path={path} target={target} close={close} />
      ))}
      {/*
        一台 IDE 都没装(或探测全失败)时,至少要让用户看见「复制路径」还在。
        原先按「条目数 ≤ 通用目标数」判;通用目标多了「默认应用」之后改成直接看有没有编辑器。
      */}
      {!directory && !items.some((target) => !isGenericTarget(target.id)) && (
        <MenuItem disabled onSelect={() => undefined}>{t('openWith.empty')}</MenuItem>
      )}
      <MenuSeparator />
      <MenuItem
        icon={<Copy size={14} />}
        onSelect={() => {
          close()
          copy('absolute')
        }}
      >
        {t('openWith.copyAbsolute')}
      </MenuItem>
      <MenuItem
        icon={<Copy size={14} />}
        onSelect={() => {
          close()
          copy('relative')
        }}
      >
        {t('openWith.copyRelative')}
      </MenuItem>
    </>
  )
}

/** 三个通用目标的名字。主进程给的是空 label(见 `shared/domain/open-target.ts` 文件头)。 */
const GENERIC_LABEL: Record<string, TranslationKey> = {
  [REVEAL_TARGET_ID]: 'openWith.reveal',
  [TERMINAL_TARGET_ID]: 'openWith.terminal',
  [DEFAULT_APP_TARGET_ID]: 'openWith.defaultApp'
}

/** 一个目标在界面上叫什么:通用目标走 i18n,编辑器用产品名(不翻译)。 */
export function openTargetLabel(t: (key: TranslationKey) => string, target: OpenTarget): string {
  const key = GENERIC_LABEL[target.id]
  return key === undefined ? target.label : t(key)
}

/**
 * 「用这个程序打开」的一行。图标 / 名字 / 失败提示三件事只在这里写一遍 ——
 * `OpenWithItems` 和文件树右键菜单都画它。
 */
export function OpenTargetItem({
  workspaceId,
  path,
  target,
  label,
  close
}: {
  workspaceId: string
  path: string
  target: OpenTarget
  /** 覆盖默认名字。只有右键菜单第一行要写成「在 X 中打开」 */
  label?: string
  /** 选中之后关掉外层面板 */
  close: () => void
}): ReactNode {
  const { t } = useI18n()
  return (
    <MenuItem
      icon={
        target.id === REVEAL_TARGET_ID ? <FolderOpen size={14} />
          : target.id === TERMINAL_TARGET_ID ? <SquareTerminal size={14} />
            : <EditorIcon icon={target.icon} />
      }
      description={target.id === TERMINAL_TARGET_ID ? t('openWith.terminalHint') : undefined}
      onSelect={() => {
        close()
        void openWithTarget(workspaceId, path, target.id).catch(() => toast.error(t('openWith.openFailed'), 'open-with'))
      }}
    >
      {label ?? openTargetLabel(t, target)}
    </MenuItem>
  )
}

/** 触发器里那枚 ▾。三处外观一致,只有尺寸跟着各自的工具条走。 */
export function OpenWithChevron({ size = 12, className }: { size?: number; className?: string }): ReactNode {
  return <ChevronDown size={size} className={cn('shrink-0', className)} aria-hidden />
}
