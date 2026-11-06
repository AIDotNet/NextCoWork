import { beforeEach, describe, expect, it } from 'vitest'
import type { ToolOutput } from '../agent/message'
import {
  base,
  clearPluginPresenters,
  clip,
  dirOf,
  humanize,
  isPinnedShape,
  isRegisteredTool,
  parseMcpId,
  pick,
  pluginPresentersSnapshot,
  presenterOf,
  registeredToolIds,
  registerPluginPresenters,
  setPresenterTranslate,
  toolLineText
} from '../domain/tool-presenter'

/**
 * presenter 的 bug 有两种表现,都不会崩:
 * 1. 行里显示成 "undefined" / 空白 —— 流式中途 input 还是半截 JSON 字符串;
 * 2. 新工具静默落进兜底 —— 长得和 MCP 工具一样,而没人会去查一个不报错的地方。
 * 这一组把两种都钉住。
 */

/**
 * 文案替身。真实文案表在渲染层 i18n,shared 测试不许反向 import(依赖方向),
 * 而这里的断言关心的是「选了哪个 key、抽出了哪些参数、缺参时退不退化」,
 * 不是中文句子本身 —— 那边由 i18n 的键一致性测试守着。没覆盖到的 key 回显
 * key 本身(与注册表的缺省注入行为一致)。
 *
 * ★ `chat.tool.title.*` 现在是**纯标签**(不带 {target}):目标由 `ToolLine.target`
 * 单独给,所以替身也只返回一个词 —— 替身要是还拼 target,这一组就测不出
 * 「目标是不是真的被拆进了自己那一格」。
 */
setPresenterTranslate((key, p = {}) => {
  switch (key) {
    case 'chat.tool.title.read':
      return '读取'
    case 'chat.tool.title.bash':
      return '终端'
    case 'chat.tool.title.edit':
      return '编辑'
    case 'chat.tool.title.webFetch':
      return '抓取'
    case 'chat.tool.fallback':
      return '工具调用'
    case 'chat.tool.summary.lines':
      return `${String(p.count)} 行`
    case 'chat.tool.summary.files':
      return `${String(p.count)} 个文件`
    case 'chat.tool.summary.matches':
      return `${String(p.count)} 处`
    case 'chat.tool.summary.createdLines':
      return `新建 ${String(p.count)} 行`
    case 'chat.tool.summary.created':
      return '新建'
    case 'chat.tool.summary.replaced':
      return `替换 ${String(p.count)} 处`
    case 'chat.tool.summary.exitCode':
      return `退出码 ${String(p.code)}`
    case 'chat.tool.summary.noOutput':
      return '无输出'
    case 'chat.tool.summary.scheduleDaily':
      return `每天 ${String(p.time)}`
    case 'chat.tool.summary.scheduleWeekly': {
      // 与真实 zh 表同构:数字串 → 星期名。替身只关心「参数原样传到」
      const marks = String(p.days).split('').map((d) => '日一二三四五六'[Number(d)] ?? '').join('')
      return `周${marks} ${String(p.time)}`
    }
    default:
      return key
  }
})

const out = (content: string, extra?: Partial<ToolOutput>): ToolOutput => ({ content, ...extra })

describe('取值原语', () => {
  it('pick 对非对象输入安全', () => {
    expect(pick('{"file_path":"a', 'file_path')).toBe('')
    expect(pick(null, 'x')).toBe('')
    expect(pick(undefined, 'x')).toBe('')
    expect(pick(42, 'x')).toBe('')
    expect(pick({ x: 1 }, 'x')).toBe('') // 数字不是字符串 → 空
    expect(pick({ x: 'v' }, 'x')).toBe('v')
  })

  it('base 同时处理正斜杠与反斜杠', () => {
    expect(base('/a/b/c.ts')).toBe('c.ts')
    expect(base('C:\\a\\b\\c.ts')).toBe('c.ts')
    expect(base('c.ts')).toBe('c.ts')
    expect(base('/a/b/')).toBe('b')
    expect(base('')).toBe('')
  })

  it('clip 折叠空白并截断', () => {
    expect(clip('a\nb   c', 40)).toBe('a b c')
    expect(clip('x'.repeat(50), 10)).toBe(`${'x'.repeat(10)}…`)
  })

  it('humanize 把下划线换成空格', () => {
    expect(humanize('create_pull_request')).toBe('create pull request')
  })

  /**
   * 目录那一格是行里回答「在哪儿」的唯一位置。入参是绝对路径,整条画出来会撑满行,
   * 所以只留末尾三段并前置省略号 —— 断言钉的是「留几段、斜杠在哪、什么时候不画」。
   */
  it('dirOf 只留末尾三段,并保留尾斜杠', () => {
    expect(dirOf('/a/b/c/d/e.ts')).toBe('…/b/c/d/')
    expect(dirOf('/w/src/index.ts')).toBe('w/src/')
    expect(dirOf('C:\\a\\b\\c.ts')).toBe('C:\\a\\b\\')
    // 没有目录可言的两种:裸文件名、根下文件
    expect(dirOf('index.ts')).toBe('')
    expect(dirOf('/index.ts')).toBe('')
    expect(dirOf('')).toBe('')
  })

  it('toolLineText 把三段拍平成一句(只给「只剩一格」的地方用)', () => {
    expect(toolLineText({ label: '读取', target: 'a.ts', context: 'src/' })).toBe('读取 a.ts')
    expect(toolLineText({ label: '读取' })).toBe('读取')
  })
})

describe('parseMcpId', () => {
  it('拆出 server 与 tool', () => {
    expect(parseMcpId('mcp__github__create_pr')).toEqual({ server: 'github', tool: 'create_pr' })
  })

  it('server 名里带连字符也能拆', () => {
    expect(parseMcpId('mcp__github-enterprise__create_pr')).toEqual({
      server: 'github-enterprise',
      tool: 'create_pr'
    })
  })

  it('非 MCP 名返回 null', () => {
    expect(parseMcpId('Read')).toBeNull()
    expect(parseMcpId('mcp__onlyserver')).toBeNull()
    expect(parseMcpId('')).toBeNull()
  })

  it('★ 超长名被 ToolNamer 截断加哈希后,哈希后缀要剥掉', () => {
    // 64 字符上限下的真实形状:尾部 _ + 8 位 FNV
    const long = `mcp__github-enterprise-internal__${'create_pull_request_review'.repeat(2)}_3a7f21b9`
    const r = parseMcpId(long)
    expect(r?.server).toBe('github-enterprise-internal')
    expect(r?.tool.endsWith('_3a7f21b9')).toBe(false)
  })

  it('★ 短名里像哈希的尾巴不能被误剥', () => {
    // 真名就叫 sync_1a2b3c4d 的工具存在,且它没被截断过
    expect(parseMcpId('mcp__db__sync_1a2b3c4d')?.tool).toBe('sync_1a2b3c4d')
  })
})

describe('presenterOf · 内置工具', () => {
  it('★ Read 把文件名和目录拆成两格 —— 渲染层据此分色,拼成一句就没得分', () => {
    const p = presenterOf('Read')
    expect(p.shape).toBe('read')
    expect(p.line({ file_path: '/w/src/main/index.ts' })).toEqual({
      label: '读取',
      target: 'index.ts',
      context: 'w/src/main/',
      path: '/w/src/main/index.ts'
    })
    expect(p.summary?.({}, out('a\nb\nc'))).toBe('3 行')
  })

  it('★ 流式中途 input 是半截 JSON 字符串时,行里只剩标签而不是崩溃或半个路径', () => {
    expect(presenterOf('Read').line('{"file_p')).toEqual({ label: '读取' })
    expect(presenterOf('Bash').line('{"comm')).toEqual({ label: '终端' })
    expect(presenterOf('Edit').line(undefined)).toEqual({ label: '编辑' })
  })

  it('★ Bash 行只说「干了什么」:有 description 就只画它,命令一个字都不进行里', () => {
    const p = presenterOf('Bash')
    expect(p.line({ command: 'ls -la', description: '列出文件' })).toEqual({
      label: '终端',
      target: '列出文件'
    })
  })

  /**
   * ★★ `description` 是可省参数,所以兜底也必须说人话。
   * 原样显示命令时,行里前四十个字符全是 `cd /Users/…/NextCoWork &&` 这种仪式,
   * 真正的动作被挤出了可视范围 —— 这正是这条用例要守住的那个 bug。
   */
  it('★ 没有 description 时剥掉 cd 前缀,只留命令主干', () => {
    const p = presenterOf('Bash')
    expect(p.line({ command: 'ls -la' })).toEqual({ label: '终端', target: 'ls -la', mono: true })
    expect(p.line({ command: 'cd /Users/token/Desktop/code/NextCoWork && npm test' }).target).toBe('npm test')
    expect(p.line({ command: 'cd "/a b" && cd /c && git status -sb' }).target).toBe('git status -sb')
    // 命令本身就是 cd:剥完什么都不剩,退回原文而不是显示空白
    expect(p.line({ command: 'cd /tmp' }).target).toBe('cd /tmp')
  })

  it('★ Bash 失败时摘要是退出码 —— 不展开就能分辨 127 和 1', () => {
    const p = presenterOf('Bash')
    expect(p.summary?.({}, out('Command exited with code 127.\nnot found'))).toBe('退出码 127')
    expect(p.summary?.({}, out('(command succeeded with no output)'))).toBe('无输出')
  })

  it('Write 区分新建与覆写', () => {
    const p = presenterOf('Write')
    expect(p.shape).toBe('mutate')
    expect(p.summary?.({}, out('Created a.ts (12 lines, 340 bytes)'))).toBe('新建 12 行')
    expect(p.summary?.({}, out('Overwrote a.ts (5 lines, 90 bytes)'))).toBe('5 行')
  })

  it('★ 输出文案对不上正则时返回 undefined,而不是猜一个数', () => {
    // 摘要错了比没有更糟:用户会拿它当真
    expect(presenterOf('Edit').summary?.({}, out('something unexpected'))).toBeUndefined()
  })

  it('Edit 摘要是替换处数', () => {
    expect(presenterOf('Edit').summary?.({}, out('Edited a.ts: replaced 3 occurrence(s).'))).toBe(
      '替换 3 处'
    )
  })

  it('Grep / Glob 归到同一形态,摘要量词不同', () => {
    expect(presenterOf('Grep').shape).toBe('search')
    expect(presenterOf('Glob').shape).toBe('search')
    expect(presenterOf('Grep').summary?.({}, out('a\nb'))).toBe('2 处')
    expect(presenterOf('Glob').summary?.({}, out('a\nb'))).toBe('2 个文件')
  })

  it('★ TodoWrite 摘要从入参算,不依赖输出文案', () => {
    const p = presenterOf('TodoWrite')
    const todos = [{ status: 'completed' }, { status: 'in_progress' }, { status: 'pending' }]
    expect(p.summary?.({ todos }, undefined)).toBe('1/3')
  })

  it('★ 定时任务周规则把星期压成数字串传给文案表 —— 星期名属于 UI 文案,不在这里翻', () => {
    const p = presenterOf('CreateScheduledTask')
    expect(
      p.summary?.({ name: 't', schedule: { kind: 'weekly', weekdays: [1, 3, 5], time: '09:00' } }, undefined)
    ).toBe('周一三五 09:00')
    // 越界的星期号直接丢弃,不猜一个名字
    expect(
      p.summary?.({ name: 't', schedule: { kind: 'weekly', weekdays: [9, 'x'], time: '09:00' } }, undefined)
    ).toBeUndefined()
    expect(p.summary?.({ name: 't', schedule: { kind: 'daily', time: '08:30' } }, undefined)).toBe(
      '每天 08:30'
    )
  })

  it('WebFetch 摘要用字节数', () => {
    expect(presenterOf('WebFetch').summary?.({}, out('x'.repeat(2048)))).toBe('2.0KB')
    expect(presenterOf('WebFetch').line({ url: 'https://example.com/a/b' })).toEqual({
      label: '抓取',
      target: 'example.com',
      mono: false
    })
  })

  it('URL 解析不了时不抛异常', () => {
    expect(presenterOf('WebFetch').line({ url: 'htt' }).target).toBe('htt')
  })

  it('运行中(output 为 undefined)所有摘要都返回 undefined', () => {
    for (const id of registeredToolIds()) {
      const p = presenterOf(id)
      // 只有 TodoWrite / Task 从入参算,给空入参它们也该返回 undefined
      expect(p.summary?.({}, undefined)).toBeUndefined()
    }
  })
})

describe('presenterOf · 兜底路径', () => {
  it('MCP 工具走 external,server 落标签、工具名落目标', () => {
    const p = presenterOf('mcp__github__create_pull_request')
    expect(p.shape).toBe('external')
    expect(p.line({})).toEqual({ label: 'github', target: 'create pull request' })
  })

  it('完全未知但可读的名字,显示名字本身 —— 比「工具调用」有用', () => {
    expect(presenterOf('some_new_tool').line({}).label).toBe('some new tool')
  })

  it('空名字才落到最终兜底', () => {
    expect(presenterOf('').line({}).label).toBe('工具调用')
  })

  it('★ 任何名字都能拿到 presenter,标签永不为空', () => {
    for (const n of ['', 'Read', 'mcp__a__b', '???', '__'.repeat(50)]) {
      expect(presenterOf(n).line({}).label).toBeTypeOf('string')
      expect(presenterOf(n).line({}).label).not.toBe('')
    }
  })
})

describe('注册表完整性', () => {
  /**
   * ★ 这条用例保护的是「新增内置工具忘了登记展示规则」。
   *
   * 注册表用数据分发而不是 switch,正是为了让这种遗漏能在测试期被发现 ——
   * switch 的 default 会让它静默通过,表现为新工具长得和 MCP 工具一模一样。
   *
   * 清单与 `src/main/kernel/tool/builtin/index.ts` 对齐。那边加了工具,
   * 这里会失败,提示同步 REGISTRY。
   */
  const BUILTIN_IDS = [
    'echo',
    'Read',
    'Write',
    'Edit',
    'LS',
    'Glob',
    'Grep',
    'Bash',
    'BashOutput',
    'KillShell',
    'TodoWrite',
    'WebFetch',
    'Skill',
    'web_search',
    'Task',
    // 工作流(实验)—— `workflow` 形态,详情区是画布
    'Workflow',
    'AskUserQuestion',
    'ProposeGoal',
    'ExitPlanMode',
    'ListScheduledTasks',
    'CreateScheduledTask',
    'UpdateScheduledTask',
    'DeleteScheduledTask',
    // 可视化那一对。`visualize_read_me` 走 `external`、`visualize_show_widget`
    // 是 `widget` 形态 —— 后者是**内置专用**的形态,见 `ToolShape` 上那段说明。
    'visualize_read_me',
    'visualize_show_widget',
    'visualize_edit_widget',
    'visualize_update_widget',
    // 对话内生图 —— `image` 形态(专属图片卡),行的主语是 prompt 片段
    'generate_image',
    // 把对话里的图写进工作区 —— `mutate` 形态,行的主语是目标文件
    'SaveImage',
    // 对话内生视频 —— `video` 形态(异步任务卡 + 播放器),行的主语是 prompt 片段
    'generate_video',
    // 把对话里的视频写进工作区 —— `mutate` 形态,同 SaveImage
    'SaveVideo',
    /*
      浏览器那一族(16 个 `browser_*`)。★ 整族此前**一个都没登记**,于是每一行
      都落进 `humanize(internalId)` 的可读名兜底 —— 中文界面上一列 `browser click` /
      `browser screenshot`。这是「新增内置工具忘了登记展示规则」那条不变式
      **唯一没守住的缺口**,把清单补全正是为了让这里重新变成一道闸门。
      形态怎么分的写在 `shared/domain/tool-presenter.ts` 的浏览器那一段。
    */
    'browser_open',
    'browser_navigate',
    'browser_snapshot',
    'browser_screenshot',
    'browser_click',
    'browser_type',
    'browser_press',
    'browser_select',
    'browser_scroll',
    'browser_cua_click',
    'browser_cua_drag',
    'browser_tabs',
    'browser_user_tabs',
    'browser_claim',
    'browser_profiles',
    'browser_close'
  ]

  it('每个内置工具都在注册表里', () => {
    const missing = BUILTIN_IDS.filter((id) => !isRegisteredTool(id))
    expect(missing).toEqual([])
  })

  /*
    ★ 生图原先归在 `network`,而 network 的详情渲染器成功时只画 output.content ——
    图在 output.images 里,界面上一张都看不到。形态退回 network 就是这个回归。
    ★ 截图(`browser_screenshot`)同理,但它先前连形态都没有,整族落进 `external`
    的通用 JSON 渲染器 —— 那里同样不读 `output.images`。以下是这两档的回归线。
  */
  it('image 与 screenshot 是「产物即回答」的形态,其余形态都随过程折叠', () => {
    expect(presenterOf('generate_image').shape).toBe('image')
    expect(presenterOf('browser_screenshot').shape).toBe('screenshot')
    expect(isPinnedShape('image')).toBe(true)
    expect(isPinnedShape('screenshot')).toBe(true)
    for (const shape of ['read', 'mutate', 'search', 'command', 'network', 'orchestration', 'interaction', 'widget', 'external', 'reasoning'] as const) {
      expect(isPinnedShape(shape), shape).toBe(false)
    }
  })

  /*
    ★ 截图是这一族里唯一产出**图**的那个,其余 15 个的结果都是文字。
    形态给错了不会报错,只表现为「模型看得见那张图、用户一个字也看不见」——
    所以这里连带钉住它的行主语(tabId)与标签都非空。
  */
  it('browser_screenshot 的行报出 tabId,且不落进 external 的通用 JSON', () => {
    const p = presenterOf('browser_screenshot')
    expect(p.shape).toBe('screenshot')
    expect(p.line({ tabId: 'tab-2' })).toMatchObject({ target: 'tab-2' })
    // 参数还在流(半截 JSON)时仍然给得出标签,不留半个 tabId
    expect(p.line('{"tabI').target).toBeUndefined()
    expect(p.line('{"tabI').label).not.toBe('')
  })

  it('注册表里没有多余的键', () => {
    const extra = registeredToolIds().filter((id) => !BUILTIN_IDS.includes(id))
    expect(extra).toEqual([])
  })

  it('每个注册项的标签在空入参下都可读', () => {
    for (const id of registeredToolIds()) {
      const { label } = presenterOf(id).line({})
      expect(label, `${id} 的标签为空`).not.toBe('')
      expect(label).not.toContain('undefined')
    }
  })
})

describe('presenterOf · 插件注入层', () => {
  beforeEach(() => { clearPluginPresenters() })

  it('注入后按 externalName 命中,并排在 humanize 兜底之前', () => {
    // 未注入时:插件工具的 externalName 落到 humanize 可读名兜底
    expect(presenterOf('plugin__acme_demo__make_thing').shape).toBe('external')
    registerPluginPresenters([
      ['plugin__acme_demo__make_thing', { shape: 'mutate', line: () => ({ label: '造个东西' }) }]
    ])
    const p = presenterOf('plugin__acme_demo__make_thing')
    expect(p.shape).toBe('mutate')
    expect(p.line({}).label).toBe('造个东西')
  })

  it('★ 注入的闭包对半截 JSON 也必须给出可读、无花括号的标签', () => {
    // 这里模拟渲染层构建的闭包:参数没齐就回退静态标题(见 stores/plugins.ts)
    registerPluginPresenters([
      [
        'plugin__acme_demo__make_thing',
        {
          shape: 'external',
          line: (input) => {
            const name = pick(input, 'name')
            return { label: name === '' ? '造个东西' : `造:${name}` }
          }
        }
      ]
    ])
    const p = presenterOf('plugin__acme_demo__make_thing')
    expect(p.line('{"na').label).toBe('造个东西') // 半截 JSON → 静态标题
    expect(p.line('{"na').label).not.toContain('{')
    expect(p.line({ name: '锤子' }).label).toBe('造:锤子')
  })

  it('clearPluginPresenters 复位,version 递增', () => {
    const v0 = pluginPresentersSnapshot()
    registerPluginPresenters([['plugin__acme_demo__x', { shape: 'external', line: () => ({ label: 'X' }) }]])
    expect(pluginPresentersSnapshot()).toBeGreaterThan(v0)
    expect(presenterOf('plugin__acme_demo__x').line({}).label).toBe('X')
    const v1 = pluginPresentersSnapshot()
    clearPluginPresenters()
    expect(pluginPresentersSnapshot()).toBeGreaterThan(v1)
    // 复位后回到 humanize 兜底
    expect(presenterOf('plugin__acme_demo__x').shape).toBe('external')
    expect(presenterOf('plugin__acme_demo__x').line({}).label).not.toBe('X')
  })
})
