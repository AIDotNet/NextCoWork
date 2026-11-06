import WebSocket, { type RawData } from 'ws'
import type { Dispose, SecureTransport } from '../../controller-client/ports'
import { DEVICE_FRAME_MAX_BYTES } from '../../shared/device/capabilities'
import { DeviceProtocolError } from '../../shared/device/errors'

const QUEUE_MAX_BYTES = 4 * DEVICE_FRAME_MAX_BYTES

/** 只适配已打开的 socket；身份验证由 direct/tunnel 的 TLS 层完成。 */
export class NodeWebSocketChannel implements SecureTransport {
  private ended = false
  private frameListener: ((frame: Uint8Array) => void) | null = null
  private readonly closeListeners = new Set<() => void>()
  private readonly early: Uint8Array[] = []
  private earlyBytes = 0

  constructor(private readonly socket: WebSocket) {
    if (socket.readyState !== WebSocket.OPEN) throw new DeviceProtocolError('disconnected', 'Socket is not open')
    socket.on('message', this.receive)
    socket.on('close', this.closed)
    socket.on('error', () => { if (!this.ended) this.close() })
  }

  send(frame: Uint8Array): Promise<void> {
    if (this.ended || this.socket.readyState !== WebSocket.OPEN) return Promise.reject(new DeviceProtocolError('disconnected', 'Socket closed'))
    if (frame.length === 0 || frame.length > DEVICE_FRAME_MAX_BYTES || this.socket.bufferedAmount + frame.length > QUEUE_MAX_BYTES) {
      return Promise.reject(new DeviceProtocolError('resource_limit', 'Socket send budget exceeded'))
    }
    return new Promise((resolve, reject) => {
      this.socket.send(frame, { binary: true }, (error) => { if (error) reject(error); else resolve() })
    })
  }

  onFrame(listener: (frame: Uint8Array) => void): Dispose {
    if (this.frameListener) throw new DeviceProtocolError('conflict', 'Socket already has a frame consumer')
    if (this.ended) return () => {}
    this.frameListener = listener
    while (!this.ended && this.frameListener === listener && this.early.length > 0) {
      const frame = this.early.shift()!
      this.earlyBytes -= frame.length
      this.deliver(frame)
    }
    return () => { if (this.frameListener === listener) this.frameListener = null }
  }

  onClose(listener: () => void): Dispose {
    if (this.ended) { listener(); return () => {} }
    this.closeListeners.add(listener)
    return () => { this.closeListeners.delete(listener) }
  }

  close(): void {
    this.closed()
    this.socket.terminate()
  }

  private receive = (data: RawData, binary: boolean): void => {
    if (this.ended) return
    if (!binary) { this.close(); return }
    const length = Array.isArray(data) ? data.reduce((sum, item) => sum + item.length, 0) : data.byteLength
    if (length === 0 || length > DEVICE_FRAME_MAX_BYTES) { this.close(); return }
    const frame = new Uint8Array(Array.isArray(data) ? Buffer.concat(data) : data instanceof ArrayBuffer ? data : data)
    if (this.frameListener) this.deliver(frame)
    else {
      if (this.earlyBytes + length > QUEUE_MAX_BYTES || this.early.length >= 64) { this.close(); return }
      this.early.push(frame)
      this.earlyBytes += length
    }
  }

  private deliver(frame: Uint8Array): void {
    try { this.frameListener?.(frame) } catch { this.close() }
  }

  private closed = (): void => {
    if (this.ended) return
    this.ended = true
    this.socket.off('message', this.receive)
    this.socket.off('close', this.closed)
    // 保留 error listener 直到 socket 回收，避免 terminate 后迟到 error 无人处理。
    this.frameListener = null
    this.early.length = 0
    this.earlyBytes = 0
    const listeners = [...this.closeListeners]
    this.closeListeners.clear()
    for (const listener of listeners) { try { listener() } catch { /* 连接已关闭，其他订阅者仍需通知。 */ } }
  }
}
