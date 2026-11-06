import { describe, expect, it } from 'vitest'
import type { AgentMessage } from '../../../shared/agent/message'
import { assistantMessage, userMessage } from '../../../shared/agent/message'
import type { ToolInfo } from '../../../shared/agent/tool'
import type { ContextSegment, ContextSegmentKind } from '../../../shared/agent/context-management'
import type { Skill } from '../../../shared/domain/skill'
import type { PersonalizationSettings } from '../../../shared/domain/settings'
import { PERSONALIZATION_MAX } from '../../../shared/domain/settings'
import {
  assemble,
  buildSystemPrompt,
  estimateMessages,
  estimateTokens,
  estimateTools,
  resolveThinkingBudget,
  type AssembleInput,
  type SystemPromptInput
} from '../context-assembler'

const NOW = Date.UTC(2026, 8, 4, 12, 0, 0)
const PLAT = { os: 'darwin', osVersion: '25.6.0', shell: '/bin/zsh' }

/**
 * `buildSystemPrompt` 的共用底座 —— 每条用例只写它**真正关心**的那几个字段。
 * ★ `permissionMode` / `webSearch` 与 `platform` 同为必填:漏传会编译不过,
 * 而不是静默地少掉几行事实。
 */
const PROMPT: SystemPromptInput = {
  mode: 'normal',
  skills: [],
  workspaceRoot: '/w',
  now: NOW,
  platform: PLAT,
  permissionMode: 'auto',
  webSearch: false
}

function skill(over: Partial<Skill> = {}): Skill {
  return {
    id: 's1',
    name: 'commit',
    description: '写提交信息',
    category: '开发工具',
    source: { kind: 'builtin', path: '' },
    globalEnabled: true,
    frontmatter: {},
    body: '按 Conventional Commits 写。',
    ...over
  }
}

function tool(over: Partial<ToolInfo> = {}): ToolInfo {
  return {
    internalId: 'read_file',
    externalName: 'read_file',
    description: '读取文件',
    inputSchema: { type: 'object' },
    readOnly: true,
    destructive: false,
    needsNetwork: false,
    source: { kind: 'builtin' },
    ...over
  }
}

function input(over: Partial<AssembleInput> = {}): AssembleInput {
  return {
    messages: [],
    tools: [],
    skills: [],
    mode: 'normal',
    thinking: 'off',
    model: 'claude-sonnet-4',
    permissionMode: 'auto',
    webSearch: false,
    workspaceRoot: '/ws',
    now: NOW, platform: PLAT,
    contextWindow: 200_000,
    maxOutputTokens: 8192,
    supportsThinking: true,
    ...over
  }
}

describe('estimateTokens', () => {
  it('空串是 0', () => {
    expect(estimateTokens('')).toBe(0)
  })

  /** 中文一个字约一个 token,英文约四个字符一个 token —— 差三倍,不能一视同仁 */
  it('中文比同样字符数的英文贵得多', () => {
    expect(estimateTokens('中'.repeat(100))).toBeGreaterThan(estimateTokens('a'.repeat(100)) * 3)
  })

  it('英文按约四字符一 token', () => {
    expect(estimateTokens('a'.repeat(400))).toBe(100)
  })

  /** `text.length` 会把一个 emoji 算成两个字符 —— 所以按码点迭代 */
  it('emoji 按一个码点算', () => {
    expect(estimateTokens('🚀🚀🚀🚀')).toBe(1)
  })

  it('单调:更长的文本不会更便宜', () => {
    const short = estimateTokens('这是一段中文')
    expect(estimateTokens('这是一段中文,后面还有更多内容')).toBeGreaterThan(short)
  })
})

describe('estimateMessages / estimateTools', () => {
  it('空数组是 0', () => {
    expect(estimateMessages([])).toBe(0)
    expect(estimateTools([])).toBe(0)
  })

  it('图按固定量级算,不按 dataRef 字符串算', () => {
    const img = userMessage('m', [{ type: 'image', mime: 'image/png', dataRef: 'x' }], NOW)
    expect(estimateMessages([img])).toBeGreaterThan(1000)
  })

  it('工具入参进入估算', () => {
    const bare = assistantMessage('m', [{ type: 'tool_call', callId: 'c', name: 'f', input: {} }], NOW)
    const fat = assistantMessage(
      'm',
      [{ type: 'tool_call', callId: 'c', name: 'f', input: { q: 'x'.repeat(4000) } }],
      NOW
    )
    expect(estimateMessages([fat])).toBeGreaterThan(estimateMessages([bare]) + 900)
  })

  /**
   * ★ 一个挂了三个 MCP server 的工作区,工具 schema 能占掉两三万 token。
   * 漏算它,压力条会在真正爆掉之前一直显示「还很空」。
   */
  it('工具 schema 进入估算', () => {
    const big = tool({
      inputSchema: {
        type: 'object',
        properties: Object.fromEntries(
          Array.from({ length: 60 }, (_, i) => [`field_${i}`, { type: 'string' }])
        )
      }
    })
    expect(estimateTools([big])).toBeGreaterThan(estimateTools([tool()]) + 200)
  })

  it('工具描述进入估算', () => {
    expect(estimateTools([tool({ description: '很长的描述'.repeat(100) })])).toBeGreaterThan(400)
  })
})

describe('buildSystemPrompt', () => {
  it('带上工作区与日期', () => {
    const s = buildSystemPrompt({ ...PROMPT, workspaceRoot: '/a/b' })
    expect(s).toContain('/a/b')
    expect(s).toContain('2026-09-04')
  })

  /**
   * ★ 平台事实必须真的落进提示词。
   *
   * 这条钉的是一整类 bash 失败:模型不知道自己在 macOS 上,就会写
   * `sed -i 's/a/b/' f`(GNU 形式,BSD 上要求 `-i ''`)、写 `readlink -f`
   * (BSD 上没有)。这些都不是「模型笨」,是我们没告诉它。
   */
  it('★ 平台与 shell 进提示词 —— 一条事实抵一段跨平台规则', () => {
    const s = buildSystemPrompt({
      ...PROMPT,
      platform: { os: 'win32', osVersion: '10.0.22631', shell: 'cmd.exe' }
    })
    expect(s).toContain('Platform: win32 (10.0.22631)')
    expect(s).toContain('Shell: cmd.exe')
  })

  it('追加运行时解析出的模式提示词', () => {
    const s = buildSystemPrompt({ ...PROMPT, mode: 'plan', modePrompt: 'PLAN WORKFLOW SENTINEL' })
    expect(s).toContain('PLAN WORKFLOW SENTINEL')
  })

  it('不再根据旧模式 id 隐式注入提示词', () => {
    const s = buildSystemPrompt({ ...PROMPT, mode: 'goal' })
    expect(s).not.toContain('Goal mode')
  })

  it('编程模式没有额外模式提示词', () => {
    const s = buildSystemPrompt({ ...PROMPT })
    expect(s).not.toContain('PLAN WORKFLOW SENTINEL')
  })

  it('没有 Skill 时不出现 Skill 段', () => {
    const s = buildSystemPrompt({ ...PROMPT })
    expect(s).not.toContain('Available Skills')
  })

  it('Skill 名字与描述进入提示词', () => {
    const s = buildSystemPrompt({
      ...PROMPT,
      skills: [skill({ name: 'commit', description: '写符合 Conventional Commits 的提交信息' })]
    })
    expect(s).toContain('commit')
    expect(s).toContain('写符合 Conventional Commits 的提交信息')
  })

  /**
   * ★★ 这一条是整个渐进披露改造的**唯一**决定性断言。
   *
   * 正向那条(名字和描述进得去)在旧的全量注入实现下也是绿的 —— 它证明不了
   * 任何事情。真正要钉住的是**正文进不去**:提示词每一轮都重发,正文一旦回到
   * 这里,装十条 Skill 就是每轮多烧十几万字符,而且提示词前缀一变,
   * 上游的 prompt cache 整体失效。
   *
   * 谁哪天为了「让模型省一次工具调用」把正文塞回目录里,这一条会红。
   */
  it('★ Skill 正文不进提示词 —— 渐进披露的全部意义', () => {
    const s = buildSystemPrompt({
      ...PROMPT,
      skills: [skill({ description: '写提交信息', body: '按 Conventional Commits 写。' })]
    })
    expect(s).not.toContain('按 Conventional Commits 写。')
    // 而且要明确告诉模型「正文得自己去取」,否则它会凭名字猜
    expect(s).toContain('THIS IS A CATALOG ONLY')
    expect(s).toContain('Skill')
  })

  it('Skill 描述也被消毒', () => {
    const s = buildSystemPrompt({
      ...PROMPT,
      skills: [skill({ description: 'x\u0007y' })]
    })
    expect(s).toContain('xy')
  })

  it('超长 Skill 描述被截断', () => {
    const s = buildSystemPrompt({
      ...PROMPT,
      skills: [skill({ description: 'y'.repeat(500_000) })]
    })
    expect(s.length).toBeLessThan(10_000)
  })

  /**
   * ★ 这一条是上面那条反向断言的**量化版本**。
   *
   * 「正文不在里面」可以靠一个巧合的断言串蒙混过去;「一个 500KB 正文的 Skill
   * 只让提示词长了一行」不能。
   */
  it('★ 一个 500KB 正文的 Skill 只往提示词里加一行', () => {
    const build = (body: string): string =>
      buildSystemPrompt({
        ...PROMPT,
        skills: [skill({ body })]
      })

    /*
      ★ 和「空正文」逐字比,而不是和一个写死的字节数比。
      写死数字的话,这条用例会在 BASE_PROMPT 每次改动时都红一次 ——
      而它想钉的从来不是提示词多长,是**正文一个字都不进去**。
    */
    expect(build('z'.repeat(500_000))).toBe(build(''))
  })

  /**
   * ★ 换了夹具,断言没换。
   *
   * 原来是 20 条 × 60KB 正文 —— 那个夹具现在撑不爆任何预算了(正文根本不进去),
   * 于是这条用例会变成一条永远绿的空断言。改成 500 条 × 1KB 描述:
   * **装几百个 Skill 是真实场景**,而目录预算就是为它存在的。
   */
  it('Skill 总长度超限时截住,并说明丢了几个', () => {
    const many = Array.from({ length: 500 }, (_, i) =>
      skill({ id: `s${i}`, name: `skill${i}`, description: 'd'.repeat(1000) })
    )
    const s = buildSystemPrompt({ ...PROMPT, skills: many })
    expect(s.length).toBeLessThan(200_000)
    expect(s).toMatch(/\d+ more Skill/)
  })

  /**
   * ★ 权限档位是**事实**,不是规则(文件头第 3 关)。
   *
   * 它挡的是这样一轮:模型不知道自己在 ask 档,先调一次 `Write`、被拒、
   * 然后**去试 `Bash` 绕**(`# Permissions` 整段就是在补救这件事)。
   * 事前告知一行,比事后拦一次便宜得多。
   */
  it('★ 权限档位与联网开关进 # Environment', () => {
    const s = buildSystemPrompt({ ...PROMPT, permissionMode: 'ask', webSearch: false })
    expect(s).toContain('Permission mode: ask')
    expect(s).toContain('wait for user approval')
    expect(s).toContain('the network switch is off')
  })

  it('full 档 + 开着联网时不吓唬模型', () => {
    const s = buildSystemPrompt({ ...PROMPT, permissionMode: 'full', webSearch: true })
    expect(s).toContain('Permission mode: full')
    expect(s).not.toContain('DENIED')
  })

  /**
   * ★ 这一句是 Skill 段唯一真正的防御 —— 前面的消毒只防意外,不防故意。
   * 真正的防线在权限层,但让模型在它自己能判断时先拒绝一次是免费的。
   */
  it('Skill 段声明权限边界', () => {
    const s = buildSystemPrompt({
      ...PROMPT,
      skills: [skill()]
    })
    expect(s).toContain('cannot widen your')
  })

  /**
   * ★ 收尾前同步任务清单的那条规则必须是**条件**的,而且不许点名某个工具。
   *
   * 它挡的是「正文里宣布完成、用户看着的清单还停在半路」——回执与界面长期对不上,
   * 而全程零报错。写成无条件的话,plan 模式(工具白名单里没有任务清单工具)的模型
   * 会去建一份它根本写不了的清单;写上工具名则会在工具不在快照里时指向一个
   * 模型拿不到的名字(见 `BASE_PROMPT` 上面那四关的第 3 条)。
   */
  it('★ 收尾前同步任务清单的规则是条件的,且不写死工具名', () => {
    const s = buildSystemPrompt(PROMPT)
    expect(s).toContain('If you used a task list')
    expect(s).toContain('still available')
    expect(s).toContain('is not an update')
    expect(s).not.toContain('TodoWrite')
  })
})

/**
 * 「偏好 › 个性化」那三栏。它们是这份提示词里**唯一由用户逐字写出来**的部分,
 * 所以这一节钉的是三件事:全空时一个字都不多、位置压得住模式说明、脏值进不来。
 */
describe('buildSystemPrompt · 个性化', () => {
  const P = (over: Partial<PersonalizationSettings> = {}): PersonalizationSettings => ({
    name: '',
    background: '',
    instructions: '',
    ...over
  })

  /**
   * ★ 从没填过这一页的用户是**绝大多数**,而他们的提示词里不该多出一个空标题 ——
   * 一个只有标题没有内容的 `# About the user` 会让模型去猜它本该是什么。
   */
  it('★ 三栏全空时一段都不加', () => {
    const blank = buildSystemPrompt({ ...PROMPT, personalization: P() })
    expect(blank).toBe(buildSystemPrompt(PROMPT))
    expect(blank).not.toContain('About the user')
  })

  it('只填了姓名时不出现「工作描述」那半句', () => {
    const s = buildSystemPrompt({ ...PROMPT, personalization: P({ name: '张三' }) })
    expect(s).toContain('Name: 张三')
    expect(s).not.toContain('What they do')
    expect(s).not.toContain('User instructions')
  })

  /**
   * ★ 事实和指令分成两段,不是拼成一段。「我是前端工程师」和「一律用中文回答」
   * 在模型眼里是两种东西,混在一个标题下会让后者读起来像是在自我介绍。
   */
  it('★ 姓名/背景进事实段,全局提示词单独成段', () => {
    const s = buildSystemPrompt({
      ...PROMPT,
      personalization: P({ name: '张三', background: '前端工程师', instructions: '用中文回答' })
    })
    expect(s.indexOf('# About the user')).toBeLessThan(s.indexOf('# User instructions'))
    expect(s).toContain('What they do: 前端工程师')
    expect(s).toContain('用中文回答')
  })

  /**
   * ★ 这条钉的是**优先级**。不写这句的话,一条「永远用中文回答」会让模型在用户
   * 明确说 "answer in English" 时也照旧说中文 —— 而用户完全不知道该去哪里关掉它。
   */
  it('★ 声明「用户最新的话优先于全局提示词」', () => {
    const s = buildSystemPrompt({ ...PROMPT, personalization: P({ instructions: '用中文回答' }) })
    expect(s).toContain("user's latest message wins")
  })

  /**
   * ★ 位置不是排版偏好。plan 模式那段说的是「你现在只有只读工具」,它必须压得住
   * 一条写着「别问了直接改」的全局提示词 —— 用户设的是默认口吻,不是权限。
   */
  it('★ 排在 # Environment 之后、模式说明之前', () => {
    const s = buildSystemPrompt({
      ...PROMPT,
      mode: 'plan',
      modePrompt: '# Plan mode\n\nInvestigate and clarify before writing the plan.',
      personalization: P({ instructions: '别问我,直接改' })
    })
    const env = s.indexOf('# Environment')
    const mine = s.indexOf('# User instructions')
    const plan = s.indexOf('# Plan mode')
    expect(env).toBeLessThan(mine)
    expect(mine).toBeLessThan(plan)
  })

  /**
   * 「可信」和「格式正确」是两回事:粘贴进来的文本能带着 C0 控制字符,
   * 而旧库里可能躺着一段在上限存在之前写下的超长指令 —— 落库那侧的闸门
   * 只管**以后**写进来的值。
   */
  it('削掉控制字符,但留住换行(多行指令要分行读)', () => {
    const s = buildSystemPrompt({
      ...PROMPT,
      personalization: P({ instructions: '第一条\n第二条' })
    })
    expect(s).toContain('第一条\n第二条')
    expect(s).not.toContain('')
  })

  it('★ 超长的全局提示词在这里再截一次,并留下明确标记', () => {
    const s = buildSystemPrompt({
      ...PROMPT,
      personalization: P({ instructions: '好'.repeat(PERSONALIZATION_MAX.instructions + 500) })
    })
    expect(s).toContain('...')
    expect(s.length).toBeLessThan(
      buildSystemPrompt(PROMPT).length + PERSONALIZATION_MAX.instructions + 500
    )
  })

  /** 只打了空格的那一栏等于没填 —— 否则提示词里会多出一行 `Name:` */
  it('纯空白的字段当作没填', () => {
    const s = buildSystemPrompt({ ...PROMPT, personalization: P({ name: '   \n  ' }) })
    expect(s).not.toContain('About the user')
  })
})

describe('resolveThinkingBudget', () => {
  it('模型不支持时永远不下发', () => {
    expect(resolveThinkingBudget('max', false, 64_000)).toBeUndefined()
  })

  it('关闭时不下发', () => {
    expect(resolveThinkingBudget('off', true, 64_000)).toBeUndefined()
  })

  /** 「自动」和「关闭」在界面上是两档,行为就不能一样 */
  it('自动会真的开一点思考', () => {
    expect(resolveThinkingBudget('auto', true, 64_000)).toBeGreaterThan(0)
  })

  it('档位越高预算越大', () => {
    const low = resolveThinkingBudget('low', true, 100_000) ?? 0
    const high = resolveThinkingBudget('high', true, 100_000) ?? 0
    expect(high).toBeGreaterThan(low)
  })

  /**
   * ★ 上游要求 `max_tokens > budget_tokens`。用户选「最高」(64000)而模型
   * maxOutputTokens 是 8192 时,请求直接 400,而错误信息里只字不提 thinking。
   */
  it('预算被 maxOutputTokens 压住', () => {
    const b = resolveThinkingBudget('max', true, 8192)
    expect(b).toBeDefined()
    expect(b as number).toBeLessThan(8192)
  })

  /** 挤不出 1024 就干脆不开 —— 用户选的是「多想一点」,不是「宁可失败」 */
  it('额度太小时降级为不开思考,而不是报错', () => {
    expect(resolveThinkingBudget('max', true, 1500)).toBeUndefined()
    expect(resolveThinkingBudget('minimal', true, 512)).toBeUndefined()
  })

  it('额度充足时按档位原值下发', () => {
    expect(resolveThinkingBudget('minimal', true, 64_000)).toBe(1024)
  })
})

describe('assemble', () => {
  it('把系统提示词与模型别名放进请求', () => {
    const { request } = assemble(input({ model: 'glm-4.6', workspaceRoot: '/proj' }))
    expect(request.model).toBe('glm-4.6')
    expect(request.system).toContain('/proj')
  })

  it('关闭思考时不带 thinkingBudget 字段', () => {
    expect(assemble(input({ thinking: 'off' })).request).not.toHaveProperty('thinkingBudget')
  })

  it('保留旧模型配置中的明确关闭意图，即使旧能力标记为 false', () => {
    expect(assemble(input({ thinking: 'off', supportsThinking: false })).request.reasoning)
      .toEqual({ mode: 'toggle', enabled: false, explicit: true })
  })

  it('开启思考时带上预算', () => {
    expect(assemble(input({ thinking: 'high', maxOutputTokens: 64_000 })).request.thinkingBudget)
      .toBeGreaterThan(0)
  })

  /** 请求要过 JSON 与 IPC,不能和调用方共享同一个数组 */
  it('messages 与 tools 是拷贝', () => {
    const messages: AgentMessage[] = [userMessage('m', [{ type: 'text', text: 'hi' }], NOW)]
    const tools = [tool()]
    const { request } = assemble(input({ messages, tools }))
    expect(request.messages).not.toBe(messages)
    expect(request.tools).not.toBe(tools)
    expect(request.messages).toEqual(messages)
  })

  it('空会话的占用远小于窗口', () => {
    const { usage } = assemble(input())
    expect(usage.used).toBeGreaterThan(0)
    expect(usage.window).toBe(200_000)
    expect(usage.shouldCompact).toBe(false)
  })

  it('历史越长占用越高', () => {
    const long = Array.from({ length: 50 }, (_, i) =>
      userMessage(`m${i}`, [{ type: 'text', text: '很长的一段中文内容'.repeat(50) }], NOW)
    )
    expect(assemble(input({ messages: long })).usage.used).toBeGreaterThan(
      assemble(input()).usage.used + 10_000
    )
  })

  it('逼近窗口时 shouldCompact 为真', () => {
    const huge = [userMessage('m', [{ type: 'text', text: '中'.repeat(90_000) }], NOW)]
    expect(assemble(input({ messages: huge, contextWindow: 100_000 })).usage.shouldCompact).toBe(
      true
    )
  })

  /**
   * ★ 上下文窗口是**输入加输出**共用的。只比较输入的话,你会在
   * 「输入刚好塞得下、回复写到一半被截断」时才发现该压缩了。
   */
  it('maxOutputTokens 算进压缩判断', () => {
    const messages = [userMessage('m', [{ type: 'text', text: '中'.repeat(70_000) }], NOW)]
    const small = assemble(input({ messages, contextWindow: 100_000, maxOutputTokens: 1024 }))
    const big = assemble(input({ messages, contextWindow: 100_000, maxOutputTokens: 32_000 }))
    expect(small.usage.shouldCompact).toBe(false)
    expect(big.usage.shouldCompact).toBe(true)
  })

  /**
   * ★ **这条钉的是一个线上 bug:压力条几乎空着,旁边却写着「接近上限」。**
   *
   * `maxOutputTokens` 按模型的**协议**窗口标(1M 窗口配 384K 输出),而传进来的
   * `contextWindow` 是被 `LONG_CONTEXT_THRESHOLD` 夹过的**有效**窗口(272K)。
   * 不给预留封顶的话,光预留一项(384K)就把阈值压成负数,判据恒为真 ——
   * 自动压缩从第一条消息起每轮触发,而且压完仍为真,永不收敛。
   *
   * 原先封顶靠「窗口 × 0.25」,现在靠 `COMPACT_MAX_OUTPUT_TOKENS`(20K,同 CC):
   * 阈值 = 272K − 20K − 13K = 239K。封顶的理由没变,数变了,所以下面那个
   * 「塞满」的长度也跟着变 —— 200K 个汉字在新公式下还没到线。
   */
  it('★ 预留封顶 —— maxOutputTokens 大于阈值时不会恒判该压缩', () => {
    const over = input({ contextWindow: 272_000, maxOutputTokens: 384_000 })
    expect(assemble(over).usage.shouldCompact).toBe(false)
    // 封顶之后判据重新跟历史长度有关:塞满就该压了
    const huge = [userMessage('m', [{ type: 'text', text: '中'.repeat(300_000) }], NOW)]
    expect(assemble({ ...over, messages: huge }).usage.shouldCompact).toBe(true)
  })

  /*
    ── 判据读的那个数:上游真值 + 其后新增的估算 ──
    ★ 这一组钉的是一个线上 bug:圆环(读上游报回的真值)已经写着「211K / 200K,
    已超出」,自动压缩却一次都没触发 —— 因为判据读的是本地 chars/4 估算,
    同一份请求在那里只有 ~152K。两个数从不对账。

    ★ 原先的修法是一个「真值 ÷ 估算」的校准系数;现在改成直接把
    `knownInputTokens`(`AgentSession.contextTokens` 算好的真值 + 增量估算)喂进来,
    判据取它和 `used` 的较大者。系数那套连同它的上下界一并删除 —— 上界 3 在长会话里
    反而会把判据夹得比真值低,而那正是它被删掉的直接原因。
  */
  it('★ 上游真值高于本地估算时判据随之为真', () => {
    // 估算约 100K(400K 拉丁字符 ÷ 4),阈值 = 200K − 8192 − 13K ≈ 178.8K
    const messages = [userMessage('m', [{ type: 'text', text: 'x'.repeat(400_000) }], NOW)]
    const base = input({ messages, contextWindow: 200_000, maxOutputTokens: 8192 })
    expect(assemble(base).usage.shouldCompact).toBe(false)
    // 上游报回来的真值是估算的两倍 —— 这一份请求其实已经 200K 了
    expect(assemble({ ...base, knownInputTokens: 200_000 }).usage.shouldCompact).toBe(true)
  })

  /** `used` 与 `segments` 是「谁占了多少」的同一套读数,真值只走判据。 */
  it('★ knownInputTokens 不动 used,也不动归因之和', () => {
    const messages = [userMessage('m', [{ type: 'text', text: 'x'.repeat(400_000) }], NOW)]
    const plain = assemble(input({ messages }))
    const known = assemble(input({ messages, knownInputTokens: 250_000 }))
    expect(known.usage.used).toBe(plain.usage.used)
    expect(known.usage.segments?.reduce((n, s) => n + s.tokens, 0)).toBe(known.usage.used)
  })

  /**
   * ★ **真值比估算还小时取估算,而不是反过来。**
   *
   * 挡的是一类具体的上游:按「未命中缓存的那部分」报 input_tokens、又不给
   * cache_read 的中转。照单全收的话,这种上游会把自动压缩整个关掉 ——
   * 判据读到一个远小于真实占用的数,永远不触发,直到上游 400。
   */
  it('★ 真值偏小时判据取较大的那个', () => {
    const messages = [userMessage('m', [{ type: 'text', text: '中'.repeat(90_000) }], NOW)]
    const base = input({ messages, contextWindow: 100_000, maxOutputTokens: 8192 })
    expect(assemble(base).usage.shouldCompact).toBe(true)
    expect(assemble({ ...base, knownInputTokens: 1_000 }).usage.shouldCompact).toBe(true)
  })

  /** 没有真值可用时(本 run 第一轮 / 刚压缩完)必须逐字退回纯估算。 */
  it('缺省时 inputTokens 与 used 逐字相等', () => {
    const messages = [userMessage('m', [{ type: 'text', text: '中'.repeat(30_000) }], NOW)]
    const out = assemble(input({ messages }))
    expect(out.inputTokens).toBe(out.usage.used)
  })

  it('工具占用计入 used', () => {
    const many = Array.from({ length: 40 }, (_, i) =>
      tool({ internalId: `t${i}`, externalName: `t${i}`, description: '描述'.repeat(50) })
    )
    expect(assemble(input({ tools: many })).usage.used).toBeGreaterThan(
      assemble(input()).usage.used + 3000
    )
  })
})

describe('assemble · 占用归因', () => {
  /** 六档各取一点,一条用例覆盖所有来源 —— 守恒只在混着的时候才容易破。 */
  function mixed(): AssembleInput {
    return input({
      messages: [
        userMessage('m1', [{ type: 'text', text: '帮我看看这段代码'.repeat(20) }], NOW),
        assistantMessage('m2', [{ type: 'text', text: '好的' }], NOW)
      ],
      tools: [
        tool({ internalId: 'read_file', externalName: 'read_file' }),
        tool({ internalId: 'bash', externalName: 'bash', description: '跑命令'.repeat(30) }),
        tool({
          internalId: 'mcp__github__pr',
          externalName: 'github_pr',
          description: 'PR'.repeat(200),
          source: { kind: 'mcp', serverId: 'github' }
        }),
        tool({
          internalId: 'mcp__github__issue',
          externalName: 'github_issue',
          source: { kind: 'mcp', serverId: 'github' }
        }),
        tool({
          internalId: 'mcp__linear__task',
          externalName: 'linear_task',
          source: { kind: 'mcp', serverId: 'linear' }
        }),
        tool({
          internalId: 'skill__commit',
          externalName: 'skill_commit',
          source: { kind: 'skill', skillId: 's1' }
        })
      ],
      skills: [skill(), skill({ id: 's2', name: 'review', description: '审查改动' })],
      personalization: { name: '张三', background: '前端工程师', instructions: '一律用中文回答' },
      reminder: { projectInstructions: '这个仓库的提交信息用中文。'.repeat(10) }
    })
  }

  function tokensOf(usage: { segments?: ContextSegment[] }, kind: ContextSegmentKind): number {
    return usage.segments?.find((s) => s.kind === kind)?.tokens ?? 0
  }

  /*
    ★★ **这是这一组里唯一不能松的一条。** 破了它的表现极其温和:界面上百分比
    加起来是 98%,看着像四舍五入 —— 于是没人会去查,而真实原因可能是整个 MCP
    那一档漏算了一半。`join` 的分隔符和分段各自 ceil 的累积都会打破它。
  */
  it('各档之和恒等于 used', () => {
    const { usage } = assemble(mixed())
    const sum = (usage.segments ?? []).reduce((n, s) => n + s.tokens, 0)
    expect(sum).toBe(usage.used)
  })

  it('空会话同样守恒', () => {
    const { usage } = assemble(input())
    expect((usage.segments ?? []).reduce((n, s) => n + s.tokens, 0)).toBe(usage.used)
    // 还没发过消息:这一档是真的 0,不是「不知道」。
    expect(tokensOf(usage, 'messages')).toBe(0)
  })

  it('MCP 分档到 server,按占用降序', () => {
    const detail = assemble(mixed()).usage.segments?.find((s) => s.kind === 'tools-mcp')?.detail
    expect(detail?.map((d) => d.id)).toEqual(['github', 'linear'])
    // 「MCP 占 42%」不可行动,「github 这一个占 28%」可以 —— 见 ContextSegmentKind。
    expect(detail?.[0]?.tokens).toBeGreaterThan(detail?.[1]?.tokens ?? 0)
  })

  it('内置工具与 MCP 工具分属不同档', () => {
    const { usage } = assemble(mixed())
    expect(tokensOf(usage, 'tools-builtin')).toBeGreaterThan(0)
    expect(tokensOf(usage, 'tools-mcp')).toBeGreaterThan(0)
  })

  /** 技能的清单段和它注册的工具在设置里是同一个开关,归因也必须是同一档。 */
  it('技能工具并入 skills,不单列', () => {
    const withSkillTool = assemble(mixed()).usage
    const withoutSkillTool = assemble({
      ...mixed(),
      tools: mixed().tools.filter((t) => t.source.kind !== 'skill')
    }).usage
    expect(tokensOf(withSkillTool, 'skills')).toBeGreaterThan(tokensOf(withoutSkillTool, 'skills'))
    expect(withSkillTool.segments?.map((s) => s.kind)).not.toContain('tools-skill')
  })

  /*
    ★ AGENTS.md 是**注入进消息流**的,但它不是对话 —— 记到 `messages` 头上的话,
    用户会看着一个「消息占 30%」的读数去按压缩,而压缩一个字节都减不掉它。
  */
  it('注入的项目指令算 instructions,不算 messages', () => {
    const withReminder = assemble(mixed()).usage
    const withoutReminder = assemble({ ...mixed(), reminder: undefined }).usage
    expect(tokensOf(withReminder, 'instructions')).toBeGreaterThan(
      tokensOf(withoutReminder, 'instructions')
    )
    expect(tokensOf(withReminder, 'messages')).toBe(tokensOf(withoutReminder, 'messages'))
  })

  /** 个性化是用户自己写的,和关不掉的基础提示词不同档。 */
  it('个性化算 instructions,不算 system', () => {
    const on = assemble(mixed()).usage
    const off = assemble({ ...mixed(), personalization: undefined }).usage
    expect(tokensOf(on, 'instructions')).toBeGreaterThan(tokensOf(off, 'instructions'))
  })

  it('挂上 MCP 之后 system 那一档不动', () => {
    const bare = assemble(input()).usage
    const heavy = assemble(
      input({
        tools: Array.from({ length: 30 }, (_, i) =>
          tool({
            internalId: `mcp__x__${i}`,
            externalName: `x_${i}`,
            description: '描述'.repeat(80),
            source: { kind: 'mcp', serverId: 'x' }
          })
        )
      })
    ).usage
    expect(tokensOf(heavy, 'system')).toBe(tokensOf(bare, 'system'))
  })
})
