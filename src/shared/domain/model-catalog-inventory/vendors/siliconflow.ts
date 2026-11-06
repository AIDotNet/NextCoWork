import type { BuiltinModelRecord } from '../types'

/**
 * 硅基流动的视频型号。
 *
 * ★★ **目录是空的,这是刻意的。** 本次核对没能定位到硅基流动当前的官方视频
 * 接口文档(旧路径、新版路径、`llms.txt` 入口全部 404),而"这家卖哪些视频模型"
 * 只能从那份文档来。凭记忆或第三方页面往目录里填几个名字,得到的是**可绑定但
 * 调不通**的条目 —— 用户会先去充钱、再花时间排查,而问题在我们这边。
 *
 * 所以这里留空并保留导出:定位到文档之后,这里是唯一需要填的地方,填完
 * `video-profiles.ts` 里 `siliconflow-video` 那条 profile 的 actions 即可。
 */
export const SILICONFLOW: readonly BuiltinModelRecord[] = []
