import type { SyncConflict, SyncPreview, SyncStatus } from '../../../shared/domain/config-sync'
import { invoke } from './ipc'

export function getStatus(): Promise<SyncStatus> {
  return invoke('configSync:getStatus', undefined)
}

export function setup(password: string, remember: boolean): Promise<SyncStatus> {
  return invoke('configSync:setup', { password, remember })
}

export function getConflicts(): Promise<SyncConflict[]> {
  return invoke('configSync:getConflicts', undefined)
}

export function getPreview(): Promise<SyncPreview> {
  return invoke('configSync:getPreview', undefined)
}

export function confirmInitial(): Promise<void> {
  return invoke('configSync:confirmInitial', undefined)
}

export function resolve(id: string, useRemote: boolean): Promise<void> {
  return invoke('configSync:resolve', { id, useRemote })
}

// 需求：使用统计的多设备合并可单独关掉；关掉后概览只剩本机数据。
export function setUsageSync(enabled: boolean): Promise<SyncStatus> {
  return invoke('configSync:setUsage', { enabled })
}
