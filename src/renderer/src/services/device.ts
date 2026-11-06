import type { DeviceInvitationPreview, DevicesState } from '../../../shared/domain/device-connection'
import { invoke, on } from './ipc'

export function getDevicesState(): Promise<DevicesState> {
  return invoke('device:getState', undefined)
}

/** 只解码并校验邀请(不联网),返回要让用户核对的设备指纹。 */
export function inspectDeviceInvitation(text: string): Promise<DeviceInvitationPreview> {
  return invoke('device:inspectInvitation', { text })
}

/** `confirmedFingerprint` 必须是用户核对过的那一个;与邀请不一致主进程会拒绝。 */
export function pairDevice(text: string, label: string, confirmedFingerprint: string): Promise<DevicesState> {
  return invoke('device:pair', { text, label, confirmedFingerprint })
}

export function selectDevice(id: string): Promise<DevicesState> {
  return invoke('device:select', { id })
}

export function useLocalDevice(): Promise<DevicesState> {
  return invoke('device:useLocal', undefined)
}

/** 移除本机保存的连接。不会撤销设备上的配对。 */
export function removeDevice(id: string): Promise<DevicesState> {
  return invoke('device:remove', { id })
}

/** 设置/清除(`url: null`)备用中转。URL 必须是不带凭据的 wss://;主进程会再校验。 */
export function setDeviceRelay(id: string, url: string | null, caPem?: string): Promise<DevicesState> {
  return invoke('device:setRelay', { id, url, ...(caPem !== undefined && caPem.trim() !== '' && { caPem }) })
}

export function onDevicesChanged(callback: (state: DevicesState) => void): () => void {
  return on('device:changed', callback)
}
