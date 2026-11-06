/**
 * 内置命令 —— 写死在代码里的那几条。
 *
 * ★ 和 `agent/builtin.ts` 同一个理由:它们先进表、可以被同名文件覆盖,
 * 于是「至少有一条命令可用」是一条无条件成立的事实,而不是
 * 「取决于用户机器上有没有那个目录」。
 */
import type { CommandDefinition } from '../../../shared/domain/command'

/**
 * `/init` —— 加载内置 Skill `repo-init`,由它产出 `AGENTS.md`(约束)、
 * `.agents/codemap/`(仓库地图)和 `.agents/memory/`(仓库记忆)。
 *
 * ★ 方法论住在 `resources/skills/repo-init/SKILL.md`,这里只剩一层薄壳:
 * - 那份提示词有 30KB,要分阶段派子代理、带一整套页面模板,还会持续迭代。
 *   放在 Skill 里改了 resources 重启即生效,不必发版改代码;
 * - 用户能用同名的项目 Skill 覆盖它(`installBundledSkills` 不动用户的同名包);
 * - 命令本身照旧是纯文本(见 `shared/domain/command.ts`),不进内核。
 *
 * ★ Skill 必须保持**单文件**:全局 Skill 的目录里只要有 SKILL.md 之外的东西,
 * SSH 会话里就会被标成 `client-assets` 拒掉(`skill/load.ts`),而「在服务器上的
 * 仓库里跑 /init」正是常见用法。`bundled.test.ts` 守着这一条。
 *
 * ★ 找不到 Skill 时**停下**,不让模型凭印象自己写。被禁用、或者工作区只选装了
 * 别的 Skill 时它不在目录里;这时模型凭项目名和几个文件名脑补出来的 AGENTS.md
 * 看起来完全正常,但每一条命令都是猜的 —— 比告诉用户去重新启用更糟。
 */
const INIT_PROMPT = `Load the \`repo-init\` Skill with the Skill tool and follow it to create or update this repository's AGENTS.md, code map (\`.agents/codemap/\`) and repository memory (\`.agents/memory/\`).

Arguments passed to /init (empty means a full run): $ARGUMENTS

If the Skill tool reports that \`repo-init\` does not exist or is unavailable, do not attempt the task from memory. Tell the user that the built-in repo-init Skill is disabled or not enabled for this workspace, and that they can enable it again in the Extensions panel under Skills. Then stop.`

export const BUILTIN_COMMANDS: readonly CommandDefinition[] = [
  {
    name: 'init',
    // 用户看到的那行副标题由渲染层按 name 翻译;这条是兜底(测试与非 UI 调用)。
    description: 'Analyze the project and generate AGENTS.md, a code map and repository memory',
    prompt: INIT_PROMPT,
    argumentHint: '[update|agents-only] [--no-history]',
    scope: 'builtin',
    source: ''
  }
]
