/**
 * 对话内生图「一次要几张」的唯一判据。
 *
 * 需求:`generate_image` 支持一次出多张(`n`),而这个数字有三个消费方 ——
 * 工具 schema 的上限(`main/kernel/tool/builtin/image.ts`)、桥的并发扇出
 * (`main/kernel/image-gen.ts`)、以及渲染层生成期要画几格加载占位
 * (`renderer/views/chat/ImageGenDetail.tsx`)。三处各写一个 `4` 的症状是
 * 上限改了一处:模型要了 6 张,卡片只画 4 格占位,多出来的两张到了没地方放。
 *
 * ★ 纯函数,入参是 `unknown`:渲染层喂进来的是**流式中途的半截入参**,
 * `n` 可能还没到、可能是个还没写完的数字 —— 一律退回 1,不猜。
 */

/**
 * 单次调用最多几张。
 *
 * ★ 4 是成本与等待的折中:桥是**逐张并发**请求(见 `image-gen.ts` 的 `run`),
 * 张数就是并发请求数,也就是按张计费的张数;再多就容易撞上游的并发限流(429),
 * 而用户要「几个备选」时 4 张已经够挑。
 */
export const MAX_IMAGE_COUNT = 4

/** 入参里的 `n` → 实际张数,钳在 1..MAX_IMAGE_COUNT;缺省/非法一律 1。 */
export function imageCountOf(input: unknown): number {
  if (typeof input !== 'object' || input === null) return 1
  return clampImageCount((input as Record<string, unknown>)['n'])
}

/** 任意值 → 合法张数。给桥用(它拿到的是已校验的 number,但仍不信任调用方)。 */
export function clampImageCount(value: unknown): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) return 1
  return Math.min(MAX_IMAGE_COUNT, Math.max(1, Math.floor(value)))
}
