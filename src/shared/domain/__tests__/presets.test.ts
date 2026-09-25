import { describe, expect, it } from 'vitest'
import { joinUpstreamUrl, normalizeBaseUrl, previewUrl, REQUEST_PATH } from '../baseurl'
import {
  BUILTIN_PROVIDER_ID,
  endpointFor,
  findPreset,
  PROVIDER_PRESETS,
  presetsByCategory,
  recommendedPresets,
  OPENCODE_GO_PROVIDER_ID,
  type ProviderPreset
} from '../presets'

/**
 * 预设表的结构断言(方案 §8)。
 *
 * ★ 这里测的**不是「数据对不对」** —— 一个 base URL 是不是真的能通,只有网络能回答,
 * 而采集时的 `curl` 探针已经回答过一次了(核实等级记在 `verification` 里)。
 * 这个文件守的是另一样东西:**四十多条手抄的数据里,有没有哪一条的形状是坏的。**
 * 形状坏了的表现全都是「配置页看着正常、请求 404」,没有一条会在别处报错。
 */

const forEachEndpoint = (
  fn: (p: ProviderPreset, e: ProviderPreset['endpoints'][number]) => void
): void => {
  for (const p of PROVIDER_PRESETS) for (const e of p.endpoints) fn(p, e)
}

describe('预设表 · 主键与完整性', () => {
  it('id 唯一', () => {
    const ids = PROVIDER_PRESETS.map((p) => p.id)
    expect(new Set(ids).size).toBe(ids.length)
  })

  it('name 唯一 —— 卡片网格里两张同名卡等于让用户抛硬币', () => {
    const names = PROVIDER_PRESETS.map((p) => p.name)
    expect(new Set(names).size).toBe(names.length)
  })

  it('endpoints 非空', () => {
    for (const p of PROVIDER_PRESETS) expect(p.endpoints.length, p.id).toBeGreaterThan(0)
  })

  /**
   * ★ 同一协议出现两次的话,`endpointFor` 拿到的是**先写的那条**,
   * 而后写的那条在界面上完全不可达 —— 一条永远不会被执行的数据,
   * 且没有任何运行时症状。
   */
  it('同一 protocol 在一条预设里不重复', () => {
    for (const p of PROVIDER_PRESETS) {
      const seen = p.endpoints.map((e) => e.protocol)
      expect(new Set(seen).size, p.id).toBe(seen.length)
    }
  })

  it('docsUrl 是 https —— 配不通时它是用户唯一的出路', () => {
    for (const p of PROVIDER_PRESETS) {
      expect(p.docsUrl, p.id).toMatch(/^https:\/\//)
      expect(() => new URL(p.docsUrl)).not.toThrow()
    }
  })

  it('apiKeyUrl 若存在则必须是可解析的 https 地址', () => {
    for (const p of PROVIDER_PRESETS) {
      const apiKeyUrl = p.apiKeyUrl
      if (apiKeyUrl === undefined) continue
      expect(apiKeyUrl, p.id).toMatch(/^https:\/\//)
      expect(() => new URL(apiKeyUrl)).not.toThrow()
    }
  })

  it('本地运行时不声明外部 API Key 获取页面', () => {
    for (const p of PROVIDER_PRESETS) {
      if (p.category === 'local') expect(p.apiKeyUrl, p.id).toBeUndefined()
    }
  })

  it('非标准凭证类型必须同时提供获取页面', () => {
    for (const p of PROVIDER_PRESETS) {
      /*
        ★ `oauth` 是这条规则的**真例外**,不是漏填。
        这条规则守的是「按钮说『获取 Access Key』,就得真能点过去拿到」;
        而 OAuth 那家根本没有「创建密钥」这个页面 —— 拿凭证的方式就是登录本身。
        硬给一个近似链接会把人送到 platform.openai.com 去建一把**用不上**的 key。
      */
      if (p.credentialKind !== undefined && p.credentialKind !== 'oauth') {
        expect(p.apiKeyUrl, p.id).toBeDefined()
      }
    }
  })

  /*
    ★★ **这条配对从「双向」放宽成了「单向」,是一次故意的语义变更。**

    原来的第二句(`oauthIssuer` ⇒ `credentialKind === 'oauth'`)把「能登录」和
    「没有可粘贴的密钥」焊成了同一件事。而 GLM Coding Plan 这两家**两样都有**:
    官方既发订阅 key、也能用账号登录。焊在一起的后果是接了登录就得把
    `credentialKind` 改成 `'oauth'`,于是**已经在用订阅 key 的用户的输入框会消失**,
    而他手里那把 key 还是好的 —— 一次纯粹由我们的数据模型造成的功能倒退。

    ★ 第一句必须留着:它守的是「画得出登录按钮」。没有 issuer 的 `'oauth'` 预设
    会退化成一个填不了的密钥框,那才是真的漏填。
  */
  it('★ credentialKind 为 oauth 的预设必须声明 issuer —— 否则退化成一个填不了的密钥框', () => {
    for (const p of PROVIDER_PRESETS) {
      if (p.credentialKind === 'oauth') expect(p.oauthIssuer, p.id).toBeDefined()
    }
  })

  it('★ 声明了 oauthIssuer 但仍有密钥的预设，必须给得出拿密钥的地址（两种凭证都得能拿到）', () => {
    for (const p of PROVIDER_PRESETS) {
      if (p.oauthIssuer !== undefined && p.credentialKind !== 'oauth') {
        expect(p.apiKeyUrl, p.id).toBeDefined()
      }
    }
  })

  it('★ GLM Coding Plan 两条：订阅密钥与账号登录并存', () => {
    for (const id of ['zai-coding', 'zhipu-coding'] as const) {
      const p = PROVIDER_PRESETS.find((x) => x.id === id)
      expect(p?.credentialKind, id).toBe('subscription-key')
      expect(p?.oauthIssuer, id).toBeDefined()
    }
  })

  it.each([
    ['kimi-coding', 'subscription-key'],
    ['zhipu-coding', 'subscription-key'],
    ['zai-coding', 'subscription-key'],
    ['routin-plan', 'subscription-key'],
    ['sensenova', 'access-key'],
    ['spark', 'api-password']
  ] as const)('%s 使用准确的凭证称呼', (id, kind) => {
    expect(findPreset(id)?.credentialKind).toBe(kind)
  })

  /**
   * ★★ **GLM 两条 Coding Plan 的首选端点必须是 coding 端点。**
   *
   * `endpoints[0]` 就是 `providerFromPreset` 取的那条(见 `provider-edit.ts`),
   * 而智谱官方 FAQ 把 `…/api/anthropic` 限定成**仅限从未购买过 Coding Plan
   * 且额外加白的账号**(2026-09-08 核对)。排反了的表现是:买了套餐的用户
   * 添加完第一次请求就 401,而上游回的「令牌已过期或验证不正确」**一个字
   * 都不提端点选错了** —— 他只会去反复检查那把其实没问题的 key。
   *
   * ⚠️ **不要把这条改写成「订阅制预设的 endpoints[0] 不能是 anthropic」这类
   * 通用不变量** —— Kimi Coding Plan 的首选端点就是 `api.kimi.com/coding`
   * 那条 anthropic 端点(它的 notes 写明该域名下只有 /coding 前缀可用),
   * 通用不变量会误伤它。这里只钉这两家。
   */
  it.each([
    ['zhipu-coding', 'https://open.bigmodel.cn/api/coding/paas/v4'],
    ['zai-coding', 'https://api.z.ai/api/coding/paas/v4']
  ] as const)('%s 的首选端点是 coding 端点,不是仅限加白账号的 anthropic 端点', (id, baseUrl) => {
    const first = findPreset(id)?.endpoints[0]
    expect(first?.baseUrl).toBe(baseUrl)
    expect(first?.protocol).toBe('openai-chat')
    // ★ anth 端点不删,只是降到第二位:加白账号用得上,它同时是「API 格式」开关的候选池
    expect(findPreset(id)?.endpoints.some((e) => e.protocol === 'anthropic')).toBe(true)
  })
})

describe('预设表 · baseUrl 形状', () => {
  it('都能被 new URL() 解析', () => {
    forEachEndpoint((p, e) => {
      expect(() => new URL(e.baseUrl), `${p.id}/${e.protocol}`).not.toThrow()
    })
  })

  /**
   * ★ **http 只允许本地。** 一条写错成 http 的云端地址意味着 API key 明文过网 ——
   * 这是本表里唯一一种「形状错了会直接变成安全问题」的错。
   */
  it('非本地一律 https;本地一律 http + 127.0.0.1', () => {
    forEachEndpoint((p, e) => {
      const u = new URL(e.baseUrl)
      if (p.category === 'local') {
        expect(u.protocol, p.id).toBe('http:')
        expect(u.hostname, `${p.id} —— 本地地址必须写 127.0.0.1,不能写 localhost`).toBe('127.0.0.1')
      } else {
        expect(u.protocol, p.id).toBe('https:')
      }
    })
  })

  /**
   * ★★ 这一条是**两个模块之间的对表**:预设表里的地址,必须是
   * `normalizeBaseUrl` 的**不动点**。
   *
   * 不成立的话表现很具体:用户从预设填进来一个地址,光标进出一下输入框,
   * **地址就自己变了** —— 看上去像应用在乱改他的配置。
   * 顺带它还免费守住了「不带尾斜杠」和 Gemini 那条尾斜杠例外:
   * 两边任何一侧改坏了,这里都会红。
   */
  it('每条 baseUrl 都是 normalizeBaseUrl 的不动点', () => {
    forEachEndpoint((p, e) => {
      expect(normalizeBaseUrl(e.baseUrl), `${p.id}/${e.protocol}`).toBe(e.baseUrl)
    })
  })

  it('Gemini 的尾斜杠保住了,别人都没有尾斜杠', () => {
    const gemini = findPreset('gemini-openai')
    expect(gemini?.endpoints[0]?.baseUrl).toBe(
      'https://generativelanguage.googleapis.com/v1beta/openai/'
    )
    forEachEndpoint((p, e) => {
      if (p.id === 'gemini-openai') return
      expect(e.baseUrl.endsWith('/'), `${p.id}/${e.protocol}`).toBe(false)
    })
  })
})

/**
 * ★★ 本文件最重要的一组:**把每条预设真的拼一遍**。
 *
 * `REQUEST_PATH` 写错过一次(版本段放进了 path),而症状只在拼接之后才看得见 ——
 * 预设表本身、`joinUpstreamUrl` 本身,单看都是对的。所以对表要在拼出来的成品上做。
 */
describe('预设表 × joinUpstreamUrl:拼出来的 URL 没有畸形', () => {
  it('没有任何一条拼出重复的版本段', () => {
    forEachEndpoint((p, e) => {
      const url = previewUrl(e.baseUrl, e.protocol)
      expect(url, `${p.id}/${e.protocol}`).not.toMatch(/\/v1\/v1\//)
      // 协议名后面那个 `//` 之外,不该再出现双斜杠
      expect(url.replace(/^https?:\/\//, ''), `${p.id}/${e.protocol}`).not.toContain('//')
    })
  })

  it('拼出来的仍是同一个 host,且路径以协议要求的端点结尾', () => {
    forEachEndpoint((p, e) => {
      const url = previewUrl(e.baseUrl, e.protocol)
      expect(new URL(url).host, `${p.id}/${e.protocol}`).toBe(new URL(e.baseUrl).host)
      expect(url.endsWith(REQUEST_PATH[e.protocol]), `${p.id}/${e.protocol}`).toBe(true)
    })
  })

  /**
   * 抽查几条**前缀最刁钻**的,把结果写死。上面那两条是通用不变量,
   * 这一条是「这几家的地址到底该长什么样」—— 它们正是当初被 `/v1/chat/completions`
   * 拼坏的那几家。
   */
  it.each([
    ['deepinfra', 'openai-chat', 'https://api.deepinfra.com/v1/openai/chat/completions'],
    ['deepinfra', 'anthropic', 'https://api.deepinfra.com/anthropic/v1/messages'],
    ['zhipu', 'openai-chat', 'https://open.bigmodel.cn/api/paas/v4/chat/completions'],
    ['zhipu-coding', 'anthropic', 'https://open.bigmodel.cn/api/anthropic/v1/messages'],
    ['volcengine', 'openai-chat', 'https://ark.cn-beijing.volces.com/api/v3/chat/completions'],
    ['qianfan', 'openai-chat', 'https://qianfan.baidubce.com/v2/chat/completions'],
    [
      'gemini-openai',
      'openai-chat',
      'https://generativelanguage.googleapis.com/v1beta/openai/chat/completions'
    ],
    ['groq', 'openai-chat', 'https://api.groq.com/openai/v1/chat/completions'],
    ['fireworks', 'anthropic', 'https://api.fireworks.ai/inference/v1/messages'],
    ['openrouter', 'anthropic', 'https://openrouter.ai/api/v1/messages'],
    ['requesty', 'anthropic', 'https://router.requesty.ai/v1/messages'],
    ['kimi-coding', 'anthropic', 'https://api.kimi.com/coding/v1/messages'],
    ['kimi-coding', 'openai-chat', 'https://api.kimi.com/coding/v1/chat/completions'],
    ['routin-plan', 'openai-responses', 'https://api.routin.ai/plan/v1/responses'],
    ['routin-plan', 'anthropic', 'https://api.routin.ai/plan/v1/messages'],
    ['xai', 'openai-responses', 'https://api.x.ai/v1/responses'],
    ['ollama', 'anthropic', 'http://127.0.0.1:11434/v1/messages'],
    ['ollama-cloud', 'openai-chat', 'https://ollama.com/v1/chat/completions'],
    ['ollama-cloud', 'anthropic', 'https://ollama.com/v1/messages']
  ] as const)('%s / %s', (id, protocol, expected) => {
    const e = endpointFor(findPreset(id)!, protocol)
    expect(e, `${id} 没有 ${protocol} 端点`).not.toBeNull()
    expect(previewUrl(e!.baseUrl, protocol)).toBe(expected)
  })

  /**
   * ★★ RoutinAI 的两条线**是两家**,合并它们会静默打错服务。
   *
   * 按量走 `api.routin.ai` 裸域名 + `/v1`,订阅(Plan)走 `/plan` + `/plan/v1`;
   * 两边的 401 信封都不一样(订阅是标准 OpenAI/Anthropic 信封,按量是网关自己的
   * `{"code":401,…,"title":"未授权访问"}`)。真正会咬人的是这两条:
   *
   * - **订阅线没有 chat/completions**(实测 404)。哪天有人「顺手补齐三种协议」,
   *   用户在「API 格式」里翻到 OpenAI Chat 会得到一个必然 404 的配置。
   * - **按量线没有 openai-responses**,理由见 `presets.ts` 里那段注释 —— 证据不足,
   *   不是漏写。加上它同样是个永远连不上的开关。
   *
   * 两条都不报错,都只表现为「表单看着正常、请求失败」。
   */
  it('★ RoutinAI 按量与订阅是两条独立的线,协议集合不能互相污染', () => {
    const pay = findPreset('routin')!
    const plan = findPreset('routin-plan')!

    expect(plan.endpoints.map((e) => e.protocol).sort()).toEqual(['anthropic', 'openai-responses'])
    expect(pay.endpoints.map((e) => e.protocol).sort()).toEqual(['anthropic', 'openai-chat'])

    // 两家的 baseUrl 没有任何重叠 —— 订阅 key 打按量地址(或反过来)只会 401
    const payBases = new Set(pay.endpoints.map((e) => e.baseUrl))
    for (const e of plan.endpoints) expect(payBases.has(e.baseUrl), e.baseUrl).toBe(false)
  })

  /**
   * ★ 两个协议的 base 差一段 `/v1`,但模型列表要拼到**同一个** `/plan/v1/models`
   * (实测 401,端点存在)。这条对的是 `model-list.ts` 的 `LIST_PATH`:
   * Anthropic 是 `/v1/models`,OpenAI 族是 `/models`。哪天有人「统一」成一种,
   * 两条里必然有一条拼歪,而症状是「拉取模型列表」按钮点下去 404。
   */
  it('★ RoutinAI Plan:两种协议拼出同一个模型列表地址', () => {
    const plan = findPreset('routin-plan')!
    const resp = endpointFor(plan, 'openai-responses')!
    const anth = endpointFor(plan, 'anthropic')!
    expect(joinUpstreamUrl(resp.baseUrl, '/models')).toBe('https://api.routin.ai/plan/v1/models')
    expect(joinUpstreamUrl(anth.baseUrl, '/v1/models')).toBe('https://api.routin.ai/plan/v1/models')
    // 两条都声称能拉,上面那两个地址就是它们的依据
    expect(resp.supportsModelList).toBe(true)
    expect(anth.supportsModelList).toBe(true)
  })

  /**
   * ★ OpenCode Go 是全表**唯一**一条 Anthropic base 自带 `/v1` 的 ——
   * 那个 `/v1` 是它的路由前缀,不是 Anthropic 的版本段。
   * 去重分支必须在这里生效,否则拼成 `/zen/go/v1/v1/messages`。
   * 这也是那个分支今天还活着的理由之一,所以单独钉一条。
   */
  it('OpenCode Go:去重分支把 /zen/go/v1 + /v1/messages 拼成实测存在的那个端点', () => {
    const e = endpointFor(findPreset('opencode-go')!, 'anthropic')
    expect(previewUrl(e!.baseUrl, 'anthropic')).toBe('https://opencode.ai/zen/go/v1/messages')
    // 直接对 joinUpstreamUrl 再钉一次,免得将来 previewUrl 换了实现就没人守这条了
    expect(joinUpstreamUrl('https://opencode.ai/zen/go/v1', '/v1/messages')).toBe(
      'https://opencode.ai/zen/go/v1/messages'
    )
  })

  /**
   * ★ `kernel/upstream/transport.ts` 按这个常量判「该不该发 `x-opencode-session`」。
   * 表里那条改了 id 而常量没跟着改的话,**不报错,只是判断从此恒为 false** ——
   * 表现是 OpenCode 又开始拒每一次对话,且没有任何线索指向那次重命名。
   */
  it('★ OPENCODE_GO_PROVIDER_ID 确实指向表里那条', () => {
    expect(findPreset(OPENCODE_GO_PROVIDER_ID)).not.toBeNull()
  })
})

describe('预设表 · 模型列表', () => {
  /** 免鉴权是「能拉」的**加强**版本,不可能免鉴权却拉不了 */
  it('modelListPublic 蕴含 supportsModelList', () => {
    forEachEndpoint((p, e) => {
      if (e.modelListPublic === true) expect(e.supportsModelList, p.id).toBe(true)
    })
  })

  it('实测免鉴权的那五家标了 modelListPublic', () => {
    const publics = PROVIDER_PRESETS.filter((p) =>
      p.endpoints.some((e) => e.modelListPublic === true)
    ).map((p) => p.id)
    expect(new Set(publics)).toEqual(
      new Set(['openrouter', 'requesty', 'deepinfra', 'opencode-go', 'ollama-cloud'])
    )
  })
})

describe('预设表 · 分类与推荐', () => {
  /**
   * ★ 「推荐」是**跨类别的精选,不是第五个类别**(见 `PresetCategory` 的注释)——
   * 所以每条推荐必然也属于四类之一,不会因为进了推荐就从原分类里消失。
   */
  it('四个分类加起来正好是全表,推荐是它们的子集', () => {
    const cats = ['domestic', 'aggregator', 'overseas', 'local'] as const
    const sum = cats.reduce((n, c) => n + presetsByCategory(c).length, 0)
    expect(sum).toBe(PROVIDER_PRESETS.length)
    for (const p of recommendedPresets()) expect(cats).toContain(p.category)
  })

  it('每个分类都非空 —— 五张 Tab 里有一张是空的就说明数据漏了', () => {
    for (const c of ['domestic', 'aggregator', 'overseas', 'local'] as const) {
      expect(presetsByCategory(c).length, c).toBeGreaterThan(0)
    }
    expect(recommendedPresets().length).toBeGreaterThan(0)
  })

  /**
   * ★★ 内置上游排「推荐服务」第一位。
   *
   * 它是全新安装唯一被种进供应商表、并且 `defaultModel` 指着的那一家
   * (`main/runtime.ts` 的 `seedBuiltinUpstream`)—— 目录里让它排第一,
   * 是让「已经给你配好的那家」和「列表里第一张卡片」是同一家。
   *
   * 这条顺序**不靠 `PROVIDER_PRESETS` 里的行号**:那张表按类别分块写,
   * RoutinAI 在 `aggregator` 那一块的中间。`recommendedPresets()` 按
   * `BUILTIN_PROVIDER_ID` 把它提到最前,所以将来换一家内置上游,
   * 改那一个常量就够了 —— 这条断言就是那句话的证据。
   */
  it('★ 推荐服务第一个是内置上游,哪怕它在表里排在中间', () => {
    const rec = recommendedPresets()
    expect(rec[0]?.id).toBe(BUILTIN_PROVIDER_ID)
    // 提上来的、不是碰巧写在表头的 —— 表里它并不在最前
    const raw = PROVIDER_PRESETS.filter((p) => p.recommended === true)
    expect(raw.findIndex((p) => p.id === BUILTIN_PROVIDER_ID)).toBeGreaterThan(0)
    // 只提不塞:剩下那些的相对次序一个都没动
    expect(rec.slice(1).map((p) => p.id)).toEqual(
      raw.filter((p) => p.id !== BUILTIN_PROVIDER_ID).map((p) => p.id)
    )
  })

  it('内置上游确实在表里,且标着推荐 —— 少一样第一位就不成立', () => {
    const builtin = findPreset(BUILTIN_PROVIDER_ID)
    expect(builtin).not.toBeNull()
    expect(builtin?.recommended).toBe(true)
  })

  it('presetsByCategory 保持表内顺序', () => {
    const local = presetsByCategory('local').map((p) => p.id)
    expect(local).toEqual(PROVIDER_PRESETS.filter((p) => p.category === 'local').map((p) => p.id))
  })
})

describe('预设表 · 核实等级与 notes', () => {
  /**
   * ★ `unverified` 不是瑕疵,是**本表最该有的东西** —— 采集环境网络不可达的那几家,
   * 与其编一个像模像样的地址,不如明说没核实。这条测试防的是相反的事:
   * 有人「顺手」把角标清掉,让一条来路不明的地址看起来和实测过的一样可信。
   */
  it('未核实的那几家仍然带着角标,且 notes 里说了原因', () => {
    const unverified = PROVIDER_PRESETS.filter((p) => p.verification === 'unverified')
    expect(unverified.map((p) => p.id).sort()).toEqual(['302ai', 'aihubmix', 'ohmygpt', 'spark'])
    for (const p of unverified) expect(p.notes ?? '', p.id).toContain('未')
  })

  /**
   * ★ 方案 §8:「所以 `notes` 是必需字段,不是装饰」—— 说的就是这几家按月/按积分
   * 计费的 Coding Plan。「配了半天 401」的头号原因是它们的 key 与按量 key 不通用,
   * 而这件事只有 notes 能说。
   *
   * ★ 名单写死,不是从某个字段筛出来的:这条约束的对象是**具体这几家**,
   * 筛选条件一旦被改窄,测试会静默地什么都不测。
   */
  it.each(['codex', 'kimi-coding', 'zhipu-coding', 'zai-coding', 'routin-plan', 'opencode-go'])(
    '%s 必须有 notes',
    (id) => {
      expect((findPreset(id)?.notes ?? '').length, id).toBeGreaterThan(0)
    }
  )

  it('supportsModelList 为 false 的不会被推荐去点那个按钮', () => {
    // 纯一致性:没有任何预设声称能拉列表却一条 endpoint 都不支持
    for (const p of PROVIDER_PRESETS) {
      if (p.endpoints.every((e) => !e.supportsModelList)) {
        expect(
          p.endpoints.some((e) => e.modelListPublic === true),
          p.id
        ).toBe(false)
      }
    }
  })
})

describe('查找函数', () => {
  it('findPreset 命中与落空', () => {
    expect(findPreset('openai')?.name).toBe('OpenAI')
    expect(findPreset('nope')).toBeNull()
  })

  /**
   * ★★ 找不到**必须返回 null,不能退回第一条**。
   * 退回去的表现是:界面显示「Anthropic 格式」,地址却是 OpenAI 那条 ——
   * 正是 §8 那张前缀对照表要防的静默失效。
   */
  it('endpointFor 找不到时返回 null,不退回第一条', () => {
    const groq = findPreset('groq')!
    expect(endpointFor(groq, 'anthropic')).toBeNull()
    expect(endpointFor(groq, 'openai-chat')?.baseUrl).toBe('https://api.groq.com/openai/v1')
  })
})
