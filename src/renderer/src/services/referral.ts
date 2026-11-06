import type { ReferralState } from '../../../shared/domain/referral'
import { invoke } from './ipc'

/**
 * 奖励中心。**只有一次拉取，没有订阅** —— 邀请数据的变化由对方的注册与消费触发，
 * 桌面端这边没有任何本地事件能预告它（见 `shared/ipc/contract.ts` 上那条说明）。
 *
 * 不会 reject：失败已经在主进程翻译成 `unavailable` 的三种原因，
 * 调用方按原因选空态文案，而不是 catch 一句 message。
 */
export const getReferralCenter = (): Promise<ReferralState> => invoke('referral:get', undefined)
