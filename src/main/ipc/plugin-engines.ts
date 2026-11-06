/**
 * 原生文档引擎 helper 的**启动门** —— 「插件包里那支原生可执行文件能不能被跑起来」。
 *
 * ## 为了什么需求建的
 *
 * `documents.open` 只需要 `workspace.read`(见 `shared/plugin/protocol.ts` 的能力表)。
 * 而真正被 spawn 的是**引擎插件**携带的原生组件(`ncw.office-runtime` 那一份
 * LibreOffice helper):打开一份文档,操作系统的进程就起来了,而跑的是**引擎插件
 * 自己的**清单 —— 在它之前,那份清单里的授权一个都没被问过。读一个文件与执行一段
 * 原生代码是两个量级的事,不能靠前者的能力放行。
 *
 * 计划 §7 里那条最小能力(`native.execute`)还没落地,所以这里**不发明**一个用户从没
 * 批过的原生权限,判据复用既有的 `process` + `allowedCommands`:引擎插件必须声明
 * `process` 并拿到授权,而且入口的**可执行名**必须落在它自己的命令白名单里。比对直接
 * 用 `narrowCommand` —— 它已经回答了「这个可执行名有没有被批准」,再写一份的话,
 * 归一规则(基名、`.exe` 后缀、shell 元字符)迟早和后者的分叉。
 *
 * ## 两处调用点,缺一不可
 *
 * 1. **provider 建立之前**(`ipc/plugins.ts` 的 `ensureProvider`):拒掉之后
 *    `documents.open` 走 `engine_unavailable`,而不是把进程起起来再失败。
 * 2. **helper 真正 spawn 之前**(`resolveEntry` 外面的包裹):provider 是**缓存**的,
 *    插件可能在它建立之后被禁用、被撤权或被升级替换。只查一次的症状是「撤掉授权之后
 *    下一次打开文档仍然跑起了那支 helper」,而界面上没有任何东西提示用户。
 *    这不是防御性代码:`revokePermissions` 只对**必选**能力停用插件
 *    (`plugin/manager.ts` 的 `revoke`),撤掉落在 `optionalPermissions` 里的
 *    `process` 之后插件照常是 enabled,provider 缓存也就照常在。
 *
 * ## 故意不做的
 *
 * - 不判「跑起来之后能做什么」:那是 RPC 的能力门(`plugin/rpc.ts`)。
 * - 不判入口摘要与路径边界:那是 `plugin/native-installer.ts` 的
 *   `resolveVerifiedEntry`(每次 spawn 前另查一次),两件事不要混。
 * - 不认识 `native.execute`:能力枚举里没有它,凭空接受一个没登记过的权限名,等于在
 *   授权界面上显示一件从未展示过的东西。
 */
import { basename } from 'node:path'
import type { PluginManifest } from '../../shared/plugin/manifest'
import { hasPermission, type PluginPermissionState } from '../../shared/plugin/permission'
import type { PluginStatus } from '../../shared/plugin/state'
import { selectNativeTarget } from '../../shared/plugin/native-component'
import { narrowCommand } from '../plugin/capabilities'

/**
 * 判据需要的全部输入 —— 就是 catalog 里那一份投影。
 * 用结构类型而不是 `InstalledPlugin`,是因为这一层不该知道渲染层要什么字段;
 * `InstalledPlugin` 结构上满足它,调用点直接传即可,不必转换。
 */
export interface EngineSpawnSubject {
  manifest: PluginManifest
  /** 装载失败 / 待批准都在这里,和 `enabled` 一起回答「这个插件此刻是不是被停用了」 */
  status: PluginStatus
  enabled: boolean
  permissions: PluginPermissionState
}

/**
 * 这个引擎贡献在**本机**的入口可执行名。
 *
 * 解析不出来时返回 `null`,而且**不是**拒绝:引擎 id 存不存在、本机有没有这个平台的
 * 构建,真源是登记表自己的 `ensure()`(`plugin-engines` 这一层只回答「能不能跑」)。
 * 两处各判一次的话,某天登记表放宽了平台判定,这里会继续把插件拒在门外。
 */
export function engineHelperCommand(
  manifest: PluginManifest,
  engineId: string,
  platform: string,
  arch: string
): string | null {
  const contribution = manifest.contributes.documentEngines?.find((engine) => engine.id === engineId)
  if (contribution === undefined) return null
  const component = manifest.nativeComponents?.find((candidate) => candidate.id === contribution.component)
  if (component === undefined) return null
  const target = selectNativeTarget(component, platform, arch)
  if (target === null) return null
  return basename(target.entry)
}

/**
 * 这个插件此刻能不能把原生代码跑起来。返回 `null` = 可以,否则是拒的理由。
 *
 * ★ 理由串是给**日志与诊断**的(作者要能看出「打开文档说引擎不可用」到底卡在哪一条),
 *   不是用户可见文案:用户那一句由调用点的既有错误路径给出(`engine_unavailable` 等)。
 */
export function helperSpawnRefusal(plugin: EngineSpawnSubject, entry: string): string | null {
  const id = plugin.manifest.id
  if (!plugin.enabled) return `plugin ${id} is disabled`
  if (plugin.status === 'error') return `plugin ${id} did not load`
  if (plugin.status === 'pending-approval') return `plugin ${id} is waiting for the user to approve its new permissions`
  const declared = [...plugin.manifest.permissions, ...plugin.manifest.optionalPermissions].includes('process')
  if (!declared || !hasPermission(plugin.permissions, 'process')) {
    return `plugin ${id} has no granted "process" permission; a native helper may not be launched`
  }
  /*
    `narrowCommand` 的拒绝理由(deploy 白名单为空、名字不在表里、带 shell 元字符)原样
    往上带:作者对着它就能改清单,再包一层措辞只会把「哪个名字不被接受」藏起来。
  */
  const narrowed = narrowCommand(plugin.manifest.allowedCommands, entry, [])
  return narrowed.ok ? null : narrowed.reason
}

/** provider 建立之前的判据:按**清单声明**的入口名收窄(实际入口还没解析出来)。 */
export function engineContributionRefusal(
  plugin: EngineSpawnSubject,
  engineId: string,
  platform: string,
  arch: string
): string | null {
  const entry = engineHelperCommand(plugin.manifest, engineId, platform, arch)
  if (entry === null) return null
  return helperSpawnRefusal(plugin, entry)
}
