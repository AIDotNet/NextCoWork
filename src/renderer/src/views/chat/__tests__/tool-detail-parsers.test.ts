/**
 * 终端信封解包与 `cat -n` 拆行号 —— 两块**允许失败**的解析,所以边界要钉死。
 *
 * 它们各自的失败模式都不会崩,只会让用户看到一段奇怪的文字:
 * 信封没剥干净 → 终端里凭空出现 `<stdout>` 两行 XML;
 * 行号拆错 → 一个 TSV 文件的第一列被当成行号切掉,而那是用户的数据。
 */
import { describe, expect, it } from 'vitest'
import { parseTerminalOutput } from '../terminal-output'
import { parseNumberedOutput } from '../read-output'
// languageOf 现在住在 components/code(改动审查和 Git 面板也要用),断言留在这里 ——
// 它的三条边界(多点文件名、没有扩展名、undefined)当初就是为工具卡写的。
import { languageOf } from '../../../components/code/language'
import { parseMatches } from '../search-matches'

describe('parseTerminalOutput', () => {
  it('拆出两条流,信封标签不留在文本里', () => {
    const r = parseTerminalOutput('<stdout>\nok\n</stdout>\n<stderr>\nwarn\n</stderr>')
    expect(r.sections).toEqual([
      { stream: 'stdout', text: 'ok' },
      { stream: 'stderr', text: 'warn' }
    ])
    expect(r.notice).toBe('')
  })

  it('★ 失败时那句说明单独一格 —— 它不是命令吐出来的', () => {
    const r = parseTerminalOutput('Command exited with code 1.\n<stderr>\nboom\n</stderr>')
    expect(r.notice).toBe('Command exited with code 1.')
    expect(r.sections).toEqual([{ stream: 'stderr', text: 'boom' }])
  })

  it('★ 没有信封就整段当 stdout,不猜也不丢', () => {
    const r = parseTerminalOutput('(command succeeded with no output)')
    expect(r.notice).toBe('')
    expect(r.sections).toEqual([{ stream: 'stdout', text: '(command succeeded with no output)' }])
    expect(parseTerminalOutput('').sections).toEqual([])
  })

  it('保留输出内部的空行与缩进 —— 终端靠列对齐读', () => {
    const r = parseTerminalOutput('<stdout>\nName    Size\n\n  a.ts  12\n</stdout>')
    expect(r.sections[0]?.text).toBe('Name    Size\n\n  a.ts  12')
  })
})

describe('parseNumberedOutput', () => {
  it('拆掉 cat -n 的行号栏,并记住起始行号', () => {
    const r = parseNumberedOutput('     1\tconst a = 1\n     2\tconst b = 2')
    expect(r.code).toBe('const a = 1\nconst b = 2')
    expect(r.startLine).toBe(1)
  })

  it('带 offset 读的片段从它自己的行号开始', () => {
    expect(parseNumberedOutput('    40\tx\n    41\ty').startLine).toBe(40)
  })

  it('★ 行号不连续就整段不拆 —— 那是正文里恰好有数字+制表符(TSV / 日志)', () => {
    const tsv = '1\tfoo\n7\tbar'
    expect(parseNumberedOutput(tsv)).toEqual({ code: tsv })
  })

  it('不是这个格式时原样返回,不给 startLine', () => {
    expect(parseNumberedOutput('plain text')).toEqual({ code: 'plain text' })
    expect(parseNumberedOutput('')).toEqual({ code: '' })
  })
})

describe('languageOf', () => {
  it('取扩展名交给高亮器,自己不维护语言表', () => {
    expect(languageOf('/a/b/c.test.ts')).toBe('ts')
    expect(languageOf('C:\\x\\y.MD')).toBe('md')
  })

  it('没有扩展名 / 没有路径 → 空串(高亮器据此不染色)', () => {
    expect(languageOf('/a/Makefile')).toBe('')
    expect(languageOf('.gitignore')).toBe('')
    expect(languageOf(undefined)).toBe('')
  })
})

describe('parseMatches', () => {
  it('Grep 的 path:line:text 拆成三段', () => {
    expect(parseMatches('src/a.ts:42:  const x = 1')).toEqual([
      { path: 'src/a.ts', line: 42, text: '  const x = 1' }
    ])
  })

  it('Windows 盘符不会在第一个冒号上切错', () => {
    expect(parseMatches('C:\\w\\a.ts:7:x')).toEqual([{ path: 'C:\\w\\a.ts', line: 7, text: 'x' }])
  })

  it('Glob 的纯路径行只有路径那一格', () => {
    expect(parseMatches('src/main/index.ts')).toEqual([{ path: 'src/main/index.ts' }])
  })

  it('★ 认不出形状的行原样留着 —— 丢一行命中,用户会以为搜索漏了', () => {
    expect(parseMatches('No matches found')).toEqual([{ text: 'No matches found' }])
  })
})
