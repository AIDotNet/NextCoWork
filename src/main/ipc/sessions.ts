/** 会话实体 IPC：所有读写都经过 state/store，避免 handler 直接写 SQL。 */
import { readFile } from 'node:fs/promises'
import { createReadStream, createWriteStream, mkdirSync, renameSync, rmSync } from 'node:fs'
import { pipeline } from 'node:stream/promises'
import { createHash } from 'node:crypto'
import { basename, dirname, extname, join } from 'node:path'
import type { Session, SessionChange, SessionPage } from '../../shared/domain/session'
import type { AgentMessage } from '../../shared/agent/message'
import { isToolResultOnly } from '../../shared/agent/message'
import { editUserMessage, removeSpan, replySpan, turnSpan } from '../../shared/agent/history-edit'
import { ownedImageFileNames, rehomeImageRefs, turnEndIndex } from '../../shared/agent/session-clone'
import { isVideoMime } from '../../shared/domain/attachment'
import { buildNcwUrl } from '../../shared/domain/attachment'
import { attachmentRoot } from '../net/attachment-protocol'
import type { SessionMode } from '../../shared/agent/run-request'
import { normalizeModeId } from '../../shared/domain/mode'
import { extOfMime, mimeOfExt } from '../../shared/domain/attachment'
import { convertInlineImages, type InlineImageDeps } from '../inline-images'
import { ulid } from '../../shared/util/id'
import { runs, type SessionActivity } from '../kernel/run-registry'
import { windows } from '../window/registry'
import { store } from '../state/store'
import { putDraftAttachment } from '../db/repo'
import { removeSessionAttachmentFiles } from './storage'
import { uploadAttachment } from './attachment'
import { endGoalSession, restoreGoal } from '../goal/runtime'
import { ensureGoalRuntime } from '../runtime'
import { interactions } from '../kernel/interaction-gate'
import { IpcError } from './errors'

function changed(event: SessionChange): void {
  windows.emitToAll('sessions:changed', event)
}

export function listSessions(req: { workspaceId: string; archived?: boolean }) {
  const activeSessionIds = new Set(
    runs.activeRunIds().map((id) => runs.get(id)?.sessionId).filter((id): id is string => id !== undefined)
  )
  return store.listSessions(req.workspaceId, req.archived).map((item) => ({
    ...item,
    running: activeSessionIds.has(item.id)
  }))
}

export function getSession(req: { sessionId: string }) {
  const detail = store.getSessionDetail(req.sessionId)
  if (detail === undefined) throw new Error(`会话不存在: ${req.sessionId}`)
  if (detail.session.parentSessionId === undefined) {
    ensureGoalRuntime()
    restoreGoal(req.sessionId, detail.messages)
  }
  return detail
}

/** Replace a session transcript after an intentional user edit. */
export function replaceHistory(req: { sessionId: string; messages: AgentMessage[] }): void {
  if (runs.isSessionBusy(req.sessionId)) {
    throw new Error('有运行中的 Agent 或历史操作，请先等待任务完成后再编辑消息')
  }
  commitEditedHistory(req.sessionId, req.messages)
}

/**
 * 转录的一页。渲染层打开会话、往上翻页都走这条,不再一次读整段历史(见 `SessionPage`)。
 */
export function getSessionPage(req: { sessionId: string; limit: number; beforeMessageId?: string }): SessionPage {
  const page = store.getSessionPage(req.sessionId, req.limit, req.beforeMessageId)
  if (page === undefined) throw new Error(`会话不存在: ${req.sessionId}`)
  if (page.session.parentSessionId === undefined) {
    ensureGoalRuntime()
    // 目标恢复要看整段历史里的标记:让它自己读库,而不是拿这一页去猜
    restoreGoal(req.sessionId)
  }
  // 旧转录里的内联 base64 图在被读到时按需转存成附件(有上限,失败保持原样)
  const messages = convertInlineImages(req.sessionId, page.messages, inlineImageDeps)
  return messages === page.messages ? page : { ...page, messages }
}

const inlineImageDeps: InlineImageDeps = {
  isBusy: (sessionId) => runs.isSessionBusy(sessionId),
  save: (sessionId, mime, bytes) => uploadAttachment({
    scope: 'session',
    ownerId: sessionId,
    displayName: `inline${extOfMime(mime)}`,
    mime,
    bytes
  }).url,
  replaceIfUnchanged: (sessionId, messageId, expectedParts, parts) =>
    store.replaceMessagePartsIfUnchanged(sessionId, messageId, expectedParts, parts),
  log: (message, error) => { console.warn(message, error) }
}

/** 会话元数据 + 消息条数。对话视图读模式/模型、提炼横幅读标题,都不需要正文 */
export function getSessionSummary(req: { sessionId: string }): { session: Session; messageCount: number } {
  const summary = store.getSessionSummary(req.sessionId)
  if (summary === undefined) throw new Error(`会话不存在: ${req.sessionId}`)
  return summary
}

/** 会话里最后一条助手消息 —— 后台子代理交差的正文 */
export function getLastAssistant(req: { sessionId: string }): AgentMessage | null {
  return store.lastAssistantMessage(req.sessionId) ?? null
}

/**
 * 编辑一条提问;`truncate` = 从这条起重跑(这条和之后的全部移除,新提问由调用方另起一轮发出)。
 *
 * ★ 按 id 在**完整历史**上改:渲染层手里只有一页,拿那一页整段 `replaceHistory`
 * 会把页外的历史当成「删掉了」。
 */
export function editMessage(req: { sessionId: string; messageId: string; text: string; truncate: boolean }): void {
  editHistory(req.sessionId, (history) => editUserMessage(history, req.messageId, req.text, req.truncate))
}

/** 删一整轮(提问 + 它引出的全部回复与工具回执)。规则见 `turnSpan` */
export function deleteTurn(req: { sessionId: string; userMessageId: string }): void {
  editHistory(req.sessionId, (history) => {
    const span = turnSpan(history, req.userMessageId)
    return span === null ? null : removeSpan(history, span)
  })
}

/** 只删一条助手回复,提问留着。规则见 `replySpan` */
export function deleteReply(req: { sessionId: string; fromId: string; toId: string }): void {
  editHistory(req.sessionId, (history) => {
    const span = replySpan(history, req.fromId, req.toId)
    return span === null ? null : removeSpan(history, span)
  })
}

function editHistory(sessionId: string, edit: (history: readonly AgentMessage[]) => AgentMessage[] | null): void {
  if (runs.isSessionBusy(sessionId)) {
    throw new Error('有运行中的 Agent 或历史操作，请先等待任务完成后再编辑消息')
  }
  const next = edit(store.getHistory(sessionId))
  if (next === null) throw new Error('要改写的消息已经不在这条会话里了')
  commitEditedHistory(sessionId, next)
}

function commitEditedHistory(sessionId: string, messages: readonly AgentMessage[]): void {
  /*
    ★★ 手动编辑转录 = 永久脱离导入来源,且必须和替换**在同一个事务里**。

    分成两步的话,中间那一瞬同步器可以插进来:它看到的还是「未修改」,
    于是用源侧转录覆盖掉用户刚编辑的结果。放进同一个事务,两者不可能交错。

    ★ 只有**内容**修改算脱离。归档/收藏/改标题不是转录编辑 ——
    因为那些而丢掉正常同步,用户会觉得收藏一下就"坏了"。
  */
  store.tx(() => {
    store.replaceHistory(sessionId, messages)
    store.detachImportedSession(sessionId)
  })
  const session = store.getSession(sessionId)
  changed({ kind: 'history', sessionIds: [sessionId], workspaceId: session?.workspaceId })
}

export function createSession(req: { workspaceId: string; title?: string; sessionId?: string; mode?: SessionMode }): Session {
  const ws = store.getWorkspace(req.workspaceId)
  const session = store.ensureSession({
    id: req.sessionId,
    workspaceId: req.workspaceId,
    title: req.title,
    mode: normalizeModeId(req.mode ?? ws?.settings.defaultMode),
    rootPathAtCreation: ws?.rootPath ?? ''
  })
  changed({ kind: 'metadata', sessionIds: [session.id], workspaceId: req.workspaceId })
  return session
}

// 会话元数据写入(模式/模型/改名/归档/收藏)搬到了 services/session-metadata.ts —— 设备服务共用,不能依赖本文件。
export { renameSession, setArchived, setFavorited, setMode, setModel } from '../services/session-metadata'

/** 一张要搬进新会话的源图:库里那一行的元数据 + 预读好的字节。 */
interface SourceImage {
  displayName: string
  mime: string
  bytes: Uint8Array<ArrayBuffer>
}

/**
 * 把源会话里要搬的托管图片**异步**读进内存,按文件名索引。
 *
 * 需求:分支/复制不能卡住主进程。原先是建好会话之后逐张 `readFileSync` ——
 * 主进程是全部窗口共用的那一个,长会话、图多时表现为点下「分支」后**整个应用**
 * (不只是聊天区)冻住,之前的按钮又没有忙碌态,用户会再点几下,于是建出好几条。
 *
 * 读失败(文件已被清理)不算失败:这张图不进表,`rehomeImageRefs` 会保留原引用。
 * 源会话里它本来就是坏的,分支不比源会话更坏;原先是整个分支跟着失败。
 */
/**
 * 视频**不读进内存**。
 *
 * ★★ 上一版把"托管的图"扩成了"托管的媒材",于是这个函数会把生成的视频也一起
 * 收进来 —— 一段 4K 几十秒就是几百兆,而它是 `readFileSync` 的形状、
 * 紧接着还有"一口气同步建会话"那一段(见 `cloneIntoNewSession` 的注释)。
 * 表现正是这个函数当初被重写成异步要修掉的那个:**点下「分支」,整个应用冻住**。
 *
 * 所以视频走**磁盘到磁盘的流式拷贝**:先 `copyFile`(它发生在异步阶段,
 * 与读图并行),再由同步阶段只登记附件行 —— 全程没有一份几百兆的 Buffer。
 */
async function copySourceVideos(sourceSessionId: string, fileNames: readonly string[]): Promise<Map<string, { path: string; size: number; checksum: string; mime: string }>> {
  const loaded = await Promise.all(fileNames.map(async (fileName): Promise<[string, { path: string; size: number; checksum: string; mime: string }] | null> => {
    const row = store.getAttachmentRowByOwnerAndFileName(sourceSessionId, fileName)
    if (row === undefined) return null
    const staging = join(dirname(row.path), `.clone-${ulid()}${extname(row.path)}`)
    /*
      ★ 边拷边哈希 —— 只读一遍。分两步(先 copy 再读一遍算校验和)在几百兆上
      就是白白多读一次盘;而附件表的 `checksum` 是去重与诊断的依据,
      不能因为"视频大"就留空。
    */
    try {
      const { stat } = await import('node:fs/promises')
      const source = await stat(row.path)
      if (!source.isFile()) return null
      const hash = createHash('sha256')
      let written = 0
      await pipeline(
        createReadStream(row.path),
        async function* (chunks: AsyncIterable<Buffer>) {
          for await (const chunk of chunks) {
            hash.update(chunk)
            written += chunk.length
            yield chunk
          }
        },
        createWriteStream(staging, { flags: 'wx', mode: 0o600 })
      )
      return [fileName, { path: staging, size: written, checksum: hash.digest('hex'), mime: mimeOfExt(row.path) }]
    } catch {
      try { rmSync(staging, { force: true }) } catch { /* 尽力而为 */ }
      return null
    }
  }))
  return new Map(loaded.filter((entry): entry is [string, { path: string; size: number; checksum: string; mime: string }] => entry !== null))
}

async function readSourceImages(sourceSessionId: string, fileNames: readonly string[]): Promise<Map<string, SourceImage>> {
  const loaded = await Promise.all(fileNames.map(async (fileName): Promise<[string, SourceImage] | null> => {
    const row = store.getAttachmentRowByOwnerAndFileName(sourceSessionId, fileName)
    if (row === undefined) return null
    try {
      const bytes = new Uint8Array(await readFile(row.path))
      return [fileName, { displayName: row.displayName ?? basename(row.path), mime: mimeOfExt(row.path), bytes }]
    } catch {
      return null
    }
  }))
  return new Map(loaded.filter((entry): entry is [string, SourceImage] => entry !== null))
}

/**
 * 建一条新会话,把 `messages` 整段搬进去(含图片附件重新落盘)。
 * `duplicateSession`(整段克隆)和 `branchSession`(只克隆到某一轮为止)共用这一步 ——
 * 两者唯一的差别就是喂给它的 `messages` 切没切。
 *
 * 分两段:先异步读图(慢的 IO 都在这里,见 `readSourceImages`),再**一口气同步**
 * 建会话 + 落附件 + 写转录。
 * ★ 第二段里不许出现 await:会话行一旦建出来,到 `replaceHistory` 之前任何一次让出
 *   事件循环,侧边栏刷新都可能看到一条空的新会话;中途退出还会留下一条空壳。
 */
async function cloneIntoNewSession(sourceSession: Session, title: string, messages: readonly AgentMessage[]): Promise<Session> {
  const sourceSessionId = sourceSession.id
  /*
    ★ 分两拨:图片读进内存(它们小),视频**流式拷贝**(它们大)。
    `ownedImageFileNames` 名字没改,但它返回的是"这条转录引用的全部托管媒材" ——
    图片与视频一起(见 `session-clone.ts` 文件头那段:漏掉视频会让新会话里的
    地址指向源会话目录,而那在新会话里被会话归属校验拒掉)。
  */
  const ownedNames = ownedImageFileNames(messages, sourceSessionId)
  const videoNames = ownedNames.filter((name) => isVideoMime(mimeOfExt(name)))
  const imageNames = ownedNames.filter((name) => !isVideoMime(mimeOfExt(name)))
  const [images, videos] = await Promise.all([
    readSourceImages(sourceSessionId, imageNames),
    copySourceVideos(sourceSessionId, videoNames)
  ])

  const sessionId = ulid()
  const session = store.createSession({
    id: sessionId,
    workspaceId: sourceSession.workspaceId,
    title,
    model: sourceSession.model,
    /*
      需求:分支/复制出来的会话沿用源会话的供应商。原先漏了这一项,新会话只剩别名 ——
      正是 `setModel` 上 ★ 说的「新别名 + 旧供应商」那个谁也没配过的组合,
      同名别名挂在多家时会悄悄换到另一家去跑。
    */
    ...(sourceSession.modelProviderId === undefined ? {} : { modelProviderId: sourceSession.modelProviderId }),
    mode: sourceSession.mode,
    thinking: sourceSession.thinking,
    rootPathAtCreation: sourceSession.rootPathAtCreation,
    /*
      需求:复制/分支出来的提炼会话仍是提炼会话 —— 继续追问时 agent 要照样看到源会话摘要。
      不带过去的症状是分支里第一句追问就换来「你要我提炼哪段对话?」。
    */
    ...(sourceSession.skillSource === undefined ? {} : { skillSource: sourceSession.skillSource })
  })
  try {
    /*
      同一张图在转录里出现多次时只落一次盘。`uploadAttachment` 自己也按校验和去重,
      但那要先把整份字节再哈希一遍;这里按文件名记住就够了。
    */
    const moved = new Map<string, string>()
    const rehome = (fileName: string): string | undefined => {
      const done = moved.get(fileName)
      if (done !== undefined) return done
      const image = images.get(fileName)
      if (image !== undefined) {
        const url = uploadAttachment({ scope: 'session', ownerId: sessionId, ...image }).url
        moved.set(fileName, url)
        return url
      }
      /*
        ★ 视频:把**已经拷好**的那份登记成新会话的附件,再删掉暂存。
        这里没有 `writeBytes`,只有一次 rename —— 那正是"同步阶段不许 await"
        与"不能有几百兆 Buffer"两条约束下唯一可行的形态。
      */
      const video = videos.get(fileName)
      if (video === undefined) return undefined
      const id = ulid()
      const target = join(attachmentRoot(), 'sessions', sessionId, `${id}${extname(video.path)}`)
      try {
        mkdirSync(dirname(target), { recursive: true })
        renameSync(video.path, target)
        putDraftAttachment({
          id,
          scope: 'session',
          ownerId: sessionId,
          path: target,
          size: video.size,
          checksum: video.checksum,
          displayName: `cloned${extname(target)}`,
          createdAt: Date.now()
        })
      } catch {
        return undefined
      }
      const url = buildNcwUrl({ scope: 'session', ownerId: sessionId, fileName: `${id}${extname(target)}` })
      if (url === null) return undefined
      moved.set(fileName, url)
      return url
    }
    const cloned = messages.map((message) => ({
      ...rehomeImageRefs(message, sourceSessionId, rehome),
      id: ulid(message.createdAt)
    }))
    store.replaceHistory(sessionId, cloned)
  } catch (error) {
    const paths = store.sessionAttachmentPaths(sessionId)
    store.deleteSession(sessionId)
    removeSessionAttachmentFiles(paths)
    throw error
  } finally {
    // ★ 暂存的视频副本:成功时已经被 rename 走,失败时留在这里成了垃圾 —— 都删一遍。
    for (const video of videos.values()) {
      try { rmSync(video.path, { force: true }) } catch { /* 尽力而为 */ }
    }
  }
  changed({ kind: 'metadata', sessionIds: [session.id], workspaceId: sourceSession.workspaceId })
  return session
}

/**
 * 读克隆要的源数据:会话元数据 + 转录。
 * ★ 不走 `getSessionDetail`:那条还会顺带聚合 run 归属、用量和模型三张表,
 *   克隆一样都用不上(新消息 id 全换了,那些归属本来也对不上)。
 */
function cloneSource(sessionId: string): { session: Session; messages: readonly AgentMessage[] } {
  const session = store.getSession(sessionId)
  if (session === undefined) throw new Error(`会话不存在: ${sessionId}`)
  return { session, messages: store.getHistory(sessionId) }
}

/** Clone a transcript into a new session, including managed image attachments. */
export async function duplicateSession(req: { sessionId: string; title: string }): Promise<Session> {
  const source = cloneSource(req.sessionId)
  return cloneIntoNewSession(source.session, req.title, source.messages)
}

/**
 * 进行中的分支请求,键是「源会话 + 切点」。
 *
 * 看起来多余,其实不是:克隆现在是异步的,同一个按钮在第一次还没回来时再被点一下
 * (渲染层有忙碌态,但两个窗口开着同一条会话时各有各的按钮),两次请求会交错着
 * 各建一条一模一样的分支。命中这张表时直接复用第一次的结果。
 */
const pendingBranches = new Map<string, Promise<Session>>()

/**
 * 从某一轮「分支」出一条新会话:只带上到这一轮为止的转录,之后的内容不带过去。
 *
 * `uptoMessageId` 是引出这一轮的**用户提问**消息 id(与 `deleteTurn` 定位同一轮
 * 的方式一致)——一路带到下一条「非纯工具结果」的用户消息之前为止,好让紧跟着
 * 这轮提问之后的工具结果消息也一并带过去。切点算法在 `shared/agent/session-clone.ts`。
 */
export function branchSession(req: { sessionId: string; uptoMessageId: string; title: string }): Promise<Session> {
  const key = `${req.sessionId}\u0000${req.uptoMessageId}`
  const pending = pendingBranches.get(key)
  if (pending !== undefined) return pending
  const task = (async (): Promise<Session> => {
    const source = cloneSource(req.sessionId)
    const end = turnEndIndex(source.messages, req.uptoMessageId)
    if (end === null) throw new Error(`消息不存在: ${req.uptoMessageId}`)
    return cloneIntoNewSession(source.session, req.title, source.messages.slice(0, end))
  })().finally(() => pendingBranches.delete(key))
  pendingBranches.set(key, task)
  return task
}

/**
 * 开一条「从会话提炼 Skill」的会话。
 *
 * 需求:用户把一段做完的业务改动沉淀成项目 Skill(`.next-cowork/skills/<name>/`)。
 * 这里只负责**建会话并记住源会话**;摘要的生成和注入在每个 run 开始时由
 * `runtime.ts` 的 `loadSkillExtraction` 做(源会话之后还可能被追加消息或删掉,快照会过期)。
 *
 * 拒绝全部带 `messageKey`,渲染层直接翻成 toast —— 这几种都是用户能看懂、能自己绕开的原因。
 */
export function createSkillExtractionSession(req: { sourceSessionId: string; title: string }): Session {
  const source = store.getSession(req.sourceSessionId)
  if (source === undefined) throw extractionRefused('skills.extraction.sourceMissing', `会话不存在: ${req.sourceSessionId}`)
  // 子代理转录不是用户的对话,侧边栏里本来也看不到;挡在这里是兜底。
  if (source.parentSessionId !== undefined) throw extractionRefused('skills.extraction.sourceMissing', '子代理会话不能提炼')
  // 「提炼的提炼」只会拿一段写 Skill 的过程去写 Skill,没有业务经验可言。
  if (source.skillSource !== undefined) throw extractionRefused('skills.extraction.nested', '提炼会话不能再次提炼')
  /*
    需求:源会话必须是一个稳定的完成快照。
    不满足会怎样:提炼会话读到的是已提交历史,但源 run 仍可能在继续改同一批文件,
    最后生成的 Skill 可能只记录半套步骤,而两个 Agent 还会同时写同一个工作区。
  */
  if (runs.activeRunIds().some((runId) => {
    // 需求:后台子代理拥有自己的 sessionId;父对话结束不代表业务文件已经停止变化。
    let sessionId = runs.get(runId)?.sessionId
    const seen = new Set<string>()
    while (sessionId !== undefined && !seen.has(sessionId)) {
      if (sessionId === source.id) return true
      seen.add(sessionId)
      sessionId = store.getSession(sessionId)?.parentSessionId
    }
    return false
  })) {
    throw extractionRefused('skills.extraction.sourceRunning', '源会话仍有运行中的 Agent')
  }
  const hasUserTurn = store.getHistory(source.id).some((message) =>
    message.role === 'user' && message.internal !== true && !isToolResultOnly(message))
  if (!hasUserTurn) throw extractionRefused('skills.extraction.sourceEmpty', '源会话还没有任何对话')

  const session = store.createSession({
    id: ulid(),
    workspaceId: source.workspaceId,
    title: req.title,
    model: source.model,
    ...(source.modelProviderId === undefined ? {} : { modelProviderId: source.modelProviderId }),
    // 需要写文件:无论源会话当时在什么模式,提炼会话都在 code 模式下跑。
    mode: 'code',
    thinking: source.thinking,
    rootPathAtCreation: store.getWorkspace(source.workspaceId)?.rootPath ?? source.rootPathAtCreation,
    skillSource: { sessionId: source.id }
  })
  changed({ kind: 'metadata', sessionIds: [session.id], workspaceId: session.workspaceId })
  return session
}

function extractionRefused(messageKey: string, message: string): IpcError {
  return new IpcError('conflict', message, undefined, { messageKey })
}

/** 渲染层据此认出「删除被运行中的 Agent 挡住」,改弹「停止并删除」确认框 */
export const SESSION_DELETE_RUNNING_KEY = 'session.deleteRunningMessage'
/** 「停止并删除」:中断之后等驱动自己收尾的上限。过了还没收尾就判它卡死,强制清掉 */
export const DELETE_STOP_GRACE_MS = 3_000
/** 没有真在跑的 run、只有残留条目(停止中 / 收尾中)时,给它们自己收尾的宽限 */
export const DELETE_STALE_GRACE_MS = 1_500
const DELETE_POLL_MS = 25

export async function deleteSession(req: { sessionId: string; stopRunning?: boolean }): Promise<void> {
  /*
    ★ 只有「待删子树里的 run」能挡住删除,别的会话在跑不算。

    删除是整棵子树级联(子代理转录跟着走),所以运行中的 run 只要落在这棵子树里
    就必须处理 —— 否则那个 run 还在往已删除的 sessionId 上写消息。
    但**别的会话**的 run 和这次删除互不相干。

    ★ 判据是**真实的 run 状态**,不是「注册表里有没有这一条」(`isSessionBusy`)。
    曾经这里只看后者,于是一条按过停止却没收尾的 run、一把收尾时没放掉的会话互斥,
    或者父 run 结束后还挂着的后台子代理(渲染层的运行中角标不数子 run,用户看到的
    是「空闲」),都会让删除永远失败 —— 表现为「删不掉,重启后就能删」。现在:
    - 真在跑(`live`)/ 手动压缩中:不静默失败,抛一个带 messageKey 的 conflict,
      渲染层弹「停止并删除 / 取消」;用户选了停止,`stopRunning` 带着再来一次;
    - 只剩残留(`stopping` / `finalizing`):等一小段宽限让它自己收尾,还不收就强制清掉,照删。
  */
  const subtree = store.sessionSubtreeIds(req.sessionId)
  const activity = runs.sessionActivity(subtree)
  const genuinelyBusy = activity.live.length > 0 || activity.operations.length > 0
  if (genuinelyBusy && req.stopRunning !== true) {
    throw new IpcError('conflict', '该会话有运行中的 Agent，请先停止任务后再删除', undefined, {
      messageKey: SESSION_DELETE_RUNNING_KEY
    })
  }
  if (genuinelyBusy) {
    for (const handle of activity.live) runs.abort(handle.runId, true, { by: 'user' })
  }
  if (genuinelyBusy || hasLeftovers(activity)) {
    await waitForSessionsIdle(subtree, genuinelyBusy ? DELETE_STOP_GRACE_MS : DELETE_STALE_GRACE_MS)
    const forced = runs.forceSettleSessions(subtree)
    if (forced > 0) console.warn(`[sessions] 删除 ${req.sessionId}:强制清掉 ${forced} 条未收尾的 run / 会话互斥`)
  }
  // Capture paths before the database cascade removes their rows.  The
  // storage helper re-checks remaining references after deletion, so a file
  // shared by an older/migrated record is never removed prematurely.
  const attachmentPaths = requireSessionAttachmentPaths(req.sessionId)
  const session = store.getSession(req.sessionId)
  const previous = session === undefined ? [] : store.listSessions(session.workspaceId)
  const deletedIndex = previous.findIndex((item) => item.id === req.sessionId)
  const sessionIds = store.deleteSession(req.sessionId)
  for (const sessionId of sessionIds) {
    interactions.cancelGoalProposals(sessionId)
    endGoalSession(sessionId)
  }
  const deleted = new Set(sessionIds)
  const remaining = previous.filter((item) => !deleted.has(item.id))
  const replacement = remaining[Math.min(Math.max(deletedIndex, 0), remaining.length - 1)]
  const physical = removeSessionAttachmentFiles(attachmentPaths)
  if (physical.undeletable.length > 0) {
    console.warn('[sessions] 会话附件未能全部删除:', physical.undeletable)
  }
  changed({ kind: 'deleted', sessionIds, workspaceId: session?.workspaceId,
    ...(replacement === undefined || session === undefined ? {} : {
      replacement: { workspaceId: session.workspaceId, id: replacement.id, title: replacement.title }
    }) })
}

function requireSessionAttachmentPaths(sessionId: string): string[] {
  // Keep SQL out of the handler; the store/repository owns the attachment
  // shape and this small accessor is exposed through the storage boundary.
  return store.sessionAttachmentPaths(sessionId)
}

function hasLeftovers(activity: SessionActivity): boolean {
  return activity.live.length + activity.stopping.length + activity.finalizing.length + activity.operations.length > 0
}

/** 等子树里的 run 与会话互斥自己收尾,最多 `timeoutMs`。到点不抛 —— 剩下的由调用方强制清 */
async function waitForSessionsIdle(sessionIds: readonly string[], timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (hasLeftovers(runs.sessionActivity(sessionIds)) && Date.now() < deadline) {
    await new Promise<void>((resolve) => setTimeout(resolve, DELETE_POLL_MS))
  }
}

export function searchAll(req: { q: string; workspaceId?: string; limit: number }) {
  return store.searchSessions(req.q, req.workspaceId, req.limit)
}
