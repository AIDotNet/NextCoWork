import type { AppSettings } from './settings'
import type { ModelAlias, UpstreamProvider } from './provider'
import type { SyncCategory } from './config-sync'

/** Exhaustive compile-time inventory: new domain fields require an explicit sync decision. */
export const PROVIDER_SYNC_FIELDS: Record<keyof UpstreamProvider, 'copy' | 'local'> = {
  id: 'copy', name: 'copy', protocol: 'copy', baseUrl: 'copy', credentialRef: 'local',
  priority: 'copy', enabled: 'copy', protocolOptions: 'copy',
  /*
    ★ 视频连接配置跟着供应商一起同步:它描述的是"这家怎么出网"(adapter +
    video base URL + 地域 / S3 桶),和聊天那份 baseUrl 同类 —— 两台机器上
    应当指向同一家。**凭据仍在 `credentialRef` 指的本地密文槽里**,不跟着走。
    ★ 它必须显式登记而不是靠"默认 copy":`Record<keyof …>` 是编译期清单,
      漏一个字段在这里就编译不过 —— 那条约束正是为了让这个决定被当面做出。
  */
  videoGeneration: 'copy'
}
// Effective protocol is recomputed on reads; only persisted model fields are portable.
export const MODEL_SYNC_FIELDS: Record<Exclude<keyof ModelAlias, 'runtimeProtocol'>, 'copy'> = {
  alias: 'copy', providerId: 'copy', upstreamModel: 'copy', protocolOverride: 'copy', priority: 'copy',
  capabilities: 'copy', contextWindow: 'copy', maxOutputTokens: 'copy', displayName: 'copy', modality: 'copy',
  enabled: 'copy', thinkingConfig: 'copy', reasoningEfforts: 'copy', requestAdapter: 'copy', source: 'copy',
  // 视频档案引用也是"这条绑定怎么发",和 protocolOverride 同类,跟着模型走。
  video: 'copy',
  catalogOverrides: 'copy'
}
export const SETTINGS_SYNC_FIELDS: Record<keyof AppSettings, SyncCategory | 'device' | 'split'> = {
  theme: 'preferences', activeThemeProfileId: 'preferences', locale: 'preferences', colorTheme: 'preferences',
  imageTheme: 'preferences', defaultPermissionMode: 'automation', permissionReviewerModel: 'providers',
  permissionReviewerModelProviderId: 'providers', goalEvaluatorModel: 'providers',
  goalEvaluatorModelProviderId: 'providers', modelProposedGoals: 'providers',
  // 压缩模型指向的是某一家供应商的别名 —— 和 goalEvaluatorModel 同类,跟着供应商配置走。
  // ★ 不能因为「它管的是上下文压缩」就归到 contextManagement 那一档('preferences'):
  //   那会把一个别名搬到另一台机器上,而那台机器未必有这家供应商。档位跟着模型归同一档,
  //   拆开会出现「模型同步过去了、档位没有」的半套配置。
  compactModel: 'providers', compactModelProviderId: 'providers', compactThinking: 'providers',
  // 输入框补全的两个模型同样是「某家供应商的别名」;各自的开关跟着模型归同一档
  //(同上面压缩模型那段的理由)。本地补全不碰任何模型,是纯粹的用户偏好。
  promptSuggestions: 'providers', completionModel: 'providers', completionModelProviderId: 'providers',
  inlineCompletion: 'providers', inlineCompletionModel: 'providers', inlineCompletionModelProviderId: 'providers',
  localCompletion: 'preferences',
  defaultModel: 'providers', defaultModelProviderId: 'providers',
  // 生图模型同样是「某家供应商的别名」——和 defaultModel 同档同理由。
  imageModel: 'providers', imageModelProviderId: 'providers',
  // 生图开关跟着生图模型走同一档:拆开会出现「模型同步过去了、开关没有」的半套配置
  //(同上面压缩模型那段的理由)。
  imageGenerationEnabled: 'providers',
  // 视频那三项与生图同档同理由:模型是"某家供应商的别名",开关跟着它走,
  // 拆开会出现「模型同步过去了、开关没有」的半套配置。
  videoModel: 'providers', videoModelProviderId: 'providers', videoGenerationEnabled: 'providers',
  contextManagement: 'preferences',
  // 「我要不要用实验功能」是用户的偏好,不是这台机器的事实 —— 同 contextManagement
  experimental: 'preferences',
  subagent: 'split', gateway: 'device', notifications: 'preferences',
  proxy: 'device', data: 'split', personalization: 'preferences', shortcuts: 'preferences', themeStudio: 'preferences',
  shell: 'device',
  // 自建 SearxNG 实例多半是 `http://localhost:8080` —— 同步到另一台机器上就是个死地址,
  // 和 `shell` 同类:它描述的是**这台机器**上跑着什么。
  builtinSearch: 'device',
  // 默认打开方式是这台机器上**装了哪个 IDE** —— 另一台机器上可能根本没有它,同 `shell`。
  defaultOpenTarget: 'device',
  upstreamIdleTimeoutSeconds: 'device',
  // 输出额度描述的是「我要多长的回答」,不是这台机器的事实 —— 和 contextManagement 同类。
  maxOutputTokens: 'preferences',
  // 账号轮换是「我这几个号怎么用」,跟着供应商配置走 —— 和 defaultModel 同类,
  // 不是设备事实(换台机器之后同一批账号仍然该按同样的规矩轮换)。
  providerAccountRotation: 'providers'
}
export const SYNC_REGISTRY: Record<SyncCategory, { order: number; confirmation: boolean; deviceFields: readonly string[] }> = {
  // 普通 provider 的 API Key/OAuth token 在 providers 密文文档中同步；只有引用名和
  // NextCoWork 自身登录 token 留在设备上。
  providers: { order: 0, confirmation: false, deviceFields: ['credentialRef', 'platformTokens'] },
  preferences: { order: 2, confirmation: false, deviceFields: ['backupDirectory', 'gateway', 'proxy', 'shell', 'builtinSearch', 'defaultOpenTarget'] },
  connections: { order: 3, confirmation: true, deviceFields: ['cwd', 'identityFile', 'knownHostsFile', 'cookies'] },
  extensions: { order: 1, confirmation: true, deviceFields: ['absolutePath', 'executionApproval'] },
  workspaces: { order: 4, confirmation: true, deviceFields: ['rootPath', 'environment', 'lastOpenedAt'] },
  automation: { order: 5, confirmation: true, deviceFields: ['nextRunAt', 'lastRunAt', 'approvalHistory'] },
  // 使用统计:每台设备只写自己那一片,合并无损,所以不需要首次确认;
  // 请求日志(usage_records)整张表留在本机,不进快照。
  usage: { order: 6, confirmation: false, deviceFields: ['requestLogs'] }
}

export function portableProvider(provider: UpstreamProvider): Omit<UpstreamProvider, 'credentialRef'> {
  const { credentialRef: _local, ...portable } = provider
  return structuredClone(portable)
}
