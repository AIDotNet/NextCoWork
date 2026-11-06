/**
 * 推送订阅的**共享形状**。M1 只做安全存储基础:**不接任何推送供应商**。
 *
 * ★ 与 `interactions.ts` 同一个理由,这里只有类型与校验,没有存储、没有网络语义:
 *   共享模块会被渲染层与将来的 controller-client 一起消费,一旦反向依赖
 *   `src/main/db/` 那个包就带上了 Node 与 SQLite。存储形状在
 *   `src/main/db/device-notifications.ts`。
 *
 * ## `registrationRef` 不是 token,也不是地址
 *
 * ★ 它**不是** APNs / FCM 的 device token,也**不允许**是 URL 或任意收件地址 ——
 *   它只是将来安全存储与推送网关之间的**不透明引用**(生存期与轮换由设备侧自己管)。
 *   形状由下面的 `registrationRefSchema` 定死:1..128 个 `[A-Za-z0-9_.-]`,首字符必须
 *   是字母数字。冒号 / 斜杠 / `@` / 空格一个都不放行,所以 `https://…`、`mailto:…`、
 *   `data:abc`、`a/b` 这些**收件地址**形状的值进不来 —— 那张表也就没法被当成
 *   "往任意地址发消息"的地址簿。
 *
 * ★ 这里**刻意不复用 `deviceIdSchema`**:那个通用 schema 的字符集含 `:`,于是
 *   `mailto:abc` 这种 scheme 样式的值会被放行,而这行注释却写着"拒 URL" ——
 *   说的和做的不一致,正是地址簿最容易长出来的那道缝。
 *
 * ★ 形状合规**不等于**授权。匹配这个 schema 不证明任何权限,它更不是凭证:
 *   谁能登记这条订阅由 Gateway 的认证与逐方法权限校验决定。
 *
 * ## 视图为什么不含 `registrationRef`
 *
 * 读路径上没有任何一处需要它(投递由设备侧拿登记引用去查安全存储),而把它回传给
 * 控制端等于把"往哪台设备推"的句柄抄进每个控制端的响应与日志里。
 */
import { z } from 'zod'

export const NOTIFICATION_PLATFORMS = ['ios', 'android'] as const
export const notificationPlatformSchema = z.enum(NOTIFICATION_PLATFORMS)
export type NotificationPlatform = z.infer<typeof notificationPlatformSchema>

/**
 * 登记引用的形状。**不用 `deviceIdSchema`** —— 那是通用设备标识,字符集含 `:`,
 * 会放行 scheme 样式的地址(`mailto:abc`)。这里只收不透明句柄的字符集:
 * 首字符字母数字,其余 `[A-Za-z0-9_.-]`,长度 1..128;冒号 / 斜杠 / `@` / 空格全拒。
 * 形状合规只是"进得来",不代表授权,更不是凭证。
 */
const registrationRefSchema = z.string().min(1).max(128).regex(/^[A-Za-z0-9][A-Za-z0-9_.-]*$/)

/**
 * `expectedRevision` 与 `device_controller_state` 同规:首次登记要求 0,
 * 更新必须等于当前版本,否则 `conflict`。
 */
const revisionSchema = z.number().int().min(0).max(Number.MAX_SAFE_INTEGER)

export const NotificationSubscriptionInputSchema = z.strictObject({
  registrationRef: registrationRefSchema,
  platform: notificationPlatformSchema,
  enabled: z.boolean(),
  expectedRevision: revisionSchema
})
export type NotificationSubscriptionInput = z.infer<typeof NotificationSubscriptionInputSchema>

/**
 * 控制端看到的订阅形状。★ `deliveryConfigured` **固定为 `false`** ——
 * 本批只把订阅登记落盘,真实推送渠道尚未接线,所以"登记成功"绝不能读成"能推了"。
 * 它是个字面量类型而不是 `boolean`:哪天渠道真的接上了,改这一处会让所有
 * 依赖"它一定是 false"的地方**编译不过**,而不是悄悄变了行为。
 */
export interface NotificationSubscriptionView {
  platform: NotificationPlatform
  enabled: boolean
  revision: number
  updatedAt: number
  deliveryConfigured: false
}

/**
 * 推送投递端口(**尚未接线,本批不提供任何实现**)。
 *
 * 存在的意义是把"将来在哪里插供应商"写下来:`registrationRef` 是**引用**不是 token,
 * 取真 token 与发网络请求都必须发生在实现方(安全存储 / 推送网关),
 * 访问器这一层永远不碰。
 */
export interface PushDelivery {
  configured(): boolean
  deliver(input: { registrationRef: string; notificationId: string }): Promise<void>
}
