/**
 * 会话「分支 / 复制」的纯逻辑:切点在哪、哪些托管图片要跟着搬家、搬完怎么改写引用。
 *
 * 需求:「从这一轮分支到新会话」与侧边栏「复制会话」都要把一段转录搬进一条新会话,
 * 并把其中**属于源会话**的 `ncw://` 图片复制成新会话自己的。这三步错了都不报错,
 * 所以抽到 shared 单测(`shared/__tests__/session-clone.test.ts`):
 *   - 切点错位 → 分支里少了/多了一轮,用户只会觉得「怎么接不上」;
 *   - 漏搬一张图 → 新会话**下一次发送整轮失败**:`kernel/upstream/images.ts` 只认
 *     本会话的托管图,别家的一律判 `foreignSession`。原先只搬了 `image` part,
 *     `generate_image` 落在 `tool_result.output.images` 里的图就是这样漏掉的。
 *
 * 故意不做的事:不读文件、不碰数据库 —— IO 全在 `main/ipc/sessions.ts`。
 */
import { parseNcwUrl } from '../domain/attachment'
import { isToolResultOnly, type AgentMessage, type ContentPart } from './message'

/**
 * `userMessageId` 那一轮的**结束位置**(不含);找不到这条用户提问时返回 null。
 *
 * 一轮 = 这条用户提问 + 其后直到下一条「非纯工具结果」的用户消息为止的全部消息 ——
 * 紧跟在提问后的工具结果消息属于这一轮,带不过去的话分支里会留下一串没有结果的
 * 工具调用。口径与渲染层 `stores/session.ts` 的 `deleteTurn` 一致(那边目前还是
 * 自己内联的同一段循环)。
 */
export function turnEndIndex(messages: readonly AgentMessage[], userMessageId: string): number | null {
  const start = messages.findIndex((m) => m.id === userMessageId && m.role === 'user')
  if (start < 0) return null
  let end = start + 1
  while (end < messages.length) {
    const m = messages[end]
    if (m !== undefined && m.role === 'user' && !isToolResultOnly(m)) break
    end += 1
  }
  return end
}

/** 一个 dataRef 若是 `ownerId` 这条会话的托管图,返回它的文件名。 */
function ownedFileName(dataRef: string, ownerId: string): string | null {
  const locator = parseNcwUrl(dataRef)
  if (locator === null || locator.scope !== 'session' || locator.ownerId !== ownerId) return null
  return locator.fileName
}

/**
 * 转录里所有属于 `ownerId` 的托管媒材文件名,去重、按首次出现排序。
 *
 * 三处都要搜:用户贴的图(`image` part)、工具产出的图(`tool_result.output.images`)
 * 与**工具产出的视频**(`tool_result.output.videos`)。视频是后加的,而漏掉它
 * 的后果比漏一张图更重:一次"复制会话"之后,新会话里的视频地址指向源会话的目录
 * —— 而那条路径在新会话里**读不出来**(会话归属校验会拒),表现为一张永远黑屏的卡,
 * 且没有任何报错指向"分支时少搬了一个文件"。
 *
 * 去重的需求:同一份媒材在长会话里可能被引用多次,主进程按这张表逐个读盘,
 * 不去重就是同一个文件读 N 遍。
 */
export function ownedImageFileNames(messages: readonly AgentMessage[], ownerId: string): string[] {
  const seen = new Set<string>()
  const visit = (dataRef: string): void => {
    const name = ownedFileName(dataRef, ownerId)
    if (name !== null) seen.add(name)
  }
  for (const message of messages) {
    for (const part of message.parts) {
      if (part.type === 'image') visit(part.dataRef)
      else if (part.type === 'tool_result') {
        for (const image of part.output.images ?? []) visit(image.dataRef)
        for (const video of part.output.videos ?? []) visit(video.url)
      }
    }
  }
  return [...seen]
}

/**
 * 按 `rehome` 改写消息里所有属于 `ownerId` 的图片引用。
 *
 * `rehome` 返回 undefined = 这张图没能搬(源文件已丢),**保留原引用**:
 * 源会话里它本来就是坏的,分支不比源会话更坏;为一张历史旧图让整个分支失败不值得。
 * 没有改动的 part / 消息保持原引用,调用方可以放心拿它和源消息做浅比较。
 */
export function rehomeImageRefs(
  message: AgentMessage,
  ownerId: string,
  rehome: (fileName: string) => string | undefined
): AgentMessage {
  const target = (dataRef: string): string | undefined => {
    const name = ownedFileName(dataRef, ownerId)
    return name === null ? undefined : rehome(name)
  }
  let changed = false
  const parts = message.parts.map((part): ContentPart => {
    if (part.type === 'image') {
      const next = target(part.dataRef)
      if (next === undefined) return part
      changed = true
      return { ...part, dataRef: next }
    }
    if (part.type === 'tool_result' && (part.output.images !== undefined || part.output.videos !== undefined)) {
      let moved = false
      const images = (part.output.images ?? []).map((image) => {
        const next = target(image.dataRef)
        if (next === undefined) return image
        moved = true
        return { ...image, dataRef: next }
      })
      /*
        ★ 视频同样要改写(见本文件头那段:不改写的话新会话读到的是源会话的
        `ncw://` 地址,而那在新会话里被会话归属校验拒掉)。
      */
      const videos = (part.output.videos ?? []).map((video) => {
        const next = target(video.url)
        if (next === undefined) return video
        moved = true
        return { ...video, url: next }
      })
      if (!moved) return part
      changed = true
      return {
        ...part,
        output: {
          ...part.output,
          ...(part.output.images === undefined ? {} : { images }),
          ...(part.output.videos === undefined ? {} : { videos })
        }
      }
    }
    return part
  })
  return changed ? { ...message, parts } : message
}
