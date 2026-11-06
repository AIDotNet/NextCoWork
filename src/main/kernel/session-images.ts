/*
 * 会话图片仓 —— 「工具产出的图 / 模型点名的图」与会话附件存储之间的那条缝。
 *
 * 需求:Agent 要能**点名**对话里的任何一张图 —— 用户粘贴的、`generate_image` 生成的 ——
 * 并把它交给后续工具(改图、`SaveImage` 落到工作区)。点名靠的是 `ncw://` 会话附件地址:
 * - 生成图经 `save` 落成本会话附件,`output.images[i].dataRef` 从内联 data URL
 *   换成 `ncw://attachments/sessions/<会话>/<ULID>.<ext>`,工具回执里逐张列出;
 * - 用户附件本来就是 `ncw://`,发给上游时由 `upstream/images.ts` 在图后附一行地址;
 * - 谁拿着地址回来,都经 `read` 走 `resolveImageDataRef` 那一条校验
 *   (只认本会话、realpath 围栏、大小、魔数)—— 地址本身不是权限,会话归属才是。
 *
 * ★ 形状同 `ImageGenBridge` / `ShellBridge`:内核只认这个窄接口,「附件根在哪、
 * 怎么登记 draft 行、怎么去重」全部留在 `main/session-images.ts`。内核仍然零 electron、
 * 拿假实现就能单测。
 *
 * ★ 故意不做:不接受 `data:` 以外的外部来源落盘(http(s) 下载在工具侧走 `downloadImage`),
 * 也不提供删除 —— 生成图随消息提交、随会话删除,生命周期与用户附件完全一致。
 */
import type { ToolOutputImage } from '../../shared/agent/message'

export interface SessionImageStore {
  /**
   * 一张内联 data URL 图 → 本会话附件。返回同一张图,`dataRef` 换成 `ncw://` 地址。
   * 失败抛错(磁盘满、超限);调用方决定要不要退回内联。
   */
  save(image: ToolOutputImage): Promise<ToolOutputImage>
  /**
   * `ncw://` 会话附件地址(或内联 data URL)→ 字节。跨会话、越界、非图片一律抛。
   * `mime` 以**读回来的字节**为准,不信地址的扩展名。
   */
  read(ref: string, signal: AbortSignal): Promise<{ mime: ToolOutputImage['mime']; bytes: Uint8Array }>
}
