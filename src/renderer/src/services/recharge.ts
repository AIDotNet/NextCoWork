/**
 * 钱包充值的 IPC 封装。失败分流在主进程做完（`main/ipc/recharge.ts`），
 * 这里只把三条频道包成具名函数 —— 渲染层其余地方不出现频道字符串。
 */
import type { RechargeCheckoutResult, RechargeOptionsState, RechargeOrderState } from '../../../shared/domain/recharge'
import { invoke } from './ipc'

export const getRechargeOptions = (): Promise<RechargeOptionsState> => invoke('recharge:getOptions', undefined)
export const createRechargeCheckout = (amount: number): Promise<RechargeCheckoutResult> => invoke('recharge:checkout', { amount })
export const getRechargeOrder = (orderNo: string): Promise<RechargeOrderState> => invoke('recharge:getOrder', { orderNo })
