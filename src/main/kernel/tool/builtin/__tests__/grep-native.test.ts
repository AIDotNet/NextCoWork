import { execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { nodeHost } from '../../../host'
import type { ToolContext } from '../../registry'
import { nativeNameHints, setGrepBackendsForTest, type NativeBackend } from '../grep-native'
import { grepTool } from '../search'

/**
 * `Grep` 的原生后端(rg / git grep / grep)。
 *
 * 核心断言是「**换后端不换输出**」:同一份夹具、同一组参数,原生后端与 JS 引擎的
 * 输出逐字节相同。机器上没装的后端整组跳过 —— 它们是可选的加速,不是必需品。
 */

let root = ''

function ctx(): ToolContext {
  return {
    workspaceRoot: root,
    signal: new AbortController().signal,
    permissionMode: 'auto',
    depth: 0,
    callId: 'call_1',
    runId: 'run_1',
    host: nodeHost(),
    emit: () => {}
  }
}

function put(rel: string, body: string): void {
  const abs = join(root, rel)
  mkdirSync(join(abs, '..'), { recursive: true })
  writeFileSync(abs, body)
}

function has(bin: string): boolean {
  try {
    execFileSync(bin, ['--version'], { stdio: 'ignore' })
    return true
  } catch {
    return false
  }
}

/**
 * 结果按文件分组、组间排序后再比。★ JS 引擎的文件顺序跟着 `readdir` 走(ext4 上是
 * 哈希序),原生后端是按层排序的 —— 比较的是「每个文件给出的内容」,不是文件的先后。
 */
function byFile(out: string): string[] {
  const blocks = new Map<string, string[]>()
  let current = ''
  for (const line of out.split('\n')) {
    const m = /^([^:-]+)[:-]/.exec(line)
    if (m !== null) current = m[1] ?? ''
    if (line === '--') continue
    blocks.set(current, [...(blocks.get(current) ?? []), line])
  }
  return [...blocks.entries()].map(([, lines]) => lines.join('\n')).sort()
}

async function grep(backends: readonly NativeBackend[], input: Parameters<typeof grepTool.execute>[0]): Promise<string> {
  setGrepBackendsForTest(backends)
  try {
    const r = await grepTool.execute(input, ctx())
    expect(r.isError, r.output.content).toBeFalsy()
    return r.output.content
  } finally {
    setGrepBackendsForTest(null)
  }
}

beforeEach(() => {
  root = join(realpathSync.native(mkdtempSync(join(tmpdir(), 'ncw-grep-native-'))), 'ws')
  mkdirSync(root)
  put('a.ts', 'import x\nconst needle = 1\nexport {}\n')
  put('src/b.ts', 'one\ntwo needle\nthree\nfour\nNEEDLE five\n')
  put('src/deep/c.js', 'needle()\n')
  put('src/d.md', 'needle in docs\n')
  put('.hidden/e.ts', 'needle hidden\n')
  put('node_modules/pkg/index.ts', 'needle in deps\n')
  put('bin.dat', 'needle\0binary')
})

afterEach(() => {
  setGrepBackendsForTest(null)
  rmSync(join(root, '..'), { recursive: true, force: true })
})

const BACKENDS: Array<{ name: NativeBackend; available: boolean; git: boolean }> = [
  { name: 'rg', available: has('rg'), git: false },
  { name: 'git', available: has('git'), git: true },
  { name: 'grep', available: has('grep'), git: false }
]

for (const b of BACKENDS) {
  describe.skipIf(!b.available)(`Grep · 原生后端 ${b.name}`, () => {
    beforeEach(() => {
      // git grep 只在工作树里用;其余两个在不在仓库里都一样
      if (b.git) execFileSync('git', ['init', '-q'], { cwd: root })
    })

    const CASES: Array<Parameters<typeof grepTool.execute>[0]> = [
      { pattern: 'needle' },
      { pattern: 'needle', '-i': true, output_mode: 'count' },
      { pattern: 'needle', output_mode: 'content', '-n': true, '-C': 1 },
      { pattern: 'needle', glob: '**/*.ts' },
      { pattern: 'needle', glob: '*.ts' },
      { pattern: 'needle', type: 'js' },
      { pattern: 'needle', path: 'src' },
      { pattern: 'needle', path: 'src/b.ts', output_mode: 'content', '-n': true },
      { pattern: 'ne+dle\\s\\w+', output_mode: 'content' }
    ]

    for (const input of CASES) {
      it(`与 JS 引擎输出一致: ${JSON.stringify(input)}`, async () => {
        expect(byFile(await grep([b.name], input))).toEqual(byFile(await grep([], input)))
      })
    }

    it('没有命中就如实说没有', async () => {
      expect(await grep([b.name], { pattern: 'nothing-matches-this' })).toContain('No content matching')
    })

    it('依赖目录与二进制文件不进结果', async () => {
      const out = await grep([b.name], { pattern: 'needle' })
      expect(out).not.toContain('node_modules')
      expect(out).not.toContain('bin.dat')
      expect(out).toContain('.hidden/e.ts')
    })
  })
}

describe.skipIf(!has('git'))('Grep · git grep 的语义', () => {
  beforeEach(() => {
    execFileSync('git', ['init', '-q'], { cwd: root })
  })

  it('遵守 .gitignore,但未跟踪的文件照样搜', async () => {
    put('.gitignore', 'ignored/\n')
    put('ignored/x.ts', 'needle ignored\n')
    put('fresh.ts', 'needle fresh\n')
    const out = await grep(['git'], { pattern: 'needle' })
    expect(out).toContain('fresh.ts')
    expect(out).not.toContain('ignored/x.ts')
  })

  it('搜索起点本身被忽略时不用 git grep(否则会静默搜不到)', async () => {
    put('.gitignore', 'ignored/\n')
    put('ignored/x.ts', 'needle ignored\n')
    const out = await grep(['git'], { pattern: 'needle', path: 'ignored' })
    expect(out).toContain('ignored/x.ts')
  })
})

describe('Grep · 原生后端用不了时顺延到 JS', () => {
  it('multiline 只有 rg 能接,其余后端落到 JS', async () => {
    put('m.ts', 'interface Foo {\n  bar: string\n}\n')
    const input = { pattern: 'interface Foo.*?bar', multiline: true, output_mode: 'content' as const, '-n': true }
    const out = await grep(['git', 'grep'], input)
    expect(out).toBe(await grep([], input))
    expect(out).toContain('m.ts')
  })

  it('非法正则最终由 JS 报错,不会变成空结果', async () => {
    setGrepBackendsForTest(['git', 'grep'])
    const r = await grepTool.execute({ pattern: '([unclosed' }, ctx())
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('Invalid regular expression')
  })
})

describe('nativeNameHints', () => {
  it('只下推与根无关的 basename 模式,并展开一层花括号', () => {
    expect(nativeNameHints('*.ts')).toEqual(['*.ts'])
    expect(nativeNameHints('**/*.{ts,tsx}')).toEqual(['*.ts', '*.tsx'])
    expect(nativeNameHints('src/**/*.ts')).toBeNull()
    expect(nativeNameHints('**/a/*.ts')).toBeNull()
    expect(nativeNameHints('*.{a,{b,c}}')).toBeNull()
  })
})
