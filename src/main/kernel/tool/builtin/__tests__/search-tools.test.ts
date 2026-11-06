import { mkdirSync, mkdtempSync, realpathSync, rmSync, utimesSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { nodeHost } from '../../../host'
import { EnvironmentError } from '../../../../environment/errors'
import type { ToolContext } from '../../registry'
import { defaultSkip } from '../ignore'
import { redosRisk } from '../redos'
import { setGrepBackendsForTest } from '../grep-native'
import { globTool, grepTool, SEARCH_LIMITS } from '../search'
import { walk } from '../walk'

/**
 * `Glob` / `Grep` 的真 IO 测试。
 *
 * 这里有两条**行为**断言(不是结果断言),改的时候别放宽:
 *
 * - ReDoS 那条断言的是「**在 X 毫秒内返回**」。它保护的是主进程不被一个
 *   模型写出来的正则卡死 —— 而卡死时连停止按钮都没反应。
 * - 忽略目录那条断言的是 node_modules 里的东西**搜不到**。搜得到的话,
 *   一次普通的 grep 会把几万个文件塞进上下文。
 */

let root = ''
let outside = ''
const SECRET = 'AKIA-TOTALLY-SECRET-CONTENT-9f3c'

function ctx(over: Partial<ToolContext> = {}): ToolContext {
  return {
    workspaceRoot: root,
    signal: new AbortController().signal,
    permissionMode: 'auto',
    depth: 0,
    callId: 'call_1',
    runId: 'run_1',
    host: nodeHost(),
    emit: () => {},
    ...over
  }
}

function put(rel: string, body: string): void {
  const abs = join(root, rel)
  mkdirSync(join(abs, '..'), { recursive: true })
  writeFileSync(abs, body)
}

beforeEach(() => {
  const base = realpathSync.native(mkdtempSync(join(tmpdir(), 'ncw-search-')))
  root = join(base, 'ws')
  outside = join(base, 'outside')
  mkdirSync(root)
  mkdirSync(outside)
  writeFileSync(join(outside, 'secret.txt'), SECRET)
})

afterEach(() => {
  rmSync(join(root, '..'), { recursive: true, force: true })
})

// ────────────────────────────── Glob ──────────────────────────────

describe('Glob', () => {
  it('reports a dropped connection instead of a successful empty search', async () => {
    put('file.ts', 'content')
    const host = nodeHost()
    const remoteHost = { ...host, remote: true, fs: { ...host.fs, stat: async () => { throw new EnvironmentError('disconnected') } } }
    const glob = await globTool.execute({ pattern: '**/*.ts' }, ctx({ host: remoteHost }))
    expect(glob.isError).toBe(true)
    expect(glob.output.content).toContain('disconnected')
    const grep = await grepTool.execute({ pattern: 'content' }, ctx({ host: remoteHost }))
    expect(grep.isError).toBe(true)
    expect(grep.output.content).toContain('disconnected')
  })

  it('跨目录匹配,且根目录下的文件也算', async () => {
    put('a.ts', '')
    put('src/b.ts', '')
    put('src/deep/c.ts', '')
    put('src/d.js', '')
    const r = await globTool.execute({ pattern: '**/*.ts' }, ctx())
    const lines = r.output.content.split('\n')
    expect(lines).toContain('a.ts')
    expect(lines).toContain('src/b.ts')
    expect(lines).toContain('src/deep/c.ts')
    expect(lines).not.toContain('src/d.js')
  })

  it('单星不跨目录', async () => {
    put('a.ts', '')
    put('src/b.ts', '')
    const r = await globTool.execute({ pattern: '*.ts' }, ctx())
    expect(r.output.content.split('\n')).toEqual(['a.ts'])
  })

  it('按修改时间排序,新的在前', async () => {
    put('old.ts', '')
    put('new.ts', '')
    const past = Date.now() / 1000 - 10_000
    utimesSync(join(root, 'old.ts'), past, past)
    const r = await globTool.execute({ pattern: '*.ts' }, ctx())
    expect(r.output.content.split('\n')).toEqual(['new.ts', 'old.ts'])
  })

  it('path 把范围限定在子目录', async () => {
    put('a.ts', '')
    put('src/b.ts', '')
    const r = await globTool.execute({ pattern: '**/*.ts', path: join(root, 'src') }, ctx())
    expect(r.output.content.split('\n')).toEqual(['src/b.ts'])
  })

  /** ★ 搜不到不是错误,但要顺手提醒最常见的那个原因(星号不跨目录) */
  it('没匹配到时给的是提示,不是 isError', async () => {
    const r = await globTool.execute({ pattern: '*.rs' }, ctx())
    expect(r.isError).toBeFalsy()
    expect(r.output.content).toContain('No files match')
  })

  it('★ node_modules 等生成物目录被跳过', async () => {
    put('src/a.ts', '')
    put('node_modules/pkg/index.ts', '')
    put('dist/bundle.ts', '')
    put('.git/hooks/x.ts', '')
    const r = await globTool.execute({ pattern: '**/*.ts' }, ctx())
    expect(r.output.content).toContain('src/a.ts')
    expect(r.output.content).not.toContain('node_modules')
    expect(r.output.content).not.toContain('dist/')
    expect(r.output.content).not.toContain('.git/')
  })

  /**
   * ★ 遍历基准要跟着目标走。目标在工作区外时,`walk()` 的 `root` 必须换成目标本身 ——
   * 继续拿工作区当 root、拿绝对路径当 start 的话,`join(root, start)` 会拼出一个谁也没要求的目录。
   */
  it('工作区外的目录也能列,路径按绝对形式给出', async () => {
    const r = await globTool.execute({ pattern: '*', path: outside }, ctx())
    expect(r.isError, r.output.content).toBeFalsy()
    expect(r.output.content).toContain(join(outside, 'secret.txt'))
  })

  it('没有工作区时,相对起点无处可算', async () => {
    const r = await globTool.execute({ pattern: '*' }, ctx({ workspaceRoot: '' }))
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('workspace')
  })

  it('没有工作区时,绝对起点照样能搜', async () => {
    const r = await globTool.execute({ pattern: '*', path: outside }, ctx({ workspaceRoot: '' }))
    expect(r.isError, r.output.content).toBeFalsy()
    expect(r.output.content).toContain(join(outside, 'secret.txt'))
  })
})

// ────────────────────────────── Grep · 输出形态 ──────────────────────────────

describe('Grep · output_mode', () => {
  beforeEach(() => {
    put('a.ts', 'alpha\nbeta\nalpha again\n')
    put('src/b.ts', 'gamma\nalpha\n')
    put('c.md', 'alpha in markdown\n')
  })

  it('默认是 files_with_matches —— 只给路径', async () => {
    const r = await grepTool.execute({ pattern: 'alpha' }, ctx())
    const lines = r.output.content.split('\n')
    expect(lines).toContain('a.ts')
    expect(lines).toContain('src/b.ts')
    expect(lines).toContain('c.md')
    // 只有路径,没有行内容
    expect(r.output.content).not.toContain('beta')
    expect(r.output.content).not.toContain('alpha again')
  })

  it('content 模式给出命中的行', async () => {
    const r = await grepTool.execute(
      { pattern: 'alpha', glob: '*.ts', output_mode: 'content' },
      ctx()
    )
    expect(r.output.content).toContain('a.ts:alpha')
    expect(r.output.content).toContain('a.ts:alpha again')
    expect(r.output.content).not.toContain('beta')
  })

  it('-n 带上行号', async () => {
    const r = await grepTool.execute(
      { pattern: 'alpha again', output_mode: 'content', '-n': true },
      ctx()
    )
    expect(r.output.content).toContain('a.ts:3:alpha again')
  })

  it('count 模式给出每个文件的命中条数', async () => {
    const r = await grepTool.execute(
      { pattern: 'alpha', glob: '**/*.ts', output_mode: 'count' },
      ctx()
    )
    expect(r.output.content).toContain('a.ts:2')
    expect(r.output.content).toContain('src/b.ts:1')
  })

  it('head_limit 截断结果', async () => {
    const r = await grepTool.execute({ pattern: 'alpha', head_limit: 1 }, ctx())
    expect(r.output.content.split('\n')[0]).not.toBe('')
    expect(r.output.content).toContain('showing the first 1')
  })
})

describe('Grep · 上下文行', () => {
  beforeEach(() => {
    put('a.txt', 'L1\nL2\nHIT\nL4\nL5\n')
  })

  it('-C 前后各带 N 行,上下文行用 - 分隔', async () => {
    const r = await grepTool.execute(
      { pattern: 'HIT', output_mode: 'content', '-C': 1, '-n': true },
      ctx()
    )
    const lines = r.output.content.split('\n')
    expect(lines).toContain('a.txt-2-L2')
    expect(lines).toContain('a.txt:3:HIT')
    expect(lines).toContain('a.txt-4-L4')
    expect(lines).not.toContain('a.txt-1-L1')
  })

  it('-B 只带前面,-A 只带后面', async () => {
    const b = await grepTool.execute(
      { pattern: 'HIT', output_mode: 'content', '-B': 1, '-n': true },
      ctx()
    )
    expect(b.output.content).toContain('a.txt-2-L2')
    expect(b.output.content).not.toContain('L4')

    const a = await grepTool.execute(
      { pattern: 'HIT', output_mode: 'content', '-A': 1, '-n': true },
      ctx()
    )
    expect(a.output.content).toContain('a.txt-4-L4')
    expect(a.output.content).not.toContain('L2')
  })

  it('不相连的两段之间插一条 --', async () => {
    put('b.txt', 'HIT\nx\nx\nx\nx\nx\nHIT\n')
    const r = await grepTool.execute(
      { pattern: 'HIT', glob: 'b.txt', output_mode: 'content', '-C': 1 },
      ctx()
    )
    expect(r.output.content).toContain('--')
  })
})

describe('Grep · 过滤与选项', () => {
  it('-i 忽略大小写', async () => {
    put('a.ts', 'HelloWorld\n')
    const off = await grepTool.execute({ pattern: 'helloworld' }, ctx())
    expect(off.output.content).toContain('No content matching')
    const on = await grepTool.execute({ pattern: 'helloworld', '-i': true }, ctx())
    expect(on.output.content).toContain('a.ts')
  })

  it('glob 按文件名过滤', async () => {
    put('a.ts', 'needle\n')
    put('a.md', 'needle\n')
    const r = await grepTool.execute({ pattern: 'needle', glob: '*.ts' }, ctx())
    expect(r.output.content).toContain('a.ts')
    expect(r.output.content).not.toContain('a.md')
  })

  it('type 按语言过滤', async () => {
    put('a.ts', 'needle\n')
    put('a.py', 'needle\n')
    const r = await grepTool.execute({ pattern: 'needle', type: 'py' }, ctx())
    expect(r.output.content).toContain('a.py')
    expect(r.output.content).not.toContain('a.ts')
  })

  /** ★ 认不出的 type 必须报错。当成「不过滤」的话,模型拿到的是范围完全不对的结果 */
  it('★ 不认识的 type 报错并列出可用值', async () => {
    put('a.ts', 'needle\n')
    const r = await grepTool.execute({ pattern: 'needle', type: 'typescript' }, ctx())
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('ts')
  })

  it('multiline 让模式跨行', async () => {
    put('a.ts', 'interface X {\n  field: string\n}\n')
    const off = await grepTool.execute({ pattern: 'interface X.*field' }, ctx())
    expect(off.output.content).toContain('No content matching')
    const on = await grepTool.execute(
      { pattern: 'interface X.*field', multiline: true },
      ctx()
    )
    expect(on.output.content).toContain('a.ts')
  })

  it('path 指到单个文件时只搜那一个', async () => {
    put('a.ts', 'needle\n')
    put('b.ts', 'needle\n')
    const r = await grepTool.execute({ pattern: 'needle', path: join(root, 'a.ts') }, ctx())
    expect(r.output.content.split('\n')).toEqual(['a.ts'])
  })

  it('二进制文件被跳过', async () => {
    writeFileSync(join(root, 'blob.bin'), Buffer.from([0x6e, 0x65, 0x00, 0x65, 0x64]))
    put('a.ts', 'ne\n')
    const r = await grepTool.execute({ pattern: 'ne' }, ctx())
    expect(r.output.content).toContain('a.ts')
    expect(r.output.content).not.toContain('blob.bin')
  })

  it('★ node_modules 等目录搜不到', async () => {
    put('src/a.ts', 'needle\n')
    put('node_modules/pkg/i.ts', 'needle\n')
    const r = await grepTool.execute({ pattern: 'needle' }, ctx())
    expect(r.output.content).toContain('src/a.ts')
    expect(r.output.content).not.toContain('node_modules')
  })

  it('正则不合法时报错,并提示这是 JS 语法', async () => {
    const r = await grepTool.execute({ pattern: '([unclosed' }, ctx())
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('JavaScript')
  })

  it('工作区外的目录也能搜,命中路径按绝对形式给出', async () => {
    const r = await grepTool.execute({ pattern: 'AKIA', path: outside }, ctx())
    expect(r.isError, r.output.content).toBeFalsy()
    expect(r.output.content).toContain(join(outside, 'secret.txt'))
  })

  it('没有工作区时,相对起点无处可算', async () => {
    const r = await grepTool.execute({ pattern: 'x' }, ctx({ workspaceRoot: '' }))
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('workspace')
  })
})

// ────────────────────────────── Grep · 并发扫描 ──────────────────────────────

/**
 * 文件是并发在搜的,但输出必须和串行时逐字节一致 —— 同一次搜索跑两遍顺序不同,
 * 模型会以为仓库变了;撞上限时截在哪个文件也不能随「谁先读完」漂移。
 */
describe('Grep · 并发扫描', () => {
  // JS 引擎自己的并发调度,原生后端关掉
  beforeEach(() => { setGrepBackendsForTest([]) })
  afterEach(() => { setGrepBackendsForTest(null) })

  it('keeps traversal order and cuts at the match cap exactly where a serial scan would', async () => {
    // 每个文件 10 条命中,40 个文件 → 上限 200 恰好落在第 20 个文件
    for (let i = 0; i < 40; i++) put(`f${String(i).padStart(2, '0')}.txt`, 'hit\n'.repeat(10))
    const listed = await walk({
      fs: nodeHost().fs, root, signal: new AbortController().signal, now: () => Date.now(), skip: defaultSkip
    })
    const expected = listed.entries.filter((e) => !e.isDir).slice(0, 20).map((e) => `${e.rel}:10`)

    for (let run = 0; run < 3; run++) {
      const r = await grepTool.execute({ pattern: 'hit', output_mode: 'count' }, ctx())
      expect(r.output.content.split('\n\n')[0]?.split('\n')).toEqual(expected)
      expect(r.output.content).toContain('match cap')
    }
  })

  it('reports line numbers that agree with split-by-newline, including the last line without a newline', async () => {
    put('a.txt', 'x\nneedle one\n\nneedle two')
    const r = await grepTool.execute({ pattern: 'needle', output_mode: 'content', '-n': true }, ctx())
    expect(r.output.content.split('\n')).toEqual(['a.txt:2:needle one', '--', 'a.txt:4:needle two'])
  })

  it('still finds a hit at the end of a file larger than the single-read threshold', async () => {
    put('big.txt', `${'filler line\n'.repeat(40_000)}needleAtTheEnd\n`)
    const r = await grepTool.execute({ pattern: 'needleAtTheEnd', output_mode: 'content', '-n': true }, ctx())
    expect(r.output.content).toBe('big.txt:40001:needleAtTheEnd')
  })

  it('skips a large file whose first bytes contain NUL', async () => {
    writeFileSync(join(root, 'big.bin'), Buffer.concat([Buffer.from([0]), Buffer.from('needle\n'.repeat(60_000))]))
    put('a.ts', 'needle\n')
    const r = await grepTool.execute({ pattern: 'needle' }, ctx())
    expect(r.output.content.split('\n')).toEqual(['a.ts'])
  })

  it('stops claiming files once a remote failure happens mid-scan, and surfaces it', async () => {
    for (let i = 0; i < 60; i++) put(`f${String(i)}.txt`, 'needle\n')
    const host = nodeHost()
    let statCalls = 0
    const remoteHost = {
      ...host,
      remote: true,
      fs: {
        ...host.fs,
        stat: async (abs: string) => {
          // 第 1 次是 run() 对搜索根本身的 stat;之后第 5 次起模拟断线
          if (++statCalls > 5) throw new EnvironmentError('disconnected')
          return host.fs.stat(abs)
        }
      }
    }
    const r = await grepTool.execute({ pattern: 'needle' }, ctx({ host: remoteHost }))
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('disconnected')
    // 给「后台还在跑的 worker」留出时间:没停手的话它们会继续把剩下的文件 stat 完
    await new Promise((resolve) => setTimeout(resolve, 50))
    expect(statCalls).toBeLessThanOrEqual(1 + SEARCH_LIMITS.GREP_CONCURRENCY)
  })
})

// ────────────────────────────── ReDoS ──────────────────────────────

/**
 * ★ 这一组断言的是**时间**,不是结果。
 *
 * `RegExp.test` 在 V8 里是原子的:跑进去之后没有任何地方能查 `signal` 或查时钟。
 * 所以「危险的正则」唯一的正确处理是**编译前拒绝**,而这几条用例就是那道闸的报警器。
 * 谁把 `redos.ts` 那道筛查去掉,这里会直接超时,而不是给出一个错误答案。
 */
describe('★ Grep · 灾难性回溯', () => {
  // 静态筛查只挡 JS 这条路 —— 原生程序在独立进程里,卡住了由 spawn 超时整树杀掉
  beforeEach(() => { setGrepBackendsForTest([]) })
  afterEach(() => { setGrepBackendsForTest(null) })

  const EVIL: string[] = ['(a+)+$', '^(a+)+$', '(\\s*\\w+)*$', '(a{2,})+$', '((a+)b)+$']

  it('嵌套无界量词被静态筛查拦下', () => {
    for (const p of EVIL) expect(redosRisk(p), p).not.toBeNull()
  })

  it('常见的正常模式不误伤', () => {
    const fine = [
      'log.*Error',
      'function\\s+\\w+',
      'interface\\s+X[\\s\\S]*?field',
      '(?:foo|bar)+',
      '(TODO)+',
      'a{1,20}b',
      '\\[[^\\]]*\\]',
      'const \\w+ = '
    ]
    for (const p of fine) expect(redosRisk(p), p).toBeNull()
  })

  it('★ 恶性正则**立刻**返回错误,不是跑到天荒地老', async () => {
    // 2000 个 a 后面跟一个 b:`(a+)+$` 匹配失败,回溯分支数 2^1999
    put('bomb.txt', `${'a'.repeat(5000)}b\n`)
    const t0 = Date.now()
    const r = await grepTool.execute({ pattern: '(a+)+$', output_mode: 'content' }, ctx())
    const dt = Date.now() - t0
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('backtracking')
    // 说明里必须写清楚怎么改,否则模型只会原样重试
    expect(r.output.content).toContain('Fix it by')
    expect(dt, `耗时 ${String(dt)}ms —— 静态筛查是不是被去掉了?`).toBeLessThan(1000)
  })

  it('良性模式配超长行也很快返回', async () => {
    put('long.txt', `${'x'.repeat(200_000)}\nneedle\n`)
    const t0 = Date.now()
    const r = await grepTool.execute({ pattern: 'needle' }, ctx())
    expect(r.output.content).toContain('long.txt')
    expect(Date.now() - t0).toBeLessThan(2000)
  })
})
