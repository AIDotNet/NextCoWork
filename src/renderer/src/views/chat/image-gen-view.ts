/**
 * 生图卡片的**纯逻辑**:这一刻卡片上该摆几格、每格是图还是加载占位。
 *
 * 需求:同一张卡片要同时服务三种时刻,而且从一种切到下一种时格子不能乱跳 ——
 *   1. 参数还在流(pending):`n` 可能还没写到,先按 1 格占位,写到了再长成 N 格;
 *   2. 工具在跑(running):N 格里已到手的那几格换成图(`ToolCallState.partialImages`,
 *      按**格子序号**放,不按完成顺序),其余继续转加载动画;
 *   3. 跑完(ok):以落盘的 `output.images` 为准 —— 重开对话时只有它,没有过程态。
 *      失败的格子不再占位,只在卡片底部说一句「成功 3/4 张」。
 *
 * ★ 抽成 `.ts` 是这个仓库的可测试性手段(AGENTS §9):这三种时刻的切换全部依赖
 * 事件到达顺序,盯屏幕复现不了,三行测试就能钉住。
 *
 * ★ 故意不做:不从 `output.content` 的英文回执里反解「失败了几张」—— 那句话是写给
 * 模型的,措辞随时会改;张数差由「请求张数 − 实际张数」直接算。
 */
import type { ToolOutput, ToolOutputImage } from '../../../../shared/agent/message'
import { imageCountOf } from '../../../../shared/domain/image-count'
import { pick } from '../../../../shared/domain/tool-presenter'

export type ImageGenSlot =
  | { kind: 'image'; index: number; image: ToolOutputImage }
  | { kind: 'loading'; index: number }

export interface ImageGenView {
  slots: ImageGenSlot[]
  /** 这次要了几张(入参 `n`,钳过) */
  requested: number
  /** 已经到手/最终拿到的张数 */
  done: number
  /** 跑完了但少于要的张数 —— 卡片底部据此出一行说明 */
  partial: boolean
  /** 带了源图就是改图 —— 加载格上的状态字跟着换 */
  mode: 'generate' | 'edit'
}

export function imageGenView(
  input: unknown,
  output: ToolOutput | undefined,
  partialImages: Readonly<Record<number, ToolOutputImage>> | undefined
): ImageGenView {
  const requested = imageCountOf(input)
  const mode = pick(input, 'image') === '' ? 'generate' : 'edit'
  if (output !== undefined) {
    /*
      ★ 跑完以 output.images 为准,**不**再按 requested 补占位:失败格补一格永远转圈的
      加载动画,等于告诉用户「还在画」,而这次调用早就结束了。
      旧转录(这一档形态出现之前、生图还归 network 的那批)同样只有 output.images,
      走的也是这条,于是历史里的图也能直接显示出来。
    */
    const images = output.images ?? []
    return {
      slots: images.map((image, index) => ({ kind: 'image', index, image })),
      requested,
      done: images.length,
      partial: images.length > 0 && images.length < requested,
      mode
    }
  }
  const slots: ImageGenSlot[] = []
  let done = 0
  for (let index = 0; index < requested; index += 1) {
    const image = partialImages?.[index]
    if (image === undefined) {
      slots.push({ kind: 'loading', index })
    } else {
      done += 1
      slots.push({ kind: 'image', index, image })
    }
  }
  return { slots, requested, done, partial: false, mode }
}
