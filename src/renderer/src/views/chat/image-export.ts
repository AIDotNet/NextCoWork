/**
 * 产物图卡片上那对「复制 / 下载」按钮的**取字节**逻辑。
 *
 * 需求:对话里生成的图对用户是产物,而它此前**没有任何出口** —— 灯箱里那个
 * 「用别的程序打开」只对磁盘上的绝对路径出现(见 `ImageLightbox` 的
 * `isFilesystemPath`),而生成图要么是 `ncw://` 会话附件、要么是内联 data URL,
 * 两条都够不着它。于是「把这张图存下来」只能去求 Agent 调一次 `SaveImage`,
 * 而用户手上明明就已经有一张图。
 *
 * ★ 取字节有两条路,判据是地址的**前缀**,不是「能不能 fetch」:
 *   · `data:`  —— 逗号后面就是 base64,直接取,不必绕一圈 fetch;
 *   · `ncw://` —— 附件协议声明了 `supportFetchAPI`、CSP 的 `connect-src` 也放行了它
 *     (`main/net/attachment-protocol.ts` / `renderer/index.html`),所以 `fetch` 拿得到。
 *     ★ 这一条不能删:落盘之后的生成图(`kernel/session-images.ts`)走的正是它。
 *   · 保留 `data:` 那一条同样不能删:旧转录里的生成图、以及**生成期逐张推来的**
 *     那几张(`ToolCallState.partialImages`)都还是内联 data URL —— 只留一条路径的话,
 *     正在生成中的图会点不动。
 *
 * ★ 这里**不碰 UI、不碰 i18n、不碰 store**:读失败一律抛,由按钮自己决定显示什么
 * (它那段短暂状态),这样这套逻辑能被单测穷尽、也能被第二张卡片直接复用
 * (AGENTS §9「可测的纯逻辑抽成同目录 .ts」)。
 */

/**
 * 建议文件名 —— **只有名字,不带扩展名**。
 *
 * ★ 扩展名由主进程按**字节的魔数**补上(`app:saveImageFile`)。字节在那边,而
 * 文件名撒谎的代价是「双击打不开」(部分看图工具按扩展名挑解码器);
 * 渲染层手里只有转录里那个 mime,它通常是准的,但没有理由让两份判断并存。
 *
 * ★ 不用 prompt 当名字:那是模型写的自然语言(可能几百字、带换行和 emoji),
 * 而这个名字会进系统保存对话框。
 * ★ 序号是**本次调用内**的位置(与卡片上「放大查看第 N 张」同一个数):同一轮
 * 画了四张时,连着存下来的四个默认名各不相同,不会互相覆盖。
 */
export function imageFileName(index: number): string {
  return `image-${String(index)}`
}

/**
 * 一张产物图 → 原图字节的 base64(**不带** `data:` 前缀)。
 *
 * 副作用面:会为 `ncw://` 地址发一次同源请求(读的是本机附件目录里的那张图)。
 */
export async function imageBase64(dataRef: string): Promise<string> {
  const inline = dataUrlBase64(dataRef)
  if (inline !== null) return inline
  const response = await fetch(dataRef)
  if (!response.ok) {
    // 附件被外部删掉 / 地址越界 —— 对用户都是「这张图现在拿不到了」。
    // 不把状态码翻成人话:调用点只画一个「失败」,而状态码进不了那张卡。
    throw new Error(`图片读取失败(${String(response.status)})`)
  }
  return base64OfBytes(new Uint8Array(await response.arrayBuffer()))
}

/**
 * `data:…;base64,xxx` → `xxx`;不是内联图(含畸形前缀)返回 null。
 *
 * ★ 判据落在 `;base64` 上,而不是「以 data: 开头」:剩下那种(比如百分号编码的 SVG)
 * 交给 `fetch` —— `data:` 本身是可 fetch 的,那样拿到的是解码后的字节,而直接取正文
 * 会把 `%3Csvg…` 当成 base64 写出去,那是一个坏文件。
 * (本应用的内联产物图不会长成那样:`ToolOutputImage.mime` 只有四种位图,
 * 落进转录时一律 `;base64,`。这条分支是兜底。)
 */
function dataUrlBase64(ref: string): string | null {
  if (!ref.startsWith('data:')) return null
  const comma = ref.indexOf(',')
  if (comma < 0) return null
  return ref.slice(0, comma).includes(';base64') ? ref.slice(comma + 1) : null
}

/**
 * 字节 → base64。
 *
 * ★ 分块累加,不用 `btoa(String.fromCharCode(...bytes))`:后者要把整个字节数组
 * 摊成参数,一张几 MB 的图会 `Maximum call stack size exceeded` —— 而生成图动辄几 MB。
 * ★ 也不走 `FileReader.readAsDataURL`(它同样能给出 base64):那一步把纯函数变成
 * 一个拿不到假实现就测不了的异步回调,而这里的分块编码在单测里直接可验。
 */
function base64OfBytes(bytes: Uint8Array): string {
  const CHUNK = 0x8000
  let binary = ''
  for (let at = 0; at < bytes.length; at += CHUNK) {
    binary += String.fromCharCode(...bytes.subarray(at, at + CHUNK))
  }
  return btoa(binary)
}
