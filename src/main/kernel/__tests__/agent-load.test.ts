import { mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { nodeHost } from '../host'
import { BUILTIN_AGENTS, GENERAL_PURPOSE } from '../agent/builtin'
import { normalizeToolName } from '../agent/tool-alias'
import type { AgentScanResult } from '../agent/load'
import { scanAgents } from '../agent/load'
import { AgentRegistry } from '../agent/registry'

/**
 * 子代理定义扫描的测试。**用真临时目录,不打桩 fs** —— 和 `skill-load.test.ts`
 * 同一个理由,而且这里更强:这个文件里最重要的两条(软链逃逸、空工具表作废)
 * 都是安全判断,而 `resolveInWorkspace` 挡住第一条靠的就是真的 `realpathSync`。
 *
 * 加载器的契约是**永不 throw**,所以几乎每条用例都同时断言两件事:
 * 「这条没进来」和「用户能知道它为什么没进来」。
 *
 * ★ 还有一条贯穿全文件的隐式断言:**内建的那条永远在**。
 * 它不是凑数的 —— 「至少有一个子代理可派」如果取决于用户机器上有没有
 * `agents/` 目录,那么 `Task` 工具就会在半数机器上变成一个必然失败的工具。
 */

const fs = nodeHost().fs

let root = ''
let globalRoot = ''
let projectRoot = ''

beforeEach(() => {
  root = mkdtempSync(join(tmpdir(), 'nextcowork-agent-'))
  globalRoot = join(root, 'global', 'agents')
  projectRoot = join(root, 'project', '.nextcowork', 'agents')
  mkdirSync(globalRoot, { recursive: true })
  mkdirSync(projectRoot, { recursive: true })
})

afterEach(() => {
  rmSync(root, { recursive: true, force: true })
})

/** 在某一层里放一个定义文件。`content` 是整个 .md 的字节。 */
function put(scopeRoot: string, file: string, content: string): void {
  writeFileSync(join(scopeRoot, file), content)
}

/** 一份最小的定义。★ 刻意不写 `name:` —— 名字由文件名给,和 CC 一致。 */
const md = (description: string | null, body = '你负责这件事。'): string =>
  `---\n${description === null ? '' : `description: ${description}\n`}---\n${body}\n`

const scan = (): Promise<AgentScanResult> => scanAgents({ fs, globalRoot, projectRoot })

/** 除了内建的那条之外,还认出了哪些 */
const loaded = (r: AgentScanResult): string[] =>
  r.agents.filter((a) => a.source.kind !== 'builtin').map((a) => a.name)

/** 诊断里有没有提到这件事 —— 断言「用户能知道原因」,不是断言逐字文案 */
const said = (r: AgentScanResult, needle: string): boolean =>
  r.diagnostics.some((d) => d.message.includes(needle))

describe('基本形状', () => {
  it('读出名字、描述、角色提示词,并标上来源', async () => {
    put(globalRoot, 'researcher.md', '---\ndescription: 只读调研\n---\n你只看,不改。\n')

    const r = await scan()

    const a = r.agents.find((x) => x.name === 'researcher')
    expect(a?.description).toBe('只读调研')
    expect(a?.prompt).toBe('你只看,不改。')
    expect(a?.source.kind).toBe('global')
    expect(a?.source.path).toContain('researcher.md')
    // 四个可选字段都没写 —— 省略,不是 undefined 占位
    expect(a?.tools).toBeUndefined()
    expect(a?.model).toBeUndefined()
    expect(a?.permissionMode).toBeUndefined()
    expect(a?.thinking).toBeUndefined()
  })

  it('thinking: 大小写不敏感,合法档位落到定义上', async () => {
    put(globalRoot, 'deep.md', '---\ndescription: 想得深一点\nthinking: HIGH\n---\n慢慢想。\n')

    const r = await scan()

    expect(r.agents.find((x) => x.name === 'deep')?.thinking).toBe('high')
  })

  it('thinking: 认不出的值 —— 子代理仍然加载,诊断里提到它,字段留空', async () => {
    put(globalRoot, 'deep.md', '---\ndescription: 想得深一点\nthinking: on\n---\n慢慢想。\n')

    const r = await scan()

    const a = r.agents.find((x) => x.name === 'deep')
    expect(a).toBeDefined()
    expect(a?.thinking).toBeUndefined()
    expect(said(r, 'thinking')).toBe(true)
  })

  it('★ 一个子代理都没有时,内建那几条仍然在', async () => {
    const r = await scan()

    expect(r.agents.map((a) => a.name)).toEqual(BUILTIN_AGENTS.map((a) => a.name))
    expect(r.agents.every((a) => a.source.kind === 'builtin')).toBe(true)
  })

  it('两层目录都不存在也不报错 —— 没有 agents 目录是常态', async () => {
    const r = await scanAgents({ fs, globalRoot: join(root, '不存在'), projectRoot: '' })

    expect(r.agents.map((a) => a.name)).toEqual(BUILTIN_AGENTS.map((a) => a.name))
    expect(r.diagnostics).toEqual([])
  })

  it('同名时项目胜出', async () => {
    put(globalRoot, 'reviewer.md', md('全局的'))
    put(projectRoot, 'reviewer.md', md('项目的'))

    const r = await scan()

    const a = r.agents.find((x) => x.name === 'reviewer')
    expect(a?.description).toBe('项目的')
    expect(a?.source.kind).toBe('project')
    expect(loaded(r)).toEqual(['reviewer'])
  })

  it('★ 用户可以用同名文件覆盖内建的那条', async () => {
    put(projectRoot, 'general-purpose.md', md('我自己的通用代理'))

    const r = await scan()

    // 覆盖的是**那一条**,其余内建照旧 —— 总数不变。
    expect(r.agents).toHaveLength(BUILTIN_AGENTS.length)
    const a = r.agents.find((x) => x.name === GENERAL_PURPOSE.name)
    expect(a?.description).toBe('我自己的通用代理')
    expect(a?.source.kind).toBe('project')
  })

  it('★ 覆盖失败(文件是坏的)时,留下的仍然是内建那条能用的', async () => {
    put(projectRoot, 'general-purpose.md', md(null)) // 缺 description → 作废

    const r = await scan()

    expect(r.agents).toHaveLength(BUILTIN_AGENTS.length)
    expect(r.agents.find((x) => x.name === GENERAL_PURPOSE.name)?.source.kind).toBe('builtin')
    expect(said(r, 'description')).toBe(true)
  })

  it('frontmatter 里的 name 胜过文件名,并且说一声', async () => {
    put(globalRoot, 'old.md', '---\nname: new-name\ndescription: 改过名字\n---\n正文\n')

    const r = await scan()

    expect(loaded(r)).toEqual(['new-name'])
    expect(said(r, '不一致')).toBe(true)
  })
})

describe('这些不进表,而且要说明原因', () => {
  it('目录被跳过', async () => {
    mkdirSync(join(globalRoot, 'somedir.md'), { recursive: true })

    const r = await scan()

    expect(loaded(r)).toEqual([])
  })

  it('非 .md 的文件静静跳过 —— README.txt 不是错误', async () => {
    put(globalRoot, 'README.txt', md('不该被读'))

    const r = await scan()

    expect(loaded(r)).toEqual([])
    expect(r.diagnostics).toEqual([])
  })

  it('文件名不合法 → 跳过并说明', async () => {
    put(globalRoot, 'Bad Name.md', md('大写加空格'))

    const r = await scan()

    expect(loaded(r)).toEqual([])
    expect(said(r, '文件名不合法')).toBe(true)
  })

  it('frontmatter 里的 name 不合法 → 整条作废', async () => {
    put(globalRoot, 'ok.md', '---\nname: NOT OK\ndescription: 有描述\n---\n正文\n')

    const r = await scan()

    expect(loaded(r)).toEqual([])
    expect(said(r, '不合法')).toBe(true)
  })

  it('★ 缺 description → 整条作废,不拿名字凑一个', async () => {
    put(globalRoot, 'nodesc.md', md(null))

    const r = await scan()

    expect(loaded(r)).toEqual([])
    expect(said(r, '无从判断')).toBe(true)
  })

  it('正文是空的 → 整条作废(没有角色提示词的角色没有意义)', async () => {
    put(globalRoot, 'empty.md', '---\ndescription: 有描述\n---\n   \n')

    const r = await scan()

    expect(loaded(r)).toEqual([])
    expect(said(r, '正文')).toBe(true)
  })

  it('★ 软链出 agents 之外 → 拒绝', async () => {
    const outside = join(root, 'outside.md')
    writeFileSync(outside, md('我在外面'))
    symlinkSync(outside, join(globalRoot, 'evil.md'))

    const r = await scan()

    expect(loaded(r)).toEqual([])
    expect(said(r, 'agents 之外')).toBe(true)
  })
})

describe('tools: —— 三种结局各不相同', () => {
  it('没写 tools → undefined,继承父 run 的全部工具', async () => {
    put(globalRoot, 'a.md', md('没写'))

    const r = await scan()

    expect(r.agents.find((x) => x.name === 'a')?.tools).toBeUndefined()
  })

  it('★ CC 的写法原样粘过来能用:tools: Read, Grep, Glob', async () => {
    put(globalRoot, 'cc.md', '---\ndescription: 抄来的\ntools: Read, Grep, Glob\n---\n正文\n')

    const r = await scan()

    expect(r.agents.find((x) => x.name === 'cc')?.tools).toEqual(['Read', 'Grep', 'Glob'])
  })

  it('小写、YAML 数组、MultiEdit 都认得,并且去重', async () => {
    put(globalRoot, 'b.md', '---\ndescription: d\ntools: [read, multiedit, edit, ls]\n---\n正文\n')

    const r = await scan()

    // multiedit 和 edit 都归一化成 Edit,去重之后只剩一个
    expect(r.agents.find((x) => x.name === 'b')?.tools).toEqual(['Read', 'Edit', 'LS'])
  })

  it('认出一部分 → 用那部分,认不出的记进诊断(不静默丢弃)', async () => {
    put(globalRoot, 'c.md', '---\ndescription: d\ntools: Read, Telepathy\n---\n正文\n')

    const r = await scan()

    expect(r.agents.find((x) => x.name === 'c')?.tools).toEqual(['Read'])
    expect(said(r, 'Telepathy')).toBe(true)
  })

  it('★ 一个都认不出 → 整条作废。空工具表的子代理会编一个答案出来', async () => {
    put(globalRoot, 'd.md', '---\ndescription: d\ntools: Telepathy, Clairvoyance\n---\n正文\n')

    const r = await scan()

    expect(loaded(r)).toEqual([])
    expect(said(r, '已作废')).toBe(true)
    // 诊断里要告诉用户怎么改,不能只说「不行」
    expect(said(r, '删掉')).toBe(true)
  })
})

describe('permissionMode: —— 唯一一个认不出就作废的可选字段', () => {
  it('认得出的档位落到定义上', async () => {
    put(globalRoot, 'ro.md', '---\ndescription: d\npermissionMode: ask\n---\n正文\n')

    const r = await scan()

    expect(r.agents.find((x) => x.name === 'ro')?.permissionMode).toBe('ask')
  })

  it('permission-mode 这个写法也认', async () => {
    put(globalRoot, 'ro2.md', '---\ndescription: d\npermission-mode: FULL\n---\n正文\n')

    const r = await scan()

    expect(r.agents.find((x) => x.name === 'ro2')?.permissionMode).toBe('full')
  })

  it('★ 认不出的值 → 整条作废,而不是「忽略这个字段」', async () => {
    put(globalRoot, 'bad.md', '---\ndescription: d\npermissionMode: readonly\n---\n正文\n')

    const r = await scan()

    expect(loaded(r)).toEqual([])
    // 理由要说清楚:忽略它等于按父代理的档位跑,和作者本意正好相反
    expect(said(r, '本意')).toBe(true)
  })
})

describe('不可信输入', () => {
  it('描述里的控制字符被消毒 —— 它逐字进 Task 的工具定义', async () => {
    const bel = String.fromCharCode(7)
    put(globalRoot, 'x.md', `---\ndescription: 干净${bel}的\n---\n正文\n`)

    const r = await scan()

    const d = r.agents.find((a) => a.name === 'x')?.description ?? ''
    expect(d).not.toContain(bel)
    expect(d).toContain('干净')
  })

  it('角色提示词里的控制字符也被消毒 —— 它进系统提示词', async () => {
    const bel = String.fromCharCode(7)
    put(globalRoot, 'y.md', `---\ndescription: d\n---\n你要${bel}这样做\n`)

    const r = await scan()

    expect(r.agents.find((a) => a.name === 'y')?.prompt).not.toContain(bel)
  })

  it('超长的角色提示词被截断,而不是整个塞进系统提示词', async () => {
    put(globalRoot, 'big.md', `---\ndescription: d\n---\n${'啊'.repeat(40_000)}\n`)

    const r = await scan()

    const p = r.agents.find((a) => a.name === 'big')?.prompt ?? ''
    expect(p.length).toBeLessThanOrEqual(16 * 1024)
  })

  it('★ 一个坏文件不会让整次扫描失败,好的那个照样进来', async () => {
    put(globalRoot, 'good.md', md('好的'))
    put(globalRoot, 'bad.md', md(null))

    const r = await scan()

    expect(loaded(r)).toEqual(['good'])
    expect(r.diagnostics.length).toBeGreaterThan(0)
  })
})

describe('AgentRegistry', () => {
  it('★ 出厂就非空 —— Task 的第一份 description 是在第一次扫描之前拼的', () => {
    const reg = new AgentRegistry()

    expect(reg.names()).toEqual(BUILTIN_AGENTS.map((a) => a.name))
  })

  it('内建的排最前,其余按名字 —— 顺序稳定,否则工具定义每次都变、缓存全失效', async () => {
    put(globalRoot, 'zeta.md', md('z'))
    put(globalRoot, 'alpha.md', md('a'))
    const reg = new AgentRegistry()

    reg.replaceAll(await scan())

    expect(reg.names()).toEqual([...BUILTIN_AGENTS.map((a) => a.name), 'alpha', 'zeta'])
  })

  it('★ general-purpose 排在内建的第一行 —— 字母序会把它推到 code-* 后面', async () => {
    // 「拿不准就选 general-purpose」这条规矩在 `Task` 的清单里没有别的载体,
    // 它靠的就是排在第一行。内建之间按名字排的话,三条 c 开头的会把它挤到第四。
    const reg = new AgentRegistry()
    reg.replaceAll(await scan())

    expect(reg.names()[0]).toBe(GENERAL_PURPOSE.name)
  })

  it('★ 内建自己写的工具名必须被 normalizeToolName 原样认出', async () => {
    /*
      认不出的那一个会被 `resolveTools` 丢掉;全认不出时**整条内建作废** ——
      而它是写死在代码里的,用户连删都删不掉,界面上也不会有诊断(诊断只对文件)。
      `AGENT_TOOL_CHOICES` 那条同形的测试守的是表单,这条守的是内建自己。
    */
    for (const agent of BUILTIN_AGENTS) {
      for (const tool of agent.tools ?? []) {
        expect(normalizeToolName(tool), `${agent.name} 的 ${tool}`).toBe(tool)
      }
    }
  })

  it('内建被同名文件覆盖之后不再占内建那几行', async () => {
    // 覆盖之后 `source.kind` 是 global —— 那已经是用户自己的子代理了。
    put(globalRoot, 'code-analyst.md', md('我自己的分析代理'))
    const reg = new AgentRegistry()

    reg.replaceAll(await scan())

    expect(reg.names()[0]).toBe(GENERAL_PURPOSE.name)
    expect(reg.names().at(-1)).toBe('code-analyst')
    expect(reg.get('code-analyst')?.description).toBe('我自己的分析代理')
  })

  it('get 认名字,认不出返回 undefined —— 绝不回落到 general-purpose', async () => {
    const reg = new AgentRegistry()
    reg.replaceAll(await scan())

    expect(reg.get('general-purpose')?.name).toBe('general-purpose')
    expect(reg.get('reviewr')).toBeUndefined()
  })

  it('诊断跟着一起换掉', async () => {
    put(globalRoot, 'bad.md', md(null))
    const reg = new AgentRegistry()

    reg.replaceAll(await scan())

    expect(reg.diagnostics().length).toBeGreaterThan(0)
  })
})
