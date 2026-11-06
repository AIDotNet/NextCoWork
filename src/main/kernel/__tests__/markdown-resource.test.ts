import { describe, expect, it } from 'vitest'
import type { KernelFs } from '../host'
import { readResourceFile, renderResourceFile, writeResourceFile } from '../markdown-resource'

/**
 * 单文件读写 —— 扩展面板里那个编辑器的内核侧。
 *
 * ★ 这里几乎每条都在回答同一个问题:**用户的文件会不会在保存一次之后变形**。
 *   这个模块的全部价值就是「原样进、原样出」，所以用例的形状基本都是
 *   「写进去 → 读回来 → 和原来比」，而不是断言生成的文本长什么样。
 */
function memoryFs(): KernelFs & { files: Map<string, string> } {
  const files = new Map<string, string>()
  let clock = 0
  const fs = {
    files,
    readFile: (path: string) => {
      const content = files.get(path)
      return content === undefined ? Promise.reject(new Error('ENOENT')) : Promise.resolve(content)
    },
    writeFile: (path: string, content: string) => {
      files.set(path, content)
      clock += 1
      return Promise.resolve()
    },
    stat: (path: string) => {
      const content = files.get(path)
      if (content === undefined) return Promise.reject(new Error('ENOENT'))
      return Promise.resolve({ size: content.length, mtimeMs: clock, isDir: false })
    },
    exists: (path: string) => Promise.resolve(files.has(path)),
    mkdirp: () => Promise.resolve(),
    readDir: () => Promise.reject(new Error('unused')),
    realpath: (path: string) => Promise.resolve(path),
    readFileBytes: (path: string) => {
      const content = files.get(path)
      return content === undefined
        ? Promise.reject(new Error('ENOENT'))
        : Promise.resolve(new TextEncoder().encode(content))
    }
  } as unknown as KernelFs & { files: Map<string, string> }
  return fs
}

const FILE = '/data/commands/deploy.md'

describe('readResourceFile', () => {
  it('文件不存在返回 null —— 那不是错误，是「还没建」', async () => {
    expect(await readResourceFile(memoryFs(), FILE)).toBeNull()
  })

  it('读出 frontmatter、正文和 revision', async () => {
    const fs = memoryFs()
    fs.files.set(FILE, '---\ndescription: 部署\nargument-hint: <env>\n---\n跑部署脚本\n')
    const got = await readResourceFile(fs, FILE)
    expect(got?.frontmatter).toEqual({ description: '部署', 'argument-hint': '<env>' })
    expect(got?.body).toBe('跑部署脚本\n')
    expect(got?.revision).toMatch(/^\d+:\d+$/)
  })

  it('★ 本应用不认识的键也读出来 —— 表单只覆盖它认识的那几个，其余要能原样带回去', async () => {
    const fs = memoryFs()
    fs.files.set(FILE, '---\ndescription: x\ncolor: blue\n---\n正文')
    expect((await readResourceFile(fs, FILE))?.frontmatter.color).toBe('blue')
  })

  it('读不懂的语法记进 skipped —— 值在 parse 阶段就没了，保存前必须当面说', async () => {
    const fs = memoryFs()
    // 一层嵌套现在读得回来了,所以这里用两层 —— 它仍然是「值没了」的那一类
    fs.files.set(FILE, '---\ndescription: x\nnested:\n  a:\n    b: 1\n---\n正文')
    expect((await readResourceFile(fs, FILE))?.skipped.length).toBeGreaterThan(0)
  })
})

describe('writeResourceFile · 并发保护', () => {
  it('新建时文件已存在算冲突 —— 「新建」不该悄悄覆盖', async () => {
    const fs = memoryFs()
    fs.files.set(FILE, '旧内容')
    expect(await writeResourceFile(fs, FILE, '新内容', undefined)).toEqual({ ok: false, reason: 'conflict' })
    expect(fs.files.get(FILE)).toBe('旧内容')
  })

  it('★ revision 对不上就拒绝写', async () => {
    const fs = memoryFs()
    await writeResourceFile(fs, FILE, 'v1', undefined)
    const stale = (await readResourceFile(fs, FILE))?.revision
    // 别人先存了一次
    await writeResourceFile(fs, FILE, 'v2', stale)
    // 我拿着过期的 revision 再存 —— 必须被挡下，否则 v2 被静默盖掉
    expect(await writeResourceFile(fs, FILE, 'v3', stale)).toEqual({ ok: false, reason: 'conflict' })
    expect(fs.files.get(FILE)).toBe('v2')
  })

  it('编辑一个已被删掉的文件算冲突', async () => {
    const fs = memoryFs()
    expect(await writeResourceFile(fs, FILE, 'x', '123:4')).toEqual({ ok: false, reason: 'conflict' })
  })

  it('revision 对得上就写进去，并回一个新的', async () => {
    const fs = memoryFs()
    await writeResourceFile(fs, FILE, 'v1', undefined)
    const rev = (await readResourceFile(fs, FILE))?.revision
    const out = await writeResourceFile(fs, FILE, 'v2', rev)
    expect(out.ok).toBe(true)
    expect(out.ok && out.revision).not.toBe(rev)
  })
})

describe('renderResourceFile · 往返', () => {
  const round = async (kind: 'command' | 'agent', fm: Record<string, string | string[]>, body: string) => {
    const fs = memoryFs()
    await writeResourceFile(fs, FILE, renderResourceFile(kind, fm, body), undefined)
    return readResourceFile(fs, FILE)
  }

  it('命令：写进去再读回来一字不差', async () => {
    const fm = { description: '部署到指定环境', 'argument-hint': '<env>' }
    const got = await round('command', fm, '把 $ARGUMENTS 部署上去\n')
    expect(got?.frontmatter).toEqual(fm)
    expect(got?.body).toBe('把 $ARGUMENTS 部署上去\n')
  })

  it('子代理：列表字段往返', async () => {
    const fm = { name: 'reviewer', description: '审代码', tools: ['Read', 'Grep'], model: 'sonnet' }
    expect((await round('agent', fm, '你是一个审查者'))?.frontmatter).toEqual(fm)
  })

  it('★ 不认识的键在往返中不丢', async () => {
    const fm = { description: 'x', color: 'blue', 'custom-thing': 'y' }
    expect((await round('agent', fm, 'body'))?.frontmatter).toEqual(fm)
  })

  it('键序由 kind 决定且稳定 —— 每存一次换一次顺序会把 diff 变成噪音', () => {
    const text = renderResourceFile('agent', { model: 'm', description: 'd', name: 'n', tools: ['T'] }, '')
    const keys = text.split('\n').filter((l) => /^[a-z]/.test(l)).map((l) => l.slice(0, l.indexOf(':')))
    expect(keys).toEqual(['name', 'description', 'tools', 'model'])
  })

  it('没有 frontmatter 的命令不写前置块', () => {
    expect(renderResourceFile('command', {}, '只有正文')).toBe('只有正文')
  })

  it('正文超长会被截断 —— 渲染层的限制挡不住 IPC 直调', () => {
    const huge = 'x'.repeat(70 * 1024)
    expect(renderResourceFile('command', {}, huge).length).toBeLessThanOrEqual(64 * 1024)
  })
})
