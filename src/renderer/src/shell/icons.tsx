/**
 * 图标表 —— 侧边栏导航项、功能 Tab、内层 Tab 三处引用同一份。
 *
 * 单独一个文件是因为**导航项和功能 Tab 必须共用同一个图标**:截图 4aa68110 里,
 * 「定时任务」的功能 Tab 打开时,侧边栏对应的那一项是高亮的 —— 两处是同一个东西的
 * 两个位置。图标各写各的话,总有一天会漂移成两个不同的图标。
 */
import {
  Blocks,
  Bookmark,
  Bug,
  ChartNoAxesCombined,
  ClockFading,
  Clock,
  Download,
  Earth,
  Eye,
  File,
  Files,
  FileDiff,
  Folder,
  GitBranch,
  Image as ImageIcon,
  Link,
  MessageSquare,
  Package,
  Pencil,
  PenTool,
  Play,
  Plus,
  Puzzle,
  Search,
  Settings,
  Shield,
  Sparkles,
  Square,
  SquareTerminal,
  Table,
  Upload,
  Wrench,
  FileText,
  Film,
  Gamepad2,
  Music,
  Tv,
  type LucideIcon
} from 'lucide-react'
import type { MenuIconName } from '../../../shared/plugin/contribution'
import type { FeatureKind, InnerTabKind } from '../../../shared/domain/tab'

/**
 * ★ 这四个字形是**对着截图放大 6~16 倍、再比对 lucide 的真实 path 数据挑的**,
 * 不是按名字猜的:
 * 「定时任务」表盘上有缺口(`ClockFading`),不是完整一圈的 `Clock`;
 * 「扩展」的 `Blocks` 是**新挑的,没有参考图可比** —— 这一格原本是「Skill 管理」,
 *   用的是带横线的卷轴(`ScrollText`);现在它扩成了技能 / 命令 / 子代理 / 钩子
 *   四类资源的统一入口,而卷轴只说得了 Skill 一件事。有了设计稿该回来重挑。
 * 「浏览器」是带大陆块的地球(`Earth`)不是经纬线球(`Globe`)——
 *   参考图那个是地球+光标,lucide 全套(earth / globe / globe-2 / …)都没有,`Earth` 是最近的;
 * 「每日回顾」是柱子加一条趋势线(`ChartNoAxesCombined`)不是打勾的日历。
 * 换回去之前先去比一眼 docs/images 与 docs/image-new 里的原图。
 */
export const FEATURE_ICON: Record<FeatureKind, LucideIcon> = {
  scheduled: ClockFading,
  extensions: Blocks,
  browser: Earth,
  git: GitBranch,
  review: ChartNoAxesCombined,
  settings: Settings
}

export const INNER_TAB_ICON: Record<InnerTabKind, LucideIcon> = {
  chat: MessageSquare,
  terminal: SquareTerminal,
  doc: FileText,
  draw: PenTool,
  browser: Earth,
  /*
    ⚠️ 这一个是**待核对的猜测**,不像上面那批是比对过 path 数据的。
    截图里「文件预览」的字形是一个圆角方框里带一道斜线/山形,`Image` 最接近
    (圆角矩形 + 小圆点 + 山形对角线)。等拿到更大的原图再确认一次。
    别名导入是因为 `Image` 和 DOM 的全局 `Image` 构造器重名。
  */
  preview: ImageIcon,
  /*
    参考里「工作区文件」那个 Tab 的字形是**带横线的单张文档**,也就是 `FileText`——
    但 `doc` 已经占了它,同一条 Tab 条上两种 kind 同图标就白画了。
    退一格用叠起来的 `Files`:它同样是"文档"语义,又能和单个文档区分开。
    **这是有意偏离参考的一处**,不是没看图。
  */
  files: Files,
  // 改动审查:带 +/− 的文档字形,正好是「这个 Tab 是一轮改动的 diff」。
  changes: FileDiff,
  // 插件接管的自定义编辑器。用拼图块而不是让插件给图标:Tab 条上那个字形是
  // 用户判断「这个 Tab 是谁提供的」的地方。
  custom: Puzzle,
  /*
    插件带进来的网页应用。用地球而不是拼图块:用户在 Tab 条上首先要认出
    「这是个网页」——「它由某个插件带进来」是第二位的信息,标题里已经有了。
    与浏览器 Tab 同字形是**有意**的:它们对用户就是同一类东西。
  */
  webapp: Earth
}

/**
 * 菜单图标名 → 组件。**闭集**,和 `shared/plugin/contribution.ts` 的
 * `MENU_ICON_NAMES` 一一对应(那边有一条测试钉住两张表不脱节)。
 *
 * ★ 为什么插件给的是**名字**而不是组件、也不是 SVG:
 *
 * - 给组件意味着插件要 import lucide,那它就得把整个图标库打进自己的 bundle;
 * - 给 SVG 意味着插件能画一个和系统图标一模一样的东西放在菜单里,
 *   而菜单是用户判断「这个操作是谁提供的」的地方;
 * - 给名字则让这一层完全在宿主控制之下:认不出的名字回落到拼图块
 *   (`normalizeMenuIcon`),永远不会渲染出一个宿主没审过的字形。
 *
 * ★★ 上面那条「不给 SVG」后来被有意**放宽了一条并行通道**,原理由保留如上:
 * claude-code / codex 这类 CLI 插件要求用官方品牌 logo 出现在 `+` 菜单里,
 * 而品牌字形不该由宿主硬编码(品牌会改版,宿主不该追)。放宽后的边界:
 * 图标文件(`contributes.commands[].iconFile`)来自**用户已安装的那个插件包**,
 * 由主进程装载时读出转 data URL(`manager.readCommandIcons`,≤32KB,svg/png),
 * 只出现在带该插件署名的菜单条目上(`<img>` 是非脚本上下文,svg 脚本不执行;
 * 渲染入口见 `InnerTabBar` 的 `menuIcon`)。风险从「宿主字形被冒充」缩小为
 * 「插件自己的图标」—— 与插件标题文案同级别的信任。名字闭集对**其余**
 * 菜单项的保证原样不变。
 */
export const MENU_ICON: Record<MenuIconName, LucideIcon> = {
  file: File,
  'file-text': FileText,
  files: Files,
  folder: Folder,
  image: ImageIcon,
  'pen-tool': PenTool,
  pencil: Pencil,
  eye: Eye,
  search: Search,
  terminal: SquareTerminal,
  'message-square': MessageSquare,
  globe: Earth,
  'git-branch': GitBranch,
  clock: Clock,
  settings: Settings,
  play: Play,
  square: Square,
  plus: Plus,
  download: Download,
  upload: Upload,
  package: Package,
  puzzle: Puzzle,
  sparkles: Sparkles,
  wrench: Wrench,
  bug: Bug,
  chart: ChartNoAxesCombined,
  table: Table,
  link: Link,
  bookmark: Bookmark,
  shield: Shield,
  // 网页应用那一类(见 shared/plugin/contribution.ts 的 MENU_ICON_NAMES)
  tv: Tv,
  film: Film,
  music: Music,
  gamepad: Gamepad2
}
