import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import type { AgentDefinition } from '../../../../../shared/domain/agent-def'
import { MAX_DEPTH } from '../../../../../shared/agent/run-request'
import { isAbortError } from '../../../abort'
import { agentRegistry } from '../../../agent/registry'
import { GENERAL_PURPOSE } from '../../../agent/builtin'
import { nodeHost } from '../../../host'
import type { SubagentOutcome, SubagentRequest, ToolContext } from '../../registry'
import type { ToolRegistration } from '../../registry'
import { taskTool } from '../task'

/**
 * `Task` 工具的测试 —— 只测**这个工具自己**那一半。
 *
 * 它不建 run、不碰 store、不发事件,全部经由 `ctx.spawnSubagent` 这条窄缝
 * 交给 `runtime.ts` 的启动器。所以这里的 `spawnSubagent` 全是假的:
 * 真正的接线由 `subagent-wiring.test.ts` 端到端地钉。
 *
 * ★ 这个文件里权重最高的是**结局映射**那一组:同样是「子代理没跑成」,
 * 「这个名字不存在」「跑完了没说话」「被中断了」三种要给出**三种不同的东西**,
 * 因为模型下一步该做的事完全不同(换个名字 / 换个 prompt / 什么都别做)。
 */

function ctx(over: Partial<ToolContext> = {}): ToolContext {
  return {
    workspaceRoot: '/tmp/does-not-matter',
    signal: new AbortController().signal,
    permissionMode: 'auto',
    depth: 0,
    callId: 'call_1',
    runId: 'run_1',
    host: nodeHost(),
    emit: () => {},
    ...over
  }
}

/** 一个只会给出这个结局的假启动器,顺带把收到的请求录下来 */
function fakeSpawn(outcome: SubagentOutcome): {
  fn: (r: SubagentRequest) => Promise<SubagentOutcome>
  seen: SubagentRequest[]
} {
  const seen: SubagentRequest[] = []
  return {
    seen,
    fn: (r) => {
      seen.push(r)
      return Promise.resolve(outcome)
    }
  }
}

const finished = (over: Partial<Extract<SubagentOutcome, { kind: 'finished' }>> = {}) =>
  ({
    kind: 'finished',
    childRunId: 'run_1:sub:1',
    status: 'done',
    text: '我查过了,配置在 src/config.ts:12。',
    ...over
  }) as SubagentOutcome

const agent = (over: Partial<AgentDefinition> = {}): AgentDefinition => ({
  name: 'researcher',
  description: '只读调研,不改任何文件',
  prompt: '你只看,不改。',
  source: { kind: 'project', path: '/w/.nextcowork/agents/researcher.md' },
  ...over
})

/**
 * 造一个带**当前注册表内容**的 `Task`。
 *
 * ★ 显式把清单传进去,不靠默认参数:注册表按工作区分桶之后,`taskTool()`
 * 的默认值是内建那几条 —— 它不知道自己是为哪个工作区造的(见 `task.ts` 的说明)。
 */
const task = (): ToolRegistration => taskTool(agentRegistry('').list())

/** 装一批子代理进 `''` 那一桶(无头调用没有工作区身份)。 */
function install(...agents: AgentDefinition[]): void {
  agentRegistry('').replaceAll({ agents: [GENERAL_PURPOSE, ...agents], diagnostics: [] })
}

beforeEach(() => {
  install(agent({ tools: ['Read', 'Grep', 'Glob'] }))
})

afterEach(() => {
  // 注册表跨用例共享 —— 不还原的话,下一个文件里的测试会看见这里装的东西
  install()
})

describe('Task · 标记与形状', () => {
  /**
   * ★ 这条钉的是一道**白送的**闸门:plan 模式下
   * `snapshot({ readOnlyOnly: true })` 会直接把 Task 摘掉,于是计划模式
   * 里没法借子代理绕开只读围栏 —— 不用写一行代码。
   *
   * 标成只读是错的,哪怕子代理自己只用了读工具:它跑的是一整条 agent 循环。
   */
  it('★ readOnly: false —— plan 模式靠这个把它摘掉', () => {
    expect(task().readOnly).toBe(false)
  })

  it('不算破坏性:派出去这个动作本身不改任何东西,子代理里的工具会各自过闸门', () => {
    expect(task().destructive).toBe(false)
  })

  it('不需要联网 —— 联不联网由子代理里那些工具各自声明', () => {
    expect(task().needsNetwork).toBe(false)
  })

  /** ★ 核心参数名逐字照搬 CC,并额外支持后台执行开关 */
  it('★ 参数名逐字照搬 Claude Code,并支持后台执行', () => {
    const props = task().inputSchema.properties ?? {}

    expect(Object.keys(props)).toEqual(['description', 'prompt', 'subagent_type', 'run_in_background'])
  })
})

describe('Task · description 就是那份清单', () => {
  /**
   * ★ 模型判断「这活派给谁」唯一的依据就是 description 里那几行。
   * 所以它必须是**注册时现拼**的 —— 写死一份常量的话,用户新加的
   * agent 文件「存在、能派、但模型永远看不见它」。
   */
  it('★ 清单跟着注册表走,新装的子代理立刻出现在描述里', () => {
    expect(task().description).toContain('researcher')

    install(agent({ name: 'reviewer', description: '审代码' }))

    const d = task().description
    expect(d).toContain('reviewer')
    expect(d).toContain('审代码')
  })

  it('内建的那条永远在清单里', () => {
    expect(task().description).toContain('general-purpose')
  })

  it('把每个子代理能用的工具也列出来 —— 省略 tools 的写成 *', () => {
    const d = task().description

    expect(d).toContain('Read, Grep, Glob')
    expect(d).toContain('*')
  })

  /**
   * ★ 子代理**看不到当前对话**这件事必须写在工具描述里,不能只靠子代理
   * 自己的角色提示词。写 prompt 的是父代理,而父代理只读得到这里。
   */
  it('★ 描述里写明子代理看不到当前对话', () => {
    expect(task().description).toContain('CANNOT SEE THIS CONVERSATION')
  })

  it('描述里写明只有最后一条消息会回来 —— 否则父代理会去找中间过程', () => {
    expect(task().description).toMatch(/ONE MESSAGE|final/)
  })
})

describe('Task · 派不出去的时候', () => {
  it('★ 深度到顶 → 说清为什么、以及该改做什么(否则模型换个名字再试)', async () => {
    const r = await task().execute(
      { description: 'd', prompt: 'p', subagent_type: 'researcher' },
      ctx({ depth: MAX_DEPTH, spawnSubagent: fakeSpawn(finished()).fn })
    )

    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('nesting limit')
    expect(r.output.content).toContain('yourself')
  })

  it('深度到顶时压根不调启动器', async () => {
    const spawn = fakeSpawn(finished())

    await task().execute(
      { description: 'd', prompt: 'p', subagent_type: 'researcher' },
      ctx({ depth: MAX_DEPTH, spawnSubagent: spawn.fn })
    )

    expect(spawn.seen).toEqual([])
  })

  /**
   * ★ 没有这条缝时给一句人话,而不是让 `ctx.spawnSubagent!()` 抛 TypeError。
   * 纯内核测试、以及任何没接启动器的宿主里都会走到这一支。
   */
  it('★ 这个环境派不了子代理 → 一句人话,不是崩溃', async () => {
    const r = await task().execute(
      { description: 'd', prompt: 'p', subagent_type: 'researcher' },
      ctx()
    )

    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('cannot be launched')
  })

  /**
   * ★ 「还没派出去就被拒」的理由要**原样**转交,一个前缀都不加。
   *
   * 启动器给的已经是一段完整的人话(「没有名为 X 的子代理。当前可用:…」)。
   * 包一层「子代理 X 失败:」的话,模型会以为那个子代理存在但坏掉了,
   * 然后原样重试一次 —— 而正确的下一步是换一个名字。
   */
  it('★ refused 的理由原样转交,不加前缀', async () => {
    const reason = '没有名为 "reviewr" 的子代理。当前可用:general-purpose、researcher。'
    const r = await task().execute(
      { description: 'd', prompt: 'p', subagent_type: 'reviewr' },
      ctx({ spawnSubagent: fakeSpawn({ kind: 'refused', reason }).fn })
    )

    expect(r.isError).toBe(true)
    expect(r.output.content).toBe(reason)
    expect(r.output.content).not.toContain('failed')
  })
})

describe('Task · 三种结局映射成三种不同的东西', () => {
  it('done + 有文字 → 原样交回,不加任何包装', async () => {
    const r = await task().execute(
      { description: 'd', prompt: 'p', subagent_type: 'researcher' },
      ctx({ spawnSubagent: fakeSpawn(finished({ text: '结论:在 src/a.ts:3' })).fn })
    )

    expect(r.isError).toBeFalsy()
    expect(r.output.content).toBe('结论:在 src/a.ts:3')
  })

  /**
   * ★ 「跑完了但一个字都没说」要报**失败**,不能返回一个空的 toolOk。
   * 空结果被当成成功的话,父代理会认为「查过了,没有」并据此往下做 ——
   * 而真实情况是这次调查根本没发生。
   */
  it('★ done + 空文字 → 报失败,并说清下一步', async () => {
    const r = await task().execute(
      { description: 'd', prompt: 'p', subagent_type: 'researcher' },
      ctx({ spawnSubagent: fakeSpawn(finished({ text: '   \n  ' })).fn })
    )

    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('without producing any text')
    expect(r.output.content).toContain('Retry')
  })

  it('error → 带上子代理的名字和原始错误', async () => {
    const r = await task().execute(
      { description: 'd', prompt: 'p', subagent_type: 'researcher' },
      ctx({ spawnSubagent: fakeSpawn(finished({ status: 'error', error: '上游 429' })).fn })
    )

    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('researcher')
    expect(r.output.content).toContain('上游 429')
    expect(r.subagent).toMatchObject({
      childRunId: 'run_1:sub:1',
      status: 'error',
      error: { message: '上游 429' }
    })
  })

  /**
   * ★ 中断**原样抛出**,不包成 toolFail —— 这是 `define.ts` 的契约。
   *
   * 父被中断时会级联中断子,此时父自己也正在中断收尾。返回一个
   * 「正常的失败结果」会和 `finalizeAbort` 的孤儿 tool_result 补齐打架:
   * 同一个 callId 会被写两条结果。
   */
  it('★ aborted → 抛中断,不是返回 toolFail', async () => {
    const call = task().execute(
      { description: 'd', prompt: 'p', subagent_type: 'researcher' },
      ctx({ spawnSubagent: fakeSpawn(finished({ status: 'aborted', text: '' })).fn })
    )

    await expect(call).rejects.toSatisfy(isAbortError)
  })
})

describe('Task · 传给启动器的东西', () => {
  it('三个入参加上 callId 一起交过去 —— callId 是事件配对用的', async () => {
    const spawn = fakeSpawn(finished())

    await task().execute(
      { description: '查配置', prompt: '找出配置读取处', subagent_type: 'researcher' },
      ctx({ callId: 'call_42', spawnSubagent: spawn.fn })
    )

    expect(spawn.seen).toEqual([
      {
        subagentType: 'researcher',
        prompt: '找出配置读取处',
        description: '查配置',
        callId: 'call_42'
      }
    ])
  })

  it('run_in_background=true 透传后台标记，并把后台结局映射为成功', async () => {
    const spawn = fakeSpawn({ kind: 'background', childRunId: 'run_1:sub:1' })

    const r = await task().execute(
      {
        description: '查配置',
        prompt: '找出配置读取处',
        subagent_type: 'researcher',
        run_in_background: true
      },
      ctx({ callId: 'call_bg', spawnSubagent: spawn.fn })
    )

    expect(r.isError).toBe(false)
    expect(r.output.content).toContain('started in the background')
    expect(spawn.seen).toEqual([{
      subagentType: 'researcher',
      prompt: '找出配置读取处',
      description: '查配置',
      callId: 'call_bg',
      background: true
    }])
  })

  it('★ 前台派出、被用户转去后台:成功,说清是用户转的、别再派一次,回执标成后台在跑', async () => {
    const spawn = fakeSpawn({ kind: 'background', childRunId: 'run_1:sub:2', detached: true })

    const r = await task().execute(
      { description: '迁移数据层', prompt: '把数据层迁到 Dapper.AOT', subagent_type: 'researcher' },
      ctx({ callId: 'call_fg', spawnSubagent: spawn.fn })
    )

    expect(r.isError).toBe(false)
    expect(r.output.content).toContain('The user moved this subagent to the background')
    expect(r.output.content).toContain('do NOT launch it again')
    expect(r.subagent).toMatchObject({ childRunId: 'run_1:sub:2', status: 'running', background: true })
  })

  it('★ 名字不做任何纠正,原样递过去 —— 回落到 general-purpose 是最坏的失败', async () => {
    const spawn = fakeSpawn({ kind: 'refused', reason: '没有名为 "RESEARCHER" 的子代理。' })

    await task().execute(
      { description: 'd', prompt: 'p', subagent_type: 'RESEARCHER' },
      ctx({ spawnSubagent: spawn.fn })
    )

    expect(spawn.seen[0]?.subagentType).toBe('RESEARCHER')
  })

  it('参数不合法时在 schema 那一层就被挡下,不会派出去', async () => {
    const spawn = fakeSpawn(finished())

    const r = await task().execute(
      { description: 'd', prompt: '', subagent_type: 'researcher' },
      ctx({ spawnSubagent: spawn.fn })
    )

    expect(r.isError).toBe(true)
    expect(spawn.seen).toEqual([])
  })
})
