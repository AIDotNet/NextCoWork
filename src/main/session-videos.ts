/*
 * `SessionVideoStore` 的主进程实现 —— 把「会话视频仓」这个内核端口接到附件存储上。
 *
 * ★ 读回那条路**复用图片那侧同一条落点校验**:只认本会话的 `ncw://`、
 * realpath 之后仍必须落在 `attachments/sessions/<本会话>/` 之内。
 *
 * ★★ 这里**故意不做**的事:不提供"把视频写回工作区" —— 那是 `SaveVideo` 通过
 * 宿主 fs 的流式写在做的(见 `kernel/tool/builtin/save-video.ts` 的文件头)。
 * 这个端口只回答"这个地址的字节是什么"。
 */
import { join } from 'node:path'
import { MAX_GENERATED_VIDEO_BYTES, videoMimeOfBytes } from '../shared/domain/video-generation'
import { mimeOfExt, parseNcwUrl } from '../shared/domain/attachment'
import { downloadVideoIntoSession } from './video-generation/download'
import type { KernelHost } from './kernel/host'
import type { SessionVideoStore } from './kernel/session-videos'

export function sessionVideoStoreFor(deps: {
  /** 这次 run 所属的会话 —— 地址的 owner,也是读回时唯一认的 owner */
  sessionId: string
  /** 现取宿主(不闭包捕获):`installHost()` 换宿主后仍拿到当下那个 */
  host: () => KernelHost
}): SessionVideoStore {
  return {
    async download(remote, signal) {
      const stored = await downloadVideoIntoSession(
        { fetch: deps.host().fetch, sessionId: deps.sessionId, now: () => deps.host().clock.now() },
        remote,
        signal
      )
      return stored.asset
    },

    async read(ref, signal) {
      signal.throwIfAborted()
      const locator = parseNcwUrl(ref)
      if (locator === null) throw new Error('Invalid video attachment location')
      /*
        ★★ 与图片那一侧同一条判据,且**同样严厉**:不是本会话的地址一律拒。
        少了这一句,`SaveVideo` 就能被一个构造出来的 `ncw://` 指向**别人的**
        会话附件 —— 而那条路径上没有任何别的东西会拦它。
      */
      if (locator.scope !== 'session' || locator.ownerId === undefined || locator.ownerId !== deps.sessionId) {
        throw new Error('That video attachment does not belong to this conversation')
      }
      const { fs, paths } = deps.host()
      const root = await fs.realpath(paths.attachments())
      const expectedOwnerRoot = join(root, 'sessions', locator.ownerId)
      const file = await fs.realpath(join(expectedOwnerRoot, locator.fileName))
      /*
        ★ 两层都要判:文件必须在**本会话的目录**里(而不是仅仅在附件根下)——
        否则一个 `file: '../../另一个会话/x.mp4'` 形状的地址(等价物)会读穿。
      */
      if (!file.startsWith(expectedOwnerRoot)) {
        throw new Error('The video attachment escaped its conversation directory')
      }
      const stat = await fs.stat(file)
      if (stat.isDir) throw new Error('The video attachment is a directory')
      if (stat.size === 0 || stat.size > MAX_GENERATED_VIDEO_BYTES) {
        throw new Error('The video attachment size is outside the allowed range')
      }
      const bytes = await fs.readFileBytes(file, stat.size + 1)
      if (bytes.length !== stat.size) throw new Error('The video attachment changed while it was being read')
      const mime = videoMimeOfBytes(bytes) ?? mimeOfExt(locator.fileName)
      return { mime, size: stat.size, bytes }
    }
  }
}
