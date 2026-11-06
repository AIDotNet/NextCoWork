/**
 * 子代理定义的扫描 —— 照搬 Claude Code 的 `agents/*.md`。
 *
 * ```
 * <appData>/agents/<name>.md                     全局
 * <workspaceRoot>/.next-cowork/agents/<name>.md  项目(同名时项目胜出)
 * ```
 *
 * 结构和 `skill/load.ts` 是刻意平行的(两层目录、同名时项目胜出、永不 throw、
 * 一切失败变诊断),差别只有两处,两处都有理由:
 *
 * 1. **单文件,不是目录**。子代理不带资产 —— 它需要的全部东西就是
 *    「你是谁 + 你能用哪些工具」。这是 CC 的实际形状。
 * 2. **内建的那条先进表**。`general-purpose` 写死在代码里(见 `builtin.ts`),
 *    它在这里第一个进 map,于是「至少有一个子代理可用」是一条无条件成立的事实,
 *    而不是「取决于用户机器上有没有那个目录」。
 */
import type { AgentDefinition } from '../../../shared/domain/agent-def'
import {
  AGENT_DESCRIPTION_MAX,
  AGENT_NAME_RE,
  AGENT_PROMPT_MAX,
  isAgentColor
} from '../../../shared/domain/agent-def'
import type { PermissionMode } from '../../../shared/agent/permission'
import { PERMISSION_MODES } from '../../../shared/agent/permission'
import { THINKING_LEVELS, type ThinkingLevel } from '../../../shared/agent/run-request'
import { fmList, fmString, parseFrontmatter } from '../frontmatter'
import type { KernelFs, WorkspacePaths } from '../host'
import { EnvironmentError } from '../../../shared/domain/environment'
import { LOCAL_SETTINGS_DIRNAME } from '../../../shared/domain/local-settings'
import { clampWithEllipsis, stripControlChars } from '../text'
import { PathEscapeError, resolveInWorkspace } from '../tool/path-guard'
import { BUILTIN_AGENTS } from './builtin'
import { normalizeToolList } from './tool-alias'

/** 目录名 —— 和 CC 一致 */
export const AGENTS_DIR = 'agents'
/** 项目级资源所在的那层目录 —— 唯一出处见 `skill/load.ts` 的同名常量。 */
export const PROJECT_AGENTS_PREFIX = LOCAL_SETTINGS_DIRNAME

/** 单个定义文件读进内存的字节上限。正文还会再被 `AGENT_PROMPT_MAX` 截一次。 */
const AGENT_FILE_MAX_BYTES = 128 * 1024

/** 一次扫描最多认多少个。它们的描述全都要进 `Task` 的 description,而那玩意每轮重发。 */
const MAX_AGENTS = 100

export interface AgentDiagnostic {
  path: string
  message: string
}

export interface AgentScanResult {
  agents: AgentDefinition[]
  diagnostics: AgentDiagnostic[]
}

export interface AgentScanInput {
  fs: KernelFs
  projectFs?: KernelFs
  projectPath?: WorkspacePaths
  /** `<appData>/agents`。空串 = 跳过全局这一层。 */
  globalRoot: string
  /** `<workspaceRoot>/.nextcowork/agents`。空串 = 没有工作区。 */
  projectRoot: string
}

/**
 * 扫两层目录,产出一张子代理表(内建那条永远在里面)。
 *
 * ★ **永不 throw**,理由和 `scanSkills` 一模一样:一台机器上一个坏文件
 * 让整次扫描失败的话,用户看到的是「所有子代理都不见了」。
 */
export async function scanAgents(input: AgentScanInput): Promise<AgentScanResult> {
  const diagnostics: AgentDiagnostic[] = []
  const byName = new Map<string, AgentDefinition>()

  // ★ 内建的先进。用户可以用同名文件覆盖它(下面两层会 set 同一个 key),
  //   但覆盖失败(文件是坏的)时留下的仍然是这条能用的 —— 而不是一个空表。
  for (const a of BUILTIN_AGENTS) byName.set(a.name, a)

  // 顺序即优先级:全局先进,项目后进覆盖同名
  for (const scope of ['global', 'project'] as const) {
    const root = scope === 'global' ? input.globalRoot : input.projectRoot
    if (root === '') continue
    await scanOneRoot(scope === 'project' ? input.projectFs ?? input.fs : input.fs, root, scope, byName, diagnostics,
      scope === 'project' ? input.projectPath : undefined)
  }

  return { agents: [...byName.values()], diagnostics }
}

async function scanOneRoot(
  fs: KernelFs,
  root: string,
  scope: 'global' | 'project',
  out: Map<string, AgentDefinition>,
  diagnostics: AgentDiagnostic[],
  path?: WorkspacePaths
): Promise<void> {
  let entries: Array<{ name: string; isDir: boolean }>
  try {
    if (!(await fs.exists(root))) return // 没有 agents 目录是常态,不是错误
    entries = await fs.readDir(root)
  } catch (err) {
    if (err instanceof EnvironmentError) throw err
    diagnostics.push({ path: root, message: `读不了这个目录:${msg(err)}` })
    return
  }

  for (const e of entries) {
    if (e.isDir) continue
    if (!e.name.endsWith('.md')) continue // README.txt 之类的不算错,静静跳过

    const stem = e.name.slice(0, -'.md'.length)
    /*
      ★ 文件名先过 `AGENT_NAME_RE`。和 Skill 那边同一个理由:名字会进
      `Task` 工具的 description、会被模型原样当成 `subagent_type` 传回来,
      而一个叫 `../../etc` 或者带换行的文件名两样都能搞坏。
    */
    if (!AGENT_NAME_RE.test(stem)) {
      diagnostics.push({ path: `${root}/${e.name}`, message: '文件名不合法,已跳过' })
      continue
    }

    if (out.size >= MAX_AGENTS) {
      diagnostics.push({
        path: root,
        message: `子代理数量超过 ${String(MAX_AGENTS)} 个,其余未加载`
      })
      return
    }

    let file: string
    try {
      /*
        ★ 挡软链逃逸:`~/.nextcowork/agents/evil.md -> /etc/passwd` 之后,
        「加载子代理」就变成了「把任意文件的内容拼进系统提示词」。
      */
      file = path ? await path.resolveWithin(root, e.name) : resolveInWorkspace(root, e.name)
    } catch (err) {
      if (err instanceof EnvironmentError) throw err
      if (err instanceof PathEscapeError) {
        diagnostics.push({
          path: `${root}/${e.name}`,
          message: '这个文件指向了 agents 之外,已跳过'
        })
        continue
      }
      diagnostics.push({ path: `${root}/${e.name}`, message: msg(err) })
      continue
    }

    const loaded = await loadOne(fs, file, stem, scope, diagnostics)
    if (loaded !== null) out.set(loaded.name, loaded)
  }
}

async function loadOne(
  fs: KernelFs,
  file: string,
  fileName: string,
  scope: 'global' | 'project',
  diagnostics: AgentDiagnostic[]
): Promise<AgentDefinition | null> {
  let raw: string
  try {
    const bytes = await fs.readFileBytes(file, AGENT_FILE_MAX_BYTES)
    raw = new TextDecoder('utf-8', { fatal: false }).decode(bytes)
  } catch (err) {
    if (err instanceof EnvironmentError) throw err
    diagnostics.push({ path: file, message: `读不了这个文件:${msg(err)}` })
    return null
  }

  const fm = parseFrontmatter(raw)
  for (const s of fm.skipped) diagnostics.push({ path: file, message: s })

  // frontmatter 里的 name 胜过文件名,但不一致时要说一声 —— 否则用户改完文件名
  // 会发现旧名字还在,而文件里明明写着新的
  const declared = fmString(fm, 'name')
  const name = declared ?? fileName
  if (declared !== undefined && declared !== fileName) {
    diagnostics.push({
      path: file,
      message: `frontmatter 里的 name "${declared}" 和文件名 "${fileName}" 不一致,以 name 为准`
    })
  }
  if (!AGENT_NAME_RE.test(name)) {
    diagnostics.push({ path: file, message: `name "${name}" 不合法,这个子代理已作废` })
    return null
  }

  /*
    ★ 缺 description 就整条作废,而不是拿名字凑一个。
    描述逐字进 `Task` 工具的 description,是模型判断「这活该不该派给它」
    **唯一**的依据 —— 没有描述,模型要么永远不派给它,要么见什么都派给它。
  */
  const description = fmString(fm, 'description')
  if (description === undefined) {
    diagnostics.push({
      path: file,
      message: 'frontmatter 里缺 description —— 没有描述,模型无从判断什么时候该派它,这个子代理已作废'
    })
    return null
  }

  const prompt = clampWithEllipsis(stripControlChars(fm.body).trim(), AGENT_PROMPT_MAX)
  if (prompt === '') {
    diagnostics.push({ path: file, message: '正文(角色提示词)是空的,这个子代理已作废' })
    return null
  }

  const tools = resolveTools(fm, file, diagnostics)
  if (tools === 'invalid') return null

  const mode = resolvePermissionMode(fm, file, diagnostics)
  if (mode === 'invalid') return null

  const model = fmString(fm, 'model')
  /*
    ★ 只在写了 `model` 时才认 `modelProviderId`:单独一个供应商没有意义
    (别名都没定,钉住一家也选不出模型),而把它带上会让 `subagentModelSelection`
    收到一个「没别名却锁着家」的对 —— 那个形状在下游没有定义。
    这里也**不校验这家是否还在**:加载器够不着路由器,而校验在运行时那一侧
    (`declaredSubagentModel`),那里查不到就退回只按别名择优。
  */
  const modelProviderId = model === undefined ? undefined : fmString(fm, 'modelProviderId')

  /*
    ★ `thinking:` 读不懂就当没写,**不作废**(同 `color`):它只影响花多少钱和想多深,
    而上面那两个 `'invalid'` 分支各自都有非装饰的理由。诊断里必须说清「照哪一档跑」 ——
    只说「忽略了这一行」的话,用户改完文件也看不出子代理实际用的是哪一档。
    `inherit` 不是合法的文件取值:文件里「跟随」表达为**没有这一行**。
  */
  const rawThinking = fmString(fm, 'thinking')?.trim().toLowerCase()
  let thinking: ThinkingLevel | undefined
  if (rawThinking !== undefined) {
    if ((THINKING_LEVELS as readonly string[]).includes(rawThinking)) thinking = rawThinking as ThinkingLevel
    else {
      diagnostics.push({
        path: file,
        message:
          `thinking "${rawThinking}" 不是 ${THINKING_LEVELS.join(' / ')} 之一,已忽略这一行 —— ` +
          '这个子代理照「默认子代理思考深度」那一栏跑(该栏为「跟随对话」时再退回父 run 的档位)。'
      })
    }
  }

  /*
    ★ 颜色读不懂就当没写,**不作废**。它纯装饰,而上面那两个 `'invalid'` 分支
    各自都有非装饰的理由(空工具表会让子代理编答案;读错的权限档位会放宽权限)。
    为一条坏颜色让用户的子代理消失,代价完全不成比例。
  */
  const rawColor = fmString(fm, 'color')?.trim().toLowerCase()
  const color = rawColor !== undefined && isAgentColor(rawColor) ? rawColor : undefined

  return {
    name,
    description: clampWithEllipsis(stripControlChars(description), AGENT_DESCRIPTION_MAX),
    prompt,
    ...(tools !== undefined ? { tools } : {}),
    ...(model !== undefined ? { model: stripControlChars(model) } : {}),
    ...(modelProviderId !== undefined ? { modelProviderId: stripControlChars(modelProviderId) } : {}),
    ...(thinking !== undefined ? { thinking } : {}),
    ...(mode !== undefined ? { permissionMode: mode } : {}),
    ...(color !== undefined ? { color } : {}),
    source: { kind: scope, path: file }
  }
}

/**
 * `tools:` → 归一化后的 internalId 列表。
 *
 * 三种结局,各自的理由都不一样:
 * - **没写** → `undefined`,继承父 run 的全部工具(和 CC 一致)。
 * - **写了、但一个都认不出** → `'invalid'`,**整条作废**。
 *   ★ 这是这个文件里最重要的一条判断:放行的话,子代理会带着一张
 *   **空工具表**跑起来,然后自信地编一个答案交回去 —— 而这件事没有任何症状。
 *   宁可让它「不存在」(模型会当场看到一条清楚的错误),也不要让它「存在但是瞎的」。
 * - **写了、认出一部分** → 用认出来的那部分,认不出的记进诊断。
 */
function resolveTools(
  fm: ReturnType<typeof parseFrontmatter>,
  file: string,
  diagnostics: AgentDiagnostic[]
): string[] | undefined | 'invalid' {
  const raw = fmList(fm, 'tools')
  if (raw === undefined) return undefined

  const { tools, unknown } = normalizeToolList(raw)
  if (unknown.length > 0) {
    diagnostics.push({
      path: file,
      message: `tools 里有认不出的工具名:${unknown.join('、')}(已忽略)`
    })
  }

  if (tools.length === 0) {
    diagnostics.push({
      path: file,
      message:
        'tools 里一个认得出的工具都没有 —— 这个子代理已作废。' +
        '带着空工具表跑起来的子代理不会报错,它会编一个答案出来。' +
        '要让它继承全部工具的话,把 tools 这一行整个删掉。'
    })
    return 'invalid'
  }
  return tools
}

/**
 * `permissionMode:` → 档位。
 *
 * ★ 认不出的值**整条作废**,不是「忽略这个字段」。这是三个可选字段里
 * 唯一这么处理的,因为它是唯一一个**安全相关**的:忽略掉一个写错的
 * `permissionMode: readonly`,子代理就按父代理的档位跑 —— 那和作者的意图
 * 正好相反,而他不会知道。
 */
function resolvePermissionMode(
  fm: ReturnType<typeof parseFrontmatter>,
  file: string,
  diagnostics: AgentDiagnostic[]
): PermissionMode | undefined | 'invalid' {
  const raw = fmString(fm, 'permissionMode') ?? fmString(fm, 'permission-mode')
  if (raw === undefined) return undefined

  const lowered = raw.trim().toLowerCase()
  const hit = PERMISSION_MODES.find((m) => m === lowered)
  if (hit === undefined) {
    diagnostics.push({
      path: file,
      message:
        `permissionMode "${raw}" 不是 ${PERMISSION_MODES.join(' / ')} 之一 —— 这个子代理已作废。` +
        '这个字段是收窄权限用的,读不懂它就照父代理的档位跑,和你写它的本意正好相反。'
    })
    return 'invalid'
  }
  return hit
}

function msg(err: unknown): string {
  return err instanceof Error ? err.message : String(err)
}

export const AGENT_LIMITS = { AGENT_FILE_MAX_BYTES, MAX_AGENTS } as const
