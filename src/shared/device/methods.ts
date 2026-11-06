/**
 * 设备 RPC 的方法契约(两端共用)。
 *
 * ★ 这些 schema 是**网络白名单**:只有这里列出的字段能进来,内部字段
 *   (`parentRunId`、`planExecution`、`inputInternal`、`inputGoalId`、`depth` 等)
 *   永远不能由控制端指定 —— 否则控制端能伪造子代理或内部协调输入。
 * ★ 前置条件(如 `expectedRevision`)放在 params 里,由幂等摘要一并覆盖。
 */
import { z } from 'zod'
import { idSchema } from './method-ids'
import type { DeviceValue } from './value'

export const DEVICE_METHODS = {
  commandsGet: 'commands.get',
  sessionsList: 'sessions.list',
  sessionsPage: 'sessions.page',
  sessionsSummary: 'sessions.summary',
  sessionsLastAssistant: 'sessions.lastAssistant',
  sessionsCreate: 'sessions.create',
  sessionsRename: 'sessions.rename',
  sessionsSetArchived: 'sessions.setArchived',
  sessionsSetFavorited: 'sessions.setFavorited',
  sessionsSetMode: 'sessions.setMode',
  sessionsSetModel: 'sessions.setModel',
  agentStart: 'agent.start',
  agentAbort: 'agent.abort',
  agentAttach: 'agent.attach',
  agentDetach: 'agent.detach',
  agentWatch: 'agent.watch',
  settingsGet: 'settings.get',
  settingsUpdate: 'settings.update',
  workspacesList: 'workspaces.list',
  workspacesCreate: 'workspaces.create',
  workspacesUpdate: 'workspaces.update',
  workspacesClose: 'workspaces.close',
  filesList: 'files.list',
  filesRead: 'files.read',
  filesWrite: 'files.write',
  filesMutate: 'files.mutate',
  controllerStateRead: 'controllerState.read',
  controllerStateSave: 'controllerState.save',
  notificationsGet: 'notifications.get',
  notificationsSave: 'notifications.save',
  notificationsRemove: 'notifications.remove',
  modelsList: 'models.list',
  modelsFetch: 'models.fetch',
  modelsSetAliases: 'models.setAliases',
  modelsRemove: 'models.remove',
  leasesAcquire: 'leases.acquire',
  leasesRenew: 'leases.renew',
  leasesRelease: 'leases.release',
  leasesHandoff: 'leases.handoff',
  leasesHolder: 'leases.holder',
  filesReadRange: 'files.readRange',
  gitOverview: 'git.overview',
  gitBranches: 'git.branches',
  gitCommits: 'git.commits',
  gitDiff: 'git.diff',
  gitStage: 'git.stage',
  gitUnstage: 'git.unstage',
  gitCommit: 'git.commit',
  gitCheckout: 'git.checkout',
  gitCreateBranch: 'git.createBranch',
  terminalsCreate: 'terminals.create',
  terminalsList: 'terminals.list',
  terminalsAttach: 'terminals.attach',
  terminalsDetach: 'terminals.detach',
  terminalsWrite: 'terminals.write',
  terminalsResize: 'terminals.resize',
  terminalsKill: 'terminals.kill',
  filesUploadBegin: 'files.uploadBegin',
  filesUploadChunk: 'files.uploadChunk',
  filesUploadCommit: 'files.uploadCommit',
  filesUploadAbort: 'files.uploadAbort',
  directoriesList: 'directories.list',
  providersGet: 'providers.get',
  providersUpsert: 'providers.upsert',
  providersRemove: 'providers.remove',
  providersSetCredential: 'providers.setCredential',
  providersCredentialInfo: 'providers.credentialInfo',
  mcpList: 'mcp.list',
  mcpUpsert: 'mcp.upsert',
  mcpRemove: 'mcp.remove',
  mcpTest: 'mcp.test',
  mcpSetSecrets: 'mcp.setSecrets',
  mcpSecretsInfo: 'mcp.secretsInfo',
  skillsList: 'skills.list',
  skillsSetEnabled: 'skills.setEnabled',
  commandsList: 'commands.list',
  commandsSetEnabled: 'commands.setEnabled',
  agentsList: 'agents.list',
  agentsSetEnabled: 'agents.setEnabled',
  modesList: 'modes.list',
  hooksList: 'hooks.list',
  hooksSetEnabled: 'hooks.setEnabled',
  interactionsList: 'interactions.list',
  interactionsRespond: 'interactions.respond',
  sessionQueue: 'session.queue'
} as const

export const commandsGetParams = z.strictObject({ key: z.string().min(1).max(128) })

export const sessionsListParams = z.strictObject({ workspaceId: idSchema, archived: z.boolean().optional() })
export const sessionsPageParams = z.strictObject({
  sessionId: idSchema,
  limit: z.number().int().min(1).max(50),
  beforeMessageId: idSchema.optional()
})

export const sessionsCreateParams = z.strictObject({
  workspaceId: idSchema,
  /** 控制端可预先生成 id,让重试天然幂等;缺省由服务端生成。 */
  sessionId: idSchema.optional(),
  title: z.string().min(1).max(256).optional(),
  mode: z.string().min(1).max(128).optional()
})
export const sessionsCreateResult = z.strictObject({ sessionId: idSchema })

/**
 * 会话元数据的写入。都是可逆的小改动(改名/归档/收藏/模式/模型),**不含删除**:
 * 删除会级联清掉整棵子代理转录与附件,没有回收站,不经网络开放。
 */
export const sessionsRenameParams = z.strictObject({ sessionId: idSchema, title: z.string().min(1).max(256) })
export const sessionsSetArchivedParams = z.strictObject({ sessionId: idSchema, archived: z.boolean() })
export const sessionsSetFavoritedParams = z.strictObject({ sessionId: idSchema, favorited: z.boolean() })
export const sessionsSetModeParams = z.strictObject({ sessionId: idSchema, mode: z.string().min(1).max(128) })
export const sessionsSetModelParams = z.strictObject({ sessionId: idSchema, model: z.string().min(1).max(256), modelProviderId: idSchema.optional() })
export const sessionsUpdatedResult = z.strictObject({ updated: z.literal(true) })

/** 元数据读取:只回会话记录与条数,或最后一条助手消息 —— 都不读整段转录。 */
export const sessionsIdParams = z.strictObject({ sessionId: idSchema })

export const permissionModeSchema = z.enum(['ask', 'auto', 'full'])
export const thinkingLevelSchema = z.enum(['auto', 'off', 'minimal', 'low', 'medium', 'high', 'higher', 'max'])

/** 控制端可决定的档位。`depth`、父子关系等永远由服务端固定。 */
export const sendOptionsSchema = z.strictObject({
  workspaceId: idSchema,
  mode: z.string().min(1).max(128),
  thinking: thinkingLevelSchema,
  webSearch: z.boolean(),
  maxContext: z.boolean().optional(),
  permissionMode: permissionModeSchema,
  model: z.string().min(1).max(256),
  modelProviderId: idSchema.optional(),
  skillIds: z.array(z.string().min(1).max(128)).max(64),
  skillSelectionMode: z.enum(['all', 'explicit']).optional()
})
export type DeviceSendOptions = z.infer<typeof sendOptionsSchema>

/** 一次用户输入的最大文本。与队列条目的上限(32000)一致。 */
export const INPUT_TEXT_MAX = 32_000

export const agentStartParams = z.strictObject({
  /** 控制端 mint 的 id(订阅在前、启动在后);服务端校验唯一,不接受已存在的。 */
  runId: idSchema,
  sessionId: idSchema,
  text: z.string().min(1).max(INPUT_TEXT_MAX),
  options: sendOptionsSchema
})
export const agentStartResult = z.discriminatedUnion('started', [
  z.strictObject({ started: z.literal(true), runId: idSchema }),
  z.strictObject({ started: z.literal(false), queued: z.literal(true) })
])

export const agentAbortParams = z.strictObject({ runId: idSchema, cascade: z.boolean().optional() })

export const sessionQueueParams = z.strictObject({
  sessionId: idSchema,
  op: z.discriminatedUnion('kind', [
    z.strictObject({ kind: z.literal('enqueue'), text: z.string().min(1).max(INPUT_TEXT_MAX), options: sendOptionsSchema }),
    z.strictObject({ kind: z.literal('promote'), id: idSchema }),
    z.strictObject({ kind: z.literal('edit'), id: idSchema, text: z.string().min(1).max(INPUT_TEXT_MAX) }),
    z.strictObject({ kind: z.literal('drop'), id: idSchema }),
    z.strictObject({ kind: z.literal('take'), id: idSchema }),
    z.strictObject({ kind: z.literal('resume') })
  ])
})

/** 订阅一个 run 的事件流并取回快照;之后的事件以 `run:<runId>` 流推送,cursor = seq。 */
export const agentAttachParams = z.strictObject({ runId: idSchema, sinceSeq: z.number().int().min(0).max(Number.MAX_SAFE_INTEGER) })
export const agentDetachParams = z.strictObject({ runId: idSchema })
/** 订阅应用级事实(谁在跑、队列欠着什么),事件流 id 固定为 `global`。 */
export const agentWatchParams = z.null()

export const interactionsListParams = z.strictObject({ runId: idSchema.optional() })

/**
 * 审批/提问的应答。id 是服务端 ULID,绑定到具体那一次请求的内容;
 * 「以后都允许」不带规则文本 —— 规则由服务端按待决项自己算,控制端不能指定写入什么。
 */
export const interactionResponseSchema = z.discriminatedUnion('kind', [
  z.strictObject({ id: idSchema, kind: z.literal('tool_permission'), decision: z.discriminatedUnion('kind', [
    z.strictObject({ kind: z.literal('allow_once') }),
    z.strictObject({ kind: z.literal('allow_always'), scope: z.literal('workspace') }),
    z.strictObject({ kind: z.literal('deny'), reason: z.string().max(2000).optional() }),
    z.strictObject({ kind: z.literal('allow_edited'), input: z.custom<unknown>((value) => value !== undefined) })
  ]) }),
  z.strictObject({ id: idSchema, kind: z.literal('ask_user'),
    answers: z.array(z.array(z.string().min(1).max(32_768)).min(1).max(64)).max(16).nullable() }),
  z.strictObject({ id: idSchema, kind: z.literal('goal_proposal'), approved: z.boolean() }),
  z.strictObject({ id: idSchema, kind: z.literal('widget_submit'), value: z.string().max(32 * 1024).nullable() }),
  z.strictObject({ id: idSchema, kind: z.literal('plan_approval'),
    action: z.enum(['approve_current', 'approve_new_session', 'request_revision', 'reject']),
    feedback: z.string().max(32_000).optional() }),
  z.strictObject({ id: idSchema, kind: z.literal('workflow_approval'),
    action: z.enum(['run', 'revise', 'cancel']),
    feedback: z.string().max(32_000).optional() })
])
export const interactionsRespondParams = z.strictObject({ response: interactionResponseSchema })
export const interactionsRespondResult = z.strictObject({ answered: z.literal(true) })

/**
 * 设置的远程写入是**白名单**,不是整份 patch:代理、shell、主题工作室等会改变执行环境或只属于
 * 控制端显示环境的字段不在其中。要放开哪个字段,在这里显式加一行。
 */
const modelRef = z.string().max(256)
export const settingsUpdateParams = z.strictObject({
  patch: z.strictObject({
    locale: z.enum(['zh-CN', 'en-US']).optional(),
    defaultPermissionMode: permissionModeSchema.optional(),
    defaultModel: modelRef.optional(),
    defaultModelProviderId: idSchema.optional(),
    permissionReviewerModel: modelRef.optional(),
    permissionReviewerModelProviderId: idSchema.optional(),
    goalEvaluatorModel: modelRef.optional(),
    goalEvaluatorModelProviderId: idSchema.optional(),
    compactModel: modelRef.optional(),
    compactModelProviderId: idSchema.optional()
  }).refine((patch) => Object.keys(patch).length > 0, 'Empty patch')
})
export const settingsUpdateResult = z.strictObject({ updated: z.literal(true) })

export const workspaceSettingsPatch = z.strictObject({
  permissionMode: permissionModeSchema.optional(),
  defaultModel: modelRef.optional(),
  defaultModelProviderId: idSchema.optional(),
  defaultMode: z.string().min(1).max(128).optional(),
  defaultThinking: thinkingLevelSchema.optional(),
  webSearch: z.boolean().optional(),
  maxContext: z.boolean().optional()
})
export const workspacesCreateParams = z.strictObject({
  /** 设备上的**绝对**路径;服务端 realpath 后必须是目录。 */
  path: z.string().min(1).max(4096),
  name: z.string().min(1).max(256).optional()
})
export const workspacesCreateResult = z.strictObject({ workspaceId: idSchema, created: z.boolean() })
export const workspacesUpdateParams = z.strictObject({
  id: idSchema, name: z.string().min(1).max(256).optional(), settings: workspaceSettingsPatch.optional()
}).refine((value) => value.name !== undefined || value.settings !== undefined, 'Empty update')
export const workspacesCloseParams = z.strictObject({ id: idSchema })
export const updatedResult = z.strictObject({ updated: z.literal(true) })

/**
 * 远程文件路径永远是**工作区相对**路径:绝对路径、盘符、`..` 段、NUL 一律在协议层拒绝。
 *
 * ★ 桌面的文件 IPC 有意允许工作区外的绝对路径(用户自己的机器、自己点开的链接);
 *   网络控制端不能继承这个宽松 —— 否则任何已配对控制端都能读 `~/.ssh` 之类的任意文件。
 *   Agent 经审批链路访问工作区外路径是另一条受权限模式约束的路径,不经过这里。
 */
export function isWorkspaceRelativePath(path: string): boolean {
  if (path.includes('\0') || path.startsWith('/') || path.startsWith('\\') || /^[A-Za-z]:/.test(path)) return false
  return !path.split(/[\\/]/).includes('..')
}
const relPath = z.string().max(4096).refine(isWorkspaceRelativePath, 'Path must be workspace-relative')
export const filesListParams = z.strictObject({ workspaceId: idSchema, path: relPath })
export const filesReadParams = z.strictObject({ workspaceId: idSchema, path: relPath.min(1) })
/**
 * 写入必须带读到的 `revision`(内容摘要):磁盘上的内容已经变了就得到 `conflict`,
 * 绝不静默覆盖别的控制端或本机编辑器刚存的内容。
 */
export const filesWriteParams = z.strictObject({
  workspaceId: idSchema, path: relPath.min(1), content: z.string().max(900_000),
  revision: z.string().min(1).max(256), encoding: z.enum(['utf8', 'base64']).optional()
})
export const filesMutateParams = z.strictObject({
  workspaceId: idSchema, path: relPath.min(1),
  operation: z.enum(['create-file', 'create-directory', 'rename', 'move', 'copy', 'delete']),
  destination: relPath.min(1).optional(), overwrite: z.literal(true).optional()
})

/**
 * 控制端自己的状态(视图/布局偏好)与推送订阅登记。身份只来自已认证连接,params 里没有
 * controllerId;写入都带 `expectedRevision`(0 = 首次),不符即 `conflict`。
 */
export const controllerStateReadParams = z.strictObject({ key: idSchema })
export const controllerStateSaveParams = z.strictObject({
  key: idSchema,
  expectedRevision: z.number().int().min(0).max(Number.MAX_SAFE_INTEGER),
  value: z.custom<DeviceValue>((value) => value !== undefined)
})
export const notificationsRemoveParams = z.strictObject({
  expectedRevision: z.number().int().min(0).max(Number.MAX_SAFE_INTEGER)
})
export { NotificationSubscriptionInputSchema as notificationsSaveParams } from './notifications'

/**
 * 输入租约(终端 / 浏览器 / 文档)。持有者身份来自已认证连接;每次授予带单调递增的 fencing token,
 * 输入类方法必须携带它,交接/过期之后旧 token 的迟到输入会被拒绝。
 */
export const LEASE_KIND_VALUES = ['terminal', 'browser', 'document'] as const
const leaseTarget = { kind: z.enum(LEASE_KIND_VALUES), resourceId: idSchema }
const leaseToken = z.number().int().min(1).max(Number.MAX_SAFE_INTEGER)
const leaseTtl = z.number().int().min(1000).max(5 * 60_000).optional()
export const leasesAcquireParams = z.strictObject({ ...leaseTarget, ttlMs: leaseTtl })
export const leasesRenewParams = z.strictObject({ ...leaseTarget, token: leaseToken, ttlMs: leaseTtl })
export const leasesReleaseParams = z.strictObject({ ...leaseTarget, token: leaseToken })
export const leasesHandoffParams = z.strictObject({ ...leaseTarget, token: leaseToken, to: idSchema, ttlMs: leaseTtl })
export const leasesHolderParams = z.strictObject(leaseTarget)
export const leaseResult = z.strictObject({ resource: z.string(), token: leaseToken, expiresAt: z.number().int().nonnegative() })

/**
 * 范围读取(下载/预览大文件、视频 seek 的基础):一次最多 512 KiB,返回 base64。
 * `revision` 是 stat 派生的版本(大小+mtime);带上上一块拿到的 `revision`,文件在两块之间变了
 * 就得到 `conflict`,而不是把两个版本拼成一份。
 */
export const RANGE_MAX_BYTES = 512 * 1024
export const filesReadRangeParams = z.strictObject({
  workspaceId: idSchema, path: relPath.min(1),
  offset: z.number().int().min(0).max(Number.MAX_SAFE_INTEGER),
  length: z.number().int().min(1).max(RANGE_MAX_BYTES),
  revision: z.string().min(1).max(128).optional()
})

/**
 * Git:读操作 + 本地写操作(暂存/提交/切换/新建分支)。**不含 pull/push**:它们要走网络与凭据,
 * 在设备上弹出认证(SSH 口令、凭据助手)无人可答,先不经网络开放。
 * 路径是工作区相对路径(同文件方法);分支名由服务端 `safeRef` 再校验一遍。
 */
const refName = z.string().min(1).max(255).refine((value) => !value.startsWith('-') && !/[\s\0~^:?*[\\]/.test(value), 'Invalid ref name')
export const gitWorkspaceParams = z.strictObject({ workspaceId: idSchema })
export const gitCommitsParams = z.strictObject({ workspaceId: idSchema, limit: z.number().int().min(1).max(500).optional() })
export const gitDiffParams = z.strictObject({ workspaceId: idSchema, path: relPath.min(1), staged: z.boolean() })
export const gitPathsParams = z.strictObject({ workspaceId: idSchema, paths: z.array(relPath.min(1)).min(1).max(200) })
export const gitCommitParams = z.strictObject({ workspaceId: idSchema, message: z.string().min(1).max(20_000) })
export const gitCheckoutParams = z.strictObject({ workspaceId: idSchema, branch: refName })
export const gitCreateBranchParams = z.strictObject({ workspaceId: idSchema, name: refName, checkout: z.boolean() })
export const gitDoneResult = z.strictObject({ done: z.literal(true) })

/**
 * 终端属于设备,控制端断开不会杀它。创建者自动获得该终端的输入租约(见 leases.*);
 * `write / resize / kill` 必须带当前 fencing token。输出以 `terminal:<id>` 流推送,cursor = seq。
 * `write / resize` 是 query 类(不落回执):击键重放会重复输入,所以不提供幂等 key,靠租约闸防串扰。
 */
const termSize = { cols: z.number().int().min(1).max(500), rows: z.number().int().min(1).max(200) }
export const TERMINAL_WRITE_MAX = 64 * 1024
export const terminalsCreateParams = z.strictObject({ workspaceId: idSchema, ...termSize })
export const terminalsListParams = z.strictObject({ workspaceId: idSchema })
export const terminalsAttachParams = z.strictObject({ id: idSchema })
export const terminalsDetachParams = z.strictObject({ id: idSchema })
export const terminalsWriteParams = z.strictObject({ id: idSchema, token: leaseToken, data: z.string().min(1).max(TERMINAL_WRITE_MAX) })
export const terminalsResizeParams = z.strictObject({ id: idSchema, token: leaseToken, ...termSize })
export const terminalsKillParams = z.strictObject({ id: idSchema, token: leaseToken })
export const okResult = z.strictObject({ ok: z.literal(true) })

/**
 * 分片上传(见 `main/services/workspace-upload.ts`):begin → chunk × N → commit。
 * 数据先落同目录的隐藏临时文件,commit 才原子落位;默认不覆盖已有文件。
 * `chunk` 顺序写、可续传(offset 必须等于已接收字节数),是 query 类 —— 靠 offset 防重放,不靠回执。
 */
export const UPLOAD_MAX_BYTES_WIRE = 256 * 1024 * 1024
export const UPLOAD_CHUNK_BYTES = 512 * 1024
export const filesUploadBeginParams = z.strictObject({
  workspaceId: idSchema, path: relPath.min(1),
  size: z.number().int().min(0).max(UPLOAD_MAX_BYTES_WIRE), overwrite: z.literal(true).optional()
})
export const filesUploadChunkParams = z.strictObject({
  uploadId: idSchema, offset: z.number().int().min(0).max(UPLOAD_MAX_BYTES_WIRE),
  /** base64;512 KiB 原始字节 ≈ 699,051 字符。 */
  data: z.string().min(1).max(Math.ceil(UPLOAD_CHUNK_BYTES / 3) * 4)
})
export const filesUploadCommitParams = z.strictObject({ uploadId: idSchema, sha256: z.string().regex(/^[0-9a-fA-F]{64}$/).optional() })
export const filesUploadAbortParams = z.strictObject({ uploadId: idSchema })

/** 目录浏览:只返回子目录名,用来选工作区根。空路径 = 服务用户的家目录。 */
export const directoriesListParams = z.strictObject({ path: z.string().max(4096) })

/**
 * 供应商与凭据(设备上的账号/密钥归设备)。
 * ★ **密钥只写不读**:`apiKey` 只出现在写入的 params 里;任何响应都只回 `hasKey / last4`,
 *   没有 reveal 方法 —— 凭据只留在设备上,不经网络出设备。
 * ★ 协议选项(`protocolOptions`)与视频路由(`videoGeneration`)不接受远程传入:它们决定「把凭据发往哪个
 *   地址」,远程更改只允许改基础字段,其余沿用设备上已有的值。
 */
export const PROVIDER_PROTOCOLS = ['anthropic', 'openai-chat', 'openai-responses'] as const
export const providersGetParams = z.strictObject({ id: idSchema })
export const providersUpsertParams = z.strictObject({
  id: idSchema,
  name: z.string().min(1).max(128),
  protocol: z.enum(PROVIDER_PROTOCOLS),
  baseUrl: z.string().min(1).max(2048),
  priority: z.number().int().min(0).max(10_000).optional(),
  enabled: z.boolean(),
  /** 写入即生效,不回显。 */
  apiKey: z.string().min(1).max(4096).optional()
})
export const providersRemoveParams = z.strictObject({ id: idSchema })
export const providersSetCredentialParams = z.strictObject({ id: idSchema, apiKey: z.string().min(1).max(4096) })
export const providersCredentialInfoParams = z.strictObject({ id: idSchema })
export const credentialMaskResult = z.strictObject({ hasKey: z.boolean(), last4: z.string().nullable() })

/**
 * 模型别名:从供应商拉取可用模型(设备用**自己存着的密钥**向供应商发请求,密钥不经网络),
 * 整表替换某家供应商的别名,按别名删除。写后设备端会修复指向已消失别名的默认设置。
 */
const modelId = z.string().min(1).max(256)
export const modelsFetchParams = z.strictObject({ providerId: idSchema })
export const modelsSetAliasesParams = z.strictObject({ providerId: idSchema, models: z.array(modelId).max(200) })
export const modelsRemoveParams = z.strictObject({ providerId: idSchema, alias: modelId })

/**
 * MCP 服务器(设备上的工具来源)。
 * ★ **配置里只有键名**(`envNames` / `headerNames`),值用 `mcp.setSecrets` **只写**进设备的加密存储,
 *   没有任何读取值的方法;`secretsInfo` 只回已存的键名。
 * ★ stdio 传输会在设备上启动 `command`:这与「已配对控制端拥有该用户的 shell(终端方法)」是同一等级的授权,
 *   配对界面必须明示;这里不假装它更安全。
 */
const mcpName = z.string().regex(/^[A-Za-z_][A-Za-z0-9_-]{0,127}$/)
const mcpBase = {
  id: z.string().regex(/^[a-zA-Z0-9_-]{1,32}$/),
  name: z.string().min(1).max(128),
  description: z.string().max(2000).optional(),
  enabled: z.boolean(),
  workspaceId: idSchema.optional()
}
export const mcpServerConfigSchema = z.discriminatedUnion('transport', [
  z.strictObject({
    ...mcpBase, transport: z.literal('stdio'), command: z.string().min(1).max(4096),
    args: z.array(z.string().max(4096)).max(64), envNames: z.array(mcpName).max(64), cwd: z.string().max(4096).optional()
  }),
  z.strictObject({
    ...mcpBase, transport: z.enum(['sse', 'streamable-http']), url: z.string().min(1).max(2048), headerNames: z.array(mcpName).max(64)
  })
])
export const mcpUpsertParams = z.strictObject({ config: mcpServerConfigSchema })
export const mcpIdParams = z.strictObject({ id: z.string().regex(/^[a-zA-Z0-9_-]{1,32}$/) })
export const mcpSetSecretsParams = z.strictObject({
  id: z.string().regex(/^[a-zA-Z0-9_-]{1,32}$/),
  values: z.record(mcpName, z.string().max(8192)).refine((values) => Object.keys(values).length <= 64, 'Too many secrets')
})
export const mcpSecretsResult = z.strictObject({ storedNames: z.array(z.string()), encryptionAvailable: z.boolean() })

/**
 * 技能 / 斜杠命令 / 子代理 / 模式:列出与启停。启停只改设备上的开关,不改文件。
 * ★ 不含「从市场/ZIP 安装」:那会把第三方内容写进设备,需要单独的来源确认流程,先不经网络开放。
 */
// eslint-disable-next-line no-control-regex -- 名字里不允许控制字符与路径分隔符
const catalogName = z.string().min(1).max(128).regex(/^[^\u0000-\u001f\u007f/\\]+$/)
export const catalogListParams = z.strictObject({ workspaceId: idSchema.optional() })
export const modesListParams = z.strictObject({ workspaceId: idSchema })
export const skillsSetEnabledParams = z.strictObject({ skillId: catalogName, enabled: z.boolean() })
export const nameSetEnabledParams = z.strictObject({ name: catalogName, enabled: z.boolean() })
export const enabledResult = z.strictObject({ updated: z.literal(true) })
/** 钩子:列出与启停。编辑/删除/测试(会在设备上执行命令)不经网络开放。 */
export const hooksSetEnabledParams = z.strictObject({
  scope: z.enum(['global', 'project']), workspaceId: idSchema.optional(), id: catalogName, enabled: z.boolean()
})
