/*
 * 会话视频仓 —— 「后台任务取回的成品」与会话附件存储之间的那条缝。
 *
 * ★ 形状与 `session-images.ts` **刻意同构**,但两处不同是要点写在这里:
 *
 * 1. **没有内存里的字节往返**。图片那侧的 `save(image)` 拿的是一个内联 data URL
 *    (因为生成接口就回那个);视频这边从头到尾只有一条路 —— **从远端 URL 流式
 *    下载到附件目录**,所以端口上只有一个 `download()`。
 *
 * 2. **读回视频不是给人播的**。`<video src="ncw://…">` 由协议层直接服务文件
 *    (含 Range),不经过这个端口;这里只给 `SaveVideo` 用 —— 它要把字节写到
 *    工作区,而那需要一条**会话归属校验过**的读路径。
 *
 * ★ 端口窄到只有两个方法,是为了让内核能拿假实现单测(见
 * `kernel/tool/builtin/__tests__/video.test.ts`)。
 */
import type { VideoAssetRef } from '../../shared/domain/video-generation'

export interface SessionVideoStore {
  /**
   * 把一个远端视频取回成本会话附件。
   *
   * ★ 失败**必须抛**:调用方(后台 manager)靠它区分"生成成功"与"取回失败",
   * 而那两条轨道在界面上说的话完全不同(见 `video-generation.ts` 的
   * `VideoRetrievalStatus`)。
   */
  download(remote: { url: string; headers?: Record<string, string>; mime?: string }, signal: AbortSignal): Promise<VideoAssetRef>
  /**
   * 本会话某个 \`ncw://\` 视频 → 字节流(给 `SaveVideo` 写进工作区)。
   * 跨会话、越界、非视频一律抛。
   *
   * ★ 与 `SessionImageStore.read` 同一条安全边界:地址本身不是权限,
   * **会话归属**才是。所以只认 \`scope === 'session' && ownerId === 本会话\`。
   */
  read(ref: string, signal: AbortSignal): Promise<{ mime: string; size: number; bytes: Uint8Array }>
}
