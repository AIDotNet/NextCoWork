/**
 * 文件图标 —— 参考实现的文件树是**彩色**的,那不是装饰。
 *
 * 一屏三十行文件名,靠字形本身几乎读不出结构;颜色让 `.ts` / `.json` / 锁文件
 * 在余光里就分得开。所以这里每一类都带一个 `className`,而不是清一色的灰色文档图标。
 *
 * 色相全部走**已有 token**(`text-accent` / `text-danger` / `text-fg-*`)加少量
 * Tailwind 调色板 —— 不新增主题变量:这套颜色是"语法高亮"性质的,
 * 它跟着深浅主题走的是明度而不是品牌色,和 §8 那 21 个 token 不是一回事。
 */
import {
  Braces,
  FileCode2,
  FileImage,
  FileLock2,
  FileSpreadsheet,
  FileText,
  FileType2,
  Folder,
  FolderOpen,
  Hash,
  NotebookText,
  Package,
  PenTool,
  Presentation,
  Settings2,
  type LucideIcon
} from 'lucide-react'
import type { FileCategory } from '../../../shared/domain/file-tree'
import { fileCategory } from '../../../shared/domain/file-tree'

interface IconSpec {
  Icon: LucideIcon
  /** 只给色,尺寸由调用方定 —— 树里是 14,别的地方可能不是 */
  className: string
}

const SPEC: Readonly<Record<FileCategory, IconSpec>> = {
  dir: { Icon: Folder, className: 'text-fg-muted' },
  // 锁文件在参考实现里是一把**黄色的锁**,是整棵树里最好认的一个
  lock: { Icon: FileLock2, className: 'text-amber-500' },
  json: { Icon: Braces, className: 'text-fg-muted' },
  ts: { Icon: FileType2, className: 'text-sky-500' },
  js: { Icon: FileCode2, className: 'text-amber-500' },
  yaml: { Icon: Settings2, className: 'text-danger' },
  markdown: { Icon: FileText, className: 'text-sky-600' },
  style: { Icon: Hash, className: 'text-teal-500' },
  html: { Icon: FileCode2, className: 'text-orange-500' },
  image: { Icon: FileImage, className: 'text-violet-500' },
  draw: { Icon: PenTool, className: 'text-accent' },
  archive: { Icon: Package, className: 'text-fg-muted' },
  code: { Icon: FileCode2, className: 'text-emerald-600' },
  /*
    办公文档四类。色相刻意照搬各自那套软件的招牌色(蓝 Word / 绿 Excel /
    橙 PowerPoint / 红 PDF)—— 这条肌肉记忆比任何自创配色都强,
    用户不需要学就认得。

    ★ `doc` 用 `NotebookText`(装订本)而不是「文件+字母」那一族:后者的两个变体
    (`FileType` / `FileType2`)已经被 `ts` 占了一个,两个并排出现时分不出来。
    ★ `pdf` 复用了 `FileText` 的轮廓 —— 这里**颜色才是识别位**:
    markdown 是蓝、未知文本是灰、pdf 是红,三者在余光里靠色相分得开,
    而 PDF 的红是这棵树里最不需要解释的一个信号。
  */
  doc: { Icon: NotebookText, className: 'text-blue-600' },
  sheet: { Icon: FileSpreadsheet, className: 'text-green-600' },
  slides: { Icon: Presentation, className: 'text-orange-500' },
  pdf: { Icon: FileText, className: 'text-red-500' },
  text: { Icon: FileText, className: 'text-fg-faint' }
}

/**
 * 目录展开时换成 `FolderOpen` —— 这是树里除了那个小箭头之外
 * 第二个"我是开着的"信号,箭头只有 12px,单靠它太弱。
 */
export function iconFor(
  name: string,
  kind: 'dir' | 'file',
  expanded = false
): IconSpec {
  if (kind === 'dir') {
    return expanded ? { Icon: FolderOpen, className: 'text-fg-muted' } : SPEC.dir
  }
  return SPEC[fileCategory(name, kind)]
}
