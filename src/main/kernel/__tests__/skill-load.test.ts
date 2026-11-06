import { mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { SKILL_BODY_MAX } from '../../../shared/domain/skill'
import { nodeHost } from '../host'
import type { SkillScanResult } from '../skill/load'
import { SKILL_LIMITS, scanSkills } from '../skill/load'
import { SkillRegistry } from '../skill/registry'

/**
 * Skill 扫描的测试。**用真临时目录,不打桩 fs。**
 *
 * ★ 理由和 `host-fs.test.ts` / `path-guard.test.ts` 一样,而且这里更强:
 * 这个文件里最重要的一条是**软链逃逸**(`skills/evil -> /`),而
 * `resolveInWorkspace` 挡住它靠的就是 `realpathSync` —— 桩掉 fs,
 * 那条用例测的就只剩下我们自己写的假对象怎么编造 realpath 了。
 *
 * 加载器的契约是**永不 throw**:每一种坏输入都要变成一条 diagnostic,
 * 而不是让整次扫描失败。所以几乎每条用例都同时断言两件事:
 * 「这条没进来」和「用户能知道它为什么没进来」。
 */

const fs = nodeHost().fs

let root = ''
let globalRoot = ''
let projectRoot = ''

beforeEach(() => {
  root = mkdtempSync(join(tmpdir(), 'nextcowork-skill-'))
  globalRoot = join(root, 'global', 'skills')
  projectRoot = join(root, 'project', '.nextcowork', 'skills')
  mkdirSync(globalRoot, { recursive: true })
  mkdirSync(projectRoot, { recursive: true })
})

afterEach(() => {
  rmSync(root, { recursive: true, force: true })
})

/** 在某一层里放一条 Skill。`content` 是整个 SKILL.md 的字节。 */
function put(scopeRoot: string, dir: string, content: string): void {
  mkdirSync(join(scopeRoot, dir), { recursive: true })
  writeFileSync(join(scopeRoot, dir, 'SKILL.md'), content)
}

/** 一份最小的 SKILL.md。★ 刻意不写 `name:` —— 名字由目录名给,和 CC 一致。 */
const md = (description: string | null, body = '照着这个做。'): string =>
  `---\n${description === null ? '' : `description: ${description}\n`}---\n${body}\n`

const scan = (): Promise<SkillScanResult> => scanSkills({ fs, globalRoot, projectRoot })

/** 诊断里有没有提到这件事 —— 断言「用户能知道原因」,而不是断言逐字文案 */
const said = (r: SkillScanResult, needle: string): boolean =>
  r.diagnostics.some((d) => d.message.includes(needle))

describe('基本形状', () => {
  it('marks client asset packages unavailable while retaining text snapshots and project overrides', async () => {
    put(globalRoot, 'text-only', md('Text snapshot'))
    put(globalRoot, 'client-package', md('Client assets'))
    mkdirSync(join(globalRoot, 'client-package', 'scripts'))
    put(globalRoot, 'overridden', md('Client assets'))
    mkdirSync(join(globalRoot, 'overridden', 'scripts'))
    put(projectRoot, 'overridden', md('Server version'))
    const result = await scanSkills({ fs, globalRoot, projectRoot, projectFs: fs })
    expect(result.skills.find((skill) => skill.name === 'text-only')?.unavailableReason).toBeUndefined()
    expect(result.skills.find((skill) => skill.name === 'client-package')?.unavailableReason).toBe('client-assets')
    expect(result.skills.find((skill) => skill.name === 'overridden')).toMatchObject({ scope: 'project' })
    expect(result.skills.find((skill) => skill.name === 'overridden')?.unavailableReason).toBeUndefined()
  })
  it('读出名字、描述、正文,并标上 scope', async () => {
    put(globalRoot, 'commit', '---\ndescription: 写提交信息\n---\n按 Conventional Commits 写。\n')

    const r = await scan()

    expect(r.skills).toHaveLength(1)
    const s = r.skills[0]
    expect(s?.name).toBe('commit')
    expect(s?.id).toBe('commit')
    expect(s?.description).toBe('写提交信息')
    expect(s?.body).toBe('按 Conventional Commits 写。')
    expect(s?.scope).toBe('global')
    expect(r.diagnostics).toEqual([])
  })

  it('项目里的那条 scope 是 project,且 source.path 指向真正的文件', async () => {
    put(projectRoot, 'commit', md('写提交信息'))

    const [s] = (await scan()).skills

    expect(s?.scope).toBe('project')
    expect(s?.source.path).toContain('SKILL.md')
    expect(s?.source.kind).toBe('folder')
  })

  it('没给 category 时落到「未分类」,给了就用给的', async () => {
    put(globalRoot, 'a', '---\ndescription: d\ncategory: 开发工具\n---\n正文\n')
    put(globalRoot, 'b', md('d'))

    const byName = new Map((await scan()).skills.map((s) => [s.name, s]))

    expect(byName.get('a')?.category).toBe('开发工具')
    expect(byName.get('b')?.category).toBe('未分类')
  })

  it('目录不存在是常态,不是错误', async () => {
    rmSync(globalRoot, { recursive: true, force: true })
    rmSync(projectRoot, { recursive: true, force: true })

    const r = await scan()

    expect(r.skills).toEqual([])
    expect(r.diagnostics).toEqual([])
  })

  it('空串的根被整层跳过 —— 没有工作区时就是这个形状', async () => {
    put(globalRoot, 'commit', md('d'))

    const r = await scanSkills({ fs, globalRoot, projectRoot: '' })

    expect(r.skills.map((s) => s.name)).toEqual(['commit'])
  })

  it('skills 目录里的散文件被忽略,不当成 Skill', async () => {
    writeFileSync(join(globalRoot, 'README.md'), '# 说明')
    put(globalRoot, 'commit', md('d'))

    const r = await scan()

    expect(r.skills.map((s) => s.name)).toEqual(['commit'])
  })
})

describe('★ 同名时项目胜出', () => {
  /**
   * ★ 这一条钉的是 `scanSkills` 里那个「全局先进、项目后进覆盖」的循环顺序。
   * 反过来写的话,项目里那条「这个仓库要用我们自己的提交规范」的 Skill
   * 就永远压不过全局那条 —— 而且症状是**静默的**:两条都叫 commit,
   * 界面上看不出生效的是哪一条,只有模型的产出不对。
   */
  it('同名两条只留项目那条,而且留的是项目那条的正文', async () => {
    put(globalRoot, 'commit', '---\ndescription: 全局的\n---\n全局正文\n')
    put(projectRoot, 'commit', '---\ndescription: 项目的\n---\n项目正文\n')

    const r = await scan()

    expect(r.skills).toHaveLength(1)
    expect(r.skills[0]?.description).toBe('项目的')
    expect(r.skills[0]?.body).toBe('项目正文')
    expect(r.skills[0]?.scope).toBe('project')
  })

  it('不同名的两层合并,不是二选一', async () => {
    put(globalRoot, 'commit', md('d'))
    put(projectRoot, 'review', md('d'))

    const names = (await scan()).skills.map((s) => s.name).sort()

    expect(names).toEqual(['commit', 'review'])
  })
})

describe('★ 无效的那些,逐条作废并说明原因', () => {
  /**
   * ★ 缺 description 必须整条作废,不能拿名字凑一个。
   *
   * 渐进披露之后,描述是模型**唯一**的判断依据。凑一个的话,模型要么永远
   * 不调它,要么见什么都调它 —— 两种都比「这条没装上」更糟,因为用户
   * 在界面上看得见它,于是会认为它在工作。
   */
  it('★ 缺 description 的整条作废,并说清为什么', async () => {
    put(globalRoot, 'commit', md(null))

    const r = await scan()

    expect(r.skills).toEqual([])
    expect(said(r, 'description')).toBe(true)
  })

  it('正文是空的也作废 —— 一条没有内容的说明书等于没装', async () => {
    put(globalRoot, 'commit', '---\ndescription: d\n---\n\n   \n')

    const r = await scan()

    expect(r.skills).toEqual([])
    expect(said(r, '正文')).toBe(true)
  })

  it('目录里没有 SKILL.md 时说一声,而不是默默跳过', async () => {
    mkdirSync(join(globalRoot, 'commit'))

    const r = await scan()

    expect(r.skills).toEqual([])
    expect(said(r, 'SKILL.md')).toBe(true)
  })

  it('目录名不合法的直接跳过(大写、点、空格都不行)', async () => {
    put(globalRoot, 'Commit', md('d'))
    put(globalRoot, '..', md('d'))
    put(globalRoot, 'a b', md('d'))

    const r = await scan()

    expect(r.skills).toEqual([])
    expect(r.diagnostics.length).toBeGreaterThanOrEqual(2)
  })

  it('完全没有前置块的 SKILL.md 作废 —— 没有 description 就没有目录项', async () => {
    put(globalRoot, 'commit', '# 只是一篇普通的 markdown\n\n没有 frontmatter。\n')

    const r = await scan()

    expect(r.skills).toEqual([])
    expect(said(r, 'description')).toBe(true)
  })
})

describe('name:与目录名', () => {
  it('frontmatter 里的 name 胜过目录名,但不一致时要报出来', async () => {
    put(globalRoot, 'old-name', '---\nname: new-name\ndescription: d\n---\n正文\n')

    const r = await scan()

    expect(r.skills[0]?.name).toBe('new-name')
    expect(r.skills[0]?.id).toBe('new-name')
    expect(said(r, '不一致')).toBe(true)
  })

  it('name 一致时不产生噪音诊断', async () => {
    put(globalRoot, 'commit', '---\nname: commit\ndescription: d\n---\n正文\n')

    expect((await scan()).diagnostics).toEqual([])
  })

  /**
   * ★ 目录名合法、frontmatter 里的 name 不合法 —— 这条缝真的存在:
   * 目录名那道闸在扫描时过,`name` 那道闸在解析之后过。少了后面这道,
   * 一个带换行的 name 会直接进系统提示词的目录里。
   */
  it('★ 目录名合法但 name 不合法时,整条作废(不回落到目录名)', async () => {
    put(globalRoot, 'commit', '---\nname: 不合法的名字\ndescription: d\n---\n正文\n')

    const r = await scan()

    expect(r.skills).toEqual([])
    expect(said(r, '不合法')).toBe(true)
  })
})

describe('★ 路径逃逸', () => {
  /**
   * ★ 这是这个文件里最重要的一条。
   *
   * `~/.nextcowork/skills/evil -> /` 之后,「扫描 skills 目录」就变成了
   * 「扫描整个磁盘」—— 而 `.nextcowork/` 是跟着仓库一起 clone 下来的,
   * 一个软链进 git 是零成本的事。`resolveInWorkspace` 会 realpath 之后
   * 做包含判断,所以它必须在这里死,而不是在某个更下游的地方。
   */
  it('★ 指向 skills 之外的软链目录被拒,且不影响同一层里正常的那条', async () => {
    const outside = join(root, 'outside', 'evil')
    mkdirSync(outside, { recursive: true })
    writeFileSync(join(outside, 'SKILL.md'), md('我在外面'))
    symlinkSync(join(root, 'outside', 'evil'), join(globalRoot, 'evil'))
    put(globalRoot, 'good', md('我在里面'))

    const r = await scan()

    expect(r.skills.map((s) => s.name)).toEqual(['good'])
    expect(said(r, '之外')).toBe(true)
  })

  it('指向 skills 内部另一个目录的软链是允许的 —— 围栏管的是「出去」', async () => {
    put(globalRoot, 'real', md('d'))
    symlinkSync(join(globalRoot, 'real'), join(globalRoot, 'alias'))

    const names = (await scan()).skills.map((s) => s.name).sort()

    expect(names).toEqual(['alias', 'real'])
  })
})

describe('★ 不可信输入的消毒与封顶', () => {
  it('★ 描述里的控制字符被剥掉 —— 它会原样进系统提示词', async () => {
    const esc = String.fromCharCode(27)
    put(globalRoot, 'commit', `---\ndescription: 写${esc}[31m提交信息\n---\n正文\n`)

    const [s] = (await scan()).skills

    expect(s?.description).not.toContain(esc)
    expect(s?.description).toContain('提交信息')
  })

  it('正文里的控制字符也被剥掉', async () => {
    const nul = String.fromCharCode(0)
    put(globalRoot, 'commit', `---\ndescription: d\n---\n正${nul}文\n`)

    expect((await scan()).skills[0]?.body).toBe('正文')
  })

  it('描述封顶在 1KB —— 它是唯一进提示词的部分', async () => {
    put(globalRoot, 'commit', `---\ndescription: ${'x'.repeat(5000)}\n---\n正文\n`)

    const s = (await scan()).skills[0]

    expect(s).toBeDefined()
    expect((s?.description.length ?? 0) <= SKILL_LIMITS.SKILL_DESCRIPTION_MAX).toBe(true)
  })

  it('正文封顶在 SKILL_BODY_MAX', async () => {
    put(globalRoot, 'commit', `---\ndescription: d\n---\n${'正'.repeat(SKILL_BODY_MAX)}\n`)

    const s = (await scan()).skills[0]

    expect(s).toBeDefined()
    expect((s?.body.length ?? 0) <= SKILL_BODY_MAX).toBe(true)
  })

  /**
   * ★ 文件字节数的闸在**解码之前**。一个 500KB 的 SKILL.md 只读前 256KB
   * 就够判断它有没有 frontmatter 了 —— 而且这一层不报错、只截断:
   * 截断之后 frontmatter 仍然完整,那条 Skill 照样可用。
   */
  it('★ 超大的 SKILL.md 只读前面一段,不把整个文件搬进内存', async () => {
    put(globalRoot, 'commit', `---\ndescription: d\n---\n${'A'.repeat(2_000_000)}\n`)

    const s = (await scan()).skills[0]

    expect(s?.description).toBe('d')
    expect((s?.body.length ?? 0) <= SKILL_LIMITS.SKILL_FILE_MAX_BYTES).toBe(true)
  })

  it('前置块里不支持的语法被记成诊断,而不是让整条失败', async () => {
    // 两层嵌套 —— 一层的 `metadata:` 现在是支持的形状,不再进诊断
    put(globalRoot, 'commit', '---\ndescription: d\nnested:\n  a:\n    b: 1\n---\n正文\n')

    const r = await scan()

    expect(r.skills).toHaveLength(1)
    expect(r.diagnostics.length).toBeGreaterThan(0)
  })
})

describe('allowed-tools', () => {
  it('CC 的写法(逗号分隔的裸标量)认得出来', async () => {
    put(globalRoot, 'commit', '---\ndescription: d\nallowed-tools: Read, Grep, Glob\n---\n正文\n')

    expect((await scan()).skills[0]?.frontmatter.allowedTools).toEqual(['Read', 'Grep', 'Glob'])
  })

  it('YAML 数组和驼峰变体也认', async () => {
    put(globalRoot, 'a', '---\ndescription: d\nallowed-tools: [Read, Grep]\n---\n正文\n')
    put(globalRoot, 'b', '---\ndescription: d\nallowedTools: Read\n---\n正文\n')

    const byName = new Map((await scan()).skills.map((s) => [s.name, s]))

    expect(byName.get('a')?.frontmatter.allowedTools).toEqual(['Read', 'Grep'])
    expect(byName.get('b')?.frontmatter.allowedTools).toEqual(['Read'])
  })

  it('没写就是 undefined,不是空数组 —— 空数组会被下游读成「一个工具都不许用」', async () => {
    put(globalRoot, 'commit', md('d'))

    expect((await scan()).skills[0]?.frontmatter.allowedTools).toBeUndefined()
  })
})

describe('★ 永不 throw', () => {
  /**
   * ★ 一次扫描因为某台机器上一个坏文件而整体抛异常的话,用户看到的是
   * 「所有 Skill 都不见了」,而真正坏掉的只有一条。这条用例把 fs 换成
   * 一个见什么抛什么的实现,断言它仍然是一次正常返回。
   */
  it('★ fs 见什么抛什么时,返回的是诊断而不是异常', async () => {
    const angry = {
      ...fs,
      exists: () => Promise.resolve(true),
      readDir: () => Promise.reject(new Error('EACCES'))
    }

    const r = await scanSkills({ fs: angry, globalRoot, projectRoot })

    expect(r.skills).toEqual([])
    expect(r.diagnostics.length).toBeGreaterThan(0)
    expect(said(r, 'EACCES')).toBe(true)
  })

  it('读不了某一个 SKILL.md 时,同一层里其他的照常加载', async () => {
    put(globalRoot, 'good', md('d'))
    const bad = join(globalRoot, 'bad')
    mkdirSync(bad)
    symlinkSync(join(bad, 'nowhere'), join(bad, 'SKILL.md'))

    const r = await scan()

    expect(r.skills.map((s) => s.name)).toEqual(['good'])
  })
})

describe('SkillRegistry', () => {
  const skill = (name: string): Parameters<SkillRegistry['replaceAll']>[0]['skills'][number] => ({
    id: name,
    name,
    description: 'd',
    category: '未分类',
    source: { kind: 'folder', path: '' },
    globalEnabled: true,
    frontmatter: {},
    body: '正文'
  })

  /**
   * ★ 按名字排序不是洁癖:目录里的顺序会直接变成系统提示词里的顺序,
   * 而 `readdir` 的顺序在不同平台、不同文件系统上不一样。抖动的提示词前缀
   * 会让上游的 prompt cache 整体失效 —— 症状是「同样的会话,今天忽然贵了」。
   */
  it('★ list() 按名字排序,不跟着目录遍历的顺序抖', () => {
    const reg = new SkillRegistry()
    reg.replaceAll({ skills: [skill('zebra'), skill('apple'), skill('mango')], diagnostics: [] })

    expect(reg.list().map((s) => s.name)).toEqual(['apple', 'mango', 'zebra'])
  })

  it('replaceAll 是整体换掉,不是合并', () => {
    const reg = new SkillRegistry()
    reg.replaceAll({ skills: [skill('a')], diagnostics: [] })
    reg.replaceAll({ skills: [skill('b')], diagnostics: [] })

    expect(reg.list().map((s) => s.name)).toEqual(['b'])
    expect(reg.get('a')).toBeUndefined()
  })

  /**
   * ★ 这一条钉的是 `resolve()` 上那段注释里那个决定。
   *
   * `DEFAULT_WORKSPACE_SETTINGS.activeSkillIds` 出厂就是 `[]`。按字面理解
   * (空清单 = 白名单为空 = 一条都不生效)的话,用户装完 Skill、在界面上
   * 看见它、然后发现模型完全没反应,而界面上没有任何地方提示开关在哪。
   * 渐进披露之后每条只占提示词里的一行,没有成本上的理由默认关掉。
   */
  it('★ 空清单 = 全部可用,不是一条都不可用', () => {
    const reg = new SkillRegistry()
    reg.replaceAll({ skills: [skill('a'), skill('b')], diagnostics: [] })

    expect(reg.resolve([]).map((s) => s.name)).toEqual(['a', 'b'])
    expect(reg.resolve(undefined).map((s) => s.name)).toEqual(['a', 'b'])
  })

  it('列了就是白名单,认不出的 id 被忽略', () => {
    const reg = new SkillRegistry()
    reg.replaceAll({ skills: [skill('a'), skill('b')], diagnostics: [] })

    expect(reg.resolve(['b', '不存在的']).map((s) => s.name)).toEqual(['b'])
  })

  it('explicit 模式下空清单保持空,不会退回全部 Skill', () => {
    const reg = new SkillRegistry()
    reg.replaceAll({ skills: [skill('a'), skill('b')], diagnostics: [] })
    expect(reg.resolve([], 'explicit')).toEqual([])
    expect(reg.resolve([], 'all').map((s) => s.name)).toEqual(['a', 'b'])
  })

  it('诊断跟着一起换,不会留着上一次扫描的', () => {
    const reg = new SkillRegistry()
    reg.replaceAll({ skills: [], diagnostics: [{ path: 'p', message: 'm' }] })
    expect(reg.diagnostics()).toHaveLength(1)

    reg.replaceAll({ skills: [], diagnostics: [] })
    expect(reg.diagnostics()).toEqual([])
  })
})
