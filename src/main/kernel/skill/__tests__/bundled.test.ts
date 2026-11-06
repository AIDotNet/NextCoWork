import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'
import { installBundledSkills } from '../bundled'
import { SKILL_DESCRIPTION_MAX, scanSkills } from '../load'
import { fmString, parseFrontmatter } from '../../frontmatter'
import { nodeHost } from '../../host'
import { SKILL_BODY_MAX } from '../../../../shared/domain/skill'

const roots: string[] = []
afterEach(() => {
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true })
})

function fixture(): { source: string; target: string } {
  const root = mkdtempSync(join(tmpdir(), 'nextcowork-bundled-'))
  roots.push(root)
  return { source: join(root, 'skills'), target: join(root, 'global', 'skills') }
}

describe('installBundledSkills', () => {
  it('installs the shipped Skill Creator as a discoverable global Skill', async () => {
    const { target } = fixture()
    const source = join(import.meta.dirname, '../../../../../resources/skills')

    expect(installBundledSkills(source, target)).toEqual([])
    expect(installBundledSkills(source, target)).toEqual([])
    const result = await scanSkills({ fs: nodeHost().fs, globalRoot: target, projectRoot: '' })

    expect(result.diagnostics).toEqual([])
    expect(result.skills).toEqual(expect.arrayContaining([
      expect.objectContaining({
        id: 'skills-creator',
        name: 'skills-creator',
        scope: 'global',
        source: expect.objectContaining({ path: expect.stringContaining('/skills/skills-creator/SKILL.md') })
      })
    ]))
  })

  it('ships repo-init as a single-file Skill within the catalog limits', async () => {
    const { target } = fixture()
    const source = join(import.meta.dirname, '../../../../../resources/skills')

    // ★ 只许有 SKILL.md:全局 Skill 目录里多一个文件,SSH 会话就会把它标成 client-assets 拒掉,
    //   而 /init 整个靠它(见 command/builtin.ts)。
    expect(readdirSync(join(source, 'repo-init'))).toEqual(['SKILL.md'])

    // ★ 量的是原文:加载器会把超长的 description / 正文静默截断,截完再量永远合格。
    const raw = parseFrontmatter(readFileSync(join(source, 'repo-init', 'SKILL.md'), 'utf8'))
    const description = fmString(raw, 'description')
    expect(description).toBeDefined()
    expect(description?.length ?? 0).toBeLessThanOrEqual(SKILL_DESCRIPTION_MAX)
    expect(raw.body.length).toBeLessThanOrEqual(SKILL_BODY_MAX)

    expect(installBundledSkills(source, target)).toEqual([])
    const result = await scanSkills({ fs: nodeHost().fs, globalRoot: target, projectRoot: '' })
    expect(result.skills).toEqual(expect.arrayContaining([
      expect.objectContaining({ name: 'repo-init', scope: 'global' })
    ]))
  })

  it('keeps single-file bundled Skills available in remote workspaces', async () => {
    const { target } = fixture()
    const source = join(import.meta.dirname, '../../../../../resources/skills')
    expect(installBundledSkills(source, target)).toEqual([])

    // 传了 projectFs = 这是一次远端(SSH)扫描,全局 Skill 的资产留在客户端
    const fs = nodeHost().fs
    const result = await scanSkills({ fs, projectFs: fs, globalRoot: target, projectRoot: '' })
    const byName = new Map(result.skills.map((s) => [s.name, s]))

    expect(byName.get('repo-init')?.unavailableReason).toBeUndefined()
    expect(byName.get('skills-creator')?.unavailableReason).toBeUndefined()
    // 带 references/ 的那条照旧被挡:资产真的只在客户端
    expect(byName.get('plugin-builder')?.unavailableReason).toBe('client-assets')
  })

  it('copies a complete package and its resources', () => {
    const { source, target } = fixture()
    mkdirSync(join(source, 'example', 'references'), { recursive: true })
    writeFileSync(join(source, 'example', 'SKILL.md'), '---\nname: example\ndescription: d\n---\nbody')
    writeFileSync(join(source, 'example', 'references', 'guide.md'), 'guide')

    expect(installBundledSkills(source, target)).toEqual([])
    expect(readFileSync(join(target, 'example', 'SKILL.md'), 'utf8')).toBe(
      readFileSync(join(source, 'example', 'SKILL.md'), 'utf8')
    )
    expect(readFileSync(join(target, 'example', 'references', 'guide.md'), 'utf8')).toBe('guide')
    expect(readdirSync(target)).toEqual(['example'])
  })

  it('keeps an existing global package unchanged', () => {
    const { source, target } = fixture()
    mkdirSync(join(source, 'example'), { recursive: true })
    mkdirSync(join(target, 'example'), { recursive: true })
    writeFileSync(join(source, 'example', 'SKILL.md'), 'new')
    writeFileSync(join(target, 'example', 'SKILL.md'), 'user version')

    expect(installBundledSkills(source, target)).toEqual([])
    expect(readFileSync(join(target, 'example', 'SKILL.md'), 'utf8')).toBe('user version')
  })

  it('★ marks installed bundled packages with the sentinel so they are recognised next startup', () => {
    const { source, target } = fixture()
    mkdirSync(join(source, 'example'), { recursive: true })
    writeFileSync(join(source, 'example', 'SKILL.md'), 'v1')

    expect(installBundledSkills(source, target)).toEqual([])
    expect(existsSync(join(target, 'example', '.nextcowork-bundled'))).toBe(true)
  })

  it('★ overwrites a previously-bundled package on the next startup (refreshes built-in content)', () => {
    const { source, target } = fixture()
    mkdirSync(join(source, 'example'), { recursive: true })
    writeFileSync(join(source, 'example', 'SKILL.md'), 'v1')

    // 首次安装 → 打哨兵
    expect(installBundledSkills(source, target)).toEqual([])
    expect(readFileSync(join(target, 'example', 'SKILL.md'), 'utf8')).toBe('v1')

    // 改了 resources 里的内置内容 → 下次启动应覆盖刷新
    writeFileSync(join(source, 'example', 'SKILL.md'), 'v2')
    expect(installBundledSkills(source, target)).toEqual([])
    expect(readFileSync(join(target, 'example', 'SKILL.md'), 'utf8')).toBe('v2')
    expect(existsSync(join(target, 'example', '.nextcowork-bundled'))).toBe(true)
    expect(readdirSync(target)).toEqual(['example']) // 覆盖后没有残留 staging / backup
  })

  it('★ 用户装的同名 skill(无哨兵)绝不被内置覆盖', () => {
    const { source, target } = fixture()
    mkdirSync(join(source, 'example'), { recursive: true })
    mkdirSync(join(target, 'example'), { recursive: true })
    writeFileSync(join(source, 'example', 'SKILL.md'), 'builtin v2')
    writeFileSync(join(target, 'example', 'SKILL.md'), 'user version')
    // 目标存在但没有哨兵 → 视为用户装的,跳过

    expect(installBundledSkills(source, target)).toEqual([])
    expect(readFileSync(join(target, 'example', 'SKILL.md'), 'utf8')).toBe('user version')
    expect(existsSync(join(target, 'example', '.nextcowork-bundled'))).toBe(false)
  })

  it('reports an incomplete bundled package without creating it', () => {
    const { source, target } = fixture()
    mkdirSync(join(source, 'broken'), { recursive: true })

    const diagnostics = installBundledSkills(source, target)
    expect(diagnostics[0]?.path).toContain('broken')
    expect(existsSync(join(target, 'broken'))).toBe(false)
  })

  it('returns installation failures as diagnostics so startup can continue', () => {
    const { source, target } = fixture()
    mkdirSync(join(source, 'example'), { recursive: true })
    writeFileSync(join(source, 'example', 'SKILL.md'), 'body')
    mkdirSync(join(target, '..'), { recursive: true })
    writeFileSync(target, 'existing file')

    expect(installBundledSkills(source, target)).toEqual([
      expect.objectContaining({ path: join(target, 'example'), message: expect.any(String) })
    ])
    expect(readFileSync(target, 'utf8')).toBe('existing file')
  })
})
