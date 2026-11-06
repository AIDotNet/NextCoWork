/**
 * 稳定 id 的两个纯函数 —— `sessionUuid` 与 `uuidFromSeed`。
 *
 * ★★ **它们为什么单独一个文件:为了断掉 `transport ⇄ oauth/registry ⇄ issuers`
 * 那个值依赖环。** 这两个函数本来长在 `upstream/transport.ts` 里,而三家 issuer
 * (chatgpt / grok / kimi)要从那儿取 —— 于是
 * `oauth/registry → issuers/* → transport → oauth/registry` 成了一个静态值环。
 * 抽到这里之后,issuer 只依赖这个**叶子模块**(仅 node:crypto),对 transport 只剩
 * 类型依赖(编译后擦除),环就不存在了。
 *
 * ★ 放在 `upstream/` 下而不是 `oauth/` 下:transport 自己也要用它们
 * (OpenCode 的会话头),而 transport 不该反过来去 import `oauth/`。
 */
import { createHash } from 'node:crypto'

/**
 * 把我们的 sessionId 折成一个 UUID 形状的串。
 *
 * ★ 起因很具体:ChatGPT 那条通道的 `session_id` 头要 UUID,而我们的 sessionId 是 **ULID**。
 *
 * ★ 第二个调用点是 OpenCode Go 的 `x-opencode-session`(见 `upstream/transport.ts` 的
 * `opencodeSession`),它**不要求 UUID 格式** —— 上游只说「每个对话稳定」。仍然折一道的
 * 理由写在那边。
 *
 * ★★ **必须是确定性纯函数,不能挂一个 `Map<sessionId, uuid>` 缓存。**
 * 缓存会在多窗口、进程重启、以及 gateway 与内核各持一份的情况下,给**同一个会话
 * 两个不同的 id** —— 而上游那边很可能拿它做会话级的关联,结果是同一段对话在
 * 上游被拆成两截。sha256 折一下,任何进程任何时刻都算出同一个值。
 *
 * sessionId 缺失时用一个固定种子:也是一个合法 UUID,且始终同一个,
 * 比每次现摇一个更符合「同一会话同一个 id」这个语义。
 */
export function sessionUuid(sessionId: string | undefined): string {
  const seed = sessionId === undefined || sessionId === '' ? 'nextcowork:no-session' : sessionId
  return uuidFromSeed(seed)
}

/**
 * 任意字符串 → 一个合法的 v4 形状 UUID。**确定性纯函数。**
 *
 * ★ 从 `sessionUuid` 里抽出来,是因为出现了第二个调用点:Kimi 那家的
 * `X-Msh-Device-Id` 要一个**跨重启稳定**的设备 id(见 `issuers/kimi.ts`)。
 * 两处各写一遍 RFC 4122 那两行位运算的代价不是「重复」,是**其中一处写漏**——
 * 漏了之后那个值只是「长得像 UUID」,而按规范校验的一方会拒,
 * 且错误信息不会提到这个字段。
 */
export function uuidFromSeed(seed: string): string {
  const h = createHash('sha256').update(seed).digest()
  const b = Buffer.from(h.subarray(0, 16))
  // RFC 4122:version 置 4、variant 置 10xx
  b[6] = ((b[6] as number) & 0x0f) | 0x40
  b[8] = ((b[8] as number) & 0x3f) | 0x80
  const hex = b.toString('hex')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}
