/**
 * `nextcowork/ui` —— **插件视图能直接用的宿主控件**。
 *
 * ## 为什么需要它(也就是不写它会怎样)
 *
 * 插件视图跑在 `ncw-plugin://` 的 iframe 里,和宿主不同源(见
 * `shell/PluginViewFrame.tsx` 的文件头)。此前宿主只往里 postMessage 了 24 个
 * 主题 token,`components/ui/**` 那 19 个控件一个都够不着。于是每个想长得像
 * 原生的插件,都只能照着截图把 Button / Dialog / Select 重画一遍 ——
 * 而宿主一改尺寸或配色,所有插件同时变歪,且没有任何人会收到通知。
 *
 * 这个入口把**宿主那份组件本身**打成一个模块,由协议层经 import map 下发。
 * 插件写 `import { Button } from 'nextcowork/ui'`,拿到的和宿主自己用的是
 * 同一份实现、同一套 token、同一套动效档位。
 *
 * ## 它**不是**第二套 UI 库
 *
 * 这里一行组件代码都没有,全是 re-export(AGENTS.md §11:先找现有抽象)。
 * 加一个控件 = 在下面的清单里加一行。**不要**在这个目录里写新组件:
 * 宿主用不到的控件插件多半也用不到,而写在这儿的那一份永远不会被宿主的
 * 视觉走查覆盖到。
 *
 * ## 为什么少了几个
 *
 * `ToastViewport` 要 `stores/toast`,而 store 是宿主窗口的单例 —— 打进来
 * 会得到一个**永远空**的 toast 列表(插件推进去的消息进不了宿主那份 store)。
 * 插件要提示用户走 `ncw.window.showMessage()`,那条路是真的能到宿主的。
 *
 * ## 跨进程的那条线在哪
 *
 * 这些组件**只画界面**。任何要改文件、跑命令、读剪贴板的事,依然只能经
 * `nextcowork/view` 的文档通道或插件逻辑侧的 `nextcowork` RPC —— iframe 里
 * 没有 preload,这个模块也没有给它开任何新口子。
 */

/*
  ★ 样式在这里 import,而不是让插件自己 `<link>` 一份。

  构建脚本据此产出 `ui.css`,协议层再把它作为 `<link>` 注入每个插件视图 ——
  也就是说插件**不写任何一行样式引用**就能拿到正确外观。
  交给插件自己引的话,「忘了引」的症状是控件有布局没颜色,而那看起来
  像是宿主坏了。
*/
import './ui.css'

// ── 按钮 ──
export { Button } from '../components/ui/Button'
export { IconButton } from '../components/ui/IconButton'
export { ActionIconButton, useTransientStatus, type TransientStatus } from '../components/ui/ActionIconButton'

// ── 输入 ──
export { TextInput } from '../components/ui/TextInput'
export { TextArea } from '../components/ui/TextArea'
export { NumberInput } from '../components/ui/NumberInput'
export { Toggle } from '../components/ui/Toggle'
export { Slider } from '../components/ui/Slider'
export { Segmented } from '../components/ui/Segmented'
export { Select, type SelectOption } from '../components/ui/Select'
export { CheckboxCards, RadioCards, type ChoiceOption } from '../components/ui/ChoiceCards'

// ── 容器与反馈 ──
export { Dialog } from '../components/ui/Dialog'
// `Surface` 这次改成了无边框的文本风格块,`SurfaceTone` / `SurfaceRail` 两个类型
// 随之删除(理由见 `components/ui/Surface.tsx` 文件头)——插件若还在传 tone/rail,
// 重新编译时会报类型错误,这是有意的:留着两个不生效的参数更难排查。
export { Surface, SurfaceRow, SurfaceReveal, SURFACE_INDENT } from '../components/ui/Surface'
export { EmptyState } from '../components/ui/EmptyState'
export { ProgressBar } from '../components/ui/ProgressBar'
export { Spinner } from '../components/ui/Spinner'
export { Tooltip } from '../components/ui/Tooltip'

// ── 浮层 ──
export { Menu, MenuItem, MenuLabel, MenuSeparator } from '../components/ui/Menu'
export { ContextMenu, type ContextMenuPosition } from '../components/ui/ContextMenu'

/**
 * 条件类名合并(twMerge:后写的赢)。
 *
 * ★ 一并导出的理由:上面每个组件都收 `className`,而插件想覆盖样式时必须用
 * 同一个合并器 —— 用字符串拼接的话 `px-3` 和 `px-2` 会同时出现在 class 上,
 * 谁赢取决于 Tailwind 生成顺序,表现为「同样的代码在两次构建后表现不同」。
 */
export { cn } from '../lib/cn'

/** 当前动效档位。用 Motion 做自定义动画时**必须**读它,理由见该文件头。 */
export { useMotionLevel, motionScale, type MotionLevel } from '../theme/useMotionLevel'
