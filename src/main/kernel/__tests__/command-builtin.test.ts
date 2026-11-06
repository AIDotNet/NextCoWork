import { describe, expect, it } from 'vitest'
import { BUILTIN_COMMANDS } from '../command/builtin'
import { expandCommandPrompt } from '../../../shared/domain/command'

function init() {
  const found = BUILTIN_COMMANDS.find((c) => c.name === 'init')
  if (found === undefined) throw new Error('builtin /init is missing')
  return found
}

describe('builtin /init', () => {
  it('delegates to the bundled repo-init Skill', () => {
    // ★ 方法论住在 resources/skills/repo-init/SKILL.md;改名的话两边要一起改
    expect(init().prompt).toContain('`repo-init`')
    expect(init().argumentHint).toBe('[update|agents-only] [--no-history]')
  })

  it('passes the user arguments through to the Skill', () => {
    const expanded = expandCommandPrompt(init().prompt, 'update --no-history')
    expect(expanded).toContain('Arguments passed to /init (empty means a full run): update --no-history')
    expect(expanded).not.toContain('$ARGUMENTS')
  })

  it('stays a full run when no arguments are given', () => {
    const expanded = expandCommandPrompt(init().prompt, '')
    expect(expanded).not.toContain('$ARGUMENTS')
    expect(expanded).toContain('Arguments passed to /init (empty means a full run): \n')
  })
})
