/**
 * 首屏骨架屏 —— 握手完成之前占着位置的那一屏。
 *
 * 在它之前这里是 `<div className="h-full bg-app" />`,一块纯色。窗口在渲染层画出
 * 第一帧之前就已经 `show()` 出来了(main/index.ts 的 `ready-to-show`),于是用户
 * 看到的是「窗口开着、里面什么都没有」—— 看着像程序坏了,而不是在加载。
 *
 * ★ **尺寸全部照抄 AppShell,一个都别自己调。** 297 / 34 / 8 这三个数是从参考截图
 * 量出来的(AppShell 和 Sidebar 的文件头写了怎么量的)。这里但凡差一像素,
 * 骨架屏换成真界面时面板边界就会挪一下 —— 那一下比白屏更刺眼。
 *
 * ★ **不做 shimmer / 呼吸动画。** 这个仓库的动效基调很克制(`--ease-panel`、
 * `reveal-delayed` 的 160ms),一个跑马灯骨架屏在里面会显得很跳。占位块保持静止。
 *
 * ★ **换成真界面时也不加淡入。** 两者的面板轮廓完全重合,硬切的观感是「内容填进来」;
 * 加淡入反而是让已经画好的面板再闪一次。
 *
 * ★ **侧边栏画展开态。** `sidebarCollapsed` 在 stores/window.ts 里是明确不落盘的
 * (「是此刻的呈现状态,不是用户的偏好」),每次启动都从 `false` 开始 —— 所以展开态
 * **就是**真界面接下来必然显示的状态,这里跟着画零跳变。别为它加什么记忆机制:
 * 那只会让骨架屏和真界面在启动时不一致。
 */
import type { ReactNode } from 'react'
import { cn } from '../lib/cn'
import { IS_MAC } from '../lib/platform'

/** 一块占位条。`bg-tint` 是界面里最轻的那一档填充(激活的内层 Tab 用的就是它) */
function Bar({ className }: { className?: string }): ReactNode {
  return <div className={cn('rounded-md bg-tint', className)} />
}

export function AppSkeleton({ focusMode = false }: { focusMode?: boolean }): ReactNode {
  return (
    <div className="app-ground flex h-full bg-app p-2">
      {/* 侧边栏面板 —— 宽度和圆角同 Sidebar 的 <aside> */}
      <div
        data-theme-region="sidebar"
        className={cn('mr-2 flex shrink-0 flex-col overflow-hidden rounded-panel bg-surface', focusMode ? 'w-[240px]' : 'w-[297px]')}
      >
        {/*
          34px 表头。macOS 上红绿灯就落在这一条的左端(hiddenInset),所以和
          Sidebar 一样留 `pl-[74px]` —— 少了它,骨架屏期间三颗灯会压在占位块上。
        */}
        <div className={cn('h-[34px] shrink-0', IS_MAC ? 'pl-[74px]' : 'pl-2')} />

        {/* 专注窗口不预留品牌行和全局导航，首屏轮廓与实际侧边栏一致。 */}
        {!focusMode && (
          <div className="flex items-center gap-2 px-4 pt-1 pb-4">
            <Bar className="size-5 rounded-full" />
            <Bar className="h-3.5 w-[104px]" />
          </div>
        )}

        <div className={cn('flex gap-1.5 px-3', focusMode ? 'items-center pt-2' : 'flex-col')}>
          <Bar className={focusMode ? 'h-8 flex-1' : 'h-8'} />
          <Bar className={focusMode ? 'size-8 shrink-0' : 'h-8'} />
        </div>
        {!focusMode && (
          <div className="mt-4 flex flex-col gap-1 px-3">
            <Bar className="h-7 w-[72%]" />
            <Bar className="h-7 w-[64%]" />
            <Bar className="h-7 w-[78%]" />
            <Bar className="h-7 w-[58%]" />
          </div>
        )}

        {/* 下半:会话区。占满剩下的高度,让面板底边不出现一段突兀的空白 */}
        <div className={cn('flex min-h-0 flex-1 flex-col gap-1 px-3', focusMode ? 'mt-4' : 'mt-6')}>
          <Bar className="mb-1 h-3 w-[84px]" />
          <Bar className="h-7 w-[88%]" />
          <Bar className="h-7 w-[70%]" />
          <Bar className="h-7 w-[81%]" />
        </div>
      </div>

      {/* 主面板 */}
      <main
        data-theme-region="canvas"
        className="app-canvas flex min-w-0 flex-1 flex-col overflow-hidden rounded-panel bg-canvas"
      >
        {/*
          外层 Tab 条。`bg-chrome` 不是 `bg-surface` —— 浅色下前者比侧边栏更暗,
          这个层次关系在 AppShell 那边有整段注释,骨架屏跟着它走才不会在切换的
          那一帧变色。
        */}
        <div
          className={cn(
            'app-drag flex h-[34px] shrink-0 items-end gap-1.5 px-2 pb-1 bg-chrome',
            !IS_MAC && 'pr-window-controls'
          )}
        >
          <Bar className="h-[26px] w-[148px]" />
        </div>

        {/* 内容区:留空。这里接下来是对话/文件/终端,画什么都可能猜错 */}
        <div className="flex min-h-0 flex-1" />
      </main>
    </div>
  )
}