/**
 * `PluginManager.contributedSkillRoots()` —— 交给 Skill 扫描器的那几个目录。
 *
 * ## 这里钉的是什么
 *
 * 这个函数是「插件自带 Skill」这条链上**唯一**决定「现在有哪几条」的地方,
 * 而它的每一条筛子都对应一种不该发生的事:
 *
 * - 禁用的插件还在供 skill → 用户关掉了它,模型下一轮还在用;
 * - 待批准的插件在供 skill → 那正是用户还没点头的东西,却已经进了他的上下文;
 * - 顺序不确定 → 两个插件撞名时的赢家随安装顺序变,同样两个插件在两台机器上
 *   给出不同结果。
 *
 * 用真目录 + 假 runtime,同 `host.test.ts` 的做法:这里测的是**策略**
 * (谁的目录该被交出去),不是机制。
 */
import { promises as fs } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { nodeHost } from '../../kernel/host'
import { PluginManager, type PluginRuntime } from '../manager'

let root = ''

/** 一个什么都不做的 runtime —— 这个文件一次都不会激活插件。 */
function idleRuntime(): PluginRuntime {
  return {
    spawn: vi.fn(async () => {}),
    invoke: vi.fn(async () => ({})),
    dispose: vi.fn(),
    disposeAll: vi.fn()
  }
}

/**
 * 在包里造一个插件,并写好它的 skill 目录。
 *
 * `permissions` 留空 —— 不需要任何能力的插件装上就是启用的,省掉一次批准,
 * 让用例只聚焦在 skill 这一件事上。
 */
async function makePlugin(
  pluginId: string,
  skills: readonly string[],
  extra: Record<string, unknown> = {}
): Promise<void> {
  const [publisher, name] = pluginId.split('.')
  const dir = join(root, pluginId)
  await fs.mkdir(join(dir, 'dist'), { recursive: true })
  await fs.writeFile(join(dir, 'dist', 'extension.js'), 'export function activate(){}')
  for (const skill of skills) {
    await fs.mkdir(join(dir, 'skills', skill), { recursive: true })
    await fs.writeFile(join(dir, 'skills', skill, 'SKILL.md'), `---\ndescription: ${skill}\n---\n做这个。\n`)
  }
  await fs.writeFile(
    join(dir, 'package.json'),
    JSON.stringify({
      publisher,
      name,
      displayName: pluginId,
      description: 'demo',
      version: '1.0.0',
      engines: { nextcowork: '^1.0.0' },
      main: './dist/extension.js',
      permissions: [],
      contributes: { skills: skills.map((s) => ({ path: `skills/${s}` })) },
      ...extra
    })
  )
}

/**
 * 起一个 manager,并把**所有装到的插件都启用**。
 *
 * ★ 必须显式启用:`load()` 对没有持久化记录的插件取 `persisted?.enabled ?? false`,
 * 也就是**新发现的插件默认是禁用的**。不启用的话这个文件里每条用例都会
 * 得到一个空数组 —— 而那个空数组看起来完全像是「功能没接上」,
 * 会把排查引向错误的方向。
 *
 * 声明了必选能力的插件会停在 `pending-approval`,`setEnabled` 也扶不正 ——
 * 那正是「待批准不贡献」那条用例依赖的行为。
 */
async function startManager(): Promise<PluginManager> {
  const kv = new Map<string, unknown>()
  const manager = new PluginManager({
    host: nodeHost(),
    runtime: idleRuntime(),
    pluginRoot: root,
    hostVersion: '1.0.0',
    apiVersion: '1.0.0',
    getKv: (key, fallback) => (kv.has(key) ? (kv.get(key) as typeof fallback) : fallback),
    setKv: (key, value) => { kv.set(key, value) },
    currentWorkspace: () => ({ id: 'ws', rootPath: root }),
    currentAppearance: () => 'dark' as const,
    approve: async () => true,
    trash: async () => {},
    openExternal: async () => {},
    clipboard: { readText: async () => '', writeText: async () => {} },
    scmFor: () => { throw new Error('scm adapter is not wired in this test') },
    openTab: () => {},
    launchTerminal: () => ({ opened: true }),
    requestInteraction: async () => null,
    emitProgress: () => {},
    reserveName: (id: string) => id,
    emitChanged: () => {},
    // 禁用会走到它(要把插件的工具从注册表里摘掉)。不给就是 TypeError。
    onToolsChanged: () => {}
  } as unknown as ConstructorParameters<typeof PluginManager>[0])
  await manager.start()
  for (const plugin of manager.catalog().plugins) await manager.setEnabled(plugin.id, true)
  return manager
}

beforeEach(async () => {
  root = await fs.mkdtemp(join(tmpdir(), 'ncw-plugin-skills-'))
})

afterEach(async () => {
  await fs.rm(root, { recursive: true, force: true })
})

describe('交给扫描器的目录', () => {
  it('把包目录和清单路径拼成绝对路径', async () => {
    await makePlugin('acme.pdf', ['pdf-tools'])
    const roots = (await startManager()).contributedSkillRoots()

    expect(roots).toHaveLength(1)
    expect(roots[0]?.pluginId).toBe('acme.pdf')
    expect(roots[0]?.dir).toBe(join(root, 'acme.pdf', 'skills', 'pdf-tools'))
  })

  it('一个插件可以贡献多条', async () => {
    await makePlugin('acme.pdf', ['read-pdf', 'write-pdf'])
    const roots = (await startManager()).contributedSkillRoots()
    expect(roots.map((r) => r.dir.split(/[\\/]/).pop())).toEqual(['read-pdf', 'write-pdf'])
  })

  it('没声明 skills 的插件不贡献任何目录', async () => {
    await makePlugin('acme.plain', [])
    expect((await startManager()).contributedSkillRoots()).toEqual([])
  })
})

describe('筛子', () => {
  it('★★ 禁用之后立刻不再贡献 —— 用户关掉了它,模型下一轮就不该再看见', async () => {
    await makePlugin('acme.pdf', ['pdf-tools'])
    const manager = await startManager()
    expect(manager.contributedSkillRoots()).toHaveLength(1)

    await manager.setEnabled('acme.pdf', false)
    expect(manager.contributedSkillRoots()).toEqual([])

    // 再打开就该回来 —— 这条链是双向的
    await manager.setEnabled('acme.pdf', true)
    expect(manager.contributedSkillRoots()).toHaveLength(1)
  })

  it('★★ 停在待批准的插件不贡献 —— 那正是用户还没点头的东西', async () => {
    /*
      声明了必选能力、而用户尚未批准的插件停在 `pending-approval`。
      让它往模型上下文里塞 skill,等于把「还没同意」当成了「同意」。
    */
    await makePlugin('acme.needy', ['risky'], { permissions: ['workspace.read'] })
    const manager = await startManager()

    const plugin = manager.catalog().plugins.find((p) => p.id === 'acme.needy')
    expect(plugin?.status).toBe('pending-approval')
    expect(manager.contributedSkillRoots()).toEqual([])
  })

  it('★ 按 pluginId 排序 —— 撞名时的赢家不能取决于安装顺序', async () => {
    /*
      同名 skill 的赢家由顺序决定(先来的赢,见 `scanPluginRoots`)。
      不排的话,赢家取决于 Map 的插入顺序,也就是用户当初的安装顺序 ——
      同样两个插件在两台机器上会给出不同的结果,而没有任何地方解释为什么。
    */
    await makePlugin('zzz.late', ['shared'])
    await makePlugin('aaa.early', ['shared'])
    const roots = (await startManager()).contributedSkillRoots()

    expect(roots.map((r) => r.pluginId)).toEqual(['aaa.early', 'zzz.late'])
  })
})
