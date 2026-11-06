/**
 * 上游供应商 / 模型别名。
 *
 * 组件不直接碰频道字符串(协议 §9),所以输入框那颗模型选择器调的是这里,
 * 而不是 `invoke('provider:listModels', …)`。
 *
 * ★ **写完不用自己 `set`** —— 主进程写完会广播 `provider:changed`,
 * `stores/models.ts` 订着它。照 `stores/mcp.ts` 的规矩:让广播成为唯一的更新入口,
 * 否则本地 set 和广播两条路会在多窗口下算出不同的结果。
 *
 * ★ `provider:test` 没有包装函数,因为主进程那一侧还是 todo(步骤 13:
 * 非 Anthropic 协议的编解码没写)。留一个调了必抛的包装,比不留更容易被误用。
 */
import type {
  CredentialInfo,
  FetchedModel,
  ModelAlias,
  RevealedCredential,
  UpstreamProvider
} from '../../../shared/domain/provider'
import type { ImportSourceKind } from '../../../shared/domain/import'
import type { ProviderAccount } from '../../../shared/domain/provider-account'
import type { ImportableProviders } from '../../../shared/domain/provider-import'
import { invoke } from './ipc'

export function listProviders(): Promise<UpstreamProvider[]> {
  return invoke('provider:list', undefined)
}

/**
 * 解析本机 Claude Code / Codex / OpenCode 里配置的提供商供审核。
 * ★ 只回元数据(名字/协议/base URL/模型名/有无本地凭证),密钥值不带回。
 */
export function listImportableProviders(sourceKind: ImportSourceKind, pickedDir?: string): Promise<ImportableProviders> {
  return invoke('providers:listImportable', pickedDir === undefined ? { sourceKind } : { sourceKind, pickedDir })
}

/** 省略 providerId = 全部别名。 */
export function listModels(providerId?: string): Promise<ModelAlias[]> {
  return invoke('provider:listModels', providerId === undefined ? {} : { providerId })
}

/**
 * 去上游问「这家有哪些模型」。**真发一次网络请求**,所以会失败,调用点必须接住。
 *
 * ★ 和上面的 `listModels` 是**两件相反的事**:那个列的是本地配好的别名,
 * 这个列的是上游报上来的全集。名字之所以差这么远,就是因为参考文档里
 * 它们撞了同一个名字(见契约里那条的注释)。
 */
export function fetchProviderModels(providerId: string): Promise<FetchedModel[]> {
  return invoke('provider:fetchModels', { providerId })
}

/**
 * 整表替换这家的别名 —— 导入弹窗点「更新列表」走这条。
 *
 * ★ 传进去的是**上游真实模型名**,而且是**全集**不是增量:没在数组里的会被删掉。
 */
export function setProviderAliases(providerId: string, models: string[]): Promise<ModelAlias[]> {
  return invoke('provider:setAliases', { providerId, models })
}

/**
 * 新建或更新。`id` 已存在就是更新。
 *
 * ★ `credentialRef` 这个字段**填什么都不算数** —— 主进程一律用库里那条已有的、
 * 或自己派生一个(`main/ipc/provider.ts` 的 upsertProvider 写了理由)。
 * 类型上它是必填的,所以调用点还是得给一个值;给现有的那个即可。
 */
export function upsertProvider(p: UpstreamProvider): Promise<UpstreamProvider> {
  return invoke('provider:upsert', p)
}

/** 连同它的别名和密钥一起删。 */
export function removeProvider(id: string): Promise<void> {
  return invoke('provider:remove', { id })
}

/** 存一把密钥。普通刷新只拿 `CredentialInfo`,不会顺带把明文带回。 */
export function setCredential(providerId: string, apiKey: string): Promise<CredentialInfo> {
  return invoke('provider:setCredential', { providerId, apiKey })
}

export function getCredentialInfo(providerId: string): Promise<CredentialInfo> {
  return invoke('provider:getCredentialInfo', { providerId })
}

/** 只在用户显式点击「查看」时调用;调用方不应缓存到全局 store。 */
export function revealCredential(providerId: string): Promise<RevealedCredential> {
  return invoke('provider:revealCredential', { providerId })
}

export function exportProviders(options: { includeCredentials?: boolean; password?: string } = {}): Promise<{ path: string; encrypted: boolean; providerCount: number; aliasCount: number } | null> {
  return invoke('provider:export', options)
}

export function importProviders(password?: string): Promise<{ path: string; providerCount: number; aliasCount: number; credentialCount: number } | null> {
  return invoke('provider:import', password === undefined ? {} : { password })
}

/**
 * 走一遍账号登录。**这条 promise 可能要等好几分钟**(用户要在浏览器里授权),
 * 中间进度靠订阅 `provider:authProgress` 拿 —— 只 await 这一条的话,
 * 按钮上会有一大段时间没有任何反馈。
 */
export function startOAuth(providerId: string): Promise<CredentialInfo> {
  return invoke('provider:startOAuth', { providerId })
}

export function cancelOAuth(providerId: string): Promise<void> {
  return invoke('provider:cancelOAuth', { providerId })
}

export function submitOAuthCode(providerId: string, code: string): Promise<CredentialInfo> {
  return invoke('provider:submitOAuthCode', { providerId, code })
}

export function signOut(providerId: string): Promise<CredentialInfo> {
  return invoke('provider:signOut', { providerId })
}

/* ── 供应商账号(OAuth 多账号) ──────────────────────────────────────────
   ★ 这一组**每一条都回整份账号列表**,包括写操作:调用点直接 `setAccounts(次)`
   就行,不用自己合并。主进程同时广播 `provider:accountsChanged`,
   两条路给出的是同一份数据(见 contract 里那段注释)。 */

export function listProviderAccounts(providerId: string): Promise<ProviderAccount[]> {
  return invoke('provider:listAccounts', { providerId })
}

/**
 * 添加一个账号 = 走一遍完整登录。
 * ★ 和 `startOAuth` 一样是**长 invoke**(用户要去浏览器里授权,可能好几分钟),
 * 中间进度同样订阅 `provider:authProgress`。
 */
export function addProviderAccount(providerId: string): Promise<ProviderAccount[]> {
  return invoke('provider:addAccount', { providerId })
}

/** 这条登录失效了,重新登录它(沿用同一行,备注名和顺序都留着) */
export function reauthProviderAccount(providerId: string, accountId: string): Promise<ProviderAccount[]> {
  return invoke('provider:reauthAccount', { providerId, accountId })
}

export function removeProviderAccount(providerId: string, accountId: string): Promise<ProviderAccount[]> {
  return invoke('provider:removeAccount', { providerId, accountId })
}

export function setProviderAccountEnabled(
  providerId: string,
  accountId: string,
  enabled: boolean
): Promise<ProviderAccount[]> {
  return invoke('provider:setAccountEnabled', { providerId, accountId, enabled })
}

/** 空串 = 清掉备注名,显示回落到邮箱 / 上游 id */
export function setProviderAccountLabel(
  providerId: string,
  accountId: string,
  label: string
): Promise<ProviderAccount[]> {
  return invoke('provider:setAccountLabel', { providerId, accountId, label })
}

export function setCurrentProviderAccount(providerId: string, accountId: string): Promise<ProviderAccount[]> {
  return invoke('provider:setCurrentAccount', { providerId, accountId })
}

/** 拖拽排序:`accountIds` 的下标即新的轮换顺序 */
export function reorderProviderAccounts(providerId: string, accountIds: string[]): Promise<ProviderAccount[]> {
  return invoke('provider:reorderAccounts', { providerId, accountIds })
}

export function clearProviderAccountLimit(providerId: string, accountId: string): Promise<ProviderAccount[]> {
  return invoke('provider:clearAccountLimit', { providerId, accountId })
}

/**
 * 主动拉一次该账号的**订阅额度**(GLM Coding Plan)。**真发一次网络请求**,
 * 没有密钥 / 不是订阅制供应商 / 上游拒绝都会抛,调用点必须接住。
 *
 * ★ 和上面那组一样回整份账号列表 —— 快照在账号行的 `quota` 上,直接 `setAccounts(次)`。
 */
export function fetchProviderAccountQuota(providerId: string, accountId: string): Promise<ProviderAccount[]> {
  return invoke('provider:fetchQuota', { providerId, accountId })
}

export function updateModel(model: ModelAlias): Promise<ModelAlias> {
  return invoke('model:update', model)
}

export function renameModel(
  providerId: string,
  alias: string,
  nextAlias: string,
): Promise<ModelAlias> {
  return invoke('model:rename', { providerId, alias, nextAlias })
}

export function removeModel(providerId: string, alias: string): Promise<void> {
  return invoke('model:remove', { providerId, alias })
}
