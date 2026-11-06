export type Dispose = () => void

/** 平台实现完成 TLS/身份校验后才提供；此接口本身不代表已经授权。 */
export interface SecureTransport {
  send(frame: Uint8Array): void | Promise<void>
  onFrame(listener: (frame: Uint8Array) => void): Dispose
  onClose(listener: () => void): Dispose
  close(): void
}

export interface ClientClock {
  schedule(callback: () => void, delayMs: number): Dispose
}
