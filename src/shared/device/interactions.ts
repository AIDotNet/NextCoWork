/**
 * 待决交互(审批 / 提问 / 计划 / 插件 / 目标)的**共享形状**。方案 §6。
 *
 * ★ 这里只有类型和校验,**没有存储、没有网络语义**。两个刻意的选择:
 *
 * 1. **不 import `src/main/db/`**。共享模块是被渲染层、未来的 controller-client 和
 *    原生夹具一起消费的;一旦反向依赖主进程数据库,那个包就带上了 Node 与 SQLite。
 *    存储形状在 `src/main/db/device-interactions.ts`,两边只共享下面这几个类型。
 * 2. **authoritative 字段(Payload/Answer)用 `DeviceValue`**。不是 `unknown`,也不是
 *    `any` —— 有界编解码(`encodeDeviceValue` / `decodeDeviceValue`)是唯一入口,
 *    所以"多大、多深、能不能放 `undefined`/类实例"在编解码那一层就已经定死了,
 *    访问器不需要再重复一遍。
 */
import { z } from 'zod'
import { deviceIdSchema, scopeIdSchema } from './identity'
import type { DeviceValue } from './value'

/**
 * 交互的持久隔离键。与 `DeviceBinding` 的字段名一一对应,但**故意不含**
 * `controllerId` / `serverEpoch` / `clientSessionId` / `bindingEpoch`:
 *
 * ★ 待决项属于**设备上的账号作用域**,不属于提出问题的那次连接。多个已授权控制端
 *   共享同一条待决项并竞争同一份回答(最多一次成功),所以把 controllerId 写进
 *   隔离键会让"另一台桌面看到同一个审批"变成不可能的。
 *   `serverEpoch` 是服务进程的启动代际,重启不该让待决项查不到;`clientSessionId` /
 *   `bindingEpoch` 是控制端本地概念,设备端没有对应的稳定值。
 *
 * `scopeEpoch` 反过来**必须在里面**:它挡的是"旧账号代际的回答落进新账号"。
 */
export const interactionScopeSchema = z.strictObject({
  deviceId: deviceIdSchema,
  dataEpoch: deviceIdSchema,
  configScope: scopeIdSchema,
  scopeEpoch: deviceIdSchema
})
export type InteractionScope = z.infer<typeof interactionScopeSchema>

/**
 * 五类待决项。**不复用** `shared/agent/interaction.ts` 的 `InteractionKind`:
 * 那边是内核的 `PendingInteraction` union(`tool_permission` / `ask_user` /
 * `plan_approval` / `goal_proposal`),描述的是"内核挂在哪个 promise 上";
 * 这里是**存储侧的稳定枚举**,要在数据库 CHECK 约束里出现,而且加了插件交互
 * (`plugin`,对应方案里"插件调用需要用户回答时存为待处理，无 UI 时不自动同意")。
 * 两者语义相邻但不是同一份契约,以后独立演进。
 */
export const DEVICE_INTERACTION_KINDS = ['permission', 'question', 'plan', 'plugin', 'goal'] as const
export const interactionKindSchema = z.enum(DEVICE_INTERACTION_KINDS)
export type DeviceInteractionKind = z.infer<typeof interactionKindSchema>

export const DEVICE_INTERACTION_STATUSES = ['pending', 'resolved', 'cancelled'] as const
export const interactionStatusSchema = z.enum(DEVICE_INTERACTION_STATUSES)
export type DeviceInteractionStatus = z.infer<typeof interactionStatusSchema>

/**
 * 写入输入。`expiresAt` 显式允许 `null`(永不自动过期),因为"永久有效的审批"
 * 确实存在(例如应用安装确认),用 `undefined` 表达会与"没传"混淆。
 */
export const createInteractionInputSchema = z.strictObject({
  id: deviceIdSchema,
  runId: deviceIdSchema,
  kind: interactionKindSchema,
  payload: z.custom<DeviceValue>((value) => value !== undefined),
  expiresAt: z.number().int().min(0).max(Number.MAX_SAFE_INTEGER).nullable().optional()
})
export type CreateInteractionInput = z.infer<typeof createInteractionInputSchema>

/** 回答输入。`payloadDigest` 是发起方看到的 payload 摘要,用来拒绝"答错了题"。 */
export const respondInteractionInputSchema = z.strictObject({
  id: deviceIdSchema,
  payloadDigest: z.string().min(1).max(128),
  answer: z.custom<DeviceValue>((value) => value !== undefined)
})
export type RespondInteractionInput = z.infer<typeof respondInteractionInputSchema>

/**
 * ★ **内部执行器广播 scope 的保留 controllerId**。
 *
 * `device_events` 按 `controller_id` 分区投递;交互事件要发给**所有**已授权控制端,
 * 所以用一个不属于任何真实控制端的标记位。真实 controllerId 由
 * `deviceIdSchema` 约束(必须以字母数字开头,`*` 不合法),**网络报文里指定不出这个值**;
 * 这个标记只防止命名冲突，不授予权限。`appendDeviceEvent` 是内部访问器，Gateway
 * 仍须独立验证方法、调用者和作用域，不能把传入一个 scope 当成已通过授权。
 */
export const DEVICE_INTERACTION_BROADCAST_CONTROLLER = '*'
