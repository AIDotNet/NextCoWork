/**
 * 内置供应商预设 —— 方案 §8。**纯数据 + 类型,没有任何逻辑。**
 *
 * 数据来自子代理实测采集(报告:`~/.claude/plans/distributed-jingling-snowglobe-agent-a47e66b04e7079eb0.md`),
 * 判据是 **`curl` 探针 + 同前缀假路径对照** —— 只有「真路径 401 + 假路径 404」才算数。
 * 这道对照不是讲究:DeepSeek / 火山 / 讯飞星火 / 智谱 `/api/paas/v4` 这几家对**任意**路径
 * 都返回 401,不做对照拿到的全是假阳性。
 *
 * ★ **这张表是会腐烂的**,而且腐烂得比代码快 —— 调研当场就实测到老别名
 * `deepseek-chat` / `deepseek-reasoner` 已于 2026-07-24 下线。所以:
 * `suggestedModels` 只是**冷启动兜底**,真正的模型列表靠运行时 `GET {base}/models` 拉。
 */
import type { UpstreamProtocol } from './provider'
import type { OAuthIssuerId } from './oauth-issuer'

/**
 * ★★ **baseUrl 挂在 endpoint 上,不挂在预设上。** 这是本文件的核心结构,
 * 也是调研推翻我第一版设计的地方。
 *
 * §1.1 把「API 格式」做成了用户随手能翻的开关,可**同一家厂商两个协议的路径前缀
 * 往往不同**(实测,不是个别现象):
 *
 * | 厂商 | OpenAI base | Anthropic base |
 * |---|---|---|
 * | DeepInfra | `…/v1/openai` | `…/anthropic` |
 * | OpenRouter | `…/api/v1` | `…/api` |
 * | Fireworks | `…/inference/v1` | `…/inference` |
 * | 智谱 | `…/api/paas/v4` | `…/api/anthropic` |
 * | 火山方舟 | `…/api/v3` | `…/api/compatible` |
 * | 硅基流动 / Together / 阶跃星辰 | `…/v1` | 裸域名 |
 *
 * 扁平结构下,用户翻一下那个开关地址就**静默失效** —— 表单看着完全正常,请求 404。
 */
export interface ProviderEndpoint {
  protocol: UpstreamProtocol
  /**
   * 规范形式:**不带尾斜杠**(Gemini 兼容层是唯一例外,见 `baseurl.ts`)。
   *
   * ★ OpenAI 族的**版本段是这里的一部分**(`/v1`、`/api/paas/v4`、`/inference/v1` …),
   * Anthropic 族则**不带** `/v1`(客户端自己补 `/v1/messages`)。
   * 两族约定是反的,理由和证据见 `baseurl.ts` 的 `REQUEST_PATH`。
   */
  baseUrl: string
  /**
   * 能否 `GET {baseUrl}/models`。false 时置灰「从服务商拉取模型列表」按钮,
   * 而不是让用户点了再报错。
   *
   * ★ **只在拿到证据时才 true。** 「没证据」和「确认没有」都记 false ——
   * 因为这个字段唯一的消费者是一个按钮的可点性,而一个点下去必然失败的按钮
   * 比一个灰着的按钮更糟。智谱 / Z.AI / 火山这几家的探针被 catch-all 401 废掉了,
   * 所以它们是 false,不代表上游真的没有。
   *
   * ★ 拉列表的 URL 是 **`{baseUrl}/models`**,不要拿 `joinUpstreamUrl` 去拼 ——
   * 那个函数是给**请求路径**用的,带着 Anthropic 的 `/v1` 去重逻辑。
   * OpenAI 族的 base 自带版本段,直接接 `/models` 就对;Anthropic 族是
   * `{baseUrl}/v1/models`。
   */
  supportsModelList: boolean
  /** 免鉴权就能拉列表(OpenRouter / DeepInfra / OpenCode Go 实测 200)—— 可以做到「key 还没填就先看有哪些模型」 */
  modelListPublic?: boolean
}

/**
 * ★ 参考图的第一个 Tab 是「推荐服务」,但调研报告写得很清楚:
 * **它是从其余四类里挑出来的精选,不是第五个独立类别。**
 *
 * 所以这里是**四个值 + 一个布尔**,而不是方案 §8 写的五值联合 ——
 * 五值联合下 OpenAI 会因为「进了推荐」而从「海外平台」里消失,
 * 用户翻海外平台那一栏找不到 OpenAI。这是照方案原样写会得到的界面 bug,
 * 所以这里**故意偏离方案**,偏离的理由写在这。
 */
export type PresetCategory = 'domestic' | 'aggregator' | 'overseas' | 'local'
export type ProviderCredentialKind =
  | 'api-key'
  | 'access-key'
  | 'api-password'
  | 'subscription-key'
  /**
   * ★ 没有可粘贴的密钥,整个字段换成一颗登录按钮(见 `ProviderPanel`)。
   *
   * 不复用 `subscription-key`:今天 `credentialKind` 只决定「获取 xxx」那颗按钮的
   * **措辞**,而这个值还要**把输入框整个换掉**。复用的话,现有那几家
   * `subscription-key` 的供应商会莫名其妙失去输入框。
   */
  | 'oauth'

export interface ProviderPreset {
  id: string
  name: string
  category: PresetCategory
  /** 第一个 Tab「推荐服务」= 这个标记为真的那些,按本表顺序 */
  recommended?: boolean
  /** 至少一条。翻「API 格式」开关时按 protocol 在这里查地址 */
  endpoints: readonly ProviderEndpoint[]
  docsUrl: string
  /** 官方创建、查看或轮换 API Key 的控制台入口；没有可靠直达地址时不提供 */
  apiKeyUrl?: string
  /** 少数供应商使用 AK/SK、API Password 或订阅凭证，避免按钮给出错误称呼 */
  credentialKind?: ProviderCredentialKind
  /**
   * ★★ **有它 = 这家走账号登录,值决定用哪套 OAuth 流程;没有 = 老样子粘 API Key。**
   *
   * 这条映射必须是**数据**,不能是 `if (id === 'codex')`:登录、刷新、发请求
   * 三处都要知道「这家怎么登」,写成分支就要在三个文件里各加一次,而漏掉第三处的
   * 表现是登录成功、第一次对话 403。
   *
   * ★ 只有**界面**查这个字段(该画登录按钮还是画输入框)。运行时(router 发请求、
   * resolver 刷 token)读的是凭证自带的 `issuer` —— 凭证自解释,用户改名换地址都不影响。
   */
  oauthIssuer?: OAuthIssuerId
  suggestedModels: readonly string[]
  /** 该预设特有的坑,直接显示在表单下方 */
  notes?: string
  /**
   * 本条数据的核实等级,**取该预设最弱的一条 endpoint**。
   *
   * - `probed` —— 探针确认(真路径 401 + 同前缀假路径 404)
   * - `documented` —— 官方文档/官方站点提取,未能实测
   * - `unverified` —— 证据不足,卡片上带角标
   *
   * 让「未核实」进代码而不是停在报告里:用户配失败时知道该去查文档,
   * 而不是怀疑自己填错了。
   */
  verification: 'probed' | 'documented' | 'unverified'
}

/** 本地运行时一律 `127.0.0.1`,理由见文件末尾的 `LOCAL_NOTE` */
const oa = (
  baseUrl: string,
  supportsModelList: boolean,
  modelListPublic?: boolean
): ProviderEndpoint => ({
  protocol: 'openai-chat',
  baseUrl,
  supportsModelList,
  ...(modelListPublic === undefined ? {} : { modelListPublic })
})

const resp = (baseUrl: string, supportsModelList: boolean): ProviderEndpoint => ({
  protocol: 'openai-responses',
  baseUrl,
  supportsModelList
})

const anth = (baseUrl: string, supportsModelList = false): ProviderEndpoint => ({
  protocol: 'anthropic',
  baseUrl,
  supportsModelList
})

/**
 * ★ 本地地址**一律写 `127.0.0.1` 而不是 `localhost`**。
 *
 * Node 17+ 起不再重排 DNS 结果,部分系统会把 `localhost` 优先解析到 IPv6 `::1`,
 * 而这些运行时默认只监听 IPv4 —— 表现是「浏览器里能打开、应用里 ECONNREFUSED」。
 * 这是本地模型接入最高频的一类假故障(同类问题见 microsoft/vscode#189805),
 * 预设里直接写死,不给用户踩坑的机会。
 */
const LOCAL_NOTE = '无需真实密钥,随便填一个占位串即可。'

export const PROVIDER_PRESETS: readonly ProviderPreset[] = [
  // ────────────────────────── 海外平台 ──────────────────────────
  {
    id: 'openai',
    name: 'OpenAI',
    category: 'overseas',
    recommended: true,
    endpoints: [oa('https://api.openai.com/v1', true), resp('https://api.openai.com/v1', true)],
    docsUrl: 'https://developers.openai.com/api/docs',
    apiKeyUrl: 'https://platform.openai.com/api-keys',
    suggestedModels: ['gpt-5.6-luna-pro', 'gpt-5.6-terra', 'gpt-5.6-sol', 'gpt-5.5-pro'],
    verification: 'documented'
  },
  {
    /*
      Codex —— **第一家走账号登录的供应商。** 用 ChatGPT 账号登录,请求走订阅额度,
      不计入 API 账单。

      ★★ **只有一条 endpoint,而且故意不给 `openai-chat`。**
      `chatgpt.com/backend-api/codex` 下**没有** `/chat/completions`。给了的话,
      用户在设置页翻一下「使用 Responses API」开关,协议就变成 openai-chat、
      地址原样留着,请求打到 `…/codex/chat/completions` → 404,而表单看着完全正常。
      这正是 `provider-edit.ts` 文件头点名要防的那类静默失效。
      (界面那边还会把这两个协议控件整个藏起来,双保险。)

      ★ 没有 `apiKeyUrl`:这家根本没有「创建 API Key」这个页面。给一个近似链接
      会把人送到 platform.openai.com 去建一把**用不上**的 key。

      ★ 不进「推荐服务」:推荐位是首屏那几张卡,而这条要走完一次浏览器授权才有用。
      把一个「点进去还得登录」的卡片放首位,对第一次打开设置的人是负担。
    */
    id: 'codex',
    name: 'Codex(ChatGPT 订阅)',
    category: 'overseas',
    endpoints: [resp('https://chatgpt.com/backend-api/codex', false)],
    docsUrl: 'https://developers.openai.com/codex/cli',
    credentialKind: 'oauth',
    oauthIssuer: 'chatgpt',
    suggestedModels: ['gpt-5.6-sol', 'gpt-5.6-luna', 'gpt-5.6-terra', 'gpt-5.5', 'gpt-6-astra'],
    notes:
      '用 ChatGPT 账号登录，走订阅额度，不计入 API 账单。' +
      '★ 需要付费 ChatGPT 计划：免费账号能登录成功，但第一次对话会返回 403。' +
      '★ 这条通道没有模型列表端点，模型名来自内置建议表，可以手动增删。',
    verification: 'documented'
  },
  {
    id: 'anthropic',
    name: 'Anthropic',
    category: 'overseas',
    recommended: true,
    endpoints: [anth('https://api.anthropic.com', true)],
    docsUrl: 'https://platform.claude.com/docs/en/api/overview',
    apiKeyUrl: 'https://platform.claude.com/settings/keys',
    suggestedModels: ['claude-opus-5', 'claude-sonnet-5', 'claude-fable-5.1', 'claude-opus-4.8'],
    verification: 'documented'
  },
  {
    id: 'gemini-openai',
    name: 'Google Gemini(OpenAI 兼容)',
    category: 'overseas',
    recommended: true,
    /*
      ★ 尾斜杠是**故意留着**的:官方 Python 示例就是这个形状,`baseurl.ts` 为它开了例外。
      兼容层的端点是 `/v1beta/openai/chat/completions` —— 版本段在 base 里,
      所以这一条和 DeepInfra、智谱那几家是同一类,不是特例。
    */
    endpoints: [oa('https://generativelanguage.googleapis.com/v1beta/openai/', true)],
    docsUrl: 'https://ai.google.dev/gemini-api/docs/openai',
    apiKeyUrl: 'https://aistudio.google.com/app/apikey',
    suggestedModels: ['gemini-3.8-flash', 'gemini-3.5-flash', 'gemini-3.1-pro'],
    notes:
      '兼容层不支持 Responses API。★ 流式响应的**每个 chunk 都带 usage**(官方 Current limitations),' +
      '朴素累加会让 token 数虚高数倍 —— 取最后一个,不要相加。',
    verification: 'documented'
  },
  {
    id: 'xai',
    name: 'xAI Grok',
    category: 'overseas',
    endpoints: [resp('https://api.x.ai/v1', true), oa('https://api.x.ai/v1', true)],
    docsUrl: 'https://docs.x.ai/overview',
    suggestedModels: ['grok-4.6', 'grok-4.5', 'grok-4.20'],
    notes: 'Responses API 是官方主推端点,chat/completions 已被归入 legacy/。',
    verification: 'documented'
  },
  {
    /*
      ★★ **和上面那条 `xai` 是两家,不是一家的两种付款方式。**
      `api.x.ai` 吃 API Key、计 API 账单;这条走 `cli-chat-proxy.grok.com`,
      凭证是 Grok 账号登录换来的 OAuth 令牌,吃的是订阅额度。
      两条路的**端点、鉴权头、模型名**都不一样,合成一条的表现是用户拿订阅登录
      成功之后第一条消息 401。

      ★ 没有 `apiKeyUrl`:这条通道不存在「创建 API Key」这回事(要 key 的人应当
      去上面那条 `xai`)。理由和 `codex` 那条逐字相同。

      ★ 不进「推荐服务」:同 `codex` —— 要走完一次浏览器授权才有用的卡片
      不该占首屏。
    */
    id: 'grok-build',
    name: 'Grok Build(Grok 订阅)',
    category: 'overseas',
    /*
      ★★ Responses 在前。2026-09-14 用 GET 做的 405/404 判别(仓库判据):
        GET /v1/responses          → 405(存在,只收 POST)
        GET /v1/chat/completions   → 405(存在)
        GET /v1/chat/completionz   → 404(同前缀的假路径,对照组)
      官方 CLI 的 `auth.json` 里写着 `backend: responses`,两个第三方实现发的也是
      `/v1/responses` —— 所以默认那条选它,`chat/completions` 留作退路。

      ★ `supportsModelList` 为 true:`GET /v1/models` 无凭证回 **401**、
      同前缀假路径 `/v1/modelz` 回 **404**(同日实测),说明列表端点是真的、
      只是要带令牌。**不标 `modelListPublic`** —— 没登录时拉不到。
    */
    endpoints: [
      resp('https://cli-chat-proxy.grok.com/v1', true),
      oa('https://cli-chat-proxy.grok.com/v1', true)
    ],
    docsUrl: 'https://docs.x.ai/docs/grok-cli',
    credentialKind: 'oauth',
    /*
      ★★ 规格表里的第二条 **RFC 8628 设备码**流程。官方 CLI 默认走的是授权码 +
      PKCE + 临时端口,我们走它的 `--device-auth` 那条 —— 理由是证据强度
      (`/oauth2/authorize` 隔着 Cloudflare,redirect_uri 的注册形态没法验证),
      完整推导在 `issuers/grok.ts` 的文件头。
    */
    oauthIssuer: 'grok-build',
    /*
      ★ `supportsModelList` 是 true,登录之后这张表会被真实列表顶掉;种在这里是为了
      **没登录时也点得出东西**。名字来自二进制的 `strings`、第三方实现的 fallback
      目录,以及它们注释里那句「observed via /v1/models」。
      `grok-build` 放第一个:它是这条通道的同名主力模型(500K 上下文)。
    */
    suggestedModels: ['grok-build', 'grok-4.6', 'grok-4.5', 'grok-composer-2.5-fast'],
    notes:
      '用 Grok 账号登录，走订阅额度，不计入 API 账单。' +
      '★ 这条通道和「xAI Grok」那条是两个不同的服务：域名、模型名、鉴权方式都不一样，' +
      '订阅登录换来的令牌在 api.x.ai 上不认。' +
      '★ 请求必须带 x-grok-client-version，缺了会返回 426 而不是 401（已由登录通道自动带上）。',
    verification: 'probed'
  },
  {
    id: 'mistral',
    name: 'Mistral AI',
    category: 'overseas',
    endpoints: [oa('https://api.mistral.ai/v1', true)],
    docsUrl: 'https://docs.mistral.ai/api',
    apiKeyUrl: 'https://console.mistral.ai/api-keys/',
    suggestedModels: ['mistral-medium-3-5', 'mistral-large-2512', 'devstral-2512'],
    verification: 'documented'
  },
  {
    id: 'cohere',
    name: 'Cohere(OpenAI 兼容)',
    category: 'overseas',
    endpoints: [oa('https://api.cohere.ai/compatibility/v1', false)],
    docsUrl: 'https://docs.cohere.com/docs/compatibility-api',
    apiKeyUrl: 'https://dashboard.cohere.com/api-keys',
    suggestedModels: ['command-a-plus-05-2026', 'command-a-03-2025'],
    notes:
      '★ 三个 URL 三种组合:兼容层在 api.cohere.ai/compatibility/v1,原生 chat 在 ' +
      'api.cohere.com/v2/chat,而模型列表在 api.cohere.com/v1/models(v1,且是另一个域名)—— ' +
      '所以「拉取模型列表」在这里用不了。兼容层的 reasoning_effort 只接受 none 与 high。',
    verification: 'documented'
  },

  {
    /*
      Ollama Cloud —— **和本表末尾那条本地 `ollama` 是同一家的两条线,不是一条。**
      本地那条打 `127.0.0.1:11434`(自己的显卡),这条打 `ollama.com`(官方托管,
      吃订阅额度)。两条都留着,因为它们解决的是两个问题。

      ★★ **模型名在两条线上不一样,这是这家最会咬人的地方。**
      云 API 上是 `gpt-oss:120b`,本地 daemon 上同一个模型要写成
      `gpt-oss:120b-cloud`(后缀是 daemon 用来决定「转发到云端」的开关)。
      填错的表现是 404,而错误信息里只有那个模型名,不会提两条线的事。

      ★★ 2026-09-15 起支持**官方 CLI 同款的密钥绑定登录**(`oauthIssuer`,
      逆向 ollama v0.34.0 + 直连实测定案,实现见 `main/kernel/oauth/issuers/ollama.ts`):
      浏览器把本机 `~/.ollama/id_ed25519` 的公钥绑到账号,之后逐请求签名 ——
      **没有 token**,和官方 CLI 共用同一个绑定。当初「套不进 OAuth 形状」的根因
      (签名头随 method+path+ts 变)已由 `UpstreamTransport.signRequest`(逐请求
      签名钩子)解决。API Key 仍然保留(`credentialKind` 不动 → 双形态并存):
      登录收尾的可用性探针若发现 `/v1` 网关只认 API Key,会当场提示退回此路。
    */
    id: 'ollama-cloud',
    name: 'Ollama Cloud(订阅)',
    category: 'overseas',
    oauthIssuer: 'ollama-cloud',
    /*
      ★★ 三个协议全在同一个域名下,2026-09-15 实测(仓库判据:真路径非 404 +
      同前缀假路径 404):
        GET /v1/models           → 200(**免鉴权**)   /v1/modelz            → 404
        GET /v1/chat/completions → 405(存在,只收 POST) /v1/chat/completionz → 404
        GET /v1/messages         → 405,且错误体是 Anthropic 形状
                                   (`{"type":"error","request_id":"req_…"}`)
        POST 上面两条 + 假 Bearer → 401
      OpenAI 族的版本段在 base 里、Anthropic 族不带 —— 两族约定是反的,
      所以这两条 baseUrl 差一段 `/v1` 不是笔误(见本文件开头那张表)。

      ★ 两条都标 `supportsModelList`,因为两族的列表 URL 恰好**是同一个**:
      OpenAI 族拼 `{base}/models`、Anthropic 族拼 `{base}/v1/models`,
      都落到 `https://ollama.com/v1/models`。
    */
    endpoints: [oa('https://ollama.com/v1', true, true), anth('https://ollama.com', true)],
    docsUrl: 'https://docs.ollama.com/cloud',
    apiKeyUrl: 'https://ollama.com/settings/keys',
    /*
      ★ 冷启动兜底而已 —— 这家的 `/v1/models` 免鉴权,用户**key 还没填就能先拉**
      真实列表(全表只有 OpenRouter / DeepInfra / OpenCode Go 有同样待遇)。
      种在这里的是 2026-09-15 实测那 20 条里的代表,`gpt-oss:120b` 放第一
      (官方文档全篇拿它举例)。
    */
    suggestedModels: [
      'gpt-oss:120b',
      'kimi-k3',
      'glm-5.3',
      'deepseek-v4-pro:0813',
      'qwen3.5:397b',
      'minimax-m3'
    ],
    notes:
      '★ 模型名不带 -cloud 后缀(那是本地 Ollama 上的写法),填错直接 404。' +
      '★ 订阅是额度制而非包月无限:Pro $20/月含 $60 用量额度、Max $100/月含 $300,' +
      '另有 5 小时与 7 天两个滚动窗口的限额,超了返回 429。' +
      '★ 已装 Ollama 并跑过 ollama signin 的话,本表末尾那条本地「Ollama」' +
      '也能跑云模型(daemon 代为签名转发),但那条路上模型名要带 -cloud 后缀。' +
      '★ Think:OpenAI 格式走 `reasoning_effort`(多数模型接受 low/medium/high/max,' +
      'gpt-oss 只认 low/medium/high 且 trace 关不掉);Anthropic 格式的档位走' +
      '`output_config.effort`、开关仍走 `thinking.type`。这两条线形对**绑在本供应商' +
      '上的所有模型**生效(含 glm-5.3 这类名字属别家目录的),推导与源码证据见' +
      '`model-catalog-inventory/vendors/ollama.ts` 与官方 think 文档。',
    verification: 'probed'
  },

  // ────────────────────────── 国内服务 ──────────────────────────
  {
    id: 'deepseek',
    name: 'DeepSeek 深度求索',
    category: 'domestic',
    recommended: true,
    /*
      ★ 标 documented 而不是 probed,**尽管报告里这一条打了 ✅**:
      DeepSeek 的网关对**任意**路径都返回 401(报告 §0 点名的四家之一),
      所以「401 = 路径存在」在这里不成立,那个 ✅ 是假阳性。地址本身来自官方文档。
    */
    endpoints: [
      oa('https://api.deepseek.com/v1', true),
      anth('https://api.deepseek.com/anthropic')
    ],
    docsUrl: 'https://api-docs.deepseek.com/',
    apiKeyUrl: 'https://platform.deepseek.com/api_keys',
    suggestedModels: [
      'deepseek-v4-pro',
      'deepseek-v4-flash',
      'deepseek-v4.1-flash-expires-on-0910'
    ],
    notes:
      '★ 老别名 deepseek-chat / deepseek-reasoner 已于 2026-07-24 下线,填了会直接 400。' +
      '官方注明地址里的 v1 与模型版本无关,带不带都通。',
    verification: 'documented'
  },
  {
    id: 'moonshot',
    name: 'Moonshot·按量 API(国内)',
    category: 'domestic',
    endpoints: [
      oa('https://api.moonshot.cn/v1', true),
      resp('https://api.moonshot.cn/v1', true),
      anth('https://api.moonshot.cn/anthropic')
    ],
    docsUrl: 'https://platform.kimi.ai/docs/guide/claude-code-kimi',
    apiKeyUrl: 'https://platform.kimi.com/console/api-keys',
    suggestedModels: ['kimi-k3', 'kimi-k2.7-code', 'kimi-k2.6'],
    notes: '★ 按量 key 与 Coding Plan 订阅 key 不通用,地址也不是同一个(见「Kimi·Coding Plan」)。',
    verification: 'probed'
  },
  {
    id: 'moonshot-global',
    name: 'Moonshot·按量 API(国际)',
    category: 'domestic',
    endpoints: [
      oa('https://api.moonshot.ai/v1', true),
      resp('https://api.moonshot.ai/v1', true),
      anth('https://api.moonshot.ai/anthropic')
    ],
    docsUrl: 'https://platform.kimi.ai/docs/guide/claude-code-kimi',
    apiKeyUrl: 'https://platform.kimi.ai/console/api-keys',
    suggestedModels: ['kimi-k3', 'kimi-k2.7-code', 'kimi-k2.6'],
    verification: 'probed'
  },
  {
    id: 'kimi-coding',
    name: 'Kimi·Coding Plan(订阅制)',
    category: 'domestic',
    recommended: true,
    /*
      ★ 同一个服务两个协议,前缀差一段 `/v1` —— OpenAI 族的版本段在 base 里,
      Anthropic 族的不在。实测存在的是 `/coding/v1/messages` 与 `/coding/v1/chat/completions`。
    */
    endpoints: [anth('https://api.kimi.com/coding'), oa('https://api.kimi.com/coding/v1', true)],
    // ★ /coding/docs 会 302 到这里(2026-09-09 实测),直接写终点少一跳
    docsUrl: 'https://www.kimi.com/code/docs/en/',
    apiKeyUrl: 'https://www.kimi.com/code/console',
    credentialKind: 'subscription-key',
    /*
      ★★ **两种凭证都能用**(理由同 `zhipu-coding` 那条):订阅 key 照填,也可以直接
      用 Kimi 账号登录。后者走的是 **RFC 8628 设备码**,规格表里的第一条设备码流程 ——
      协议来自官方一方源码 `@moonshot-ai/kimi-code`,细节和实测证据在
      `issuers/kimi.ts` 的文件头。

      ★ 登录成功后端点会被切到 `endpoints[1]`(`/coding/v1`,openai-chat)——
      那正是 kimi-code 自己用的 base URL。切换逻辑在渲染层的 `SIGN_IN_PROTOCOL`。
    */
    oauthIssuer: 'kimi-code',
    /*
      ★★ **这里填的是 Model ID,不是版本名 —— 填错不是「查不到」,是直接调用失败。**
      官方那页把话说死了(2026-09-09 核对):可用的 Model ID 只有下面四个,
      「填 `Kimi K3` / `K2.7 Code` 这类版本名会导致调用失败」。而这条 preset 的
      `endpoints[0]` 是 Anthropic 端点、`supportsModelList` 为 false —— 拉不了
      真实列表,种进去什么用户就只有什么,种错了就是开箱即坏。

      四个的对应关系(同一页的表):
        k3                        Kimi K3,旗舰,最高 1M 上下文(Moderato 及以上)
        k3-256k                   K3 的 256K 版,省额度
        kimi-for-coding           K2.7 Code,全会员可用
        kimi-for-coding-highspeed K2.7 Code 高速版,~5-6× 输出速度、3× 额度消耗
                                  (Allegretto 及以上)

      ★ 后两个按会员档位可能无权调用。**仍然种**:那种情况上游回的是明确的权限
      错误,用户看得懂也改得动;而漏种的表现是「这个模型根本不存在」——
      他连自己买的档位包含它都不会知道。同 `provider-edit.ts` 那句「宁可多种
      两个能删的名字,也不要给出一家点什么都没有的供应商」。
    */
    suggestedModels: ['k3', 'k3-256k', 'kimi-for-coding', 'kimi-for-coding-highspeed'],
    notes:
      '★ 域名是 api.kimi.com,**不是** api.moonshot.cn —— 社区里大量「Coding Plan 配 ' +
      'api.moonshot.cn/anthropic 报 401」都出在这里。api.kimi.com 上只有 /coding 前缀可用。',
    verification: 'probed'
  },
  {
    id: 'zhipu',
    name: '智谱·按量 API(国内)',
    category: 'domestic',
    endpoints: [oa('https://open.bigmodel.cn/api/paas/v4', false)],
    docsUrl: 'https://docs.bigmodel.cn/cn/guide/develop/claude',
    apiKeyUrl: 'https://open.bigmodel.cn/usercenter/apikeys',
    suggestedModels: ['glm-5.3', 'glm-5.2', 'glm-4.7'],
    notes: '★ 按量 key 打不到 Coding Plan 的地址,反之亦然 —— 两套独立的鉴权域。',
    verification: 'probed'
  },
  {
    id: 'zhipu-coding',
    name: '智谱·GLM Coding Plan(订阅制)',
    category: 'domestic',
    recommended: true,
    /*
      ★★ **Coding 端点排第一,Anthropic 端点排第二 —— 顺序是结论,不是随手写的。**
      `endpoints[0]` 是 `providerFromPreset` 取的那条主推形态(见 `provider-edit.ts`),
      而官方 FAQ 把 `…/api/anthropic` 限定成**仅限从未买过 Coding Plan 且额外加白的账号**
      (2026-09-08 核对)。anth 排第一的话,买了套餐的用户开箱第一次请求就是 401,
      而上游回的那句「令牌已过期或验证不正确」**一个字都不提端点选错了** ——
      他只会去反复检查自己那把 key,而 key 是好的。

      ★ anth 仍然留着,只是降到第二位:加白账号确实用得上,而 `endpoints` 同时是
      「API 格式」开关的候选池,删掉等于那些账号没法切过去。

      ★ 两个地址 2026-09-08 探针验证存活(真路径 401、同前缀假路径 `/api/NOPE/v4` 404)。
      探针只证明地址在,证不了端点与套餐的匹配关系 —— 那条靠上面的官方 FAQ。
    */
    endpoints: [
      oa('https://open.bigmodel.cn/api/coding/paas/v4', false),
      anth('https://open.bigmodel.cn/api/anthropic')
    ],
    docsUrl: 'https://docs.bigmodel.cn/cn/coding-plan/overview',
    apiKeyUrl: 'https://open.bigmodel.cn/usercenter/apikeys',
    credentialKind: 'subscription-key',
    /*
      ★★ **`credentialKind` 和 `oauthIssuer` 同时存在 = 这家两种凭证都能用。**
      前者管「获取订阅密钥」那颗按钮的措辞,后者管「还画一颗登录按钮」——
      两者不再互斥(见 `provider-auth.ts` 的三态 `providerAuthMode`)。
      写成互斥的话,接了登录就等于把已经在用订阅 key 的用户的输入框拿走。

      ★ 这条(智谱/BigModel)的登录链路是**探索性**的,见 `issuers/zcode-bigmodel.ts`
      的文件头:授权那一跳 2026-09-09 有实测数据,换 token 那一跳还没抓到。
      做不通时的修法是改那个文件里的数据,不是把这一行删掉。
    */
    oauthIssuer: 'zcode-bigmodel',
    /*
      ★★ **只有这两个,而且 `glm-5.2` 不在其中。**
      官方「可用模型」原话(2026-09-09 核对,中英两站一致):所有套餐均支持
      GLM-5.3、GLM-5.3-Flash;**调用 GLM-5.2 / GLM-5.1 会被自动切换到 GLM-5.3**,
      GLM-5-Turbo / GLM-4.7 会被自动切换到 GLM-5.3-Flash。
      写 `glm-5.2` 不报错 —— 它只是让用户在模型下拉里选一个到不了的名字,
      而积分照着 5.3 扣。这种「能用,但不是你选的那个」比 404 难查得多。

      ★★ **flash 必须种进来。** 这条 preset 的端点 `supportsModelList: false`,
      「从服务商拉取模型列表」按钮是灰的(见 `seedModelsForPreset`),
      所以这张表就是用户能拿到的**全部**。漏了 flash,他就用不上套餐里抵扣系数
      低两三倍的那一半(积分系数 6.9/1.7/24 对 2.3/0.56/8),而界面上没有任何
      一处会告诉他还存在这个模型。

      ★ 官方另给了 `glm-5.3-flash[1m]` 这种 1M 上下文变体写法。**不种**:
      它是同一个模型的参数形态而不是另一个模型,种进来这张表里就会有两个
      看不出区别的名字。要用的人在别名里手填即可。
    */
    suggestedModels: ['glm-5.3', 'glm-5.3-flash'],
    notes:
      '★ 订阅 key 与按量 key 不通用。这是「配了半天 401」的头号原因。' +
      '★ 翻到 Anthropic 格式前先确认账号已加白 —— 已购 Coding Plan 的账号用不了那条。',
    verification: 'probed'
  },
  {
    id: 'zai',
    name: 'Z.AI(智谱国际)按量',
    category: 'domestic',
    endpoints: [oa('https://api.z.ai/api/paas/v4', false)],
    docsUrl: 'https://docs.z.ai/devpack/quick-start',
    apiKeyUrl: 'https://z.ai/manage-apikey/apikey-list',
    suggestedModels: ['glm-5.3', 'glm-5.2'],
    verification: 'probed'
  },
  {
    id: 'zai-coding',
    name: 'Z.AI Coding Plan(订阅制)',
    category: 'domestic',
    /* ★ 顺序与理由同 `zhipu-coding` —— 海外站只是把域名换成 api.z.ai,路径规则一样 */
    endpoints: [
      oa('https://api.z.ai/api/coding/paas/v4', false),
      anth('https://api.z.ai/api/anthropic')
    ],
    docsUrl: 'https://docs.z.ai/devpack/quick-start',
    apiKeyUrl: 'https://z.ai/manage-apikey/apikey-list',
    credentialKind: 'subscription-key',
    /* ★ 两种凭证都能用,理由见 `zhipu-coding` 那条;这一条的链路已实测走通 */
    oauthIssuer: 'zcode-zai',
    /* ★ 可用模型同 `zhipu-coding` —— 官方英文站是同一句:GLM-5.2/5.1 自动路由到 5.3 */
    suggestedModels: ['glm-5.3', 'glm-5.3-flash'],
    notes:
      '★ 订阅 key 与按量 key 不通用。' +
      '★ 翻到 Anthropic 格式前先确认账号已加白 —— 已购 Coding Plan 的账号用不了那条。',
    verification: 'probed'
  },
  {
    id: 'minimax',
    name: 'MiniMax(国内)',
    category: 'domestic',
    endpoints: [
      oa('https://api.minimaxi.com/v1', true),
      resp('https://api.minimaxi.com/v1', true),
      anth('https://api.minimaxi.com/anthropic')
    ],
    docsUrl: 'https://platform.minimax.io/docs/token-plan/claude-code',
    apiKeyUrl: 'https://platform.minimaxi.com/user-center/basic-information/interface-key',
    suggestedModels: ['minimax-m3', 'minimax-m2.7'],
    notes: '★ 国内是 minimaxi.com(多一个 i),国际是 minimax.io —— 两个域名都真实存在。',
    verification: 'probed'
  },
  {
    id: 'minimax-global',
    name: 'MiniMax(国际)',
    category: 'domestic',
    endpoints: [oa('https://api.minimax.io/v1', true), anth('https://api.minimax.io/anthropic')],
    docsUrl: 'https://platform.minimax.io/docs/token-plan/claude-code',
    apiKeyUrl: 'https://platform.minimax.io/user-center/basic-information/interface-key',
    suggestedModels: ['minimax-m3', 'minimax-m2.7'],
    verification: 'probed'
  },
  {
    id: 'dashscope',
    name: '阿里百炼 / 通义(国内)',
    category: 'domestic',
    endpoints: [
      oa('https://dashscope.aliyuncs.com/compatible-mode/v1', true),
      resp('https://dashscope.aliyuncs.com/compatible-mode/v1', true)
    ],
    docsUrl: 'https://help.aliyun.com/zh/model-studio/claude-code',
    suggestedModels: ['qwen3.8-max', 'qwen3.8-flash', 'qwen-plus'],
    notes:
      '★ Anthropic 兼容层没有做进预设:它的地址里带 WorkspaceId 与 region,每个账号都不同,' +
      '填不出一个通用值 —— 需要的话照官方文档手填。另:那一层**只有 /v1/messages,没有 /v1/models**。',
    verification: 'probed'
  },
  {
    id: 'dashscope-intl',
    name: '阿里百炼(国际)',
    category: 'domestic',
    endpoints: [oa('https://dashscope-intl.aliyuncs.com/compatible-mode/v1', true)],
    docsUrl: 'https://help.aliyun.com/zh/model-studio/claude-code',
    suggestedModels: ['qwen3.8-max', 'qwen3.8-flash'],
    verification: 'probed'
  },
  {
    id: 'volcengine',
    name: '火山方舟·豆包',
    category: 'domestic',
    endpoints: [
      oa('https://ark.cn-beijing.volces.com/api/v3', false),
      anth('https://ark.cn-beijing.volces.com/api/compatible')
    ],
    docsUrl: 'https://www.volcengine.com/docs/82379',
    apiKeyUrl: 'https://console.volcengine.com/ark/region:ark+cn-beijing/apiKey',
    /* 报告没能核实到可用的模型 ID(火山用的是接入点 ID),宁可空着也不编 */
    suggestedModels: [],
    notes: '★ 火山用「接入点 ID」而不是模型名,需要先在控制台创建接入点,再把它的 ID 填成模型。',
    verification: 'documented'
  },
  {
    id: 'qianfan',
    name: '百度千帆',
    category: 'domestic',
    endpoints: [oa('https://qianfan.baidubce.com/v2', false)],
    docsUrl: 'https://qianfan.baidubce.com',
    apiKeyUrl: 'https://console.bce.baidu.com/iam/#/iam/apikey/list',
    suggestedModels: [],
    notes: '★ 千帆走 IAM AK/SK 鉴权,不一定吃 Bearer —— 配不通时先对一遍官方文档的鉴权方式。',
    verification: 'probed'
  },
  {
    /*
      2026-09 改版:老的 `api.hunyuan.cloud.tencent.com` 换成了大模型服务平台
      **TokenHub**(文档产品号从 1729 挪到 1823)。这不只是换个域名 ——
      平台从「只有混元」变成了聚合腾讯混元 / DeepSeek / 智谱 GLM / Kimi /
      MiniMax / Qwen / MiMo,三种协议(chat completions、responses、messages)
      共用同一组 base URL,只换路径。

      ★ 老域名今天还活着(带假 key 打 `/v1/models` 仍是 401,报错里指的是
      `console.cloud.tencent.com/hunyuan/start` 这个旧控制台),但它是上一代
      平台的入口,模型和价格都对不上 1823 那套。不留双端点:同一 protocol
      写两条,后一条在界面上永远不可达(见上面「同一 protocol 不重复」)。

      探针(2026-09-10,真路径 401 / 假路径 404 明面对照,这家没有 catch-all):
        /v1/models 401 · /api/v1/models 404
        /v1/chat/completions 401 · /v1/chat/completionsx 404
        /v1/responses 401 · /v1/responsesx 404
        /v1/messages **401 且是 Anthropic 原生错误形状**
          (`{"error":{"type":"authentication_error"},"type":"error"}`,
           跟其余路径的 `gateway_error` 信封不是一套) · /v1/messagesx 404
      —— 最后一条推翻了上一版那句「实测不提供 Anthropic 兼容端点」。
    */
    id: 'hunyuan',
    name: '腾讯混元',
    category: 'domestic',
    endpoints: [
      oa('https://tokenhub.tencentmaas.com/v1', true),
      resp('https://tokenhub.tencentmaas.com/v1', true),
      anth('https://tokenhub.tencentmaas.com', true)
    ],
    docsUrl: 'https://cloud.tencent.com/document/product/1823/130078',
    apiKeyUrl: 'https://console.cloud.tencent.com/tokenhub/apikey',
    suggestedModels: ['hy4-preview', 'hy3', 'hy-mt2-pro', 'hunyuan-role-latest'],
    notes:
      '★ 地址**分地域,不能跨地域调用**:广州(中国大陆)是 tokenhub.tencentmaas.com,' +
      '新加坡(全球)要改成 tokenhub-intl.tencentmaas.com;两个域名各有一个 .cn 结尾的备用地址。' +
      '★ 模型要先在控制台开通,没开通的调用返回 **402**,跟协议支不支持无关。' +
      '★ Responses 协议只覆盖一部分模型(Hy-MT2 系列、GLM-5 系列、Kimi Code 系列都没有),' +
      '配不通时先退回 OpenAI Chat Completions。',
    verification: 'probed'
  },
  {
    id: 'stepfun',
    name: '阶跃星辰 StepFun',
    category: 'domestic',
    endpoints: [
      oa('https://api.stepfun.com/v1', true),
      resp('https://api.stepfun.com/v1', true),
      anth('https://api.stepfun.com')
    ],
    docsUrl: 'https://platform.stepfun.com/docs',
    apiKeyUrl: 'https://platform.stepfun.com/interface-key',
    suggestedModels: [],
    verification: 'probed'
  },
  {
    id: 'baichuan',
    name: '百川智能',
    category: 'domestic',
    endpoints: [oa('https://api.baichuan-ai.com/v1', true)],
    docsUrl: 'https://platform.baichuan-ai.com/docs/api',
    suggestedModels: [],
    verification: 'probed'
  },
  {
    id: 'sensenova',
    name: '商汤日日新 SenseNova',
    category: 'domestic',
    endpoints: [oa('https://api.sensenova.cn/compatible-mode/v1', false)],
    docsUrl: 'https://console.sensecore.cn/help/docs/model-as-a-service/nova',
    apiKeyUrl: 'https://console.sensecore.cn/iam/Security/access-key',
    credentialKind: 'access-key',
    suggestedModels: [],
    notes: '实测没有 /models 接口,「拉取模型列表」用不了,模型 ID 需要手填。',
    verification: 'probed'
  },
  {
    id: 'spark',
    name: '讯飞星火',
    category: 'domestic',
    endpoints: [oa('https://spark-api-open.xf-yun.com/v1', false)],
    docsUrl: 'https://www.xfyun.cn/doc/spark/Web.html',
    apiKeyUrl: 'https://console.xfyun.cn/services/cbm',
    credentialKind: 'api-password',
    suggestedModels: [],
    notes: '★ 地址未能实测核实(该网关对任意路径都 401),配不通时以官方文档为准。',
    verification: 'unverified'
  },

  // ────────────────────────── 聚合平台 ──────────────────────────
  {
    id: 'routin',
    name: 'RoutinAI',
    category: 'aggregator',
    recommended: true,
    /*
      探针(2026-09-04,真路径 + 同前缀假路径对照):
        GET  /v1/messages         → 405(POST-only)  ← Anthropic 端点存在
        GET  /v1/chat/completions → 405(POST-only)  ← OpenAI chat 端点存在
        GET  /v1/models           → 401 API Key is required
        GET  /v1/nc-decoy-9x      → 404             ← 对照,证明上面不是 catch-all
        GET  /models              → 404             ← 版本段是必须的

      ★ 两个协议的 base **不一样**,原因是 `REQUEST_PATH` 那条两族相反的约定:
      Anthropic 自己补 `/v1/messages`(base 裸域名),OpenAI 族的 `/v1` 在 base 里。
      写成同一个 base 的话,翻一下「API 格式」开关地址就静默失效。

      ★★ **不给 openai-responses**。`/v1/responses` 回的是 426
      「This endpoint requires a WebSocket upgrade request」—— 那是一个**同名的
      WebSocket 端点**,不是 OpenAI 的 Responses API。凭路径存在就录进来,
      用户翻到那个开关会得到一个永远连不上的配置。
    */
    endpoints: [anth('https://api.routin.ai', true), oa('https://api.routin.ai/v1', true)],
    docsUrl: 'https://api.routin.ai/',
    apiKeyUrl: 'https://routin.ai/dashboard/api-keys',
    /*
      ★ 顺序有意义:`runtime.ts` 的 seed 按这张表种别名,**第一条是全新安装的
      `defaultModel`,第二条是 `subagent.model`**。deepseek 那两条排在前面不是偏好,
      是那两个设置项要指的东西。
      `claude-fable-5-1` 留在末位 —— 参考图那颗药丸(`brands.ts`、`Thread.tsx` 都引了
      `RoutinAI / claude-fable-5-1` 这一对)说的是它,删掉那些注释就成了悬空引用。
    */
    suggestedModels: ['deepseek-v4-pro', 'deepseek-v4-flash', 'claude-fable-5-1'],
    notes:
      '同一个域名下两种协议的地址不同(Anthropic 不带 /v1,OpenAI 带)——翻「API 格式」时地址会跟着换。',
    verification: 'probed'
  },
  {
    id: 'routin-plan',
    name: 'RoutinAI(订阅)',
    category: 'aggregator',
    recommended: true,
    /*
      探针(2026-09-07,真路径 + 同前缀假路径对照,**GET 与 POST 各打一遍**):
        POST /plan/v1/responses        → 401 {"error":{"type":"unauthorized",…}}         ← OpenAI 信封
        POST /plan/v1/messages         → 401 {"type":"error","error":{"type":"authentication_error"…}}
        GET  /plan/v1/models           → 401
        POST /plan/v1/chat/completions → 404   ← **这条不存在**,所以下面没有 openai-chat
        POST /plan/v1/nc-decoy-9x      → 404   ← 对照,证明上面不是 catch-all

      ★★ **必须用 POST 探,只 GET 会把这条线整个判死。**
      上面 `routin` 那段注释从「GET /v1/responses → 426 WebSocket upgrade」推出
      「那是个同名的 WebSocket 端点」—— 而 `/plan/v1/responses` 的 GET **也**回 426,
      POST 过去却是标准 OpenAI 401。426 是这个网关对 GET 的统一回法,不是端点性质。

      ★ 即便如此,`/v1` 那条**仍然不给** openai-responses:它 POST 回的是网关自己的
      信封(`{"code":401,…,"title":"未授权访问"}`),而这里回的是 OpenAI 信封 ——
      两个不同的后端。按量那条背后有没有真的接着 Responses API,证据不足,不录。

      ★ 两个协议的 base 差一段 `/v1`,还是 `REQUEST_PATH` 那条两族相反的约定:
      Anthropic 自己补 `/v1/messages`(base 到 `/plan` 为止),OpenAI 族的 `/v1` 在 base 里。
      两条拼出来的模型列表地址正好都是 `/plan/v1/models`,所以两条都能拉。
    */
    endpoints: [
      resp('https://api.routin.ai/plan/v1', true),
      anth('https://api.routin.ai/plan', true)
    ],
    docsUrl: 'https://api.routin.ai/',
    apiKeyUrl: 'https://routin.ai/dashboard/api-keys',
    credentialKind: 'subscription-key',
    /*
      ★ 顺序即优先级:`runtime.ts` 的 seed 按下标算别名的 `priority`,而
      `enabled-models.ts` 拿这家的 `aliases[0]` 当副标题那个「主模型」。
      所以 `gpt-5.6-sol` 排第一不是审美,是「这条线默认用哪个」的落地方式;
      `gpt-5.6-terra` 跟在后面是同一个理由的子代理版。

      ★ 注意**这两个设置项本身是全局的**(`settings.defaultModel` /
      `settings.subagent.model`,见截图里那两个下拉框),按量那条线才是它们的来源
      (deepseek-v4-pro / -flash)。这里的顺序只决定**这一家在列表里显示哪个模型**,
      不会去抢那两个全局值 —— 两者容易混,搞反了的表现是用户一打开就在用订阅额度。
    */
    suggestedModels: [
      'gpt-5.6-sol',
      'gpt-5.6-terra',
      'gpt-5.3-codex-spark',
      'gpt-5.4',
      'gpt-5.4-mini',
      'gpt-5.5',
      'gpt-5.6-luna',
      'gpt-6-astra'
    ],
    notes:
      '★ 订阅 key 与按量 key 不通用,地址前缀也不同(/plan/v1 与 /v1)—— 拿按量 key 打这里只会 401。' +
      '★ 这条线**没有 chat/completions**(实测 404),只有 Responses 与 Anthropic 两种格式,' +
      '「API 格式」里选不到 OpenAI Chat 是对的,不是漏配。' +
      '额度按倍率扣:gpt-5.3-codex-spark 0.5x,gpt-5.4 / 5.4-mini / 5.5 / 5.6-sol / 5.6-terra / ' +
      '5.6-luna 均 1x(gpt-6-astra 的倍率未核实)。',
    verification: 'probed'
  },
  {
    id: 'openrouter',
    name: 'OpenRouter',
    category: 'aggregator',
    recommended: true,
    endpoints: [
      oa('https://openrouter.ai/api/v1', true, true),
      resp('https://openrouter.ai/api/v1', true),
      anth('https://openrouter.ai/api')
    ],
    docsUrl: 'https://openrouter.ai/docs/api_reference/overview',
    apiKeyUrl: 'https://openrouter.ai/settings/keys',
    suggestedModels: ['anthropic/claude-fable-5.1', 'moonshotai/kimi-k3', 'z-ai/glm-5.3'],
    notes: '模型 ID 带 vendor/ 前缀。模型列表免鉴权就能拉 —— 没填 key 也能先看有哪些模型。',
    verification: 'probed'
  },
  {
    id: 'requesty',
    name: 'Requesty',
    category: 'aggregator',
    endpoints: [
      oa('https://router.requesty.ai/v1', true, true),
      resp('https://router.requesty.ai/v1', true),
      anth('https://router.requesty.ai')
    ],
    docsUrl: 'https://docs.requesty.ai',
    apiKeyUrl: 'https://app.requesty.ai/api-keys',
    suggestedModels: ['claude-fable-5.1', 'kimi-k3', 'glm-5.3'],
    notes:
      '模型 ID 可用 vendor/model 形式(如 openai/gpt-4o-mini),也可用托管路由的短名(如 claude-fable-5.1)。' +
      '模型列表免鉴权就能拉,没填 key 也能先看有哪些模型。' +
      '欧盟区把地址换成 router.eu.requesty.ai 即可,同一个 key 通用。',
    verification: 'probed'
  },
  {
    id: 'siliconflow',
    name: '硅基流动(国内)',
    category: 'aggregator',
    recommended: true,
    endpoints: [oa('https://api.siliconflow.cn/v1', true), anth('https://api.siliconflow.cn')],
    docsUrl: 'https://docs.siliconflow.cn/cn/userguide/quickstart',
    apiKeyUrl: 'https://cloud.siliconflow.cn/account/ak',
    suggestedModels: ['deepseek-ai/DeepSeek-V3.2', 'zai-org/GLM-5.2'],
    notes: '模型 ID 是 HuggingFace 的 Org/Name 形式,**大小写敏感**。不支持 Responses API。',
    verification: 'probed'
  },
  {
    id: 'siliconflow-intl',
    name: '硅基流动(国际)',
    category: 'aggregator',
    endpoints: [oa('https://api.siliconflow.com/v1', true), anth('https://api.siliconflow.com')],
    docsUrl: 'https://docs.siliconflow.com/en/userguide/quickstart',
    apiKeyUrl: 'https://cloud.siliconflow.com/account/ak',
    suggestedModels: ['deepseek-ai/DeepSeek-V3.2', 'zai-org/GLM-5.2'],
    notes: '★ 国内站(.cn)与国际站(.com)的账号是否互通未核实,建议按两个供应商分别配 key。',
    verification: 'probed'
  },
  {
    id: 'together',
    name: 'Together AI',
    category: 'aggregator',
    endpoints: [
      oa('https://api.together.xyz/v1', true),
      resp('https://api.together.xyz/v1', true),
      anth('https://api.together.xyz')
    ],
    docsUrl: 'https://docs.together.ai/docs/quickstart',
    apiKeyUrl: 'https://api.together.ai/settings/projects/~current/api-keys',
    suggestedModels: [],
    verification: 'documented'
  },
  {
    id: 'fireworks',
    name: 'Fireworks AI',
    category: 'aggregator',
    endpoints: [
      oa('https://api.fireworks.ai/inference/v1', true),
      resp('https://api.fireworks.ai/inference/v1', true),
      anth('https://api.fireworks.ai/inference')
    ],
    docsUrl: 'https://docs.fireworks.ai/tools-sdks/anthropic-compatibility',
    apiKeyUrl: 'https://app.fireworks.ai/api-keys',
    suggestedModels: ['accounts/fireworks/models/deepseek-v3p2'],
    notes:
      '模型 ID 形如 accounts/fireworks/models/<name>,且**点号写作 p**(v3.2 → v3p2)。' +
      'Anthropic 地址官方明写不带 /v1。',
    verification: 'probed'
  },
  {
    id: 'deepinfra',
    name: 'DeepInfra',
    category: 'aggregator',
    endpoints: [
      oa('https://api.deepinfra.com/v1/openai', true, true),
      anth('https://api.deepinfra.com/anthropic')
    ],
    docsUrl: 'https://docs.deepinfra.com/api-reference/introduction',
    apiKeyUrl: 'https://deepinfra.com/dash/api_keys',
    suggestedModels: ['deepseek-ai/DeepSeek-V3.2'],
    notes:
      '★ **最容易配错的一家**:OpenAI 前缀是 /v1/openai,Anthropic 前缀是 /anthropic —— ' +
      '完全不同。/v1/openai/messages、/v1/messages、/v1/anthropic/messages 三种直觉写法全部 404(实测)。' +
      '不支持 Responses API。',
    verification: 'probed'
  },
  {
    id: 'groq',
    name: 'Groq',
    category: 'aggregator',
    endpoints: [
      oa('https://api.groq.com/openai/v1', true),
      resp('https://api.groq.com/openai/v1', true)
    ],
    docsUrl: 'https://console.groq.com/docs/openai',
    apiKeyUrl: 'https://console.groq.com/keys',
    suggestedModels: ['llama-3.3-70b-versatile', 'openai/gpt-oss-120b'],
    notes:
      '没有官方 Anthropic 端点。Responses API 是**子集**:previous_response_id / store / ' +
      'truncation / include 都不支持。模型 ID 风格混合 —— Llama 系裸名,其余带命名空间。',
    verification: 'documented'
  },
  {
    id: 'opencode-go',
    name: 'OpenCode Go(订阅制)',
    category: 'aggregator',
    /*
      ★★ **这家上游对客户端有准入要求**,不满足就一条对话都发不出去。
      文档(https://opencode.ai/docs/go.md 的 "Where can I use it?")列了三条:
      发典型 coding agent 流量、**用自己的 User-Agent 而不是通用 SDK 名**
      (`kernel/user-agent.ts` 那一行对这家不是可有可无的自报家门)、以及
      **每个对话带一个稳定的 `x-opencode-session`**。

      少了第三条,每一次对话都是
      `Request is missing x-opencode-session and cannot be routed efficiently` ——
      一句**措辞像性能建议、实则是硬拒绝**的错误,用户从里面读不出任何可做的事。
      那个头在 `kernel/upstream/transport.ts` 的 `opencodeSession` 里装,按
      **id 或主机名**认这家:用户手填 opencode.ai 地址建的自定义供应商 id 带
      `custom-` 前缀,只按 id 认会把他整个漏掉,而他撞的是同一个错。
    */
    /*
      ★ 三个协议**同一个 base**,这是全表唯一一处 Anthropic base 带 `/v1` 的例外 ——
      这里的 `/v1` 是路由前缀,不是 Anthropic 的版本段。`joinUpstreamUrl` 的去重分支
      正好把它拼成实测存在的那个 `POST /zen/go/v1/messages`,不会变成 `/v1/v1/`。
      (这也是那个分支今天唯一还在服务的真实场景之一。)
    */
    endpoints: [
      oa('https://opencode.ai/zen/go/v1', true, true),
      resp('https://opencode.ai/zen/go/v1', true),
      anth('https://opencode.ai/zen/go/v1')
    ],
    docsUrl: 'https://opencode.ai/docs/go/',
    suggestedModels: ['minimax-m3', 'kimi-k3', 'glm-5.3', 'deepseek-v4-pro', 'grok-4.6'],
    notes:
      '★ **协议是跟着模型走的,不是你选的**:GLM / Kimi / DeepSeek / LongCat / MiMo / 混元 ' +
      '走 chat/completions,MiniMax / Qwen 走 messages,Grok / GPT / Muse Spark 走 responses。' +
      '选错的表现是一句 `500 Internal server error` —— 从那句话里看不出和协议有任何关系。' +
      '应用会按官方端点表自动给每个模型钉好协议(见 kernel/upstream/opencode-protocol.ts),' +
      '这里写出来只为让你能核对;要改的话在「编辑模型」的协议下拉里改。' +
      '额度按美元计($10/月),不按 token。模型列表免鉴权就能拉。',
    verification: 'probed'
  },
  {
    id: 'aihubmix',
    name: 'AiHubMix',
    category: 'aggregator',
    endpoints: [oa('https://aihubmix.com/v1', false), anth('https://aihubmix.com')],
    docsUrl: 'https://docs.aihubmix.com/en/quick-start',
    suggestedModels: [],
    notes:
      '★ 地址未能核实(采集环境不可达),以官方文档为准。中转站透传上游原名,模型 ID 不加命名空间。',
    verification: 'unverified'
  },
  {
    id: '302ai',
    name: '302.AI',
    category: 'aggregator',
    endpoints: [oa('https://api.302.ai/v1', false), anth('https://api.302.ai')],
    docsUrl: 'https://doc.302.ai/365145292e0',
    suggestedModels: [],
    notes: '★ 地址未能核实(采集环境不可达),以官方文档为准。',
    verification: 'unverified'
  },
  {
    id: 'ohmygpt',
    name: 'OhMyGPT',
    category: 'aggregator',
    endpoints: [oa('https://api.ohmygpt.com/v1', false)],
    docsUrl: 'https://docs.ohmygpt.com/docs/api',
    apiKeyUrl: 'https://www.ohmygpt.com/apis/keys',
    suggestedModels: [],
    notes: '★ 地址未能核实(采集环境不可达),以官方文档为准。',
    verification: 'unverified'
  },

  // ────────────────────────── 本地模型 ──────────────────────────
  {
    id: 'ollama',
    name: 'Ollama',
    category: 'local',
    recommended: true,
    endpoints: [oa('http://127.0.0.1:11434/v1', true), anth('http://127.0.0.1:11434')],
    docsUrl: 'https://docs.ollama.com/openai',
    suggestedModels: [],
    notes: `${LOCAL_NOTE}默认只绑 127.0.0.1:11434,跨源访问需要设 OLLAMA_ORIGINS。`,
    verification: 'documented'
  },
  {
    id: 'lmstudio',
    name: 'LM Studio',
    category: 'local',
    endpoints: [
      oa('http://127.0.0.1:1234/v1', true),
      resp('http://127.0.0.1:1234/v1', true),
      anth('http://127.0.0.1:1234')
    ],
    docsUrl: 'https://lmstudio.ai/docs/developer/openai-compat',
    suggestedModels: [],
    notes: `${LOCAL_NOTE}三种协议齐全,是本地里唯一确认支持 Responses API 的。`,
    verification: 'documented'
  },
  {
    id: 'vllm',
    name: 'vLLM',
    category: 'local',
    endpoints: [oa('http://127.0.0.1:8000/v1', true), anth('http://127.0.0.1:8000')],
    docsUrl: 'https://docs.vllm.ai/en/latest/serving/openai_compatible_server.html',
    suggestedModels: [],
    notes:
      '★ --api-key 只保护 /v1、/v2、/inference 前缀,不保护 /invocations。' +
      '另:Anthropic 适配器只有 Python frontend 有,Rust frontend 还没实现。',
    verification: 'documented'
  },
  {
    id: 'llamacpp',
    name: 'llama.cpp server',
    category: 'local',
    endpoints: [oa('http://127.0.0.1:8080/v1', true), anth('http://127.0.0.1:8080')],
    docsUrl: 'https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md',
    suggestedModels: [],
    notes:
      `${LOCAL_NOTE}★ 工具调用**必须加 --jinja**,否则 tool_calls 静默不生效 —— ` +
      '这是「模型明明支持工具却不调用」的头号原因。默认端口 8080 与 LocalAI 撞车。',
    verification: 'documented'
  },
  {
    id: 'localai',
    name: 'LocalAI',
    category: 'local',
    endpoints: [oa('http://127.0.0.1:8080/v1', true), anth('http://127.0.0.1:8080')],
    docsUrl: 'https://localai.io/features/openai-functions/',
    suggestedModels: [],
    notes: `${LOCAL_NOTE}★ 默认端口 8080 与 llama.cpp server 撞车,两个一起跑时记得改一个。`,
    verification: 'documented'
  },
  {
    id: 'jan',
    name: 'Jan',
    category: 'local',
    endpoints: [oa('http://127.0.0.1:1337/v1', true)],
    docsUrl: 'https://jan.ai/docs/api-server',
    suggestedModels: [],
    notes: `${LOCAL_NOTE}Anthropic 端点未核实,没有做进预设。`,
    verification: 'documented'
  }
]

/**
 * 内置上游用的是哪一条预设。
 *
 * ★ 住在这里而不是 `main/runtime.ts`,是因为**两侧都要它**:主进程照它种供应商,
 * 设置页照它判断「这条删了下次启动还会回来」。渲染层不能 import `main/`,
 * 而在两边各写一个 `'routin'` 字面量,改预设 id 时只会有一边跟着改 ——
 * 另一边不报错,只是从此判断恒为 false。
 */
export const BUILTIN_PROVIDER_ID = 'routin'

/**
 * 同一家的订阅线(`/plan/v1`,Codex 系模型走 Responses)。也种,理由同上。
 *
 * ★ 它和 `BUILTIN_PROVIDER_ID` 是**两条供应商记录**,不是一条的两种协议 ——
 * 两边的 key 不通用,地址前缀也不同。合成一条的话用户填了订阅 key,
 * 按量那半会全部 401。
 */
export const BUILTIN_PLAN_PROVIDER_ID = 'routin-plan'
/** Platform hosted provider, unlocked after desktop account sign-in. */
export const CLIENT_PROVIDER_ID = 'nextcowork'

/**
 * OpenCode Go 那条预设。
 *
 * ★ 和上面几个同一个理由:`kernel/upstream/transport.ts` 拿它判「这次请求该不该
 * 发 `x-opencode-session`」—— 不是只有本表在用。在那边另写一个 `'opencode-go'`
 * 字面量的话,改预设 id 时只会有一边跟着改,而另一边**不报错,只是判断从此恒为
 * false**:表现是 OpenCode 又开始拒每一次对话,且没有任何一处线索指向那次重命名。
 */
export const OPENCODE_GO_PROVIDER_ID = 'opencode-go'

/**
 * Ollama 的两条线 —— 本地 daemon(`127.0.0.1:11434`)和官方托管(`ollama.com`)。
 *
 * ★★ 存在的理由:`model-binding.ts` 拿它判「这个绑定是不是跑在 Ollama 上」,
 * 是就地重写 thinkingConfig 的唯一依据(Ollama 只读 `reasoning_effort`,而
 * `glm-5.3` 这类名字命中的是智谱官方条目,方言字段会被它的兼容层静默丢弃
 * —— 推导和证据见 `model-catalog-inventory/vendors/ollama.ts`)。
 * 在那边另写两个字面量的话,改预设 id 时只会有一边跟着改,而另一边**不报错,
 * 只是判断从此恒为 false**:表现是 Ollama 的思考开关又变回无效,且没有任何
 * 一处线索指向那次重命名。
 */
export const OLLAMA_PROVIDER_IDS = ['ollama', 'ollama-cloud'] as const

/**
 * 全新安装会被种进供应商表的那些(`main/runtime.ts` 的 `seedBuiltinUpstream`)。
 *
 * ★★ 设置页靠它选删除提示的措辞:内置那条**删了下次启动会回来**,自己加的删了就真没了。
 * 少一个 id,那条供应商就会显示「删了就真没了」,而它下次启动又出现 ——
 * 正是 `ProviderPanel` 里那段注释点名要避免的误导,反过来的版本。
 */
export const BUILTIN_PROVIDER_IDS: readonly string[] = [
  BUILTIN_PROVIDER_ID,
  BUILTIN_PLAN_PROVIDER_ID,
  CLIENT_PROVIDER_ID
]

export function isBuiltinProvider(id: string): boolean {
  return BUILTIN_PROVIDER_IDS.includes(id)
}

/** 按分类取,保持表内顺序 */
export function presetsByCategory(category: PresetCategory): ProviderPreset[] {
  return PROVIDER_PRESETS.filter((p) => p.category === category)
}

/**
 * 第一个 Tab。见 `PresetCategory` 上面那段:推荐是跨类别的精选,不是第五个类别。
 *
 * ★ **内置上游永远排第一**,而且是在这里按 `BUILTIN_PROVIDER_ID` 提上来的,
 * 不是把它那条记录挪到 `PROVIDER_PRESETS` 的表头。两个理由:那张表是按类别
 * 分块写的(RoutinAI 属于 `aggregator` 那一块),挪出去这块就断了;更要紧的是
 * 「谁排第一」该跟着**内置上游是谁**走,而不是跟着一条记录碰巧写在文件第几行 ——
 * 将来换一家内置上游,改那一个常量就够了,不用记得还要挪位置。
 *
 * 只提不塞:内置上游自己没标 `recommended` 时不会被硬塞进来。那种情况下
 * 「第一个是内置上游」这条不成立,由 `presets.test.ts` 里那条断言当场报出来,
 * 而不是在这里偷偷替表做决定。
 */
export function recommendedPresets(): ProviderPreset[] {
  const picked = PROVIDER_PRESETS.filter((p) => p.recommended === true)
  const builtin = picked.find((p) => p.id === BUILTIN_PROVIDER_ID)
  if (builtin === undefined) return picked
  return [builtin, ...picked.filter((p) => p !== builtin)]
}

export function findPreset(id: string): ProviderPreset | null {
  return PROVIDER_PRESETS.find((p) => p.id === id) ?? null
}

/**
 * 用户在表单里翻「API 格式」开关时用它换地址。
 *
 * ★ 找不到返回 null,**不要退回第一条** —— 退回去就是「界面显示 Anthropic、
 * 地址却是 OpenAI 那条」,正是这套结构要防的静默失效。
 */
export function endpointFor(
  p: ProviderPreset,
  protocol: UpstreamProtocol
): ProviderEndpoint | null {
  return p.endpoints.find((e) => e.protocol === protocol) ?? null
}
