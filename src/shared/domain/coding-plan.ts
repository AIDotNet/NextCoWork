/**
 * GLM Coding Plan(智谱订阅制套餐)的**配额查询目标** —— 纯数据 + 纯函数。
 *
 * ## 为了什么需求建的
 *
 * 订阅制供应商(`zhipu-coding` / `zai-coding`)的剩余额度挂在一个和 AI 端点
 * **不同的域**上:AI 走 `open.bigmodel.cn` / `api.z.ai` 的 coding 路径,
 * 额度走 `bigmodel.cn` / `api.z.ai` 的 `/api/monitor/usage/quota/limit`。
 * 主进程发请求、渲染层判断「这家能不能显示额度」都要回答同一个问题
 * 「这家是哪一家的套餐」,答案只允许有一份 —— 两边各写一份判据的症状是
 * 界面画出了刷新按钮、主进程却拒绝执行(或者反过来)。
 *
 * ## 证据
 *
 * 端点形状 2026-09-29 逆向 ZCode.app v3.11(`out/host/index.js` 的
 * `buildBigModelQuotaUrl` / `buildZaiQuotaUrl`)得到,并用无鉴权探针验证过
 * 两个域都活着(401 信封 `{"code":1001,"msg":"Header中未收到Authorization参数…",
 * "success":false}`)。
 *
 * ## 故意不做什么
 *
 * - **不给按量供应商返回 family**。额度接口只认订阅密钥域,按量 key 打过去
 *   是 401(两家预设的 notes 里都记着「订阅 key 与按量 key 不通用」)。
 * - **不做网络、不解析响应**。那是主进程 `kernel/upstream/coding-plan-quota.ts` 的事。
 */
import type { OAuthIssuerId } from './oauth-issuer'
import { findPreset } from './presets'

export type CodingPlanFamily = 'bigmodel' | 'zai'

/**
 * 额度接口的基域。★ **不是** AI 端点的域:bigmodel 的 biz/monitor API 在主域
 * `bigmodel.cn` 上,`open.bigmodel.cn` 只承载 AI 路径 —— 合并成一个域的表现是
 * 请求发到 `open.bigmodel.cn/api/monitor/…`,404 且没有任何文档提到为什么。
 */
const QUOTA_BASE_URL: Readonly<Record<CodingPlanFamily, string>> = {
  bigmodel: 'https://bigmodel.cn',
  zai: 'https://api.z.ai'
}

/** 两家的额度路径逐字节相同(逆向确认),只换域名 */
const QUOTA_PATH = '/api/monitor/usage/quota/limit'

export function codingPlanQuotaUrl(family: CodingPlanFamily): string {
  return QUOTA_BASE_URL[family] + QUOTA_PATH
}

/**
 * 额度归属按 **issuer** 认,不按 provider id 认:一家供应商换名字、用户建了
 * 指向同一家的自定义条目时,issuer 不变。穷尽 `Record`:加 issuer 不补这里
 * 就是编译错误,漏掉的表现是「那家登录了但永远查不到额度」且零报错。
 */
const ISSUER_FAMILY: Readonly<Record<OAuthIssuerId, CodingPlanFamily | null>> = {
  chatgpt: null,
  'zcode-zai': 'zai',
  'zcode-bigmodel': 'bigmodel',
  'kimi-code': null,
  'grok-build': null,
  'ollama-cloud': null
}

export function codingPlanFamilyForIssuer(issuer: OAuthIssuerId): CodingPlanFamily | null {
  return ISSUER_FAMILY[issuer]
}

/**
 * 这家供应商支持订阅额度查询吗。**判据是预设的 `oauthIssuer`**,和账号登录
 * (`provider-auth.ts` 的 `providerAuthMode`)完全同源 —— 一家既有订阅又有按量
 * 时,只有带 issuer 的那条预设是订阅制。
 *
 * ★ `null` = 不支持。调用方据此**不画**刷新按钮,而不是画出来等 401(§5)。
 */
export function codingPlanFamilyFor(providerId: string): CodingPlanFamily | null {
  const issuer = findPreset(providerId)?.oauthIssuer
  return issuer === undefined ? null : codingPlanFamilyForIssuer(issuer)
}
