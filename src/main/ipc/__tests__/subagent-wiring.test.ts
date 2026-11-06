/**
 * 子代理的**接线**验收 —— 从父代理调 `Task` 一路到子 run 的事件落在父窗口上。
 *
 * ## 为什么这份测试必须存在,而且必须断言「信封」
 *
 * 子 run 有自己的 runId,于是有自己的事件 topic。`RunPump.flush()` 在
 * `!windows.hasSubscribers(topic)` 时**整批丢弃**事件 —— 所以订阅继承
 * (`windows.inherit`)没接上的表现是:**不报错、子 run 正常跑完、
 * 父代理拿到正确的结果,而界面上什么都不发生**。
 *
 * 只断言「子 run 的 status 是 done」的话,这条线整个断掉也照样全绿。
 * 所以这里断言的是**父窗口收到的信封里存在 `runId === childRunId` 的那一批**,
 * 并且配了一条反向用例:不装启动器时,那批信封**不存在**。
 * 两条合起来,才证明是 `windows.inherit` 让它工作的。
 *
 * ## 上游是自己写的,不是内置演示上游
 *
 * 演示上游挑的是 `tools[0]`(那是 `echo`,顺序有意义,见 `builtin/index.test.ts`),
 * 没法让它去调 `Task`。所以这里自己拼 SSE —— 但用的是 `renderDemoSse`,
 * 即和演示上游**同一个渲染器**:分片、事件序列、解析器全都是生产路径那份。
 */
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { nodeHost } from '../../kernel/host'
import type { WebContents } from 'electron'
import type { AgentEvent } from '../../../shared/agent/event'
import type { RunRequest } from '../../../shared/agent/run-request'
import { LONG_CONTEXT_THRESHOLD } from '../../../shared/agent/context-management'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'
import type { AgentEventEnvelope } from '../../../shared/ipc/contract'
import { runs } from '../../kernel/run-registry'
import {
  DEMO_ALIAS,
  DEMO_ALIASES,
  DEMO_PROVIDER,
  DEMO_PROVIDER_ID,
  demoHost,
  renderDemoSse
} from '../../kernel/upstream/demo'
import {
  ensureSeeded,
  installChildRunLauncher,
  installHost,
  resetRuntimeForTest
} from '../../runtime'
import { store } from '../../state/store'
import type { WindowContext } from '../../window/registry'
import { startChildRun, startRun } from '../agent'

/** 只出现在**交给子代理的 prompt** 里的记号 —— 假上游靠它区分父子两条 run */
const CHILD_MARK = 'SUBTASK-MARK'
const CHILD_REPORT = '子代理报告:配置读取在 src/config.ts:12。'
/** 演示上游底下的第二条别名 —— 「默认子代理」那一栏指的那个,好和父 run 分得开 */
const SUB_ALIAS = 'nextcowork-demo-fast'

/** 假上游要派给谁。用例里改它,就等于改模型填进 `subagent_type` 的那个字符串。 */
let subagentType = 'general-purpose'

/**
 * 下发给**子 run** 的那张工具表(外部名)。假上游在子 run 的第一次请求里记下来。
 *
 * 这是「子代理不问人」那一半唯一能直接观测到的地方 —— 快照是在
 * `agent-session.ts` 每轮开头现取的,拿不到对象,只能看它发出去的请求体。
 */
let childTools: string[] = []

/** 下发给**父 run** 的那张工具表。和 `childTools` 成对,用来证明收窄的只有 depth > 0。 */
let parentTools: string[] = []

/**
 * 让子代理在交报告之前先调一次这个工具(外部名,大小写不敏感);`null` 表示不调。
 * 用来把子 run 推到权限闸门跟前 —— `Bash` 在 `auto` 档是 destructive,判定为 `ask`。
 */
let childCalls: string | null = null

class FakeWebContents {
  readonly sent: Array<{ channel: string; payload: unknown }> = []
  constructor(readonly id: number) {}
  once(): void {}
  isDestroyed(): boolean {
    return false
  }
  send(channel: string, payload: unknown): void {
    this.sent.push({ channel, payload })
  }

  /**
   * WindowRegistry 判「帧还活不活」靠的是这里,不是 send 抛不抛 ——
   * 真实的 `webFrameMain.send` 自己把异常吞了(见 window/registry.ts)。
   */
  get mainFrame(): { isDestroyed: () => boolean; detached: boolean; send: (channel: string, payload: unknown) => void } {
    return {
      isDestroyed: () => this.isDestroyed(),
      detached: false,
      send: (channel, payload) => this.send(channel, payload)
    }
  }
  envelopes(): AgentEventEnvelope[] {
    return this.sent
      .filter((m) => m.channel === 'agent:event')
      .map((m) => m.payload as AgentEventEnvelope)
  }
}

let nextId = 900
function fakeWindow(): { wc: FakeWebContents; ctx: WindowContext } {
  const wc = new FakeWebContents(nextId++)
  return { wc, ctx: { id: wc.id, kind: 'main', sender: wc as unknown as WebContents } }
}

interface UpstreamBody {
  model?: string
  messages?: Array<{ role: string; content?: Array<Record<string, unknown>> }>
  tools?: Array<{ name: string }>
}

/**
 * 一个只会做一件事的假上游:父代理调一次 `Task`,子代理直接交报告。
 *
 * ★ 父子共用同一个 fetch(它们本来就跑在同一个 host 上),靠 prompt 里的
 * 记号区分 —— 这也顺带钉住了「子代理拿到的是我们写进 prompt 的那段文字」。
 */
function fakeUpstream(): typeof fetch {
  let seq = 0
  return (_input, init) => {
    seq += 1
    const raw = typeof init?.body === 'string' ? init.body : '{}'
    const body = JSON.parse(raw) as UpstreamBody
    const messages = body.messages ?? []
    const model = body.model ?? DEMO_ALIAS

    const texts = messages.flatMap((m) =>
      (m.content ?? [])
        .filter((b) => b.type === 'text')
        .map((b) => (typeof b.text === 'string' ? b.text : ''))
    )
    const isChild = texts.some((t) => t.includes(CHILD_MARK))
    // 每次请求都记下这一轮下发的工具表 —— 父子各一张,「子代理不问人」那组要对着看
    if (isChild) childTools = (body.tools ?? []).map((t) => t.name)
    else parentTools = (body.tools ?? []).map((t) => t.name)

    // 子代理那条 run
    if (isChild) {
      const answered = (messages[messages.length - 1]?.content ?? []).find((b) => b.type === 'tool_result')
      if (answered !== undefined) {
        /*
          ★ 工具回话**原样带进报告**。子 run 的 `tool_end` 事件要经 topic 继承
          才到得了窗口,而这条路本身就是别的用例在钉的东西;把它抄进文本,
          断言就只依赖「子代理确实收到了一个结算」,与订阅接线解耦。
        */
        const said = typeof answered.content === 'string' ? answered.content : JSON.stringify(answered.content)
        return Promise.resolve(sse({ blocks: [{ kind: 'text', text: `${CHILD_REPORT}\n工具回话:${said}` }] }, model))
      }
      if (childCalls !== null) {
        const want = childCalls.toLowerCase()
        const called = (body.tools ?? []).map((t) => t.name).find((n) => n.toLowerCase() === want)
        if (called === undefined) throw new Error(`子 run 的工具表里没有 ${childCalls}`)
        return Promise.resolve(
          sse(
            {
              blocks: [{ kind: 'tool_use', id: `child_${String(seq)}`, name: called, input: { command: 'echo hi' } }],
              stopReason: 'tool_use'
            },
            model
          )
        )
      }
      // 不调任何工具:直接交一份报告
      return Promise.resolve(sse({ blocks: [{ kind: 'text', text: CHILD_REPORT }] }, model))
    }

    const last = messages[messages.length - 1]
    const result = (last?.content ?? []).find((b) => b.type === 'tool_result')
    if (result !== undefined) {
      const got = typeof result.content === 'string' ? result.content : ''
      return Promise.resolve(sse({ blocks: [{ kind: 'text', text: `父代理收工:${got}` }] }, model))
    }

    // ★ 按**外部名**调用 —— 名字映射是会话内的事,测试不该自己猜
    const name = (body.tools ?? []).map((t) => t.name).find((n) => n.toLowerCase() === 'task')
    if (name === undefined) throw new Error('工具表里没有 Task —— 接线在更靠前的地方就断了')

    return Promise.resolve(
      sse(
        {
          blocks: [
            { kind: 'text', text: '这活我派个子代理去做。' },
            {
              kind: 'tool_use',
              id: `toolu_${String(seq)}`,
              name,
              input: {
                description: '查配置读取处',
                prompt: `${CHILD_MARK} 找出这个仓库里读配置的地方`,
                subagent_type: subagentType
              }
            }
          ],
          stopReason: 'tool_use'
        },
        model
      )
    )
  }
}

function sse(
  reply: {
    blocks: Array<
      | { kind: 'text'; text: string }
      | { kind: 'tool_use'; id: string; name: string; input: Record<string, unknown> }
    >
    stopReason?: 'end_turn' | 'tool_use'
  },
  model: string
): Response {
  const text = renderDemoSse(
    { blocks: reply.blocks, stopReason: reply.stopReason ?? 'end_turn' },
    model,
    20
  )
  return new Response(text, {
    status: 200,
    headers: { 'content-type': 'text/event-stream', 'cache-control': 'no-cache' }
  })
}

let runSeq = 0
const PARENT_SESSION = 'parent-session'

const req = (over: Partial<RunRequest> = {}): RunRequest => ({
  runId: `sub-wire-${++runSeq}`,
  sessionId: PARENT_SESSION,
  workspaceId: 'w1',
  depth: 0,
  input: [{ type: 'text', text: '帮我查一下配置在哪里读的' }],
  mode: 'normal',
  thinking: 'off',
  webSearch: false,
  // ★ `auto` 档:Task 的 destructive 是 false,于是闸门放行。
  //   `ask` 档下写与执行一律被拒,那是另一个文件的事。
  permissionMode: 'auto',
  model: DEMO_ALIAS,
  skillIds: [],
  ...over
})

async function waitForEnd(runId: string, timeoutMs = 15_000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  for (;;) {
    const h = runs.get(runId)
    if (h === undefined || h.status !== 'running') return
    if (Date.now() > deadline) throw new Error(`run ${runId} 在 ${String(timeoutMs)}ms 内没有结束`)
    await new Promise((r) => setTimeout(r, 5))
  }
}

const allEvents = (wc: FakeWebContents): AgentEvent[] => wc.envelopes().flatMap((e) => e.events)

beforeEach(() => {
  subagentType = 'general-purpose'
  childTools = []
  parentTools = []
  childCalls = null
  resetRuntimeForTest()
  store.putWorkspace({ id: 'w1', name: 'Local test workspace', rootPath: '', environment: { kind: 'local' }, settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1 })
  store.ensureSession({ id: PARENT_SESSION, workspaceId: 'w1', rootPathAtCreation: '' })
  store.setHistory(PARENT_SESSION, [])
  installHost(demoHost({ fetch: fakeUpstream() }, { chunkDelayMs: 0 }))
  // ★ `seed()` 不再种演示上游(理由见 runtime.ts 的 seed 文件头),而这份测试
  // 全程用 DEMO_ALIAS 发 run —— 自己种两行,机器本身一点没变
  store.putProvider(DEMO_PROVIDER)
  for (const alias of DEMO_ALIASES) store.putAlias(alias)
  /*
    ★ `seed()` 会给全新安装种一个「默认子代理」(内置上游的 flash),而它**排在
    父 run 前面**(见 `subagentModelSelection`)。这份测试断言的是**继承**那一档,
    所以要把那一栏清成「跟随对话」—— 不清的话每条用例都在测种子,而不是接线。

    ★★ 顺序不能反:`seed()` 是**懒的**(第一次 `getRouter()` 才跑),而它只在
    这一栏为空时才种。先清后种的话种子照样落回来,清了等于没清。
    `ensureSeeded()` 就是为「先把种子跑完」准备的那个入口。
  */
  ensureSeeded()
  store.updateSettings({ subagent: { model: '', modelProviderId: undefined, thinking: 'inherit' } })
})

afterEach(() => {
  runs.abortAll()
  resetRuntimeForTest()
})

describe('父代理派子代理 · 全链路', () => {
  it.skipIf(process.platform === 'win32' || !existsSync('/bin/bash'))('SubagentStop 钩子沿用子任务的 Shell，不跟随中途改动的全局选择', async () => {
    const root = mkdtempSync(join(tmpdir(), 'ncw-subagent-shell-'))
    try {
      mkdirSync(join(root, '.next-cowork'))
      writeFileSync(join(root, '.next-cowork', 'settings.local.json'), JSON.stringify({
        version: 1,
        hooks: { SubagentStop: [{ id: 'shell-probe', command: 'printf "%s" "$0" > hook-shell.txt' }] }
      }))
      store.putWorkspace({ ...store.getWorkspace('w1')!, rootPath: root })
      let selected = '/bin/sh'
      const upstream = demoHost({ fetch: fakeUpstream() }, { chunkDelayMs: 0 })
      installHost(nodeHost({
        paths: { userData: () => join(root, 'userData'), attachments: () => join(root, 'attachments'), temp: () => root },
        secrets: upstream.secrets,
        fetch: (input, init) => {
          if (String(init?.body ?? '').includes(CHILD_MARK)) selected = '/bin/bash'
          return upstream.fetch(input, init)
        }
      }, () => selected))
      installChildRunLauncher(startChildRun)
      const r = req()
      startRun(r, fakeWindow().ctx)
      await waitForEnd(r.runId)
      expect(runs.get(r.runId)?.status).toBe('done')
      expect(selected).toBe('/bin/bash')
      const marker = join(root, 'hook-shell.txt')
      const deadline = Date.now() + 3000
      while ((!existsSync(marker) || readFileSync(marker, 'utf8') === '') && Date.now() < deadline) {
        await new Promise((resolve) => setTimeout(resolve, 10))
      }
      expect(readFileSync(marker, 'utf8')).toBe('/bin/sh')
    } finally {
      runs.abortAll()
      rmSync(root, { recursive: true, force: true })
    }
  })

  it('★ 子 run 的事件落在父窗口上 —— 断言信封的 runId,不是子 run 的 status', async () => {
    installChildRunLauncher(startChildRun)
    const { wc, ctx } = fakeWindow()
    const r = req()

    startRun(r, ctx)
    await waitForEnd(r.runId)

    const started = allEvents(wc).find((e) => e.type === 'subagent_start')
    expect(started).toBeDefined()
    const childRunId = started?.type === 'subagent_start' ? started.childRunId : ''
    expect(childRunId).not.toBe('')

    // ★ 这一行就是这份文件存在的理由
    const childEnvelopes = wc.envelopes().filter((e) => e.runId === childRunId)
    expect(childEnvelopes.length).toBeGreaterThan(0)
  })

  it('★ 不装启动器时那批信封就不存在 —— 证明是 windows.inherit 让它工作的', async () => {
    // 刻意**不**调 installChildRunLauncher:走 runtime 里那个降级分支
    const { wc, ctx } = fakeWindow()
    const r = req()

    startRun(r, ctx)
    await waitForEnd(r.runId)

    const started = allEvents(wc).find((e) => e.type === 'subagent_start')
    const childRunId = started?.type === 'subagent_start' ? started.childRunId : ''
    expect(childRunId).not.toBe('')

    expect(wc.envelopes().filter((e) => e.runId === childRunId)).toHaveLength(0)
    // 但子 run 本身是跑了的 —— 父代理照样拿到了结果
    expect(runs.get(r.runId)?.status).toBe('done')
  })

  it('subagent_start / subagent_end 成对出现,并且带同一个 callId 和 childRunId', async () => {
    installChildRunLauncher(startChildRun)
    const { wc, ctx } = fakeWindow()
    const r = req()

    startRun(r, ctx)
    await waitForEnd(r.runId)

    const evs = allEvents(wc)
    const s = evs.find((e) => e.type === 'subagent_start')
    const e = evs.find((x) => x.type === 'subagent_end')
    expect(s?.type).toBe('subagent_start')
    expect(e?.type).toBe('subagent_end')
    if (s?.type !== 'subagent_start' || e?.type !== 'subagent_end') return
    expect(e.callId).toBe(s.callId)
    expect(e.childRunId).toBe(s.childRunId)
    expect(e.status).toBe('done')
  })

  it('子代理的产出变成 tool_result,父代理据此收工', async () => {
    installChildRunLauncher(startChildRun)
    const { wc, ctx } = fakeWindow()
    const r = req()

    startRun(r, ctx)
    await waitForEnd(r.runId)

    // 落地的文字看 message_commit —— 那是最终归一化之后的那份
    const committed = JSON.stringify(allEvents(wc).filter((e) => e.type === 'message_commit'))
    expect(committed).toContain('父代理收工')
    expect(committed).toContain(CHILD_REPORT)

    // 子代理的产出确实是经 tool_result 回来的,不是父代理自己编的
    const ended = allEvents(wc).find((e) => e.type === 'tool_end')
    expect(ended?.type === 'tool_end' ? ended.isError : true).toBe(false)
    expect(JSON.stringify(ended)).toContain(CHILD_REPORT)
  })
})

describe('父子两条转录互不相干', () => {
  /**
   * ★ 子 run 用的是**派生**的 sessionId(`<父>:sub:<childRunId>`)。
   *
   * 复用父的会被 `store.setHistory` 整数组覆盖直接毁掉:父子并发跑完,
   * 谁后 finally 谁赢,父的转录凭空少几轮。
   */
  it('★ 子 run 结束后,父转录里仍然有父自己的消息', async () => {
    installChildRunLauncher(startChildRun)
    const { ctx } = fakeWindow()
    const r = req()

    startRun(r, ctx)
    await waitForEnd(r.runId)

    const parent = store.getHistory(PARENT_SESSION)
    expect(parent.length).toBeGreaterThan(0)
    expect(parent.some((m) => m.role === 'user')).toBe(true)
    // 父的转录里有它自己那次工具调用,而不是被子代理那一轮顶掉
    expect(JSON.stringify(parent)).toContain('派个子代理')
  })

  it('子 run 的转录落在自己的 sessionId 下,是一份全新的上下文', async () => {
    installChildRunLauncher(startChildRun)
    const { wc, ctx } = fakeWindow()
    const r = req()

    startRun(r, ctx)
    await waitForEnd(r.runId)

    const started = allEvents(wc).find((e) => e.type === 'subagent_start')
    const childRunId = started?.type === 'subagent_start' ? started.childRunId : ''
    const childSessionId = `${PARENT_SESSION}:sub:${childRunId}`
    const child = store.getHistory(childSessionId)

    expect(child.length).toBeGreaterThan(0)
    // ★ 子代理**看不到父对话**:父那句原话一个字都不在它的窗口里
    expect(JSON.stringify(child)).not.toContain('帮我查一下配置在哪里读的')
    expect(JSON.stringify(child)).toContain(CHILD_MARK)

    /*
      ★ 派生的 id 只是惯例,`parent_session_id` 才是那条转录「不进侧边栏」的
      **事实依据** —— 过滤和级联删除全读这一列,没有一处再去 parse 上面那个
      `:sub:`(理由见 `db/schema.ts` 第 10 条迁移)。这一条钉住的是接线:
      少了它,子会话又会以「新对话」的名义出现在最近对话里。
    */
    expect(store.getSession(childSessionId)?.parentSessionId).toBe(PARENT_SESSION)
    expect(store.listSessions(r.workspaceId).map((s) => s.id)).not.toContain(childSessionId)
  })
})

describe('派不出去的时候', () => {
  /**
   * ★ `subagent_type` 认不出时**绝不静默回落**到 general-purpose。
   *
   * 回落是所有失败模式里最坏的一个:名字敲错的用户会拿到一份「看起来对」的、
   * 由错误代理产出的结果,而且永远不会发现。所以这里同时断言两件事 ——
   * 拒绝的理由里带着可用清单(模型下一步能选对),以及**一条子 run 都没建**。
   */
  it('★ 名字认不出 → 报错并列出可用清单,而不是回落到 general-purpose', async () => {
    installChildRunLauncher(startChildRun)
    subagentType = 'reviewr'
    const { wc, ctx } = fakeWindow()
    const r = req()

    startRun(r, ctx)
    await waitForEnd(r.runId)

    const ended = allEvents(wc).find((e) => e.type === 'tool_end')
    expect(ended?.type === 'tool_end' ? ended.isError : false).toBe(true)
    const said = JSON.stringify(ended)
    expect(said).toContain('reviewr')
    expect(said).toContain('general-purpose') // 清单在,模型下一次能选对
    expect(said).not.toContain(CHILD_REPORT) // 没有任何子代理真的跑过

    // 连 subagent_start 都不该发 —— 界面上不该出现一个转瞬即逝的空节点
    expect(allEvents(wc).some((e) => e.type === 'subagent_start')).toBe(false)
    expect(runs.get(r.runId)?.children.size ?? 0).toBe(0)
  })
})

/**
 * ★★ 别名和供应商必须**成对**决定。
 *
 * `agents/<name>.md` 的 frontmatter 只写得下一个裸别名、没有供应商的概念,
 * 所以子代理声明了别名时,供应商就该是「没指定」(按优先级择优),
 * 而**不是**继承父亲锁的那家。写成两条独立的 `??` 就会拼出
 * 「A 家的别名 + B 家的锁」—— 候选集返回空,然后报一条指着 B 的错,
 * 而 B 跟这次调用根本没关系,无从排查。
 */
describe('子代理继承模型时的别名/供应商配对', () => {
  /** 装一个探针启动器:先记下子 RunRequest,再照常交给真启动器 */
  const captureChildReq = (): RunRequest[] => {
    const seen: RunRequest[] = []
    installChildRunLauncher((parent, childReq, driver) => {
      seen.push(childReq)
      return startChildRun(parent, childReq, driver)
    })
    return seen
  }

  it('子代理没声明模型时,连供应商一起继承父亲的', async () => {
    const seen = captureChildReq()
    const r = req({ modelProviderId: DEMO_PROVIDER.id })

    startRun(r, fakeWindow().ctx)
    await waitForEnd(r.runId)

    expect(seen[0]).toMatchObject({ model: DEMO_ALIAS, modelProviderId: DEMO_PROVIDER.id })
  })

  it('父亲没锁供应商时子代理也没有 —— 不凭空多出一个锁', async () => {
    const seen = captureChildReq()
    const r = req()

    startRun(r, fakeWindow().ctx)
    await waitForEnd(r.runId)

    expect(seen[0]?.model).toBe(DEMO_ALIAS)
    expect(seen[0]?.modelProviderId).toBeUndefined()
  })

  /**
   * ★★ 设置页那一栏**真的会落到子 run 上**。
   *
   * 它以前是死配置:界面能选、能存,而 `childRequestFor` 只在「子代理自己声明的」
   * 和「父 run 的」之间二选一,那一栏一次都没被读过 —— 用户把子代理配成便宜模型,
   * 账单照着主力模型走,而且没有任何迹象。这条用例就是钉住它不再退化回去。
   */
  it('★ 设置里配了「默认子代理」时,子 run 用它 —— 别名和供应商成对', async () => {
    store.putAlias({ ...DEMO_ALIASES[0]!, alias: SUB_ALIAS })
    store.updateSettings({ subagent: { model: SUB_ALIAS, modelProviderId: DEMO_PROVIDER_ID } })
    const seen = captureChildReq()
    const r = req({ modelProviderId: DEMO_PROVIDER_ID })

    startRun(r, fakeWindow().ctx)
    await waitForEnd(r.runId)

    // 父 run 用的是 DEMO_ALIAS,子 run 必须换成设置里那个
    expect(seen[0]).toMatchObject({ model: SUB_ALIAS, modelProviderId: DEMO_PROVIDER_ID })
  })

  it('设置里那一对悬空时退回父 run —— 而不是拿一个查不到的别名去发请求', async () => {
    store.updateSettings({ subagent: { model: 'ghost-model', modelProviderId: 'no-such-provider' } })
    const seen = captureChildReq()
    const r = req({ modelProviderId: DEMO_PROVIDER_ID })

    startRun(r, fakeWindow().ctx)
    await waitForEnd(r.runId)

    expect(seen[0]).toMatchObject({ model: DEMO_ALIAS, modelProviderId: DEMO_PROVIDER_ID })
  })
})

/**
 * ★★ 「子代理思考深度」那一栏 —— 和上面模型那组是**同一件事的两半**:
 * 三档来源(声明的 > 设置里那一栏 > 父 run)必须真的接到子 run 上,
 * 而且落地前要按子 run 自己的模型归一化一次(见 `runtime.ts` 的 `childThinkingFor`)。
 */
describe('子代理继承思考深度', () => {
  const captureChildReq = (): RunRequest[] => {
    const seen: RunRequest[] = []
    installChildRunLauncher((parent, childReq, driver) => {
      seen.push(childReq)
      return startChildRun(parent, childReq, driver)
    })
    return seen
  }

  it('设置里那一栏配了档位时,子 run 用它', async () => {
    store.updateSettings({ subagent: { thinking: 'high' } })
    const seen = captureChildReq()
    const r = req({ thinking: 'low' })

    startRun(r, fakeWindow().ctx)
    await waitForEnd(r.runId)

    expect(seen[0]?.thinking).toBe('high')
  })

  it('默认(「跟随对话」)时,子 run 拿到父 run 这一轮的档位', async () => {
    const seen = captureChildReq()
    const r = req({ thinking: 'low' })

    startRun(r, fakeWindow().ctx)
    await waitForEnd(r.runId)

    expect(seen[0]?.thinking).toBe('low')
  })

  it('★ 归一化:子代理的模型不支持这一档时静默降级,不是把它原样发出去', async () => {
    // 演示别名默认 capabilities.thinking:true、没有 thinkingConfig —— 对它
    // `modelThinkingLevels` 会放行全档,测不出归一化。这里另造一条只认
    // low/medium 的别名,专门触发降级。
    const NARROW_ALIAS = 'nextcowork-demo-narrow-thinking'
    store.putAlias({
      ...DEMO_ALIASES[0]!,
      alias: NARROW_ALIAS,
      thinkingConfig: { mode: 'effort', defaultEnabled: true },
      reasoningEfforts: ['low', 'medium']
    })
    store.updateSettings({
      subagent: { model: NARROW_ALIAS, modelProviderId: DEMO_PROVIDER_ID, thinking: 'high' }
    })
    const seen = captureChildReq()
    const r = req({ modelProviderId: DEMO_PROVIDER_ID, thinking: 'low' })

    startRun(r, fakeWindow().ctx)
    await waitForEnd(r.runId)

    expect(seen[0]?.model).toBe(NARROW_ALIAS)
    expect(seen[0]?.thinking).toBe('auto')
  })
})


/**
 * ★ 「最大上下文」开关的**继承**。
 *
 * 这个开关的语义是**花钱**,不是解锁容量:开着就意味着这一轮可以越过
 * `LONG_CONTEXT_THRESHOLD`(272K)那条计费分界,输入按双倍算。于是两个方向
 * 各挡着一种真实的坏结果 ——
 *
 * - **不继承「开」**:用户明确开了,子代理却仍按 272K 压缩,长活儿在子代理里
 *   被腰斩,而界面上那颗药丸显示的是"开着"。
 * - **不继承「关」**:子代理替用户决定多花一倍的钱,账单上没有任何一处能追到
 *   是哪个子代理花的。
 *
 * 第二条用例走到 `context_usage` 才停,因为「字段传过去了」和「分母真的变了」
 * 是两件事 —— 中间还隔着 `effectiveContextWindow` 那一跳。
 */
describe('子代理继承「最大上下文」开关', () => {
  /** 装一个探针启动器:先记下子 RunRequest,再照常交给真启动器 */
  const captureChildReq = (): RunRequest[] => {
    const seen: RunRequest[] = []
    installChildRunLauncher((parent, childReq, driver) => {
      seen.push(childReq)
      return startChildRun(parent, childReq, driver)
    })
    return seen
  }

  it('开着 / 关着都原样传下去,父亲没给时不凭空补一个 false', async () => {
    const seen = captureChildReq()

    const on = req({ maxContext: true })
    startRun(on, fakeWindow().ctx)
    await waitForEnd(on.runId)
    expect(seen[0]?.maxContext).toBe(true)

    const off = req({ maxContext: false })
    startRun(off, fakeWindow().ctx)
    await waitForEnd(off.runId)
    expect(seen[1]?.maxContext).toBe(false)

    /*
      ★ 旧队列条目 / 旧 run 的 RunRequest 里没有这一项。补成 `false` 看着无害,
      实际上把"没表过态"和"明确关掉"抹成了同一个值 —— 以后想给这一档换默认值
      就再也分不出来了。所以这里断言的是**键不存在**,不是值为 false。
    */
    const legacy = req()
    startRun(legacy, fakeWindow().ctx)
    await waitForEnd(legacy.runId)
    expect(seen[2]).not.toHaveProperty('maxContext')
  })

  it('★ 分母真的跟着变 —— 子 run 的 context_usage 按有效窗口发', async () => {
    // 演示别名本来是 200K(< 272K),那个区间里开关没有可观测的效果
    store.putAlias({ ...DEMO_ALIASES[0]!, contextWindow: 1_050_000 })
    installChildRunLauncher(startChildRun)

    const childWindow = async (maxContext: boolean): Promise<number | undefined> => {
      const { wc, ctx } = fakeWindow()
      const r = req({ maxContext })
      startRun(r, ctx)
      await waitForEnd(r.runId)
      const started = allEvents(wc).find((e) => e.type === 'subagent_start')
      const childRunId = started?.type === 'subagent_start' ? started.childRunId : ''
      expect(childRunId).not.toBe('')
      const usage = wc
        .envelopes()
        .filter((e) => e.runId === childRunId)
        .flatMap((e) => e.events)
        .find((e) => e.type === 'context_usage')
      return usage?.type === 'context_usage' ? usage.window : undefined
    }

    expect(await childWindow(true)).toBe(1_050_000)
    expect(await childWindow(false)).toBe(LONG_CONTEXT_THRESHOLD)
  })
})

/**
 * ★★ 「子代理不问人」—— 两道闸,合起来才堵得住。
 *
 * `Task` 的工具描述里写着 *A SUBAGENT CANNOT ASK THE USER ANYTHING*,但机制一直
 * 没跟上。子代理有两条路能走到 `InteractionGate.request()`,而那个 Promise
 * **只在 `answer()` 或 abort 时结算**:
 *
 * 1. 它自己调 `AskUserQuestion`(快照没收窄,默认子代理拿得到);
 * 2. 它调一个落在 `ask` 档的工具,`approveWith` 发起 `tool_permission` 审批。
 *
 * 两条都通向同一个结果,因为子代理的待决项在界面上**根本走不到用户面前**:
 * `InteractionPanel` 挂在父 run 的 `activeRunId` 上,父 run 一结束就卸载;
 * `listInteractions` 在父 handle 回收后返回空数组;`applyChildEvent` 又把
 * `interaction_request` 当未知事件丢掉。于是那次 await 永远不结算 ——
 * 用户看到的就是那张跑满一小时、六百多次工具调用、里面什么也没发生的卡片。
 *
 * 所以两条用例都不是在测文案,是在测**「会不会结算」**。卡死的回归长这样:
 * 用例不报错,只是永远不返回,最后死在 vitest 的超时上。
 */
describe('子代理不问人', () => {
  it('★ 子 run 的工具表里没有 AskUserQuestion —— 主动提问那条路在快照层就没了', async () => {
    installChildRunLauncher(startChildRun)
    const { ctx } = fakeWindow()
    const r = req()

    startRun(r, ctx)
    await waitForEnd(r.runId)

    // 先确认这张表真的抓到了,否则下面那条 not.toContain 对着空数组也会绿
    expect(childTools.length).toBeGreaterThan(0)
    expect(childTools.map((n) => n.toLowerCase())).not.toContain('askuserquestion')
    // 只摘交互工具,别的一个不少 —— 子代理照样是个能干活的代理
    expect(childTools.map((n) => n.toLowerCase())).toContain('read')
  })

  /**
   * ★ 配对的反面用例。少了它,把 `noInteraction` 写成常量 `true`
   * (主代理也问不了人了)照样全绿 —— 那是一次沉默得多的退化。
   */
  it('父 run 的工具表里 AskUserQuestion 照常在 —— 收窄的只有 depth > 0', async () => {
    installChildRunLauncher(startChildRun)
    const r = req()

    startRun(r, fakeWindow().ctx)
    await waitForEnd(r.runId)

    expect(parentTools.map((n) => n.toLowerCase())).toContain('askuserquestion')
    expect(childTools.map((n) => n.toLowerCase())).not.toContain('askuserquestion')
  })

  /**
   * ★★★ 这条是整组里最重要的一条:**`ask` 档的工具当场被拒,而不是挂起**。
   *
   * `Bash` 在 `auto` 档是 destructive,`evaluate()` 判 `ask` —— 也就是原来会走到
   * `interactions.request` 的那条路。父 run 留在 `auto` 是有意的:`Task` 的
   * destructive 是 false,父自己那次调用照常放行,于是这条用例里唯一撞到闸门的
   * 就是子代理那一次。
   *
   * 断言的是**结算**:run 在 `waitForEnd` 的窗口内跑完了,而且子代理的报告里
   * 带着那句拒绝的原文。退化回挂起的话,这条会超时而不是断言失败。
   */
  it('★ 子代理碰到 ask 档的工具 → 当场拿到一条说得清的拒绝,run 继续跑完', async () => {
    installChildRunLauncher(startChildRun)
    childCalls = 'Bash'
    const { wc, ctx } = fakeWindow()
    const r = req()

    startRun(r, ctx)
    await waitForEnd(r.runId)

    expect(runs.get(r.runId)?.status).toBe('done')

    const committed = JSON.stringify(allEvents(wc).filter((e) => e.type === 'message_commit'))
    expect(committed).toContain('工具回话')
    // 理由要写成模型能据此行动的一句话 —— 只说「被拒绝」它会原地重试
    expect(committed).toContain('a subagent cannot ask for it')
    expect(committed).toContain('parent agent must run it')
  })

  /** 反面:同一个 `Bash`,父代理自己调的时候这道闸不该动它(它该走审批) */
  it('这道闸只认 parentRunId —— 父代理的工具表不受影响', async () => {
    installChildRunLauncher(startChildRun)
    const { wc, ctx } = fakeWindow()
    const r = req()

    startRun(r, ctx)
    await waitForEnd(r.runId)

    // 子代理没调工具时,转录里不该凭空出现那句拒绝
    expect(JSON.stringify(allEvents(wc))).not.toContain('a subagent cannot ask for it')
  })
})
