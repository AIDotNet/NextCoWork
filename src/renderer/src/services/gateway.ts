/** 需求：开放网关页面只使用具名服务；频道、解包与退订留在服务边界。 */
import type { GatewayStatus } from '../../../shared/domain/provider'
import type { Unsubscribe } from '../../../shared/ipc/contract'
import { invoke, on } from './ipc'

export function getGatewayStatus(): Promise<GatewayStatus> { return invoke('gateway:getStatus', undefined) }
export function getGatewayKey(): Promise<string> { return invoke('gateway:getKey', undefined) }
export function getGatewayAddresses(): Promise<string[]> { return invoke('gateway:getAddresses', undefined) }
export function onGatewayStatus(callback: (status: GatewayStatus) => void): Unsubscribe { return on('gateway:status', callback) }
export function resetGatewayHealth(): Promise<GatewayStatus> { return invoke('gateway:resetHealth', {}) }
