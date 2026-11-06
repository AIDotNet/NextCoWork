/**
 * 插件 **API 版本** —— 与应用版本(`app.getVersion()`)是两件东西。
 *
 * ## 为什么必须分开(这里在修一个把所有插件判成红色的 bug)
 *
 * `engines.nextcowork` 原本是拿 **应用版本** 去比的。而应用现在是 `2.x`,
 * 官方脚手架与文档里写的却是 `^0.2.0` —— `satisfiesEngine('^0.2.0', '2.2.2')`
 * 走 0.x 那一支,要求 `host.major === 0`,于是**恒为 false**:
 * 按官方模板生成的插件装上就是 `status: 'error'`,诊断写着
 * 「requires ^0.2.0, this host is 2.2.2」,而作者什么都没做错。
 *
 * 两个版本号回答的是不同的问题:
 *
 * - 应用版本:用户手上这个 NextCoWork 是哪一版(市场按它做灰度,见
 *   `ipc/plugin-market.ts` 的 `client` 参数 —— 所以 `PluginCatalog.hostVersion`
 *   保持应用版本不动);
 * - **API 版本**:`nextcowork` 这个模块的形状是哪一版。插件声明的是后者。
 *
 * ## 这份版本号什么时候动
 *
 * 1.0 之前按 `^0.x` 的规矩:**minor 递增 = 允许 break**。新增方法/贡献点而
 * 不删不改时递增 patch;删方法、改语义、改必填字段时递增 minor。
 */
import { satisfiesEngine } from './manifest'

/**
 * 当前插件 API 版本。★ 改它之前先读 `LEGACY_API_RANGES` 的说明。
 *
 * 与 `packages/plugin-api/nextcowork.d.ts` 的 `version` 常量、
 * `packages/create-nextcowork-plugin` 的 `DEFAULT_ENGINES` 是同一个值的三处抄写;
 * 三处不同步的症状是「脚手架生成的包装不上」,和上面那个 bug 一模一样。
 *
 * 0.3.0 → 0.3.1(patch,纯新增):文档通道加了**图片支线**(`ncw:doc:open`
 * 对 `kind: 'image'` 下发 dataUrl+mime,`ncw:doc:save` 接受 `encoding: 'base64'`),
 * 且宿主开始派发 `onCustomEditor:` 激活事件 —— 依赖这两条任意一条的插件
 * (图片/Markdown 编辑器类)可以把下限写成 `>=0.3.1`,在 0.3.0 宿主上会以
 * 明确诊断拒绝装载,而不是开出一个 403 的 iframe。
 */
// 需求：0.3.2 新增 documents 会话 API；依赖它的插件不能在缺少桥接的旧宿主上激活。
// 需求：0.3.3(patch,纯新增)`nextcowork/view` 加了文档引擎画布(`openEngineDocument`、
// `DocumentCanvas` 与画布换算函数);用它的编辑器把下限写成 >=0.3.3,旧宿主上明确拒绝装载,
// 而不是打开一个永远等不到 `ncw:engine:opened` 的空白画布。
export const PLUGIN_API_VERSION = '0.3.3'

/**
 * 认得、但已经弃用的 range —— **照常装载,只推一条 warn**。
 *
 * ★ 这是一条**有拆除条件的临时兼容**:0.2 声明的那批插件在 0.3 之前的宿主上
 * 从来就没跑起来过(见文件头那个 bug),所以「兼容它们」实际上等于「让它们
 * 第一次能跑」,不存在破坏既有行为的风险。
 *
 * **拆除条件**:当市场上声明 `0.2.x` 的包降到 0(或下一次 minor 提升时),
 * 把这张表清空 —— 那时 `engineCompatibility` 会自然把它们判成 incompatible,
 * 而作者已经有足够长的窗口改掉清单。
 *
 * ★ 表里**没有** `>=0.2.0`:那条 range 本来就被 0.3.0 满足,走的是 `'ok'`,
 * 列在这里只会让人以为它被特殊对待过。
 */
export const LEGACY_API_RANGES: readonly string[] = ['^0.2.0', '~0.2.0', '0.2.0']

export type EngineCompatibility = 'ok' | 'deprecated' | 'incompatible'

/**
 * 一份清单的 `engines.nextcowork` 与当前 API 版本的关系。
 *
 * ★ 读不懂的 range 返回 `'incompatible'`(不是 ok),同 `satisfiesEngine`
 * 的规矩:宁可装不上,也不要在一个语义不明的约束下跑第三方代码。
 */
export function engineCompatibility(
  range: string,
  apiVersion: string = PLUGIN_API_VERSION
): EngineCompatibility {
  if (satisfiesEngine(range, apiVersion)) return 'ok'
  if (LEGACY_API_RANGES.includes(range.trim())) return 'deprecated'
  return 'incompatible'
}
