/*
 * 需求:codex(ChatGPT 订阅线)要在**两条**出站路径上都自报成 Codex CLI ——
 * 业务请求走 `transport.headers`,换 token 与第二天的刷新走 `spec.oauthHeaders`。
 *
 * 这里只钉 OAuth 那一半:业务那一半由 `upstream/__tests__/transport.test.ts`
 * 和 `router-oauth.test.ts` 负责。钉的是**绝对串**而不是 /^codex_cli_rs\// 的形状 ——
 * 版本号是证据不是装饰(见 chatgpt.ts 里 CODEX_USER_AGENT 的实测出处),
 * 谁要动它必须带着本机 `@openai/codex` 的真实版本来。
 *
 * 不满足会怎样:漏在 `oauthHeaders` 上的症状是**能登录、第二天刷新 403**,
 * 而错误信息里不会出现任何一个头的名字(见 registry.ts 那个字段的注释)。
 */
import { describe, expect, it } from 'vitest'
import { CHATGPT_OAUTH } from '../chatgpt'

describe('chatgpt/codex 的私货 UA', () => {
  it('OAuth 换 token / 刷新也带 codex_cli_rs/0.157.1,不漏成通用 UA', () => {
    expect(CHATGPT_OAUTH.oauthHeaders?.['user-agent']).toBe('codex_cli_rs/0.157.1')
  })
})
