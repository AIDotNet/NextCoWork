import { describe, expect, it } from 'vitest'
import { fileCategory, sortEntries, type FileEntry } from '../file-tree'

const f = (name: string, extra: Partial<FileEntry> = {}): FileEntry => ({
  name,
  path: name,
  kind: 'file',
  hidden: name.startsWith('.'),
  ...extra
})
const d = (name: string, extra: Partial<FileEntry> = {}): FileEntry => f(name, { kind: 'dir', ...extra })

const names = (list: readonly FileEntry[]): string[] => list.map((e) => e.name)

/**
 * 主进程列目录、渲染层画树,两边都调这一个 —— 所以顺序在这里定死一次,
 * 而不是在树组件里再排一遍(那样刷新前后的顺序会因为谁排的而不同)。
 */
describe('sortEntries', () => {
  it('目录永远在文件前面,与排序键无关', () => {
    const list = [f('a.ts'), d('zzz'), f('b.ts'), d('aaa')]
    for (const by of ['name', 'mtime', 'size'] as const) {
      const out = names(sortEntries(list, by))
      expect(out.slice(0, 2).sort(), by).toEqual(['aaa', 'zzz'])
      expect(out.slice(2).sort(), by).toEqual(['a.ts', 'b.ts'])
    }
  })

  /**
   * ★ `numeric: true` 的全部意义:纯字符串比较会把 `v10` 排在 `v2` 前面,
   * 因为 `'1' < '2'`。文件名里带序号是极常见的(`part1`…`part10`、`v2`…`v10`),
   * 而那个顺序看着就是坏的。
   */
  it('名字里的数字按值比较,不按字符比较', () => {
    const list = [f('v10.ts'), f('v2.ts'), f('v1.ts')]
    expect(names(sortEntries(list))).toEqual(['v1.ts', 'v2.ts', 'v10.ts'])
  })

  /**
   * ★ `sensitivity: 'base'` 让大小写不参与排序。不这么做的话按 ASCII 码
   * 全部大写名字会整块沉在小写名字前面(`README` < `index` < `readme`),
   * 而 macOS 的访达、VS Code 的资源管理器都不是这个顺序。
   */
  it('大小写不影响顺序', () => {
    expect(names(sortEntries([f('B.ts'), f('a.ts')]))).toEqual(['a.ts', 'B.ts'])
  })

  /** 中文名按拼音排,不按码点排 —— 码点序对中文用户等于没排 */
  it('中文按拼音排序', () => {
    expect(names(sortEntries([f('上海'), f('广州'), f('北京')]))).toEqual(['北京', '广州', '上海'])
  })

  it('按修改时间是**倒序**:最近改过的在最上面', () => {
    const list = [f('old', { mtime: 100 }), f('new', { mtime: 300 }), f('mid', { mtime: 200 })]
    expect(names(sortEntries(list, 'mtime'))).toEqual(['new', 'mid', 'old'])
  })

  it('按大小是**倒序**:最大的在最上面', () => {
    const list = [f('s', { size: 1 }), f('l', { size: 900 }), f('m', { size: 50 })]
    expect(names(sortEntries(list, 'size'))).toEqual(['l', 'm', 's'])
  })

  /**
   * ★ 平手时回落到名字,而不是听凭原始顺序。目录里一批文件同一秒写出来
   * (`git checkout` 之后就是这样)是常态,没有这个回落,每次刷新的顺序都可能不一样。
   */
  it('时间/大小相同时按名字打破平局', () => {
    const same = [f('c', { mtime: 5 }), f('a', { mtime: 5 }), f('b', { mtime: 5 })]
    expect(names(sortEntries(same, 'mtime'))).toEqual(['a', 'b', 'c'])
    const sized = [f('c', { size: 7 }), f('a', { size: 7 }), f('b', { size: 7 })]
    expect(names(sortEntries(sized, 'size'))).toEqual(['a', 'b', 'c'])
  })

  /** 缺字段的条目当 0 —— 主进程 stat 失败时 `size`/`mtime` 就是 undefined */
  it('缺 mtime / size 的条目排在最后,不是排在最前', () => {
    expect(names(sortEntries([f('none'), f('has', { mtime: 1 })], 'mtime'))).toEqual(['has', 'none'])
    expect(names(sortEntries([f('none'), f('has', { size: 1 })], 'size'))).toEqual(['has', 'none'])
  })

  it('永不原地修改入参 —— 调用方持有的 listing 要保持不可变', () => {
    const src = [f('b'), f('a')]
    const out = sortEntries(src)
    expect(names(src)).toEqual(['b', 'a'])
    expect(out).not.toBe(src)
  })

  it('空数组不炸', () => {
    expect(sortEntries([])).toEqual([])
  })
})

/**
 * 决定树里那个图标的颜色。参考截图里 `package-lock.json` 是一把**黄锁**、
 * 不是那个 `{}` —— 这一条就是本文件存在的理由。
 */
describe('fileCategory', () => {
  it('目录不看名字', () => {
    expect(fileCategory('package.json', 'dir')).toBe('dir')
    expect(fileCategory('随便什么', 'dir')).toBe('dir')
  })

  /**
   * ★ **锁文件的判断必须排在后缀表前面。** `package-lock.json` 的后缀是 json,
   * 按后缀查会得到 `json`,画成 `{}` —— 而参考实现画的是一把锁。
   * 也就是说这里的**判断顺序本身是规则的一部分**,不是实现细节。
   */
  it('锁文件先于后缀被认出来', () => {
    expect(fileCategory('package-lock.json')).toBe('lock')
    expect(fileCategory('pnpm-lock.yaml')).toBe('lock')
    expect(fileCategory('yarn.lock')).toBe('lock')
    expect(fileCategory('bun.lock')).toBe('lock')
    // 对照组:同样是 json / yaml 后缀,不带 -lock 就走后缀表
    expect(fileCategory('package.json')).toBe('json')
    expect(fileCategory('pnpm-workspace.yaml')).toBe('yaml')
  })

  it('同一族的后缀归一类', () => {
    for (const n of ['a.ts', 'a.tsx', 'a.mts', 'a.cts']) expect(fileCategory(n), n).toBe('ts')
    for (const n of ['a.js', 'a.jsx', 'a.mjs', 'a.cjs']) expect(fileCategory(n), n).toBe('js')
    for (const n of ['a.yml', 'a.yaml', 'a.toml']) expect(fileCategory(n), n).toBe('yaml')
    for (const n of ['a.png', 'a.svg', 'a.webp']) expect(fileCategory(n), n).toBe('image')
  })

  it('后缀大小写不敏感', () => {
    expect(fileCategory('README.MD')).toBe('markdown')
    expect(fileCategory('App.TSX')).toBe('ts')
  })

  /**
   * 办公文档分四类,不是一个笼统的 office —— 图标要给出不同的预期动作。
   * `.csv` 归表格是有意的:它打开是一张表,给纯文本图标会让人以为要自己数逗号。
   * ★ `.key` **不该**是幻灯片:仓库里的 `*.key` 几乎都是私钥。
   */
  it('办公文档按用途分四类', () => {
    for (const n of ['a.doc', 'a.docx', 'a.rtf', 'a.odt']) expect(fileCategory(n), n).toBe('doc')
    for (const n of ['a.xlsx', 'a.ods', 'a.csv', 'a.tsv']) expect(fileCategory(n), n).toBe('sheet')
    for (const n of ['a.ppt', 'a.pptx', 'a.odp']) expect(fileCategory(n), n).toBe('slides')
    expect(fileCategory('规格书.pdf')).toBe('pdf')
    expect(fileCategory('server.key')).toBe('text')
  })

  /**
   * ★ 三种「没有后缀」长得不一样,但都该落到 `text`:
   * 完全没有点、以点开头且只有那一个点、以及点在末尾。
   * 第三种是最容易漏的 —— `slice(dot + 1)` 会得到空串,
   * 表里查不到就必须有兜底,否则 `BY_EXT['']` 是 undefined 直接往下传。
   */
  it('没有可用后缀时落到 text', () => {
    expect(fileCategory('LICENSE')).toBe('text')
    expect(fileCategory('Makefile')).toBe('text')
    expect(fileCategory('.gitignore')).toBe('text')
    expect(fileCategory('.env')).toBe('text')
    expect(fileCategory('trailing.')).toBe('text')
  })

  it('不认识的后缀落到 text,不抛也不返回 undefined', () => {
    expect(fileCategory('a.xyzzy')).toBe('text')
    expect(fileCategory('a.')).toBe('text')
  })

  /** 多段后缀只看最后一段 —— `.tar.gz` 是压缩包,不是 `tar` 那一类的什么别的 */
  it('多段后缀只认最后一段', () => {
    expect(fileCategory('bundle.tar.gz')).toBe('archive')
    expect(fileCategory('tsconfig.node.tsbuildinfo')).toBe('text')
    expect(fileCategory('未命名绘图.excalidraw')).toBe('draw')
  })

  it('隐藏文件也按后缀分类', () => {
    expect(fileCategory('.eslintrc.json')).toBe('json')
    expect(fileCategory('.prettierrc.yaml')).toBe('yaml')
  })
})
