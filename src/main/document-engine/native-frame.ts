/**
 * 主进程 ↔ 文档引擎 helper 的**分帧**编解码。纯函数 + 一个小状态机,不碰进程。
 *
 * ## 为了什么需求建的
 *
 * helper 是插件包里带的原生进程(LibreOfficeKit 承载),与主进程之间走 stdio。
 * stdio 是字节流,不保证一次 `data` 事件就是一条完整消息 —— 画布 tile 动辄几百 KB,
 * 会被切成很多块;反过来几条小的控制消息也可能粘在一块里。不分帧的话,
 * 表现是「偶尔 JSON.parse 报错、画面缺一块」,且只在大文档 / 慢机器上复现。
 *
 * ## 帧格式(版本 1)
 *
 * ```
 * [u32 BE 负载长度][u8 类型][负载]
 * 类型 0 = UTF-8 JSON 控制消息,1 = 二进制附件(tile / 导出字节)
 * ```
 *
 * ## 不变式
 *
 * - **单帧上限是硬的。** 超限直接判协议错误并要求调用方杀掉 helper,而不是尝试
 *   分配:一个坏掉(或被篡改)的 helper 报一个 4GB 长度,就能让主进程分配失败崩溃。
 * - 解码器只在拿齐一整帧时产出,不产出半帧。
 */

export const FRAME_HEADER_BYTES = 5
/**
 * 单帧负载上限。★ 64 MiB 足够一次整页 tile(8K×8K BGRA 是 256 MiB,不允许一次传,
 * helper 必须按 tile 切片);更大的导出字节走文件,不走管道。
 */
export const MAX_FRAME_BYTES = 64 * 1024 * 1024

export type FrameKind = 'json' | 'binary'
export type Frame = { kind: 'json'; value: unknown } | { kind: 'binary'; bytes: Uint8Array }

export class FrameProtocolError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'FrameProtocolError'
  }
}

export function encodeJsonFrame(value: unknown): Buffer {
  return encode(0, Buffer.from(JSON.stringify(value), 'utf8'))
}

export function encodeBinaryFrame(bytes: Uint8Array): Buffer {
  return encode(1, Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength))
}

function encode(type: number, payload: Buffer): Buffer {
  if (payload.length > MAX_FRAME_BYTES) throw new FrameProtocolError(`frame of ${payload.length} bytes exceeds ${MAX_FRAME_BYTES}`)
  const header = Buffer.alloc(FRAME_HEADER_BYTES)
  header.writeUInt32BE(payload.length, 0)
  header.writeUInt8(type, 4)
  return Buffer.concat([header, payload])
}

/**
 * 增量解码器。`push` 一块字节,拿回这块凑齐的所有整帧。
 *
 * ★ 抛 `FrameProtocolError` 之后解码器进入**坏态**,之后的 push 一律再抛:
 * 字节流一旦失步就没有可靠的办法找回帧边界,继续解只会把 tile 字节当 JSON 解。
 */
export class FrameDecoder {
  private chunks: Buffer[] = []
  private buffered = 0
  private broken: FrameProtocolError | null = null

  push(chunk: Uint8Array): Frame[] {
    if (this.broken !== null) throw this.broken
    this.chunks.push(Buffer.from(chunk.buffer, chunk.byteOffset, chunk.byteLength))
    this.buffered += chunk.byteLength
    const out: Frame[] = []
    try {
      for (;;) {
        if (this.buffered < FRAME_HEADER_BYTES) break
        const header = this.peek(FRAME_HEADER_BYTES)
        const length = header.readUInt32BE(0)
        const type = header.readUInt8(4)
        if (length > MAX_FRAME_BYTES) throw new FrameProtocolError(`frame length ${length} exceeds ${MAX_FRAME_BYTES}`)
        if (type !== 0 && type !== 1) throw new FrameProtocolError(`unknown frame type ${type}`)
        if (this.buffered < FRAME_HEADER_BYTES + length) break
        this.take(FRAME_HEADER_BYTES)
        const payload = this.take(length)
        if (type === 1) {
          out.push({ kind: 'binary', bytes: new Uint8Array(payload) })
          continue
        }
        let value: unknown
        try { value = JSON.parse(payload.toString('utf8')) } catch { throw new FrameProtocolError('control frame is not valid JSON') }
        out.push({ kind: 'json', value })
      }
    } catch (error) {
      if (error instanceof FrameProtocolError) this.broken = error
      throw error
    }
    return out
  }

  /** 缓冲里还有没凑齐的字节吗。helper 退出时用它判断「最后一帧被截断」。 */
  get pendingBytes(): number {
    return this.buffered
  }

  private peek(n: number): Buffer {
    const first = this.chunks[0]
    if (first !== undefined && first.length >= n) return first.subarray(0, n)
    return Buffer.concat(this.chunks, this.buffered).subarray(0, n)
  }

  private take(n: number): Buffer {
    const all = this.chunks.length === 1 ? (this.chunks[0] as Buffer) : Buffer.concat(this.chunks, this.buffered)
    const head = Buffer.from(all.subarray(0, n))
    const rest = all.subarray(n)
    this.chunks = rest.length === 0 ? [] : [rest]
    this.buffered = rest.length
    return head
  }
}
