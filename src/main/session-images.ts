/*
 * `SessionImageStore` 的主进程实现 —— 把「会话图片仓」这个内核端口接到附件存储上。
 *
 * 需求:生成图要和用户粘贴的图**同一种身份**:同一个附件根、同一种 `ncw://` 地址、
 * 同一条 draft → committed 生命周期(随消息提交、随会话删除、孤儿按 TTL 回收)。
 * 所以这里不另写落盘逻辑,而是直接复用 `uploadAttachment`(去重、临时文件 + rename、
 * 魔数认格式、draft 登记全在那一份里);读回则复用 `resolveImageDataRef`
 * (只认本会话、realpath 围栏、大小、魔数)。两者各复制一份的症状是安全校验只修一份。
 *
 * ★ 已知窗口:生成图以 **draft** 登记,要等这条 tool_result 消息提交时才转 committed。
 * 这段时间里切走再切回会话,`ChatView` 恢复草稿附件区时会把它当成一张待发附件列出来。
 * 正常路径下窗口只有「同一批并行工具里最慢那个跑完」那么长。拆除条件:附件表有了
 * 「工具产出、未提交」这一种独立状态(不进草稿区、仍按 TTL 回收)之后,改用那种状态登记。
 *
 * 故意不做:不存 displayName 以外的来源信息(模型、提示词)—— 那些在工具卡片的入参里。
 */
import type { ToolOutputImage } from '../shared/agent/message'
import { extOfMime, imageMimeOfBytes, mimeOfExt, parseNcwUrl } from '../shared/domain/attachment'
import { uploadAttachment } from './services/attachments'
import type { KernelHost } from './kernel/host'
import type { SessionImageStore } from './kernel/session-images'
import { resolveImageDataRef } from './kernel/upstream/images'

/** `data:<mime>;base64,<payload>` → 字节。形状不对当场抛,绝不落一个空文件。 */
function bytesOfDataUrl(dataRef: string): Uint8Array<ArrayBuffer> {
  const marker = ';base64,'
  const at = dataRef.indexOf(marker)
  if (!dataRef.startsWith('data:') || at < 0) throw new Error('Expected a base64 image data URL')
  return new Uint8Array(Buffer.from(dataRef.slice(at + marker.length), 'base64'))
}

/**
 * 解析前给 `resolveImageDataRef` 的 mime 猜测。它只用于通过白名单闸门 ——
 * 真正的 mime 以读回来的字节为准(那个函数和下面的 `imageMimeOfBytes` 都会复核)。
 */
function guessMime(ref: string): string {
  if (ref.startsWith('data:')) return ref.slice('data:'.length, Math.max(ref.indexOf(';'), 'data:'.length))
  return mimeOfExt(parseNcwUrl(ref)?.fileName ?? '')
}

export function sessionImageStoreFor(deps: {
  /** 这次 run 所属的会话 —— 地址的 owner,也是读回时唯一认的 owner */
  sessionId: string
  /** 现取宿主(不闭包捕获):`installHost()` 换宿主后仍拿到当下那个 */
  host: () => KernelHost
}): SessionImageStore {
  return {
    // 契约要求 Promise;`uploadAttachment` 本身是同步的
    async save(image) {
      const attachment = uploadAttachment({
        scope: 'session',
        ownerId: deps.sessionId,
        displayName: `generated${extOfMime(image.mime)}`,
        mime: image.mime,
        bytes: bytesOfDataUrl(image.dataRef)
      })
      return { mime: image.mime, dataRef: attachment.url }
    },
    async read(ref, signal) {
      const resolved = await resolveImageDataRef({ mime: guessMime(ref), dataRef: ref }, deps.host(), { sessionId: deps.sessionId }, signal)
      const bytes = bytesOfDataUrl(resolved.dataRef)
      const mime: ToolOutputImage['mime'] | null = imageMimeOfBytes(bytes)
      if (mime === null) throw new Error('The referenced file is not a recognized image (jpg/png/gif/webp)')
      return { mime, bytes }
    }
  }
}
