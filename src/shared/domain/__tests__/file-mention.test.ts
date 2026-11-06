/**
 * `@` 文件引用的格式与光标逻辑。
 *
 * 这里的错误代价是**不对称**的:
 * - 认漏一条引用 → chip 不显示,用户看得见也能自己接受(文本仍然是对的);
 * - 认多一条 / 切错位置 → 高亮层与 `<textarea>` 的字符不再逐字对齐,
 *   光标从此画在错的地方,而那个 bug 看起来像输入框坏了。
 *
 * 所以「拼回去必须等于原文」和「网址不是文件」这两条写得最细。
 */
import { describe, expect, it } from 'vitest'
import {
  appendMention,
  hasMention,
  insertMention,
  isFilePath,
  mentionQueryAt,
  parseMentions
} from '../file-mention'
import { fileRefMarkdown } from '../../agent/message'

describe('parseMentions', () => {
  it('切出「文本 / 引用 / 文本」三节', () => {
    expect(parseMentions('看下 [App.tsx](src/App.tsx) 这个文件')).toEqual([
      { kind: 'text', raw: '看下 ' },
      { kind: 'mention', raw: '[App.tsx](src/App.tsx)', name: 'App.tsx', path: 'src/App.tsx' },
      { kind: 'text', raw: ' 这个文件' }
    ])
  })

  it('★ 各节 raw 拼起来逐字等于原文 —— 高亮层对齐的前提', () => {
    const samples = [
      '',
      '没有引用',
      '[a](b)',
      '[a](b)[c](d)',
      '开头[a](x/y.ts)结尾',
      '换行\n[a](b)\n还有 [c](d/e)',
      '[空名]()后面',
      '未闭合 [a](b 这样',
      '[外链](https://example.com) 和 [文件](src/a.ts)'
    ]
    for (const s of samples) {
      expect(parseMentions(s).map((x) => x.raw).join('')).toBe(s)
    }
  })

  it('★ 网址不是文件引用 —— 画成文件 chip 是在撒谎', () => {
    expect(parseMentions('[文档](https://example.com)')).toEqual([
      { kind: 'text', raw: '[文档](https://example.com)' }
    ])
    expect(hasMention('看 [站点](http://a.b)')).toBe(false)
  })

  it('★ Windows 盘符不是 scheme', () => {
    expect(isFilePath('C:\\src\\a.ts')).toBe(true)
    expect(isFilePath('https://x')).toBe(false)
    expect(isFilePath('mailto:a@b.c')).toBe(false)
    // 空目标既不是路径也没什么可指的
    expect(isFilePath('')).toBe(false)
  })

  it('绝对路径同样是文件引用 —— 工作区外的文件走这条', () => {
    expect(parseMentions('[a.log](/var/log/a.log)')).toEqual([
      { kind: 'mention', raw: '[a.log](/var/log/a.log)', name: 'a.log', path: '/var/log/a.log' }
    ])
  })

  it('★ 模块级正则的 lastIndex 每次归零 —— 否则第二次调用会漏掉开头', () => {
    const text = '[a](x.ts) 和 [b](y.ts)'
    expect(parseMentions(text)).toEqual(parseMentions(text))
    expect(parseMentions(text).filter((s) => s.kind === 'mention')).toHaveLength(2)
  })

  it('★ 与 fileRefMarkdown 同一种写法 —— 拖进来的和 @ 选的必须长得一样', () => {
    const raw = fileRefMarkdown({ name: 'a.ts', path: 'src/a.ts' })
    expect(parseMentions(raw)).toEqual([
      { kind: 'mention', raw, name: 'a.ts', path: 'src/a.ts' }
    ])
  })
})

describe('mentionQueryAt', () => {
  it('刚敲下 @ 就触发,查询是空串', () => {
    expect(mentionQueryAt('@', 1)).toEqual({ start: 0, end: 1, query: '' })
  })

  it('取 @ 与光标之间的那几个字', () => {
    // 看(0)下(1) 空格(2) @(3) —— 触发符在 3,光标 8 收下整个 chat
    expect(mentionQueryAt('看下 @chat', 8)).toEqual({ start: 3, end: 8, query: 'chat' })
  })

  it('★ 邮箱地址不触发 —— @ 前面得是空白或开括号', () => {
    expect(mentionQueryAt('me@example.com', 14)).toBeNull()
    expect(mentionQueryAt('(@a', 3)).not.toBeNull()
  })

  it('★ 空白截断:@ 之后打了空格就不再是一次检索', () => {
    expect(mentionQueryAt('@src a', 6)).toBeNull()
    expect(mentionQueryAt('@src\nx', 6)).toBeNull()
  })

  it('★ 光标回到写了一半的查询中间时重新触发 —— 判据是位置不是「刚敲过」', () => {
    expect(mentionQueryAt('@chatview', 5)).toEqual({ start: 0, end: 5, query: 'chat' })
  })

  it('★ 已落地的引用里不触发 —— 括号会截断回扫', () => {
    expect(mentionQueryAt('[a](src/@b.ts)', 13)).toBeNull()
  })

  it('超长查询不再当作文件检索', () => {
    const long = `@${'x'.repeat(200)}`
    expect(mentionQueryAt(long, long.length)).toBeNull()
  })

  it('光标越界返回 null 而不是抛', () => {
    expect(mentionQueryAt('@a', -1)).toBeNull()
    expect(mentionQueryAt('@a', 99)).toBeNull()
  })
})

describe('insertMention', () => {
  const file = { name: 'App.tsx', path: 'src/App.tsx' }

  it('把 @查询 换成引用,并把光标放到后面那个空格之后', () => {
    const r = insertMention('看下 @chat', { start: 3, end: 8 }, file)
    expect(r.text).toBe('看下 [App.tsx](src/App.tsx) ')
    expect(r.text.slice(0, r.caret)).toBe('看下 [App.tsx](src/App.tsx) ')
  })

  it('保留后半段文本', () => {
    const r = insertMention('@a 这个文件', { start: 0, end: 2 }, file)
    expect(r.text).toBe('[App.tsx](src/App.tsx) 这个文件')
  })

  it('★ 后面已经有空白就不再补第二个', () => {
    expect(insertMention('@a b', { start: 0, end: 2 }, file).text).toBe('[App.tsx](src/App.tsx) b')
    expect(insertMention('@a\nb', { start: 0, end: 2 }, file).text).toBe('[App.tsx](src/App.tsx)\nb')
  })

  it('插入结果本身能被解析回来 —— 插入与解析是一对', () => {
    const r = insertMention('@', { start: 0, end: 1 }, file)
    expect(parseMentions(r.text).filter((s) => s.kind === 'mention')).toEqual([
      { kind: 'mention', raw: '[App.tsx](src/App.tsx)', name: 'App.tsx', path: 'src/App.tsx' }
    ])
  })
})

describe('skills in drafts', () => {
  it('parses skill tags while preserving raw text', async () => {
    const { parseMentions } = await import('../file-mention')
    expect(parseMentions('用 <skill name="data-analysis" /> 完成')).toEqual([
      { kind: 'text', raw: '用 ' },
      { kind: 'skill', raw: '<skill name="data-analysis" />', name: 'data-analysis' },
      { kind: 'text', raw: ' 完成' }
    ])
  })

  it('detects slash queries and inserts a skill tag', async () => {
    const { skillQueryAt, insertSkill } = await import('../file-mention')
    expect(skillQueryAt('/data', 5)).toEqual({ start: 0, end: 5, query: 'data' })
    expect(skillQueryAt('https://x/y', 10)).toBeNull()
    expect(skillQueryAt('/usr', 4)).toBeNull()
    expect(insertSkill('/data', { start: 0, end: 5 }, 'data-analysis').text).toBe('<skill name="data-analysis" /> ')
  })
})

describe('appendMention', () => {
  const file = { name: 'App.tsx', path: 'src/App.tsx' }

  it('空草稿:只有引用和它后面那个空格', () => {
    expect(appendMention('', file)).toBe('[App.tsx](src/App.tsx) ')
  })

  it('★ 草稿末尾不是空白时先补一个空格,引用不会和前一个字粘在一起', () => {
    expect(appendMention('看下这个', file)).toBe('看下这个 [App.tsx](src/App.tsx) ')
  })

  it('末尾已经有空白 / 换行就不再补第二个', () => {
    expect(appendMention('看下 ', file)).toBe('看下 [App.tsx](src/App.tsx) ')
    expect(appendMention('看下\n', file)).toBe('看下\n[App.tsx](src/App.tsx) ')
  })

  it('追加进来的这段和 `@` 选中产出的是同一种引用 —— 输入框照样画成 chip', () => {
    const segments = parseMentions(appendMention('看下', file))
    expect(segments.some((segment) => segment.kind === 'mention' && segment.path === 'src/App.tsx')).toBe(true)
  })
})
