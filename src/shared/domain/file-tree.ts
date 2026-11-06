/**
 * 右侧「工作区文件」那棵树的领域模型。
 *
 * 排序和分类都是**纯函数**放在 shared 里,不是渲染层的临时逻辑:
 * 树是懒加载的(展开一层拉一层),所以每次拉回来的那一批都要按同一套规则排;
 * 分类同理 —— 主进程将来做「按类型筛选」也得用同一张表,两边各写一份必然漂移。
 */

/**
 * 目录里的一项。`path` 是**工作区相对**、始终用 `/` 分隔 ——
 * 它同时是树的展开状态、选中状态、和打开的 Tab 三处的 key,
 * 所以必须是跨平台稳定的那一种写法(见 main 的 `toWorkspaceRelative`)。
 */
export interface FileEntry {
  name: string
  path: string
  kind: 'dir' | 'file'
  /** 名字以 `.` 开头。筛选放在渲染层(工具条上那个"眼睛"要能实时切) */
  hidden: boolean
  /** 文件大小,目录为 undefined */
  size?: number
  mtime?: number
}

export interface DirListing {
  /** 被列的目录,工作区相对;`''` = 工作区根 */
  path: string
  entries: FileEntry[]
  /**
   * 条目太多被截断了。**必须有** —— `node_modules` 一个目录就能有上万项,
   * 整批塞进 IPC 会把渲染层卡住,而 UI 得知道自己看到的不是全部。
   */
  truncated: boolean
}

export const DIR_LISTING_LIMIT = 2000

/**
 * 输入框 `@` 检索回来的一条候选。
 *
 * ★ 只有 `path` 和 `name`,**没有 kind/size/mtime** —— 它不是树里的一项,
 * 而是「一条能落进草稿的引用」。`path` 是工作区相对、`/` 分隔的,
 * 因为它会原样写进 `[name](path)` 发给模型,而那正是 `displayPath` 认的写法。
 */
export interface FileSuggestion {
  path: string
  name: string
}

/** 工具条上那个「↑↓」的三档。**目录永远在前**,排序只作用在两组各自内部。 */
export type SortBy = 'name' | 'mtime' | 'size'

/**
 * ★ **目录在前,文件在后,各自再按 `by` 排**。参考实现就是这个顺序
 * (build / docs / node_modules / out / scripts / src,然后才是 bun.lock 起的那串)。
 *
 * 名字比较用 `localeCompare` 带 `numeric` —— 否则 `v10` 会排在 `v2` 前面,
 * 而带序号的文件名在项目目录里到处都是。
 *
 * 按时间/大小排时**平局回落到名字**:否则两个 mtime 相同的文件每次列目录
 * 都可能换位置,树看着会自己抖。
 */
export function sortEntries(entries: readonly FileEntry[], by: SortBy = 'name'): FileEntry[] {
  const byName = (a: FileEntry, b: FileEntry): number =>
    a.name.localeCompare(b.name, 'zh-Hans-CN', { numeric: true, sensitivity: 'base' })

  return [...entries].sort((a, b) => {
    if (a.kind !== b.kind) return a.kind === 'dir' ? -1 : 1
    if (by === 'mtime') return (b.mtime ?? 0) - (a.mtime ?? 0) || byName(a, b)
    if (by === 'size') return (b.size ?? 0) - (a.size ?? 0) || byName(a, b)
    return byName(a, b)
  })
}

/**
 * 文件的**图标类别**。故意不是"语言" —— 图标要表达的是"这是什么东西",
 * 而 `package-lock.json` 是一把锁不是一个 JSON(参考实现里就是这么画的)。
 */
export type FileCategory =
  | 'dir'
  | 'lock'
  | 'json'
  | 'ts'
  | 'js'
  | 'yaml'
  | 'markdown'
  | 'style'
  | 'html'
  | 'image'
  | 'draw'
  | 'archive'
  | 'code'
  /*
    需求:办公文档也要认得出来。工作区里躺着的 `.docx` / `.xlsx` / `.pdf` 不是代码,
    但它们是用户自己的东西,落进「未知 → text」那一档后,反而成了一列灰色文档图标里
    最难找的几个。

    ★ 四类分开写,不合并成一个 `office`:它们在界面上的**预期动作**不同
    (文档是读、表格是查数、幻灯是放映、PDF 基本是只读附件),
    合成一类等于放弃了图标本来要给的那点预判。
  */
  | 'doc'
  | 'sheet'
  | 'slides'
  | 'pdf'
  | 'text'

/** 后缀 → 类别。放在模块级,分类函数因此是一次查表而不是一串 if。 */
const BY_EXT: Readonly<Record<string, FileCategory>> = {
  ts: 'ts',
  tsx: 'ts',
  mts: 'ts',
  cts: 'ts',
  js: 'js',
  jsx: 'js',
  mjs: 'js',
  cjs: 'js',
  json: 'json',
  jsonc: 'json',
  json5: 'json',
  yml: 'yaml',
  yaml: 'yaml',
  toml: 'yaml',
  md: 'markdown',
  mdx: 'markdown',
  css: 'style',
  scss: 'style',
  sass: 'style',
  less: 'style',
  html: 'html',
  htm: 'html',
  xml: 'html',
  svg: 'image',
  png: 'image',
  jpg: 'image',
  jpeg: 'image',
  gif: 'image',
  webp: 'image',
  avif: 'image',
  ico: 'image',
  excalidraw: 'draw',
  doc: 'doc',
  docx: 'doc',
  rtf: 'doc',
  odt: 'doc',
  xls: 'sheet',
  xlsx: 'sheet',
  ods: 'sheet',
  /*
    ★ csv/tsv 归表格而不是 text:它们**打开来就是一张表**,给纯文本图标等于让人
    以为要自己数逗号。类别说的是内容形态,不承诺用什么编辑器打开
    (同一条理由见上面 `package-lock.json` 归 lock)。
    ★ 故意**不收 `key`**:仓库里的 `*.key` 几乎都是私钥,不是 Keynote。
    认错方向的图标比没有图标更糟。
  */
  csv: 'sheet',
  tsv: 'sheet',
  ppt: 'slides',
  pptx: 'slides',
  odp: 'slides',
  pdf: 'pdf',
  zip: 'archive',
  gz: 'archive',
  tgz: 'archive',
  tar: 'archive',
  rar: 'archive',
  '7z': 'archive',
  py: 'code',
  go: 'code',
  rs: 'code',
  java: 'code',
  kt: 'code',
  rb: 'code',
  php: 'code',
  c: 'code',
  h: 'code',
  cc: 'code',
  cpp: 'code',
  hpp: 'code',
  cs: 'code',
  swift: 'code',
  sh: 'code',
  bash: 'code',
  zsh: 'code',
  fish: 'code',
  sql: 'code',
  vue: 'code',
  svelte: 'code'
}

export function fileCategory(name: string, kind: 'dir' | 'file' = 'file'): FileCategory {
  if (kind === 'dir') return 'dir'

  const lower = name.toLowerCase()

  /*
    锁文件先判,而且**按整名判不按后缀判**:`package-lock.json` 的后缀是 json,
    但它是一把锁。后缀表放在后面,所以这里的顺序本身是规则的一部分。

    三条各有各的形状,合不成一条:`yarn.lock` / `*.lock` 靠后缀,
    `package-lock.json` / `pnpm-lock.yaml` 靠 `-lock.` 这个中缀,
    而 `bun.lock` 两条都不沾,只能整名列出来。
  */
  if (lower.endsWith('.lock') || /-lock\.(json|yaml|yml)$/.test(lower) || lower === 'bun.lock') {
    return 'lock'
  }

  const dot = lower.lastIndexOf('.')
  // 没有后缀(LICENSE、Makefile),或者以点开头且只有那一个点(.gitignore)
  if (dot <= 0) return 'text'
  return BY_EXT[lower.slice(dot + 1)] ?? 'text'
}
