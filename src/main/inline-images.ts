/**
 * 旧转录里的内联图 → 会话附件,在**被读到时**按需转存。
 *
 * 需求:截图、旧版本生成的图曾以整段 base64 写进消息(一张截图最多 8MiB,base64 后约 10.7MB
 * 字符)。它们每被读一次就要整段过一次 IPC、在渲染层常驻一份。新产生的图已经直接落成附件
 * (见 `kernel/tool/builtin/browser.ts`);这里处理已经在库里的那些 —— 不做全库迁移,
 * 只在某一页被读出来时顺手转掉这一页里的,每次有上限。
 *
 * 三条不变式:
 * 1. **原图一个字节都不改**:原样落盘、按内容去重,不压缩、不缩放。模型看到的仍是同样的
 *    字节(发往上游时 `prepareRequestImages` 把本会话的 `ncw://` 换回 data URL)。
 * 2. **比较并替换**:只在这条消息仍是读出来时那一份时才写;转存期间被编辑、删除、导入改写的,
 *    放弃这一次,原样返回,下次再读到再说。
 * 3. **会话忙时不动**:正在跑的那一轮在收尾时会整段写回它内存里的那份历史,这时改了也会被
 *    盖回去,白白多一次转存。
 *
 * 任何一张转存失败(超限、魔数不对、磁盘满)都只是保持原样,不影响这一页的读取。
 */
import type { AgentMessage, ContentPart, ToolOutputImage } from '../shared/agent/message'

export interface InlineImageDeps {
  isBusy(sessionId: string): boolean
  /** 一张图的字节 → 本会话附件地址(`ncw://`)。失败抛错 */
  save(sessionId: string, mime: string, bytes: Uint8Array<ArrayBuffer>): string
  /** 这条消息仍是 `expectedParts` 那一份时才换成 `parts`;返回是否真的写了 */
  replaceIfUnchanged(sessionId: string, messageId: string, expectedParts: string, parts: ContentPart[]): boolean
  log(message: string, error?: unknown): void
}

/** 一次读页最多转几张 —— 读页是同步的,不能为一次打开会话卡住主进程太久 */
export const INLINE_CONVERT_MAX_IMAGES = 12
/** 一次读页最多转多少字节(解码后) */
export const INLINE_CONVERT_MAX_BYTES = 32 * 1024 * 1024

const DATA_URL = /^data:([^;,]+);base64,([A-Za-z0-9+/]+={0,2})$/

interface Budget { images: number; bytes: number }

/**
 * 返回转换之后的这一页。没有内联图、会话忙、预算用完时原样返回(同一个数组)。
 */
export function convertInlineImages(sessionId: string, messages: AgentMessage[], deps: InlineImageDeps): AgentMessage[] {
  if (!messages.some(hasInlineImage) || deps.isBusy(sessionId)) return messages
  const budget: Budget = { images: INLINE_CONVERT_MAX_IMAGES, bytes: INLINE_CONVERT_MAX_BYTES }
  let changed = false
  const out = messages.map((message) => {
    if (budget.images <= 0 || !hasInlineImage(message)) return message
    let converted = false
    const parts = message.parts.map((part): ContentPart => {
      if (part.type === 'image' && part.dataRef.startsWith('data:')) {
        const ref = store(sessionId, part, budget, deps)
        if (ref === null) return part
        converted = true
        return { ...part, dataRef: ref }
      }
      if (part.type === 'tool_result' && (part.output.images ?? []).some((image) => image.dataRef.startsWith('data:'))) {
        let any = false
        const images = (part.output.images ?? []).map((image): ToolOutputImage => {
          if (!image.dataRef.startsWith('data:')) return image
          const ref = store(sessionId, image, budget, deps)
          if (ref === null) return image
          any = true
          return { ...image, dataRef: ref }
        })
        if (!any) return part
        converted = true
        return { ...part, output: { ...part.output, images } }
      }
      return part
    })
    if (!converted) return message
    try {
      if (!deps.replaceIfUnchanged(sessionId, message.id, JSON.stringify(message.parts), parts)) return message
    } catch (error) {
      deps.log(`[images] inline image conversion was not saved: ${message.id}`, error)
      return message
    }
    changed = true
    return { ...message, parts }
  })
  return changed ? out : messages
}

function hasInlineImage(message: AgentMessage): boolean {
  return message.parts.some((part) => (part.type === 'image' && part.dataRef.startsWith('data:'))
    || (part.type === 'tool_result' && (part.output.images ?? []).some((image) => image.dataRef.startsWith('data:'))))
}

function store(sessionId: string, image: { mime: string; dataRef: string }, budget: Budget, deps: InlineImageDeps): string | null {
  if (budget.images <= 0) return null
  const match = DATA_URL.exec(image.dataRef)
  if (match === null) return null
  // 解码前按长度估算,超预算的整张跳过,不白解码
  const estimated = Math.floor((match[2]!.length * 3) / 4)
  if (estimated > budget.bytes) return null
  const bytes = new Uint8Array(Buffer.from(match[2]!, 'base64'))
  budget.images -= 1
  budget.bytes -= bytes.byteLength
  try {
    return deps.save(sessionId, match[1]!, bytes)
  } catch (error) {
    deps.log('[images] inline image could not be stored; keeping it inline', error)
    return null
  }
}
