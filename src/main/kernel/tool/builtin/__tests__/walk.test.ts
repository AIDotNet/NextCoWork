import { mkdirSync, mkdtempSync, realpathSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { createWorkspacePaths } from '../../../../environment/paths'
import { EnvironmentError } from '../../../../environment/errors'
import { nodeFs } from '../../../node-fs'
import { defaultSkip } from '../ignore'
import { walk } from '../walk'
import type { WalkOptions } from '../walk'

/**
 * `walk` 的三道闸门(软链成环、软链出界、量与时间)+ 顺序。
 *
 * 目录列表和 realpath 是并发取的,这里守的是「并发之后行为与串行逐项一致」:
 * BFS 顺序、截断落点、两条软链指向同一目录时留下的是排在前面那条。
 */

let base = ''
let root = ''

beforeEach(() => {
  base = realpathSync.native(mkdtempSync(join(tmpdir(), 'ncw-walk-')))
  root = join(base, 'ws')
  mkdirSync(root)
})

afterEach(() => {
  rmSync(base, { recursive: true, force: true })
})

function put(rel: string): void {
  const abs = join(root, rel)
  mkdirSync(join(abs, '..'), { recursive: true })
  writeFileSync(abs, 'x')
}

function run(over: Partial<WalkOptions> = {}): ReturnType<typeof walk> {
  return walk({
    fs: nodeFs(), root, signal: new AbortController().signal, now: () => Date.now(), skip: defaultSkip, ...over
  })
}

describe('walk', () => {
  it('lists breadth-first: every shallower entry comes before any deeper one', async () => {
    for (let i = 0; i < 12; i++) put(`d${String(i)}/sub/deep.txt`)
    put('top.txt')
    const r = await run()
    const depths = r.entries.map((e) => e.rel.split('/').length)
    expect(depths).toEqual([...depths].sort((a, b) => a - b))
    expect(r.entries.filter((e) => !e.isDir)).toHaveLength(13)
  })

  it('terminates on a symlink loop and descends into a symlinked directory once', async () => {
    put('a/file.txt')
    symlinkSync(join(root, 'a'), join(root, 'a', 'loop'), 'dir')
    const r = await run()
    expect(r.entries.map((e) => e.rel)).toContain('a/loop')
    expect(r.entries.some((e) => e.rel.startsWith('a/loop/'))).toBe(false)
  })

  it('keeps the first of two sibling links to the same directory, as a serial walk would', async () => {
    put('real/inner.txt')
    symlinkSync(join(root, 'real'), join(root, 'zz-alias'), 'dir')
    const r = await run()
    const rels = r.entries.map((e) => e.rel)
    // readdir 的顺序依赖文件系统,所以不写死谁在前:先出现在列表里的那条被展开,另一条不展开
    const first = rels.indexOf('real') < rels.indexOf('zz-alias') ? 'real' : 'zz-alias'
    const second = first === 'real' ? 'zz-alias' : 'real'
    expect(rels).toContain(`${first}/inner.txt`)
    expect(rels).not.toContain(`${second}/inner.txt`)
  })

  it('skips a symlink that escapes the root without failing the walk', async () => {
    mkdirSync(join(base, 'outside'))
    writeFileSync(join(base, 'outside', 'secret.txt'), 'x')
    symlinkSync(join(base, 'outside'), join(root, 'escape'), 'dir')
    put('ok.txt')
    const r = await run()
    const rels = r.entries.map((e) => e.rel)
    expect(rels).toContain('ok.txt')
    expect(rels).not.toContain('escape/secret.txt')
  })

  it('truncates at maxEntries keeping the entries a serial BFS would keep', async () => {
    for (let i = 0; i < 10; i++) put(`d${String(i)}/f.txt`)
    const full = await run()
    const cut = await run({ maxEntries: 13 })
    expect(cut.truncated).toBe(true)
    expect(cut.entries).toEqual(full.entries.slice(0, 13))
  })

  it('surfaces a remote failure from resolveWithin instead of returning a partial tree', async () => {
    for (let i = 0; i < 20; i++) put(`d${String(i)}/f.txt`)
    const fs = nodeFs()
    const path = createWorkspacePaths(fs, process.platform)
    const broken = { ...path, resolveWithin: async () => { throw new EnvironmentError('disconnected') } }
    await expect(run({ path: broken })).rejects.toThrow()
  })
})
