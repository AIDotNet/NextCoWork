import { cpSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readdirSync, renameSync, rmSync, writeFileSync, type Dirent } from 'node:fs'
import { join } from 'node:path'
import { SKILL_NAME_RE } from '../../../shared/domain/skill'
import type { SkillDiagnostic } from './load'

/**
 * 标记「这个已安装的 skill 是我们从 resources 铺下去的内置版」的哨兵文件。
 *
 * ★ 它是「能不能覆盖」的唯一依据:带哨兵 = 我们上次铺的,可以刷新;不带 = 用户
 * 自己装的(folder/zip),哪怕同名也绝不动。哨兵是空文件,skill 加载器不认它、忽略。
 *
 * ★ 导出给 `load.ts` 的远端资产判定用:那里按「目录里有没有 SKILL.md 之外的文件」
 * 判断 client-assets,不放过这个哨兵的话,**每一条**内置 Skill(哪怕是单文件)
 * 在 SSH 会话里都会被拒掉。
 */
export const BUNDLED_MARKER = '.nextcowork-bundled'

/**
 * 启动时把内置 skill 从 `resources/skills` 铺到全局 skill 根。
 *
 * ★ **内置 skill 每次启动都覆盖刷新**(这样改了 resources 里的内容,下次启动即生效),
 * 但**绝不动用户自己装的同名 skill** —— 靠 `BUNDLED_MARKER` 哨兵区分:
 *   - 目标不存在 → 安装(并打哨兵)。
 *   - 目标存在且带哨兵(是我们上次铺的)→ 覆盖刷新(重打哨兵)。
 *   - 目标存在但**不带**哨兵(用户的 skill 恰好同名)→ 跳过,保住用户的东西。
 *
 * 覆盖是原子的:新副本先在 staging 里备好 → 旧目标挪进 staging 当 backup →
 * 把新副本 rename 到目标。任一步失败就把 backup 挪回来,发现不了半个 skill。
 */
export function installBundledSkills(bundledRoot: string, globalRoot: string): SkillDiagnostic[] {
  const diagnostics: SkillDiagnostic[] = []
  let entries: Dirent[]
  try {
    entries = readdirSync(bundledRoot, { withFileTypes: true })
  } catch (error) {
    return [{ path: bundledRoot, message: errorMessage(error) }]
  }

  for (const entry of entries) {
    if (!entry.isDirectory() || !SKILL_NAME_RE.test(entry.name)) continue
    const source = join(bundledRoot, entry.name)
    const target = join(globalRoot, entry.name)

    let staging: string | undefined
    let movedBackup = false
    try {
      // existsSync also treats an ENOTDIR parent as absent, so this startup path can report it below.
      const exists = existsSync(target)

      // 目标已存在但没有哨兵 → 是用户装的同名 skill,不是我们的内置版,别碰。
      if (exists && lstatSync(join(target, BUNDLED_MARKER), { throwIfNoEntry: false }) === undefined) {
        continue
      }
      if (!lstatSync(join(source, 'SKILL.md'), { throwIfNoEntry: false })?.isFile()) {
        diagnostics.push({ path: source, message: 'Bundled Skill is missing SKILL.md' })
        continue
      }

      mkdirSync(globalRoot, { recursive: true })
      staging = mkdtempSync(join(globalRoot, `.bundled-${entry.name}-`))
      const stagedPackage = join(staging, entry.name)
      // force:true —— 覆盖场景下 staging 是全新目录,这里只是不因残留报错。
      cpSync(source, stagedPackage, { recursive: true, force: true })
      // 打哨兵:即便 resources 源里没带,也标明这是我们铺的内置版,供下次启动识别。
      writeFileSync(join(stagedPackage, BUNDLED_MARKER), '')

      if (exists) {
        // 旧目标挪进 staging 当 backup(同一文件系统,rename 才是原子的)。
        renameSync(target, join(staging, '.old'))
        movedBackup = true
      }
      renameSync(stagedPackage, target)
      movedBackup = false // 新副本已就位,backup 不再需要回滚
    } catch (error) {
      // 回滚:旧目标挪走了、新的没上去 → 把 backup 挪回来,别留下一个空洞。
      if (movedBackup && staging !== undefined && !existsSync(target)) {
        try {
          renameSync(join(staging, '.old'), target)
        } catch (rollbackError) {
          diagnostics.push({ path: target, message: errorMessage(rollbackError) })
        }
      }
      diagnostics.push({ path: target, message: errorMessage(error) })
    } finally {
      if (staging !== undefined) {
        try {
          rmSync(staging, { recursive: true, force: true })
        } catch (error) {
          diagnostics.push({ path: staging, message: errorMessage(error) })
        }
      }
    }
  }

  return diagnostics
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}
