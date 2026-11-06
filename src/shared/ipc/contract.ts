/**
 * 类型化 IPC 契约 —— 方案 §3。
 *
 * `docs/ipc-protocol.md` 的三条约定照单全收:三原语语义严格区分、`模块:动作` 冒号命名、
 * 所有 `on*` 必须返回退订函数。四处收紧:
 *
 * | 协议文档 | NextCoWork |
 * |---|---|
 * | §5.2 两种返回风格「模块内保持一致」 | **只用一种** —— IpcResult 信封,全局一致 |
 * | §5.3 靠异常穿过 handle 传播 | **异常绝不穿过 handle** |
 * | §6 长任务无序号、不可重放 | **信封带 seq,可 attach(sinceSeq) 重放** |
 * | §2 每个 API 方法内部硬编码目标频道 | **由契约生成白名单**,preload 运行时校验 |
 */
import type { WidgetActionPreflight, WidgetActionResult } from '../domain/widget'
import type { AgentError } from '../agent/error'
import type { ActiveRunEntry, AgentEvent, RunSnapshot } from '../agent/event'
import type { AgentMessage, ContentPart } from '../agent/message'
import type { ContextPreview } from '../agent/context-management'
import type { InteractionResponse, PendingInteraction } from '../agent/interaction'
import type { InterjectItem } from '../agent/interject'
import type { PermissionMode } from '../agent/permission'
import type { RunRequest, SendOptions, SessionMode } from '../agent/run-request'
import type { ToolInfo } from '../agent/tool'
import type { Bootstrap } from '../domain/bootstrap'
import type { DeviceInvitationPreview, DevicesState } from '../domain/device-connection'
import type { DirListing, FileSuggestion } from '../domain/file-tree'
import type { ComposerCompletionContext } from '../domain/composer-completion'
import type { ReviewChangeSet, ReviewFileDiff, ReviewMutationResult } from '../domain/review'
import type { McpSecretsInfo, McpServerConfig, McpServerStatus } from '../domain/mcp'
import type { ProxyPasswordInfo } from '../domain/proxy'
import type { PluginPermission } from '../plugin/permission'
import type { PluginActivity, PluginCatalog } from '../plugin/state'
import type { PluginInteractionRequest, PluginTabTarget } from '../plugin/ui-request'
import type { PluginMarketItem, PluginMarketPage, PluginUpdate, PluginUpdateResult } from '../plugin/market'
import type {
  Attachment,
  AttachmentScope,
  AttachmentUploadRequest,
  PickedAttachment
} from '../domain/attachment'
import type {
  SessionInputPersistence,
  SessionInputState,
  SessionQueueOp,
  SessionQueueResult,
  SessionQueueSnapshot,
  SubagentReportStatus
} from '../domain/queued-input'
import type { VideoJobView } from '../domain/video-generation'
import type { SearchProviderId, SearchProviderStatus } from '../domain/search'
import type {
  CredentialInfo,
  RevealedCredential,
  FailoverEvent,
  FetchedModel,
  GatewayStatus,
  ModelAlias,
  UpstreamProvider
} from '../domain/provider'
import type { ModelCatalogDefinition } from '../domain/model-catalog'
import type { ModelPricing } from '../domain/pricing'
import type { PricingSyncPreview } from '../domain/models-dev'
import type { ModelCatalogSyncPreview } from '../domain/models-dev-catalog'
import type { ProviderAccount } from '../domain/provider-account'
import type { SearchHit, Session, SessionChange, SessionDetail, SessionListItem, SessionPage } from '../domain/session'
import type { AppSettings, AppSettingsPatch, ResolvedTheme, StorageStats } from '../domain/settings'
import type { InnerTabState, WindowKind, WindowTabState } from '../domain/tab'
import type { ImageTheme, ThemeProfile } from '../domain/theme'
import type { TerminalBuffer, TerminalCreateRequest, TerminalInfo, TerminalPreparation } from '../domain/terminal'
import type { BackgroundShellInfo } from '../domain/shell'
import type { SkillListItem, SkillMarketItem, SkillInstallScope } from '../domain/skill'
import type { CommandDefinition } from '../domain/command'
import type { AgentDraft } from '../domain/agent-def'
import type { HookDiagnostic, HookEvent, HookListItem, HookRunReport, HookScope, HookUpsert } from '../domain/hook'
import type { ActiveGoal, GoalChange } from '../domain/goal'
import type {
  AgentListItem,
  CommandListItem,
  MarkdownResourceFile,
  MarkdownResourceKind,
  MarkdownResourceSave,
  MarkdownResourceScope
} from '../domain/markdown-resource'
import type { ModeDefinition } from '../domain/mode'
import type { Workspace, WorkspaceSettings } from '../domain/workspace'
import type { ConnectionProfile, ConnectionProfileInput, ConnectionStatus, PreparedWorkspace, RemoteDirectory, SshAuthRequest, SshAuthResponse } from '../domain/environment'
import type { UpdateCheckResult, UpdateState } from '../domain/update'
import type { ClientAuthState, ClientAuthUser, ClientUsageEntry } from '../domain/client-auth'
import type { ReferralState } from '../domain/referral'
import type { RechargeCheckoutResult, RechargeOptionsState, RechargeOrderState } from '../domain/recharge'
import type { MigrationState } from '../domain/data-migration'
import type { SyncConflict, SyncPreview, SyncSetupRequest, SyncStatus } from '../domain/config-sync'
import type {
  WorkspaceFile,
  WorkspaceFileMutationRequest,
  WorkspaceFileMutationResult,
  WorkspaceRecoveryListing,
  WorkspaceFileRequest,
  WorkspaceFileWriteRequest
} from '../domain/workspace-file'
import type { OpenTarget, WorkspacePathKind } from '../domain/open-target'
import type {
  DocumentViewChanged,
  DocumentViewCommandRequest,
  DocumentViewHeaders,
  DocumentViewHeadersRequest,
  DocumentViewInputRequest,
  DocumentViewList,
  DocumentViewListRequest,
  DocumentViewInputResult,
  DocumentViewLayout,
  DocumentViewLayoutRequest,
  DocumentViewOpenRequest,
  DocumentViewOpened,
  DocumentViewRenderRequest,
  DocumentViewRenderResult,
  DocumentViewState,
  DocumentViewTokenRequest
} from '../document-engine/view'
import type { BrowserChange, BrowserCuaEvent, BrowserProfile, BrowserTab } from '../domain/browser'
import type { GitBranchSummary, GitCommitSummary, GitDiff, GitDiffSides, GitOverview } from '../domain/git'
import type { ScheduledRun, ScheduledTask, ScheduledTaskInput } from '../domain/scheduled'
import type {
  BackupStatus,
  CleanupAge,
  CleanupPreview,
  CleanupResult,
  ImportApplyResult,
  ImportPreview,
  RestoreResult
} from '../domain/data'
import type { ProviderImportResult } from '../domain/provider-export'
import type {
  ImportApplyRequest,
  ImportBatchItemsPage,
  ImportConflictResolution,
  ImportHistoryPage,
  ImportJobStatus,
  ImportPreview as SourceImportPreview,
  ImportPreviewPage,
  ImportPreviewQuery,
  ImportSourceKind,
  ImportSourceState,
  ImportSyncPatch
} from '../domain/import'
import type { ImportableProviders } from '../domain/provider-import'
import type {
  UsageActivityStats,
  UsageAttemptRecord,
  UsageDailyBucket,
  UsageDimensionStat,
  UsageRequestLogsPage,
  UsageRequestLogsQuery,
  UsageSummary,
  UsageWindow
} from '../domain/usage'

// ═══════════════════════════════════════════════════════════════
// 一、信封
// ═══════════════════════════════════════════════════════════════

/**
 * ★ 绝不让异常穿过 ipcMain.handle(方案 §3 规则 3)。
 * Electron 会把它 stringify 成 `Error: Error invoking remote method 'x': ...`,
 * 错误类型全丢 —— 而 §4.11 那套 code 分类正是 UI 用来决定「跳设置页 / 提示 compact /
 * 显示重试倒计时」的唯一依据。
 */
export type IpcResult<T> = { ok: true; data: T } | { ok: false; error: AgentError }

/**
 * 事件信封。★ seq 放在**信封上**,不放在 event 里(方案 §3 规则 1)。
 * 渲染层防漂移逻辑因此只有一行:seq !== lastSeq + 1 → agent:attach(runId, lastSeq) 补齐。
 * 事后加 seq 要动每个 emit 点、每个 reducer、以及已落盘的格式。
 */
export interface AgentEventEnvelope {
  runId: string
  /** 本批**最后一个**事件的序号;批内事件依次是 seq - events.length + 1 … seq */
  seq: number
  /** 复数:主进程侧 16–33ms 合批(方案 §8) */
  events: AgentEvent[]
}

/**
 * 批内首个事件的序号。信封只带最后一个,这是唯一的反推公式 ——
 * 所以它必须只写一遍。发送端(合批泵)与接收端(session store)对
 * 「seq 指的是哪一个事件」的理解一旦分岔,症状是**每批都报缺口**、
 * 于是每批都触发一次 attach,看起来像网络抖动,实际是一个 ±1。
 */
export function envelopeFirstSeq(env: AgentEventEnvelope): number {
  return env.seq - env.events.length + 1
}

/**
 * 与已应用的 lastSeq 之间是否有缺口 → 走 `agent:attach(runId, lastSeq)` 补齐
 * (方案 §3 规则 1)。
 *
 * 重复的批也会被判为「有缺口」,这是**故意的**:attach 重放是幂等的,
 * 而试图在这里分辨「重复」与「丢失」需要维护一个已见 seq 集合,不值当。
 */
export function hasSeqGap(env: AgentEventEnvelope, lastSeq: number): boolean {
  return envelopeFirstSeq(env) !== lastSeq + 1
}

/**
 * `theme:importImage` 的回程:主进程已经把文件收进 `userData/themes/`,
 * 但**还没登记** —— 种子色和色点要等渲染层解码完这张位图才算得出来
 * (`extractPalette` 要的是 RGBA,而主进程没有 canvas)。
 *
 * 所以导入是**两相**的:这一相给字节,渲染层算完再走 `theme:saveImage` 落表。
 * 中途放弃(窗口关了、这张图解不开)留下的是一个没进表的孤儿文件,下次启动扫掉 ——
 * 而不是一条读得出文件、读不出颜色的坏记录。**宁可丢文件,不要留坏行。**
 */
export interface ImportedImage {
  id: string
  /** 文件名去掉扩展名,当作这张卡的默认名字 */
  name: string
  mime: string
  /**
   * ★ **`<ArrayBuffer>` 不是装饰。** TS 5.7 起 `Uint8Array` 对底层缓冲泛型化,
   * 而裸 `Uint8Array` 推出来的是 `ArrayBufferLike` —— 里面含着 `SharedArrayBuffer`,
   * 于是 `new Blob([bytes])` 不给过(`BlobPart` 只收 `ArrayBufferView<ArrayBuffer>`)。
   * 而渲染层拿到这些字节**唯一要做的事**就是建 Blob 去解码。
   *
   * 写死成 `<ArrayBuffer>` 是**如实描述**,不是糊弄编译器:主进程那边是
   * `new Uint8Array(readFileSync(...))`,结构化克隆过来的也从不是共享内存。
   * 换成在调用点 `new Uint8Array(bytes)` 复制一份,代价是白拷一次最大 16MB。
   */
  bytes: Uint8Array<ArrayBuffer>
}

export interface ThemeImageMetadata {
  width: number
  height: number
  thumbnail: Uint8Array<ArrayBuffer>
  animated: boolean
}

// ═══════════════════════════════════════════════════════════════
// 二、渲染 → 主,要返回值(invoke)
// ═══════════════════════════════════════════════════════════════

/**
 * `context:preview` 的入参。★ 字段一律从 `RunRequest` 上 `Pick` 下来,不重新声明 ——
 * 预览装配的必须和真正发送时装配的是**同一组输入**,各写一份的话哪天 `RunRequest`
 * 多一个影响提示词的开关,预览会安静地继续按旧口径算。
 */
export type ContextPreviewRequest = Pick<
  RunRequest,
  | 'sessionId'
  | 'workspaceId'
  | 'model'
  | 'modelProviderId'
  | 'mode'
  | 'thinking'
  | 'permissionMode'
  | 'webSearch'
  | 'maxContext'
>

export interface IpcInvokeMap {
  // ── 应用 ──
  'app:getBootstrap': { req: void; res: Bootstrap }
  'app:openExternal': { req: { url: string }; res: void }
  'app:checkForUpdates': { req: void; res: UpdateCheckResult }
  'app:updateCheck': { req: void; res: UpdateState }
  'app:updateDownload': { req: void; res: UpdateState }
  'app:updateInstall': { req: void; res: void }
  'app:updateGetState': { req: void; res: UpdateState }
  'app:copyText': { req: { text: string }; res: void }
  // ── 设备(控制端本地:设备清单与配对凭据只在这台电脑上) ──
  /** 已配对设备清单 + 当前选择与连接状态。不含证书/私钥。 */
  'device:getState': { req: void; res: DevicesState }
  /** 只解码并校验邀请(不联网),返回要让用户核对的设备指纹。 */
  'device:inspectInvitation': { req: { text: string }; res: DeviceInvitationPreview }
  /** 配对。`confirmedFingerprint` 必须是用户核对过的那一个,与邀请不一致即拒绝。 */
  'device:pair': { req: { text: string; label: string; confirmedFingerprint: string }; res: DevicesState }
  /** 切换到某台已配对设备并记住选择;连不上时保持「离线」,不回退本机。 */
  'device:select': { req: { id: string }; res: DevicesState }
  /** 回到本机。 */
  'device:useLocal': { req: void; res: DevicesState }
  /** 移除本机保存的连接(含私钥)。**不会**撤销设备上的配对。 */
  'device:remove': { req: { id: string }; res: DevicesState }
  /** 设置/清除(`url: null`)这台设备的备用中转(wss://…,自签证书可附 `caPem`)。直连永远优先。 */
  'device:setRelay': { req: { id: string; url: string | null; caPem?: string }; res: DevicesState }
  /**
   * 把一张图写进系统剪贴板。`base64` 是**原图字节**,不带 `data:` 前缀。
   *
   * ★ 需求:对话里生成的图此前没有任何出口(灯箱的「用别的程序打开」只对磁盘上的
   * 绝对路径出现,而生成图是 `ncw://` 附件或内联 data URL)。用户要把一张图贴进
   * 别的应用,唯一的做法是让 Agent 调 `SaveImage` 落盘再自己找 —— 而剪贴板是
   * 这一步最短的路。
   *
   * ★ 和 `app:copyText` 分开,而不是给它加一个「这次是图」的开关:Electron 里
   * `writeText` 与 `write(ClipboardItem)` 是两套 API,走错一条**不报错** ——
   * 用户粘出来是一串 base64 文本。
   *
   * ★ 不带 mime:主进程按**字节的魔数**判格式(同 `imageMimeOfBytes` 在别处的用法),
   * 不信渲染层自报 —— 一张自报成 png 的 jpeg 在粘贴板上会被当成 png 贴出去。
   *
   * ★ 不走渲染层的 `navigator.clipboard`(它也能写图):仓库里所有复制都经主进程
   * (见 `services/app.ts` 的 `copyText` 与那条钉住它的测试),一处校验、一处失败面。
   */
  'app:copyImage': { req: { base64: string }; res: void }
  /**
   * 存一段文本到用户挑的位置。路径由主进程的 showSaveDialog 产出 ——
   * 渲染层只给**文件名建议**,永不指定任意路径(方案 §9)。取消时返回 null。
   */
  'app:saveTextFile': { req: { defaultName: string; text: string }; res: { path: string } | null }
  /**
   * 存一张图片（邀请海报 / 对话里生成的图）。`base64` 是原图字节去掉前缀的那一段。
   *
   * ★ 和 `app:saveTextFile` 分开，而不是给它加个「二进制」开关:两者的保存对话框
   * 过滤器和写入编码都不同,合并之后每个调用点都要带一个「这次是文本还是二进制」
   * 的参数 —— 传错一次就写出一个内容是 base64 文本的 .png,而且打开之前看不出来。
   *
   * ★ 也不带 mime:扩展名与过滤器由主进程按字节判(理由见上面那条)。
   */
  'app:saveImageFile': { req: { defaultName: string; base64: string }; res: { path: string } | null }
  'app:openSessionWindow': { req: { workspaceId: string; sessionId: string }; res: void }

  // ── NextCoWork 账户 ──
  'clientAuth:getState': { req: void; res: ClientAuthState }
  'clientAuth:startLogin': { req: void; res: ClientAuthState }
  'clientAuth:selectTeam': { req: { teamId: string }; res: ClientAuthState }
  'clientAuth:useOffline': { req: void; res: ClientAuthState }
  'clientAuth:signOut': { req: void; res: ClientAuthState }
  'clientAuth:getUser': { req: void; res: ClientAuthUser | null }
  'clientAuth:getUsage': { req: { from?: string; to?: string }; res: ClientUsageEntry[] }
  /**
   * 奖励中心的一次性快照（邀请码 / 链接 / 活动配置 / 统计 / 两张记录表）。
   *
   * ★ **没有推送频道，只有拉取。** 邀请数据的变化由对方的注册和消费触发，
   * 桌面端这边没有任何本地事件能预告它 —— 做成订阅只会是一条永远不发的频道。
   * 面板打开时拉一次，用户按刷新再拉一次。
   *
   * 失败不抛：`res` 自带 `unavailable` 分支（未登录 / 平台没这条接口 / 网络），
   * 三种空态的文案和用户下一步都不同，见 `shared/domain/referral.ts`。
   */
  'referral:get': { req: void; res: ReferralState }
  /**
   * 钱包充值（`/api/client/recharge/*`）。三条都**失败不抛**，原因分流见
   * `shared/domain/recharge.ts`。`checkout` 成功时主进程已经把 Stripe 收银页交给系统
   * 浏览器，渲染层只拿订单号去轮询 —— 收银地址不经过渲染层。
   */
  'recharge:getOptions': { req: void; res: RechargeOptionsState }
  'recharge:checkout': { req: { amount: number }; res: RechargeCheckoutResult }
  'recharge:getOrder': { req: { orderNo: string }; res: RechargeOrderState }
  'configSync:getStatus': { req: void; res: SyncStatus }
  /** 密码只用于本次创建/解锁 vault，不进入设置或事件广播。 */
  'configSync:setup': { req: SyncSetupRequest; res: SyncStatus }
  'configSync:getConflicts': { req: void; res: SyncConflict[] }
  'configSync:getPreview': { req: void; res: SyncPreview }
  'configSync:confirmInitial': { req: void; res: void }
  'configSync:resolve': { req: { id: string; useRemote: boolean }; res: void }
  /** 数据页「同步使用统计」开关;关掉会丢弃本机持有的其它设备统计。 */
  'configSync:setUsage': { req: { enabled: boolean }; res: SyncStatus }

  // ── 设置 ──
  'settings:get': { req: void; res: AppSettings }
  // 嵌套块可以只给要改的属性 —— 见 AppSettingsPatch 的注释(那是一整类竞态)
  'settings:update': { req: AppSettingsPatch; res: AppSettings }

  // ── 图片主题(设置 › 偏好 › 图片主题)──
  /** ★ 选文件走主进程 dialog.showOpenDialog —— 渲染层永不指定任意路径(方案 §9) */
  'theme:importImage': { req: void; res: ImportedImage | null }
  /**
   * 导入第二相:渲染层解码算出 seed/palette 之后才登记。返回登记后的全表 ——
   * 让调用点不必再补一次 `theme:listImages`(两次调用之间的那一小段里,
   * 界面上的列表和磁盘上的表是两回事)。
   */
  'theme:saveImage': {
    req: { id: string; name: string; seed: string; palette: string[]; metadata?: ThemeImageMetadata }
    res: ImageTheme[]
  }
  'theme:listImages': { req: void; res: ImageTheme[] }
  /**
   * ★ **id → 查表 → 路径。** 渲染层递进来的 id 只用来查一条记录,
   * 拿到的是主进程当初自己写下的路径;id 本身**从不参与 `join`**(方案 §9)。
   */
  'theme:readImage': {
    req: { id: string }
    res: { mime: string; bytes: Uint8Array<ArrayBuffer> }
  }
  'theme:deleteImage': { req: { id: string }; res: ImageTheme[] }
  'theme:listProfiles': { req: void; res: ThemeProfile[] }
  'theme:saveProfile': { req: ThemeProfile; res: ThemeProfile[] }
  'theme:deleteProfile': { req: { id: string }; res: ThemeProfile[] }
  'theme:renameProfile': { req: { id: string; name: string }; res: ThemeProfile[] }

  // ── 工作区 ──
  'workspace:list': { req: void; res: Workspace[] }
  'workspace:prepare': { req: { workspaceId: string; requestId: string; allowLocalCommands?: boolean }; res: PreparedWorkspace }
  'workspace:commitActivation': { req: { ticket: string; requestId: string }; res: void }
  'workspace:releaseActivation': { req: { ticket: string }; res: void }
  'workspace:createSsh': { req: { browseId: string; path: string; requestId: string }; res: Workspace }
  /** ★ `hasPassword` 是**布尔**:存下的密码永不回渲染层,界面只需要知道"有没有" */
  'connection:list': { req: void; res: Array<{ profile: ConnectionProfile; status: ConnectionStatus; hasPassword: boolean }> }
  'connection:upsert': { req: ConnectionProfileInput; res: ConnectionProfile }
  'connection:remove': { req: { id: string }; res: void }
  'connection:connect': { req: { id: string; requestId: string; allowLocalCommands: boolean }; res: RemoteDirectory }
  'connection:disconnect': { req: { id: string }; res: void }
  'connection:cancel': { req: { requestId: string }; res: void }
  'connection:browse': { req: { browseId: string; path: string; requestId: string }; res: RemoteDirectory }
  'connection:closeBrowse': { req: { browseId: string }; res: void }
  'connection:respond': { req: SshAuthResponse; res: void }
  'connection:pickFile': { req: void; res: string | null }
  /** ★ 走主进程 dialog.showOpenDialog —— 渲染层永不指定任意路径(方案 §9) */
  'workspace:pick': { req: void; res: Workspace | null }
  'workspace:update': {
    req: { id: string; name?: string; settings?: Partial<WorkspaceSettings> }
    res: Workspace
  }
  /** 关闭 = 从列表移除这条记录,与「关闭外层 Tab」是两回事(方案 §8) */
  'workspace:close': { req: { id: string }; res: void }
  /**
   * 渲染层上报**这个窗口当前停在哪个工作区**。
   *
   * ★ 主进程原本没有这个状态,需要它的地方(目前是插件的路径类能力)只能拿
   * `listWorkspaces()[0]` —— 按 `last_opened_at` 倒序的第一条。那是个**近似值**:
   * 它记的是「何时最后一次打开」而不是「现在停在哪」,用户在已开的几个工作区
   * 之间切 Tab 根本不动这个时间戳。于是插件新建的文件会落进上一次打开的
   * 那个工作区 —— 用户看着 B 点的新建,文件出现在 A 里。
   *
   * ★ **按窗口记**,不是全局单值:`activeWorkspaceId` 在渲染层就是每窗口一份
   * (`stores/window.ts`),两个窗口停在不同工作区是正常状态,合成一个全局值
   * 等于让后切的那个窗口覆盖掉前一个。
   *
   * `null` = 这个窗口没有停在任何工作区(全部关掉了)。
   */
  'workspace:setActive': { req: { workspaceId: string | null }; res: void }
  /**
   * 右侧文件树列一层。**懒加载**:展开一个目录才拉它,不递归 ——
   * 一次把 `node_modules` 整棵树拉回来,IPC 那一下就够卡半秒。
   *
   * `path` 是**工作区相对**路径,或一条绝对路径(工具卡片和 Markdown 链接会给出
   * 工作区外的绝对路径,点开要落得到)。它来自渲染层,是不可信输入,
   * 一律经 `resolveAnywhere` 归一化(方案 §9)。
   */
  'workspace:listDir': { req: { workspaceId: string; path: string }; res: DirListing }
  /**
   * 输入框 `@` 的文件检索。**打分在主进程做,回来的只有十几条。**
   *
   * ★ 不提供「把整份清单拉过来」的通道:一个中等仓库是上万条路径,
   * 整批过结构化克隆的代价,加上「渲染层那份缓存什么时候失效」这个问题,
   * 都比在主进程排个序贵(见 `ipc/workspace-search.ts` 文件头)。
   *
   * `query` 是不可信输入,但它**不参与任何路径拼接** —— 只拿去和一份
   * 已经遍历好的索引做字符串匹配,所以这里没有 §9 的那道解析。
   */
  'workspace:searchFiles': {
    req: { workspaceId: string; query: string; limit?: number }
    res: FileSuggestion[]
  }
  'workspace:readFile': { req: WorkspaceFileRequest; res: WorkspaceFile }
  /*
    res 放宽为 `WorkspaceFile`:base64 支线(图片编辑器覆写)返回 image 形状,
    文本支线不变。存量调用方只读 `revision`,不受影响。
  */
  'workspace:writeFile': { req: WorkspaceFileWriteRequest; res: WorkspaceFile }
  'workspace:mutateFile': { req: WorkspaceFileMutationRequest; res: WorkspaceFileMutationResult }
  'workspace:revealFile': { req: WorkspaceFileRequest; res: void | { remote: true; path: string; parent: string; name: string } }
  /**
   * 「打开方式」下拉里那几项:文件管理器 / 终端 / 这台机器上装了的 IDE。
   *
   * ★ **res 里没有路径**,只有 `{ id, label, icon }` —— 绝对路径不进渲染层
   *   (`shared/domain/workspace-file.ts` 顶上那条约定)。菜单内容只取决于
   *   这台机器装了什么,所以这条**不带 workspaceId**。
   *
   * ★ 不登记事件频道:装了新 IDE 不会发通知,菜单下次探测时自然看得见
   *   (主进程侧缓存一次,失败不缓存)。
   */
  'workspace:listOpenTargets': { req: void; res: OpenTarget[] }
  /**
   * 用某个程序打开一个文件。`targetId` 只用来查主进程那张表 —— 渲染层指定不了
   * 要跑什么命令,只能从 `workspace:listOpenTargets` 给过的那几个里挑。
   */
  'workspace:openWith': { req: { workspaceId: string; path: string; targetId: string }; res: void }
  /**
   * 把路径写进系统剪贴板。★ 返回**真正写进去的那一串**:工作区外的文件复制出来
   * 的是绝对路径,界面据此如实说明,而不是一律说「已复制相对路径」。
   */
  'workspace:copyPath': { req: { workspaceId: string; path: string; kind: WorkspacePathKind }; res: string }
  /**
   * 文件树右键「另存为…」。落点由主进程的 showSaveDialog 产出(同 `app:saveTextFile`),
   * 渲染层只给工作区相对路径。`false` = 用户取消。
   */
  'workspace:saveFileAs': { req: { workspaceId: string; path: string }; res: boolean }
  'workspace:listRecovery': { req: { workspaceId: string }; res: WorkspaceRecoveryListing }

  // ── 改动审查(回复底部审查卡 / 右侧 changes tab / 撤销·恢复,schema 第 23 条)──
  'review:getChangeSet': { req: { runId: string }; res: ReviewChangeSet | null }
  'review:getFileDiff': { req: { runId: string; path: string }; res: ReviewFileDiff | null }
  'review:precheckUndo': { req: { runId: string }; res: { conflicts: string[] } }
  'review:undo': { req: { runId: string; force?: boolean }; res: ReviewMutationResult }
  'review:redo': { req: { runId: string }; res: ReviewMutationResult }

  // ── 浏览器工作台 ──
  'browser:list': { req: { workspaceId: string }; res: BrowserTab[] }
  'browser:open': {
    req: { workspaceId: string; url: string; title?: string; profileId?: string; clientTabId?: string }
    res: BrowserTab
  }
  'browser:navigate': { req: { workspaceId: string; tabId: string; url: string }; res: BrowserTab }
  'browser:close': { req: { workspaceId: string; tabId: string }; res: void }
  'browser:bind': { req: { workspaceId: string; tabId: string; webContentsId: number }; res: void }
  'browser:profiles': { req: void; res: BrowserProfile[] }
  'browser:createProfile': { req: { name: string; domains?: string[]; startUrl?: string }; res: BrowserProfile }
  'browser:deleteProfile': { req: { id: string }; res: void }
  'browser:exportCookies': { req: { workspaceId: string; profileId: string }; res: boolean }
  'browser:importCookies': { req: { workspaceId: string; profileId: string }; res: number | null }
  'browser:clearProfileState': { req: { workspaceId: string; profileId: string }; res: void }

  // ── Git(侧边栏入口,管理当前工作区的仓库)──
  /*
    ★ 一律按 `workspaceId` 寻址,渲染层**从不递路径**给 git(方案 §9 同一条规矩):
    主进程自己去 store 查 rootPath。渲染层递进来的 `path` 是仓库相对路径,只被
    原样拼回给 git 当参数,不参与 fs 拼接 —— 所以这里的校验是「非空、无 NUL」,
    而不是 workspace-files 那套逐段 lstat 审计。
  */
  'git:getOverview': { req: { workspaceId: string }; res: GitOverview }
  'git:listBranches': { req: { workspaceId: string }; res: GitBranchSummary[] }
  'git:listCommits': { req: { workspaceId: string; limit?: number }; res: GitCommitSummary[] }
  /** 单个文件的 diff。`staged` 选择看暂存区还是工作区。 */
  'git:getDiff': { req: { workspaceId: string; path: string; staged: boolean }; res: GitDiff }
  /** 同一份 diff 两侧的完整文本(代码对比视图用)。`null` = 没法按全文对比,退回 unified 文本 */
  'git:getDiffSides': { req: { workspaceId: string; path: string; staged: boolean }; res: GitDiffSides | null }
  'git:stage': { req: { workspaceId: string; paths: string[] }; res: void }
  'git:unstage': { req: { workspaceId: string; paths: string[] }; res: void }
  /** 提交暂存区。`message` 空白一律拒 —— 空提交信息没有任何意义。 */
  'git:commit': { req: { workspaceId: string; message: string }; res: GitCommitSummary }
  'git:checkoutBranch': { req: { workspaceId: string; branch: string }; res: void }
  'git:createBranch': { req: { workspaceId: string; name: string; checkout: boolean }; res: GitBranchSummary }
  'git:pull': { req: { workspaceId: string }; res: void }
  'git:push': { req: { workspaceId: string }; res: void }
  /**
   * 「AI 写一条提交信息」。产出一份**草稿**交给渲染层填进输入框等人过目 ——
   * 这里不提交,和 `agents:generate` 同一个道理。
   *
   * ★ 不收 model 参数:模型取设置里的默认模型,在主进程里定。渲染层要是能指定
   *   模型,这颗按钮旁边就得多一个模型选择器,而用户此刻要决定的是提交信息
   *   的措辞,不是用哪家模型。
   */
  'git:generateCommitMessage': { req: { workspaceId: string }; res: { message: string } }

  // ── 输入框补全(docs/composer-autocomplete.md)──
  /**
   * 回合结束后建议下一步。`text` 为空 = 不出声(模型选择沉默、条件不满足、失败、被取消一律如此)。
   * ★ `triggerKey` 是渲染层看到的那次 `runEndedAt`:主进程按 (sessionId, triggerKey) 合流,
   *   两个窗口开着同一条会话只发一次请求。
   * ★ `model` 是输入框此刻的生效模型;设置里点名了补全模型时主进程改用那一个。
   */
  'composer:suggestNext': {
    req: { requestId: string; workspaceId: string; sessionId: string; triggerKey: number; model: string; modelProviderId?: string }
    res: { requestId: string; text: string }
  }
  /** 本工作区发过的指令(新→旧、去重、≤500 条),给本地历史补全用 */
  'composer:history': { req: { workspaceId: string }; res: { prompts: string[] } }
  /** 打字中续写。默认关;模型只取设置里点名的那一个 */
  'composer:complete': {
    req: { requestId: string; workspaceId: string; sessionId: string | null; draft: string; context: ComposerCompletionContext }
    res: { requestId: string; text: string }
  }
  /** 取消上面两类请求中的一个。已经结束的 id 静默忽略 */
  'composer:cancel': { req: { requestId: string }; res: void }

  // ── 定时任务 ──
  'scheduled:listTasks': { req: { workspaceId?: string }; res: ScheduledTask[] }
  'scheduled:getTask': { req: { id: string }; res: ScheduledTask | null }
  'scheduled:create': { req: ScheduledTaskInput; res: ScheduledTask }
  'scheduled:update': { req: { id: string; patch: Partial<ScheduledTaskInput> }; res: ScheduledTask }
  'scheduled:delete': { req: { id: string }; res: void }
  'scheduled:setEnabled': { req: { id: string; enabled: boolean }; res: ScheduledTask }
  'scheduled:runNow': { req: { id: string }; res: ScheduledRun }
  'scheduled:listRuns': { req: { taskId?: string; limit?: number }; res: ScheduledRun[] }
  'scheduled:getRun': { req: { id: string }; res: ScheduledRun | null }
  'scheduled:deleteRun': { req: { id: string }; res: void }

  // ── Tab 状态(读;写走 send,见 IpcSendMap) ──
  'tabs:getInner': { req: { workspaceId: string }; res: InnerTabState }

  /**
   * 未发出的输入(草稿 + 插入队列)。★ **读走 invoke,写走 send** ——
   * 与 Tab 布局同构。返回 null = 没有存档 / 存档已失效(版本不符或超 TTL),
   * 校验在主进程侧做,渲染层拿到的要么是可用的,要么是 null。
   */
  'session:getInput': { req: { sessionId: string }; res: SessionInputState | null }
  /**
   * 队列的写操作。★ **主进程是队列唯一的写入者**(`main/session-runtime.ts`):
   * 续跑在那边决定,所以没有窗口在看这条会话时,排队的消息照样会被发出去。
   * 回执即新的权威队列;同样的快照也经 `session:queueChanged` 广播给所有窗口。
   */
  'session:queue': { req: { sessionId: string; op: SessionQueueOp }; res: SessionQueueResult }
  /**
   * 用户在后台子代理卡片上点「处理」。`options` 是渲染层按工作区默认值拼的兜底档位 ——
   * 本进程没见过这条会话发消息(重启之后)时,汇报靠它才发得出去。
   */
  'session:reportBackground': {
    req: { sessionId: string; callId: string; options?: SendOptions }
    res: { status: SubagentReportStatus }
  }

  // ── 附件(上传走 invoke;读取走 ncw:// 协议,不经 IPC) ──
  /**
   * ★ 上传必须知道成败与落点,所以是 invoke 而不是 send。
   * 返回的 `Attachment.url` 是 `ncw://` 地址 —— **绝对路径不出主进程**。
   */
  'attachment:upload': { req: AttachmentUploadRequest; res: Attachment }
  /**
   * ★ 走主进程 dialog,渲染层永不指定任意路径(方案 §9)。取消返回空数组。
   *
   * ★ 返回的是 `PickedAttachment` 而不是 `Attachment`:图片落盘、非图片回传
   * 真实路径,与拖拽/粘贴同一套分流规则。回传路径不破坏 §9 —— 路径是用户在
   * 系统对话框里定的既成事实,渲染层仍然没有「指定」任何路径的能力。
   */
  'attachment:pick': {
    req: { scope: AttachmentScope; ownerId?: string; stageFiles?: boolean }
    res: PickedAttachment[]
  }
  'attachment:remove': { req: { id: string }; res: void }
  'attachment:prepareWorkspaceUpload': {
    req: { id: string; sessionId: string; workspaceId: string }
    res: import('../domain/attachment').WorkspaceAttachmentIntent
  }
  'attachment:completeWorkspaceUpload': { req: { ticket: string }; res: import('../domain/attachment').WorkspaceAttachmentReference }
  'attachment:cancelWorkspaceUpload': { req: { ticket: string }; res: void }
  /** 已上传但还没发出去的 —— 重启后恢复草稿附件区 */
  'attachment:listBySession': { req: { sessionId: string }; res: Attachment[] }

  // ── 会话 ──
  'sessions:list': { req: { workspaceId: string; archived?: boolean }; res: SessionListItem[] }
  'sessions:get': { req: { sessionId: string }; res: SessionDetail }
  'sessions:replaceHistory': { req: { sessionId: string; messages: AgentMessage[] }; res: void }
  /**
   * 转录的一页(见 `SessionPage`)。渲染层打开会话、往上翻都走它 —— 不再一次读整段历史。
   */
  'sessions:getPage': { req: { sessionId: string; limit: number; beforeMessageId?: string }; res: SessionPage }
  /** 会话元数据 + 消息条数,不读正文。只想知道模式 / 模型 / 标题的地方用它,别为此读整段转录 */
  'sessions:getSummary': { req: { sessionId: string }; res: { session: Session; messageCount: number } }
  /** 最后一条助手消息(后台子代理交差的正文),不读整段转录 */
  'sessions:lastAssistant': { req: { sessionId: string }; res: AgentMessage | null }
  /**
   * 按消息 id 改写转录。★ 主进程在**完整历史**上改:渲染层手里只有一页,
   * 拿那一页整段 `sessions:replaceHistory` 会把页外的历史当成「删掉了」。
   */
  'sessions:editMessage': { req: { sessionId: string; messageId: string; text: string; truncate: boolean }; res: void }
  'sessions:deleteTurn': { req: { sessionId: string; userMessageId: string }; res: void }
  'sessions:deleteReply': { req: { sessionId: string; fromId: string; toId: string }; res: void }
  'sessions:create': { req: { workspaceId: string; title?: string; sessionId?: string; mode?: SessionMode }; res: Session }
  'sessions:duplicate': { req: { sessionId: string; title: string }; res: Session }
  /** 从某一轮「分支」：只克隆到这一轮为止的转录，之后的内容不带过去。 */
  'sessions:branch': { req: { sessionId: string; uptoMessageId: string; title: string }; res: Session }
  /**
   * 从一条已有会话开一条「提炼 Skill」会话(`Session.skillSource` 指向源会话)。
   * 模型 / 供应商 / 思考档位沿用源会话;`title` 由渲染层翻译好传入。
   * 拒绝时带 `messageKey`(`skills.extraction.*`),渲染层直接翻成 toast。
   */
  'sessions:createSkillExtraction': { req: { sourceSessionId: string; title: string }; res: Session }
  'sessions:rename': { req: { sessionId: string; title: string }; res: void }
  'sessions:setMode': { req: { sessionId: string; mode: SessionMode }; res: void }
  /**
   * 记住这条会话选中的模型。★ 和 `setMode` 同理:模型是**每条会话自己的**记忆,
   * 在另一条会话里换模型不该把这条会话也换掉(工作区默认值只影响新会话)。
   */
  'sessions:setModel': { req: { sessionId: string; model: string; modelProviderId?: string }; res: void }
  'sessions:setArchived': { req: { sessionId: string; archived: boolean }; res: void }
  'sessions:setFavorited': { req: { sessionId: string; favorited: boolean }; res: void }
  /**
   * 删除会话(级联子代理转录)。待删子树里有真在跑的 Agent 时抛 `conflict`
   * (messageKey `session.deleteRunningMessage`);`stopRunning: true` = 用户已确认
   * 「停止并删除」:先中断(连同子代理)、等它收尾,卡住的强制清掉,再删。
   */
  'sessions:delete': { req: { sessionId: string; stopRunning?: boolean }; res: void }
  /** 全局入口,但结果可按工作区筛(同「定时任务」页那个筛选器) */
  'conversations:searchAll': {
    req: { q: string; workspaceId?: string; limit: number }
    res: SearchHit[]
  }
  /**
   * 手动压缩上下文(/compact)—— 让模型总结边界之后的对话,提交一条压缩边界消息。
   * `instructions` 是 `/compact <文字>` 里用户要求重点保留的内容。
   *
   * ★ 只在会话**空闲**时可用:跑着的那个 run 已经把自己的上下文冻在内存里,
   * 此时插一条边界会和它随后提交的消息交错。
   */
  'context:compact': {
    req: { sessionId: string; instructions?: string }
    res: { message: AgentMessage; inputTokens: number }
  }
  /**
   * 还没发过请求时的占用归因 —— 装配一次但**不发出去**。
   *
   * ★ **只读进程里已经有的那份**:不重扫技能目录、不去连 MCP、不租工作区环境。
   * 那三件事各自都会动全局单例或者起子进程,而这条通道会在用户每次点开上下文
   * 菜单时被调到 —— 一个只为了画几个百分比的副作用是不能接受的。代价是诚实的:
   * MCP 还没连上时那一档就是 0,连上之后再点开才有数(见 `previewContext`)。
   *
   * 会话里已经有历史时 `messages` 那一档是**真的**(从库里读),所以这条通道
   * 对「重开一个老会话」同样有用,不只是给空会话的。
   */
  'context:preview': {
    req: ContextPreviewRequest
    res: ContextPreview | undefined
  }

  // ── Agent ──
  /**
   * ★ runId 由调用方传入,不由这里返回(方案 §3 规则 2)。
   * `started: false` = 这条会话其实已经在跑(排队消息刚续上、或另一个窗口先发了),
   * 这句话被放进了队列;回执里是新的队列,调用方据此撤掉乐观消息。
   */
  'agent:run': { req: RunRequest; res: { started: true } | { started: false; queue: SessionQueueResult } }
  'agent:attach': { req: { runId: string; sinceSeq: number }; res: RunSnapshot }
  /**
   * 这个窗口不再看这个 run(及其子代理)的正文 —— 会话 Tab 关了 / 切走一段时间了。
   * ★ 只摘**调用方自己**的订阅,不停 run、不影响别的窗口。再看时走 `agent:attach`。
   */
  'agent:unwatch': { req: { runId: string }; res: void }
  'agent:abort': { req: { runId: string; cascade: boolean }; res: void }
  /**
   * 把一个**前台**子代理转去后台:父代理那次 `Task` 调用不再等它、当场返回,
   * 子代理继续跑,结论之后按后台子代理那条路送回主代理。
   *
   * 返回 `false` = 没转成(它已经跑完 / 本来就是后台 / 不在这个进程里)。不是错误。
   */
  'agent:backgroundSubagent': { req: { childRunId: string }; res: boolean }
  /**
   * 插话 —— 把这些条目排进正在跑的那个 run,由它在**下一个轮次边界**注入。
   *
   * ★ **全量替换,不是追加。** 渲染层每次「引入/取消引入/编辑/删除」都把当前
   * 全部 promoted 条目重发一遍,主进程整个换掉自己那份。追加语义要为
   * 取消、编辑、删除各配一条频道,而三条频道之间的到达顺序无法保证 ——
   * 全量替换让「最后一次发的就是真相」,乱序也收敛到同一个结果。
   *
   * ★ 空数组是合法且有意义的载荷:它就是「取消全部插话」。
   */
  'agent:interject': { req: { runId: string; items: InterjectItem[] }; res: void }
  /**
   * 权限档位药丸切到更宽松/更严格的一档时,立刻对**这个正在跑的 run**接下来的
   * 工具调用生效,而不必等到下一条新消息——`RunRequest.permissionMode` 是
   * run 开始时冻的快照(见其注释),这条频道改的是 `RunHandle` 上的活值
   * (`run-registry.ts` 的 `livePermissionMode`)。
   *
   * ★ 不影响已经在等用户点的那个审批弹窗:那个弹窗可能是钩子/工作区规则
   * 强制问人,不是纯档位决定的,放宽档位不该替用户把它自动点掉。
   */
  'agent:setPermissionMode': { req: { runId: string; mode: PermissionMode }; res: void }
  /** ★ 审批 / 反问 / 计划确认三种 kind 共用这一个(方案 §4.6) */
  'agent:respondInteraction': { req: InteractionResponse; res: void }
  'agent:listInteractions': { req: { runId?: string; sessionId?: string }; res: PendingInteraction[] }
  /**
   * 对话里 widget 的宿主能力(`main/ipc/widget.ts`)。iframe 碰不到 IPC,这几条都由
   * 渲染层 `WidgetFrame` 在校验过 iframe 报文之后代发。
   */
  'widget:getState': { req: { sessionId: string; callId: string }; res: unknown }
  'widget:setState': { req: { sessionId: string; callId: string; state: unknown }; res: void }
  'widget:submit': { req: { callId: string; value: string | null }; res: void }
  'widget:runAction': {
    req: { sessionId: string; callId: string; actionId: string; params: Record<string, unknown>; confirmed: boolean }
    res: WidgetActionPreflight | ({ kind: 'result' } & WidgetActionResult)
  }
  'widget:complete': { req: { sessionId: string; prompt: string; system?: string; maxTokens?: number }; res: string }
  'widget:saveFile': { req: { name: string; data: string; encoding: 'utf8' | 'base64' }; res: { path: string } | null }
  'agent:listTools': { req: { workspaceId: string }; res: ToolInfo[] }

  // ── Agent 的 shell ──
  /**
   * 只停**这一条**正在跑的命令 —— 工具卡片上那颗停止按钮。
   *
   * ★ 和 `agent:abort` 是两件事,不能合并:`agent:abort` 停的是整轮对话
   * (模型写到一半的回复一起没了);这条只掐掉那个跑飞的 `npm test`,run 继续,
   * 模型收到一条「用户停止了这条命令」的**工具失败**并据此换路。
   * 合并的代价用户当场就能看到:想掐一条命令,结果整段回复停在半截。
   *
   * 返回 `false` = 那条命令已经不在跑了(点击与收尾撞在同一刻)。这不是错误,
   * 调用方不必提示 —— 用户要的结果已经达成。
   */
  'shell:stopToolCall': { req: { runId: string; callId: string }; res: boolean }
  /**
   * 把**这一条**正在跑的前台命令转去后台 —— 工具卡片上那颗「转后台」按钮。
   *
   * 命令不被杀,只是不再被等:这次工具调用当场返回(带上到目前为止的输出和一个
   * shell id),命令作为后台 shell 继续跑,模型之后用 `BashOutput` / `KillShell` 管它。
   *
   * 返回 `false` = 没转成:已经跑完了、这个环境转不了(SSH),或后台 shell 名额已满。
   * 与 `shell:stopToolCall` 同理不是错误。
   */
  'shell:backgroundToolCall': { req: { runId: string; callId: string }; res: boolean }
  /**
   * 这个工作区里 agent 开过的后台 shell(含已结束、还没被淘汰的)—— 右上角活动胶囊的
   * 初始数据。之后的变化走 `shell:backgroundChanged`。
   */
  'shell:listBackground': { req: { workspaceId: string }; res: BackgroundShellInfo[] }
  /**
   * 看一眼某个后台 shell 的输出尾巴。★ **不消费**模型那份 `BashOutput` 缓冲 ——
   * 界面看过一眼,模型下次照样读得到(见 `agent-shells.ts` 的 `tail`)。
   * `null` = 它已经被淘汰了,不是错误。
   */
  'shell:peekBackground': { req: { id: string }; res: { info: BackgroundShellInfo; output: string } | null }
  /**
   * 用户在活动面板里停掉一个后台 shell。与模型调 `KillShell` 是同一件事,
   * 状态记成 `killed`,模型下次 `BashOutput` 会读到「被停了」。
   * `null` = 它已经被淘汰了。
   */
  'shell:killBackground': { req: { id: string }; res: BackgroundShellInfo | null }

  // ── 终端 ──
  'terminal:create': { req: TerminalCreateRequest; res: TerminalInfo }
  'terminal:prepare': { req: TerminalCreateRequest; res: TerminalPreparation }
  'terminal:approve': { req: { id: string; approved: boolean }; res: string | null }
  'terminal:kill': { req: { id: string }; res: void }
  'terminal:list': { req: { workspaceId: string }; res: TerminalInfo[] }
  'terminal:getBuffer': { req: { id: string }; res: TerminalBuffer }

  // ── MCP ──
  'mcp:list': { req: void; res: McpServerStatus[] }
  'mcp:upsert': { req: McpServerConfig; res: McpServerStatus }
  'mcp:remove': { req: { id: string }; res: void }
  'mcp:testConnection': { req: { id: string }; res: McpServerStatus }
  /**
   * stdio 的环境变量值 / HTTP 的请求头值。**只写不读**,和 `provider:setCredential`
   * 同一条规矩 —— 回程只说存了哪几个键名(见 `McpSecretsInfo`)。
   *
   * 和 `mcp:upsert` 分开两条频道,而不是把值塞进 `McpServerConfig`:
   * 那个类型会被写进 `mcp_servers.json` 那一列,也会被将来的「导出配置」读到。
   * 值一旦进了这个类型,总有一天会跟着配置一起落到明文里。
   */
  'mcp:setSecrets': { req: { id: string; values: Record<string, string> }; res: McpSecretsInfo }
  'mcp:getSecretsInfo': { req: { id: string }; res: McpSecretsInfo }

  // ── 搜索服务(设置 › 连接 › 搜索服务)──
  /*
    ★ 叫 `websearch:*` 而不是 `search:*`。本仓库已经有一个「搜索」了 ——
    `conversations:searchAll` 那个本地全文检索。两个 `search:` 前缀混在同一张表里,
    下一个人接手时得先读实现才知道哪个是哪个,而频道名恰恰是最该自解释的地方。
  */
  'websearch:list': { req: void; res: SearchProviderStatus[] }
  'websearch:setEnabled': { req: { id: SearchProviderId; enabled: boolean }; res: void }
  /** 拖拽排序的落点。全量给一遍顺序,而不是 `{from,to}` —— 界面已经算好了 */
  'websearch:reorder': { req: { ids: SearchProviderId[] }; res: void }
  /** ★ 只写不读,和 `provider:setCredential` 同一条规矩(方案 §9) */
  'websearch:setCredential': { req: { id: SearchProviderId; apiKey: string }; res: CredentialInfo }
  'websearch:clearCredential': { req: { id: SearchProviderId }; res: void }
  /**
   * 真发一次最小查询。**不返回搜索结果**,只返回通不通 ——
   * 返回结果的话这条频道就成了一个绕过工具链、绕过联网开关的搜索入口。
   */
  'websearch:test': {
    req: { id: SearchProviderId }
    res: { ok: boolean; latencyMs?: number; message?: string }
  }
  /**
   * 测一次免 Key 的内置兜底(公共/自建 SearxNG → 直抓结果页)。
   *
   * 和上面那条同样**不返回搜索结果**,只回通不通 + 是哪个源给的
   * (`source` 是实例域名或引擎名 —— 「可用」两个字帮不了正在排查的用户)。
   */
  'websearch:testBuiltin': {
    req: void
    res: { ok: boolean; latencyMs?: number; source?: string; message?: string }
  }

  // ── 网络代理(设置 › 连接 › 网络)──
  /*
    代理的其余字段都在 `AppSettings.proxy` 里,经 `settings:update` 走 ——
    **只有密码另开频道**。理由和 `provider:setCredential` 一样:`AppSettings`
    是要被 `settings:get` 整个读回渲染层的,密码进了那个类型,就等于每次
    打开设置页都往渲染进程送一次明文。

    回程是 `{hasKey, encryptionAvailable}`,**没有 last4**。API Key 的末四位
    是用来认「我填的是哪一把」的(用户手里往往有好几把);密码只有一个,
    末四位帮不上忙,却实实在在泄了四个字符。
  */
  'proxy:setPassword': { req: { password: string }; res: ProxyPasswordInfo }
  'proxy:clearPassword': { req: void; res: ProxyPasswordInfo }
  'proxy:getPasswordInfo': { req: void; res: ProxyPasswordInfo }

  // ── Skill ──
  'skills:list': { req: { workspaceId?: string }; res: SkillListItem[] }
  'skills:pickZip': { req: void; res: { path: string; name: string } | null }
  'skills:installZip': { req: { path: string; workspaceId?: string; scope?: SkillInstallScope }; res: SkillListItem }
  'skills:installMarket': { req: { slug: string; version?: string; workspaceId?: string; scope?: SkillInstallScope }; res: SkillListItem }
  'skills:uninstall': { req: { skillId: string; workspaceId?: string; scope?: SkillInstallScope }; res: void }
  'skills:marketList': { req: { q?: string; category?: string }; res: SkillMarketItem[] }
  'skills:marketCategories': { req: void; res: string[] }
  'skills:marketDetail': { req: { slug: string }; res: SkillMarketItem & { versions?: Array<{ version: string; changelog?: string; sha256?: string; fileSize?: number }> } }
  'skills:diagnostics': { req: { workspaceId?: string }; res: Array<{ path: string; message: string }> }
  'skills:setGlobalEnabled': { req: { skillId: string; enabled: boolean }; res: void }
  'skills:setWorkspaceActive': {
    req: { skillId: string; workspaceId: string; active: boolean }
    res: void
  }

  // ── 插件 ──
  /*
    ★ **一次取回全量 catalog,不做增量。** 插件数量是个位数,而增量协议要多
      一套「我这份是不是过期了」的判断 —— 那正是 `mcp:changed` 当初踩过的坑
      (状态变化推了、但推的是一条没有内容的通知,渲染层要自己再拉一次)。
      这里干脆让每个写操作都返回新的 catalog,渲染层直接换掉手里那份。
  */
  'plugins:list': { req: void; res: PluginCatalog }
  'plugins:setEnabled': { req: { pluginId: string; enabled: boolean }; res: PluginCatalog }
  'plugins:uninstall': { req: { pluginId: string }; res: PluginCatalog }
  'plugins:pickPackage': { req: void; res: { path: string; name: string } | null }
  'plugins:installPackage': { req: { path: string; scope?: 'global' | 'workspace'; workspaceId?: string }; res: PluginCatalog }
  /** 安装/升级时逐条勾选的结果。**越过清单上界的那些会被丢掉**,见 permission.ts */
  'plugins:grantPermissions': { req: { pluginId: string; permissions: PluginPermission[] }; res: PluginCatalog }
  'plugins:revokePermissions': { req: { pluginId: string; permissions: PluginPermission[] }; res: PluginCatalog }
  /** 活动日志(环形缓冲)。插件详情页的「活动」标签直接展示 */
  'plugins:activity': { req: { pluginId?: string }; res: PluginActivity[] }
  /** 执行一条插件命令 —— 菜单项、命令面板、快捷键三处共用 */
  /** args:调用点上下文(目前只有 `+` 菜单附带的 workspaceId),随 command.run 透传给插件 handler */
  'plugins:runCommand': { req: { pluginId: string; commandId: string; args?: Record<string, unknown> }; res: void }
  /**
   * 打开插件自定义编辑器的 Tab 之前,请主进程按 `onCustomEditor:<viewType>`
   * 唤醒该插件。★ 不是权限检查:插件视图的静态文件只对「已被唤醒过」的
   * 插件可服务(协议层的 roots 表在 spawn 时才填),不先唤醒,iframe 会
   * 收到 403 —— 症状是 Tab 里一片 "forbidden",且零报错。
   */
  'plugins:activateEditor': { req: { pluginId: string; viewType: string }; res: { activated: boolean } }
  /**
   * 用户点了实时工具卡片上的按钮 —— 送给仍在运行的那次工具调用(第 2 层交互)。
   * 即发即忘:动作到不了(工具已结束 / callId 不属于该插件)时静默丢弃。
   */
  'plugins:cardAction': { req: { pluginId: string; callId: string; actionId: string; value?: unknown }; res: void }
  /**
   * 用户回答了 `plugins:interaction` 的那一问。
   *
   * ★ `value` 的形状由 `request.kind` 决定:quickPick → 选中的 id(`string`),
   * input → 输入的文本(`string`),confirm → `boolean`。取消一律是 `null`
   * (confirm 取消是 `false`)。主进程侧会再核一次(quickPick 的回执必须是
   * 给出去的那几个 id 之一),所以渲染层的这一条不是安全边界。
   */
  'plugins:interactionReply': { req: { requestId: string; value: unknown }; res: void }
  /** 用户点了侧边栏上某个插件网页应用的入口。 */
  'plugins:openWebApp': { req: { pluginId: string; webAppId: string }; res: void }
  /**
   * 插件的自定义编辑器视图报告「我有没有没存的东西」。
   *
   * ★ 这条是**宿主代发**的:视图 iframe 够不着插件自己那条 RPC 通道
   * (`__runtime.js` 只服务于隐藏的宿主页面),而 `plugins:confirmClose` 的
   * 挽留判断全靠这张表。没有它,插件编辑器里没存的改动会在关 Tab 时静默消失。
   */
  'plugins:setEditorDirty': {
    req: { pluginId: string; documentId: string; path: string; dirty: boolean }
    res: void
  }
  /**
   * 设置项的读写。**独立 kv,不扩 `AppSettings`** —— 那份 blob 是全应用的,
   * 每次写都整份重写、整份同步,一个插件的开关不该有那个代价。
   */
  /**
   * 关掉一个文件 / 工作区之前先问一遍插件编辑器。
   *
   * ★ 返回 `false` = **别关**。不接这条的话,插件编辑器里没存的改动会在
   * 关 Tab 的那一刻静默消失 —— 而内置文档的挽留(`confirmDocumentChanges`)
   * 完全看不见它们。
   */
  'plugins:confirmClose': { req: { path?: string }; res: { safe: boolean } }
  // ── 插件市场 ──
  'plugins:marketList': { req: { q?: string; category?: string; page?: number }; res: PluginMarketPage }
  'plugins:marketCategories': { req: void; res: string[] }
  'plugins:marketDetail': { req: { slug: string }; res: PluginMarketItem & { versions?: { version: string; changelog?: string; permissionEscalated?: boolean }[] } }
  'plugins:installMarket': { req: { slug: string; version?: string }; res: PluginCatalog }
  /**
   * 已装的插件里有哪些能更新。
   *
   * ★ 判定整个在主进程完成(见 `PluginUpdate` 的注释)。`force` 绕过市场
   * 列表的 TTL 缓存 —— 缓存的是**网络结果**,不是算好的这张表,所以更新完
   * 一个插件之后重算,计数立刻就减下去了,不必等 TTL 过期。
   */
  'plugins:checkUpdates': { req: { force?: boolean }; res: PluginUpdate[] }
  /**
   * 把所有**市场来源**的可更新插件挨个更新掉。**串行**,失败不中断。
   *
   * ★ 串行不是保守:`PluginManager.records` 和 KV 的 read-modify-write 之间
   * 没有锁,两次 `install()` 交叠会互相踩。顺带也避免了 N 个 20MB 的包
   * 同时躺在主进程内存里。
   */
  'plugins:updateAll': { req: void; res: PluginUpdateResult }
  'plugins:getConfiguration': { req: { pluginId: string }; res: Record<string, boolean | string | number> }
  'plugins:setConfiguration': {
    req: { pluginId: string; key: string; value: boolean | string | number | null }
    res: Record<string, boolean | string | number>
  }

  // ── 文档引擎编辑器画布(形状与不变式见 `shared/document-engine/view.ts`)──
  /*
    ★ `open` 的 workspace / path / plugin / viewType 由渲染层从 **Tab 绑定**取,不是插件视图报的;
    之后只凭 token。token 在主进程侧绑定发起窗口,别的窗口拿着也用不了。
  */
  'documentEngine:open': { req: DocumentViewOpenRequest; res: DocumentViewOpened }
  'documentEngine:render': { req: DocumentViewRenderRequest; res: DocumentViewRenderResult }
  'documentEngine:input': { req: DocumentViewInputRequest; res: DocumentViewInputResult }
  'documentEngine:command': { req: DocumentViewCommandRequest; res: DocumentViewInputResult }
  'documentEngine:list': { req: DocumentViewListRequest; res: DocumentViewList }
  'documentEngine:headers': { req: DocumentViewHeadersRequest; res: DocumentViewHeaders }
  'documentEngine:layout': { req: DocumentViewLayoutRequest; res: DocumentViewLayout }
  'documentEngine:state': { req: DocumentViewTokenRequest; res: DocumentViewState }
  'documentEngine:save': { req: DocumentViewTokenRequest; res: DocumentViewState }
  'documentEngine:close': { req: DocumentViewTokenRequest; res: void }

  // ── 斜杠命令(`/命令`)──
  /*
    ★ **正文一起下发**,不另开一条「取正文」的频道。展开发生在发送前的渲染层
    (见 `shared/domain/command.ts` 文件头),要正文才能展开;而命令总共就那么
    几条、每条几 KB,渐进披露在这里只会换来一次多余的往返和一个新的失败态。
  */
  'commands:list': { req: { workspaceId?: string }; res: CommandDefinition[] }
  'commands:diagnostics': { req: { workspaceId?: string }; res: Array<{ path: string; message: string }> }

  /*
    扩展面板里的命令 / 子代理管理。

    ★ `commands:list` 和 `commands:listAll` 是**两个**频道，不是一个带参数的：
    前者给运行期用（输入框的 `/` 弹层），已经滤掉关掉的那些；后者给管理界面用，
    关掉的也要列出来，否则用户没法再把它打开。合成一个 `includeDisabled` 参数的话，
    调用点漏传一次就会把禁用的命令重新喂给模型 —— 那种 bug 不会报错，只会「失效了」。
  */
  'commands:listAll': { req: { workspaceId?: string }; res: CommandListItem[] }
  'commands:setEnabled': { req: { name: string; enabled: boolean }; res: void }
  'agents:list': { req: { workspaceId?: string }; res: AgentListItem[] }
  'agents:diagnostics': { req: { workspaceId?: string }; res: Array<{ path: string; message: string }> }
  'agents:setEnabled': { req: { name: string; enabled: boolean }; res: void }
  /*
    「AI 生成一个子代理」。产出**草稿**,不落盘 —— 生成完先填进表单等人过目,
    要存还得走 `resource:save`。直接写文件的话,一次误点就在用户的 agents 目录里
    留下一个他没看过的文件,而这些文件是会进每一轮系统提示词的。
  */
  'agents:generate': { req: { requirement: string; workspaceId?: string }; res: AgentDraft }

  'modes:list': {
    req: { workspaceId: string }
    res: { modes: readonly ModeDefinition[]; diagnostics: ReadonlyArray<{ path: string; message: string }>; tools: readonly string[] }
  }

  /* 命令、子代理和模式共用这三个 —— 磁盘上它们是同构的，见 shared/domain/markdown-resource.ts。 */
  'resource:get': {
    req: { kind: MarkdownResourceKind; scope: MarkdownResourceScope; name: string; workspaceId?: string }
    res: MarkdownResourceFile
  }
  'resource:save': { req: MarkdownResourceSave; res: MarkdownResourceFile }
  'resource:delete': {
    req: { kind: MarkdownResourceKind; scope: MarkdownResourceScope; name: string; workspaceId?: string }
    res: void
  }

  // ── 钩子（`settings.local.json` / `<appData>/settings.json` 的 hooks 段）──
  'hooks:list': { req: { workspaceId?: string }; res: HookListItem[] }
  'hooks:diagnostics': { req: { workspaceId?: string }; res: HookDiagnostic[] }
  /** 按 id upsert。id 省略 = 新建，主进程铸 ULID 并回传。 */
  'hooks:save': {
    req: { scope: HookScope; workspaceId?: string; hook: HookUpsert }
    res: HookListItem
  }
  'hooks:delete': { req: { scope: HookScope; workspaceId?: string; id: string }; res: void }
  'hooks:setEnabled': { req: { scope: HookScope; workspaceId?: string; id: string; enabled: boolean }; res: void }
  /** 试运行。★ 跑的是弹层里此刻的草稿，不读磁盘上那一条。 */
  'hooks:test': {
    req: { workspaceId?: string; scope: HookScope; event: HookEvent; timeoutMs: number }
      & ({ type?: 'command'; command: string } | { type: 'prompt'; prompt: string })
    res: HookRunReport
  }

  // ── 会话目标（goal）──
  //
  // ★ **不走 `agent:event`**。那条流是 run 生命周期内的（有 seq、有 attach 补齐语义），
  //   而目标的寿命比 run 长：设目标时可能根本没有 run 在跑，run 结束后目标还活着。
  //   塞进 `agent:event` 会让 `agent:attach` 的 seq 补齐多出一种
  //   「这条事件不属于任何 run」的分支。
  'goal:get': { req: { sessionId: string }; res: ActiveGoal | undefined }
  /** 设立 / 覆盖。条件为空或超长时不设立，回一条说得清的理由。 */
  'goal:set': {
    req: { sessionId: string; condition: string }
    /**
     * `kickoff` 是要注入给主模型的那条 **internal** 消息。
     *
     * ★ 由主进程给，不在渲染层拼：它是一段**进模型上下文**的英文 prompt
     *   （措辞是被调过的，见 `main/goal/prompt.ts` 文件头）。在渲染层拼的话
     *   它会被当成 UI 文案而走进 i18n，那等于换掉一个判定器的行为。
     */
    res:
      | { ok: true; goal: ActiveGoal; kickoff: ContentPart[] }
      | { ok: false; reason: 'empty' | 'too_long'; length: number }
  }
  'goal:clear': { req: { sessionId: string }; res: void }



  // ── 供应商 / 模型别名 ──
  // ── 视频生成任务(见下面未实现清单上那段:它不走 agent:event)──
  'video:listBySession': { req: { sessionId: string }; res: VideoJobView[] }
  'video:get': { req: { id: string }; res: VideoJobView | undefined }
  'video:cancel': { req: { id: string }; res: { ok: true; state: 'requested' | 'already-done' } | { ok: false; reason: string } }
  'video:retryRetrieval': { req: { id: string }; res: { ok: boolean; reason?: string } }
  'video:saveFile': { req: { id: string; defaultName?: string }; res: { path: string } | null }

  'provider:list': { req: void; res: UpstreamProvider[] }
  /** 解析本机 Claude Code / Codex / OpenCode 里配置的提供商供审核。★ 只回元数据,不回密钥值。 */
  'providers:listImportable': { req: { sourceKind: ImportSourceKind; pickedDir?: string }; res: ImportableProviders }
  'provider:upsert': { req: UpstreamProvider; res: UpstreamProvider }
  'provider:remove': { req: { id: string }; res: void }
  /** ★ 常规状态只回 { hasKey, last4 };明文仅经 revealCredential 显式返回 */
  'provider:setCredential': { req: { providerId: string; apiKey: string }; res: CredentialInfo }
  'provider:getCredentialInfo': { req: { providerId: string }; res: CredentialInfo }
  /**
   * ★ 这是唯一允许把凭证明文送到渲染层的通道,只供用户显式点击「查看」调用。
   * 禁止列表、初始化、轮询或广播复用它 —— 否则密钥会常驻每个窗口的状态树。
   */
  'provider:revealCredential': { req: { providerId: string }; res: RevealedCredential }
  'provider:export': { req: { includeCredentials?: boolean; password?: string }; res: { path: string; encrypted: boolean; providerCount: number; aliasCount: number } | null }
  'provider:import': { req: { password?: string }; res: ({ path: string } & ProviderImportResult) | null }
  /**
   * 走一遍账号登录(OAuth 授权码 + PKCE),成功后凭证落进和 API Key 同一个槽。
   *
   * ★★ **这是一条「长 invoke」**:里面要打开浏览器、等用户授权,最长 5 分钟。
   * 做成 invoke 而不是「send + 纯事件」,是因为界面上那颗按钮需要一个**确定的终态**
   * 才能退出 loading —— 而 `IpcResult` 信封顺带把失败原因免费带回来了。
   * 中间进度靠 `provider:authProgress` 补,终态靠这条的返回值。
   *
   * ★ **不复用 `provider:setCredential`。** 那条频道的契约是「写入一把 key、回状态摘要」,
   * 让它同时表示「打开浏览器走一遍 OAuth」,等于让一条安全敏感频道做两件不相干的事,
   * 而它的校验只按其中一件写过。
   */
  'provider:startOAuth': { req: { providerId: string }; res: CredentialInfo }
  'provider:cancelOAuth': { req: { providerId: string }; res: void }
  /** 手动粘贴授权码的那种形态(部分厂商的授权页把 code 显示出来让用户复制) */
  'provider:submitOAuthCode': {
    req: { providerId: string; code: string }
    res: CredentialInfo
  }
  /** 退出登录。★ 一定成功 —— 删除现有密文不需要主密钥,见 `signOut` 的注释 */
  'provider:signOut': { req: { providerId: string }; res: CredentialInfo }

  // ── 供应商账号(OAuth 多账号,schema 第 24 条) ──
  /**
   * ★★ **每一条写频道都回整份账号列表,不回增量。**
   *
   * 渲染层于是永远不需要自己合并。回增量的话,「拖拽排序写成功了但列表还是旧顺序」
   * 这类问题只在两次写挨得很近时出现 —— 而拖拽恰好是最容易连着写两次的操作。
   *
   * ★ 列表里**一个 token 字符都没有**(`ProviderAccount` 只有元数据 + 登录态摘要),
   * 和 `CredentialInfo` 同一条规矩:明文只经 `provider:revealCredential` 单次返回。
   */
  'provider:listAccounts': { req: { providerId: string }; res: ProviderAccount[] }
  /**
   * 新增一个账号 = 走一遍完整登录。**和 `provider:startOAuth` 同属「长 invoke」**,
   * 中间进度照样走 `provider:authProgress`。
   *
   * ★ 不复用 `startOAuth`:那条的语义是「把这家登录成(替换当前凭证)」,
   * 老渲染层和导入路径都依赖它。多一个账号是另一件事,分开之后两边的契约
   * 各自成立 —— 而合并的话,`startOAuth` 会在老界面上悄悄变成「每点一次多一个号」。
   */
  'provider:addAccount': { req: { providerId: string }; res: ProviderAccount[] }
  /** 这条登录失效了,重新登录它(沿用同一条账号行与顺序) */
  'provider:reauthAccount': { req: { providerId: string; accountId: string }; res: ProviderAccount[] }
  /** ★ 连带删掉它的密文,见 `db/provider-accounts.ts` 的不变式 */
  'provider:removeAccount': { req: { providerId: string; accountId: string }; res: ProviderAccount[] }
  'provider:setAccountEnabled': {
    req: { providerId: string; accountId: string; enabled: boolean }
    res: ProviderAccount[]
  }
  /** 备注名。空串 = 清掉,显示回落到邮箱 / 上游 id */
  'provider:setAccountLabel': {
    req: { providerId: string; accountId: string; label: string }
    res: ProviderAccount[]
  }
  /**
   * 「设为当前账号」。★ 它决定的是**镜像回旧槽的是谁**和界面上那个标记,
   * 不改变轮换顺序(轮换看 `order`,见 `selectAccount` 那条注释)。
   */
  'provider:setCurrentAccount': { req: { providerId: string; accountId: string }; res: ProviderAccount[] }
  /** 拖拽排序:整批写。`accountIds` 的下标即新的 `order` */
  'provider:reorderAccounts': { req: { providerId: string; accountIds: string[] }; res: ProviderAccount[] }
  /** 「立即解除限流」。用户升级了套餐、或者我们判早了 —— 不该让他干等 */
  'provider:clearAccountLimit': { req: { providerId: string; accountId: string }; res: ProviderAccount[] }
  /**
   * 主动拉一次这个账号的**订阅额度**(GLM Coding Plan,见
   * `kernel/upstream/coding-plan-quota.ts` 的端点注释)。真发一次网络请求,
   * 没有密钥 / 这家不是订阅制 / 上游拒绝都会抛。
   *
   * ★ 回整份账号列表 —— 快照写进了账号行的 `quota`,和其它账号写频道共用
   * 「渲染层不需要自己合并」那条约定。设置页和圆环菜单因此读同一份数据。
   */
  'provider:fetchQuota': { req: { providerId: string; accountId: string }; res: ProviderAccount[] }

  'provider:listModels': { req: { providerId?: string }; res: ModelAlias[] }
  /**
   * ★★ **叫 `fetchModels` 而不是 `listModels`,因为那个名字已经被上面那条占了 ——
   * 而且占的是相反的意思。** 上面那条列的是「本地配置过的别名」,这条是
   * 「去问上游它有哪些模型」。参考文档 `docs/ipc-protocol.md:337` 恰好用
   * `listModels` 表示后者,照抄过来就会得到两个同名、语义相反的频道。
   *
   * 真发一次网络请求,所以是 async 且可能失败(密钥没填 / 这家没有列表端点)。
   */
  'provider:fetchModels': { req: { providerId: string }; res: FetchedModel[] }
  /**
   * 整表替换这家的模型别名。
   *
   * ★ **是「替换」不是「追加」,因为导入弹窗的语义就是替换** ——
   * 参考图那句「取消勾选会从当前列表删除」和按钮上的「更新列表(17/20)」
   * 说的是同一件事:弹窗关掉之后,这家的别名 = 当时勾着的那些,一个不多一个不少。
   * 拆成 upsert/remove 两条的话,渲染层得自己算差集,而算错的表现是
   * 「取消勾选了但它还在」—— 一个没人会去 diff 的静默错。
   *
   * `models` 是**上游真实模型名**(`FetchedModel.id`),顺序即别名表顺序。
   * 上限 `MAX_ALIASES_PER_PROVIDER`,主进程照样拒绝(不只是界面置灰)。
   */
  'provider:setAliases': { req: { providerId: string; models: string[] }; res: ModelAlias[] }
  'provider:test': { req: { providerId: string }; res: { ok: boolean; latencyMs?: number } }
  /** Model management console writes. */
  'model:update': { req: import('../domain/provider').ModelAlias; res: import('../domain/provider').ModelAlias }
  'model:rename': { req: { providerId: string; alias: string; nextAlias: string }; res: import('../domain/provider').ModelAlias }
  'model:remove': { req: { providerId: string; alias: string }; res: void }
  /** User-created catalogue rows. Provider model discovery never writes here. */
  'modelCatalog:list': { req: void; res: ModelCatalogDefinition[] }
  'modelCatalog:upsert': { req: ModelCatalogDefinition; res: ModelCatalogDefinition }
  'modelCatalog:remove': { req: { id: string }; res: void }
  /** models.dev 模型目录同步:只新增目录里没有的;apply 取主进程缓存的预览,界面传不来任意数据。 */
  'modelCatalog:syncPreview': { req: void; res: ModelCatalogSyncPreview }
  'modelCatalog:syncApply': { req: { ids: string[] }; res: { applied: number } }

  /** models.dev 定价同步。预览不落盘;apply 只采用用户勾选的条目,价格取主进程缓存的那次预览。 */
  'pricing:listSynced': { req: void; res: ModelPricing[] }
  'pricing:syncPreview': { req: void; res: PricingSyncPreview }
  'pricing:syncApply': { req: { modelIds: string[] }; res: { applied: number } }
  'pricing:clearSynced': { req: void; res: void }

  // ── 使用统计 ──
  'usage:getSummary': { req: UsageWindow; res: UsageSummary }
  'usage:getRequestLogs': { req: UsageRequestLogsQuery; res: UsageRequestLogsPage }
  /** 当前会话的逐次账目；不能用搜索日志代替，搜索不是 session_id 精确匹配。 */
  'usage:getSessionAttempts': { req: { sessionId: string }; res: UsageAttemptRecord[] }
  'usage:getProviderStats': { req: UsageWindow; res: UsageDimensionStat[] }
  'usage:getModelStats': { req: UsageWindow; res: UsageDimensionStat[] }
  /**
   * 概览区的两条。★ handler 会先跑一次汇总刷新再查 —— 所以它们比上面四条慢,
   * 别放进任何每秒轮询的路径。
   */
  'usage:getDailySeries': { req: UsageWindow; res: UsageDailyBucket[] }
  /** 不带时间窗:「最长连续天数」按定义就是问全部历史。 */
  'usage:getActivityStats': { req: void; res: UsageActivityStats }

  // ── 本地网关 ──
  'gateway:getStatus': { req: void; res: GatewayStatus }
  'gateway:getKey': { req: void; res: string }
  'gateway:getAddresses': { req: void; res: string[] }
  'gateway:setEnabled': { req: { enabled: boolean }; res: GatewayStatus }
  'gateway:resetHealth': { req: { providerId?: string }; res: GatewayStatus }

  // ── 数据(界面「数据」页) ──
  'storage:getStats': { req: void; res: StorageStats }
  'storage:vacuum': { req: void; res: StorageStats }
  'storage:openDataDirectory': { req: void; res: void }
  'storage:export': { req: { includeEncryptedKeys?: boolean; password?: string }; res: { path: string; encrypted: boolean; bytes: number } | null }
  'storage:importPreview': { req: void; res: ImportPreview | null }
  'storage:importApply': { req: { password?: string }; res: ImportApplyResult }
  'storage:chooseBackupDirectory': { req: void; res: string | null }
  'storage:getBackupStatus': { req: void; res: BackupStatus }
  'storage:createBackup': { req: { manual?: boolean }; res: BackupStatus }
  'storage:restoreBackup': { req: { confirm?: boolean }; res: RestoreResult | null }
  'storage:cleanupPreview': { req: { kind: 'attachments' | 'age' | 'history' | 'local-data'; age?: CleanupAge }; res: CleanupPreview }
  'storage:cleanupAttachments': { req: void; res: CleanupResult }
  'storage:cleanupByAge': { req: { age: CleanupAge }; res: CleanupResult }
  'storage:clearHistory': { req: void; res: CleanupResult }
  'storage:clearLocalData': { req: { confirm: boolean }; res: { deleted: boolean } }

  // ── 启动迁移闸门(见 shared/domain/data-migration.ts) ──
  /*
    ★ 这五条是**数据库打开之前唯一能调用的业务 IPC**（无状态窗口控制除外）。

    它们必须能通,因为闸门存在的那段时间里数据库还没打开 —— 渲染层这时候只有
    一个窗口、一屏进度,别的什么都调不了(这也是「阻止用户使用」的实现方式:
    不是禁用控件,而是后端根本没起来)。

    ★ 所以这五条的实现里**一个字节的库读写都不能有**。谁哪天顺手往
    `dataMigration:getState` 里加一句 `store.getSettings()`,表现就是迁移期间
    主进程先开了一个内存兜底库,随后 `openDatabase()` 直接抛错、启动失败。
  */
  'dataMigration:getState': { req: void; res: MigrationState }
  /** 从失败的那一步继续。已完成的步骤不重跑(每一步自己判前置条件,天然幂等)。 */
  'dataMigration:retry': { req: void; res: MigrationState }
  /**
   * 放弃迁移并继续启动。★ 库此时是**完整**的(合并按会话提交,只 INSERT),
   * 所以这条路不会开出一个坏库,只是少了一部分旧数据。
   */
  'dataMigration:skip': { req: void; res: MigrationState }
  /**
   * 撤销本次启动合并进去的行。只删本次记在清单里的 id —— 不是「恢复到某个快照」,
   * 所以合并之后新产生的会话不受影响。
   */
  'dataMigration:undoMerge': { req: void; res: MigrationState }
  /**
   * 打开数据目录。★ 走主进程,渲染层拿不到也传不了路径。
   *
   * 为什么闸门要自己有一条而不是复用 `storage:openDataDirectory`:那条是
   * **数据库 handler**,而闸门存在的意义正是「数据库还没打开」。
   */
  'dataMigration:openDataDirectory': { req: void; res: void }

  // ── 从其他 AI 应用导入(设置 › 导入) ──
  /*
    ★ 频道名是 `imports:*`(复数),而上面「数据」页那三条是 `storage:import*` ——
    两者是**两个功能**:那边导入的是 NextCoWork 自己的整库备份,这边是外部来源。
    共用前缀的话,下一个人接手时得先读实现才知道哪个是哪个,而 `storage:importApply`
    会合并全套设置、按时间戳覆盖,误用一次的代价是用户配置被一份外部数据盖掉。

    ★ 读与写分得很开:detect/getState/preview/previewItems/history/historyItems
    都是纯读,只有 chooseSource(要弹系统对话框)、apply、cancel、updateSync、
    syncNow、resolveConflict 会改状态。
  */
  /** 探测本机来源。不弹对话框,找不到就如实回 not-found。 */
  'imports:detect': { req: { sourceKind: ImportSourceKind }; res: ImportSourceState }
  /**
   * 让用户自己指定配置目录。★ 走主进程 dialog.showOpenDialog ——
   * 渲染层永不指定任意路径(方案 §9)。取消返回当前状态,不报错。
   *
   * 这条入口不能省:Finder 启动的 Electron 没有 shell 环境变量,
   * `CLAUDE_CONFIG_DIR` 在那种启动方式下读不到。
   */
  'imports:chooseSource': { req: { sourceKind: ImportSourceKind }; res: ImportSourceState }
  'imports:getState': { req: { sourceId: string }; res: ImportSourceState }
  /**
   * 扫描并产出不可变快照。★ 返回的是**句柄 + 计数**,不是全部条目 ——
   * 条目走 `imports:previewItems` 分页取(见 `ImportPreview` 的注释)。
   */
  'imports:preview': { req: { sourceId: string; requestId: string }; res: SourceImportPreview }
  'imports:previewItems': { req: ImportPreviewQuery; res: ImportPreviewPage }
  /** 提交。只接受快照内的 item id(见 `ImportApplyRequest`)。 */
  'imports:apply': { req: ImportApplyRequest; res: ImportJobStatus }
  'imports:status': { req: { sourceId: string }; res: ImportJobStatus | null }
  /** 在单项原子提交边界生效:保留已成功项,记 cancelled。 */
  'imports:cancel': { req: { jobId: string }; res: void }
  'imports:history': { req: { offset: number; limit: number }; res: ImportHistoryPage }
  'imports:historyItems': { req: { batchId: string; offset: number; limit: number }; res: ImportBatchItemsPage }
  /** 同步开关与已授权范围。★ 只影响后续扫描,不删已导入数据。 */
  'imports:updateSync': { req: { sourceId: string; patch: ImportSyncPatch }; res: ImportSourceState }
  'imports:syncNow': { req: { sourceId: string }; res: ImportJobStatus }
  'imports:resolveConflict': { req: ImportConflictResolution; res: void }
}

// ═══════════════════════════════════════════════════════════════
// 三、渲染 → 主,高频无返回(send)—— 协议 §3.2
// ═══════════════════════════════════════════════════════════════

/**
 * ★ 这张表存在的理由就是终端(方案 §6):每次按键做一次请求/响应往返,
 * 在快速输入下会肉眼可见地卡。
 */
export interface IpcSendMap {
  'terminal:write': { id: string; data: string }
  'terminal:resize': { id: string; cols: number; rows: number }

  /** 握手第一步(协议 §7):window:ready → app:getBootstrap → 首屏 → 增量事件 */
  'window:ready': { kind: WindowKind }

  /**
   * Windows/Linux 自绘的最小化 / 最大化 / 关闭。三颗按钮画在渲染层
   * (`shell/WindowControls.tsx`),这里只把动作递给主进程。
   *
   * ★ `close` 走 `win.close()`,而这个托盘应用的 `win.on('close')` 把它拦成隐藏
   *   —— 语义和过去那三颗系统按钮完全一致,渲染层不必知道这件事。
   *
   * 用 send 不用 invoke:没有返回值。**最大化状态不靠这条的回执**,靠
   * `window:maximized` 回推 —— 用户还能拖窗口边缘、双击拖动区、按 Win+↑ 改变它。
   * macOS 不发这条(那边是 hiddenInset 红绿灯)。
   */
  'window:control': { action: 'minimize' | 'toggleMaximize' | 'close' }

  /**
   * 「这次退出我确认了,别让未保存的文件再挡一次」—— 渲染层在用户于
   * `DocumentDialogs` 里选完「保存 / 丢弃」之后发。
   *
   * ★ **没有它,Cmd+Q 会退不掉。** 退出流程先关窗,关窗会走渲染层的
   * `beforeunload`;那个处理器看到有未保存的改动就顶住 unload,整次退出因此
   * 作废(理由见 `main/quit-flow.ts` 文件头)。用户随后在应用自己的对话框里选
   * 「丢弃」—— 那正是「退出」这个意图的延续,所以渲染层重发一次这条消息,
   * 这一轮里它的 `allowUnload` 已经是 true,不会再被顶。
   *
   * 用 send 不用 invoke:没有返回值,调用方也不该等它。
   */
  'app:quitConfirmed': Record<string, never>

  /**
   * Tab 布局持久化。★ 用 send 不用 invoke:拖动排序时每帧都在变,
   * 而渲染层不需要任何返回值。主进程侧防抖 500ms 再落 kv 表(方案 §9)。
   */
  'tabs:persistOuter': { kind: WindowKind; state: WindowTabState }
  'tabs:persistInner': { workspaceId: string; state: InnerTabState }

  /**
   * 草稿落盘。主进程侧防抖 500ms(`immediate` 跳过防抖,用于草稿 Tab 绑定会话、撤回到输入框这类
   * 离散动作)。
   *
   * ★ **只有草稿,不带队列。** 队列由主进程独占(见 `session:queue`);渲染层整份覆盖的话,
   * 一次迟到的草稿防抖会把主进程刚刚消费掉的排队消息写回去,同一句话被续跑两次。
   * 强杀(kill -9)仍拦不住那 500ms 的草稿窗口 —— 代价是半个词。
   */
  'session:persistDraft': {
    sessionId: string
    draft: string
    immediate: boolean
  }
}

// ═══════════════════════════════════════════════════════════════
// 四、主 → 渲染,定向推送(协议 §7)
// ═══════════════════════════════════════════════════════════════

/**
 * ★ 事件按订阅定向推送,**绝不广播**(方案 §3 规则 5)。
 * WindowRegistry 维护 runId → Set<webContents>;⌥Space 快捷窗不该收到主窗的 token 流。
 * 每次 send 前判 webContents.isDestroyed()。
 */
export interface IpcEventMap {
  'connection:status': ConnectionStatus
  'connection:changed': void
  'connection:auth': SshAuthRequest
  'agent:event': AgentEventEnvelope
  /**
   * 「现在还有哪些顶层 run 活着」的**权威集合**,每次有 run 起止就全量广播一次。
   *
   * 需求:渲染层那三处运行中指示(外层工作区 Tab、内层对话 Tab、侧边栏会话行)
   * 读的是渲染层自己的一份投影,而那份投影原先**只能**靠 `agent:event` 里的
   * `run_end` 收敛。那条流是按 run 订阅定向推送的,于是有两类 run 的结束永远送不到:
   * 定时任务起的 run(没有任何窗口订阅过它,`RunPump.flush()` 整批丢弃),
   * 以及 ⌘R 重载后被 bootstrap 补回索引、但对应会话 store 还没建出来的那些。
   * 不满足会怎样:**Agent 早就结束了,工作区 Tab 上的圆点还在转**,
   * 状态行写着「已完成」,重启应用才消 —— 且全程零报错。
   *
   * ★ 走 `emitToAll` 而不是 run 主题:谁在跑是**全局事实**,不是「我这个窗口在看的那个 run」。
   *   它带不了任何转录内容(那仍然只发给订阅者),所以广播不泄露任何东西。
   * ★ 全量而不是增量:增量要求每条都不丢,而这条广播存在的全部意义就是
   *   **能在丢过消息之后自己收敛**。
   */
  'agent:activeRuns': { runs: ActiveRunEntry[] }
  /**
   * 某条会话的排队消息变了(入队、插话、续跑消费、送达回执)。**全量 + 全局广播**:
   * 队列是「这条会话还欠着什么」的事实,与谁在看哪个 run 无关,且条数有上限(20 条)。
   * `rev` 单调递增,渲染层只收比手上更新的那份 —— 它与 `session:queue` 的回执可能乱序到达。
   */
  'session:queueChanged': SessionQueueSnapshot
  /** 后台子代理的结果交回主代理到了哪一步。卡片上的「待汇报 / 已汇报」读它 */
  'session:subagentReport': { sessionId: string; callId: string; status: SubagentReportStatus }
  /**
   * 某条会话的输入(草稿 + 队列)这一次落盘成没成。**全量 + 全局广播**:
   * 「这条会话的话还没保住」是应用级事实,与谁在看哪个 run 无关。
   *
   * ★ 载荷里**没有异常**:见 `SessionInputPersistence` 的注释。
   * ★ 生产端是 `session-runtime` 的 `onPersistenceChange`(经 `services/agent.ts`
   *   的投递端口转发),渲染层据此弹 / 撤一条持久错误 toast。
   */
  'session:inputPersistence': SessionInputPersistence
  'terminal:data': { id: string; seq: number; chunk: string }
  'terminal:exit': { id: string; code: number }
  /**
   * 最大化状态。**每窗口一份**,所以走 `windows.emitTo` 定向推 —— 它被登记进
   * `TargetedEventChannel`,`emitToAll` 在编译期就拿不到它。自绘的中间那颗按钮
   * (`shell/WindowControls.tsx`)据此在「□」和「双叠框」之间换字形。
   */
  'window:maximized': { maximized: boolean }
  /**
   * 这个窗口被最小化 / 隐藏(`false`)或者又露出来了(`true`)。**每窗口一份**,定向推。
   *
   * ★ `false` 发出之前主进程已经摘掉了它对所有 run 正文的订阅 —— 藏着的窗口
   * 不再接收、也不再累积任何正文。`true` 时渲染层按快照 + 历史重建它正在看的会话。
   * 失焦不算:副屏上还开着的窗口仍然要看见新内容。
   */
  'window:visibility': { visible: boolean }
  'gateway:status': GatewayStatus
  'gateway:failover': FailoverEvent
  /**
   * 视频任务状态变化。**全量广播**(谁在跑视频是应用级事实,不是"我这个窗口
   * 在看的那一个"),带一个单调递增的 revision,渲染层据此做增量合并。
   *
   * ★ 与 `agent:activeRuns` 同一个理由:它必须能在丢过消息之后自己收敛 ——
   * 而渲染层还可以用 `video:listBySession` 主动补齐。
   */
  'video:jobChanged': VideoJobView
  /**
   * 后台 shell 列表变了(新开、退出、被杀、被淘汰)。**全量 + 全局广播**,
   * 理由同 `agent:activeRuns`:丢过一条之后下一条自己收敛。渲染层按 `workspaceId` 自己筛。
   * ★ 输出增长不发 —— 刷日志的进程会把这条频道淹没,输出由面板展开时自己去 peek。
   */
  'shell:backgroundChanged': { shells: BackgroundShellInfo[] }
  'settings:changed': AppSettings
  'theme:changed': { resolved: ResolvedTheme }
  'theme:libraryChanged': { profiles: ThemeProfile[]; images: ImageTheme[] }
  'workspace:changed': { workspaces: Workspace[] }
  'skills:changed': void
  /**
   * 装一条 Skill 走到哪一步了。形状与 `plugins:installProgress` 严格一致 ——
   * 两边的渲染层共用同一个 `installLabel` / `installRatio`,字段一分叉,
   * 共用的那份就得改成两套。
   *
   * ★ 同样走全局广播:装 Skill 是全局副作用,B 窗口的市场页也该看到那颗
   * 按钮在跑。代价一样 —— 渲染层基于 `key` 的渲染必须是纯查表。
   */
  'skills:installProgress': {
    /** `market:<slug>` 或 `local:<路径>`,同插件那边的规矩 */
    key: string
    phase: 'preparing' | 'downloading' | 'installing' | 'done' | 'failed'
    /** `downloading` 阶段的已收字节 */
    received?: number
    /** content-length 或版本表里的 fileSize;两个都没有 → 渲染层走 indeterminate */
    total?: number
    /**
     * 只在 `failed` 上有值,而且是 **key 不是句子**。
     *
     * ★ 必须有:`emitToAll` 把事件推给所有窗口,但只有发起那个窗口能 catch 到
     * invoke 的 rejection。没有它,其他窗口看到的是进度条凭空消失。
     */
    messageKey?: string
  }
  /** 插件装/卸/启/禁/激活状态变了。渲染层收到后重新 `plugins:list` */
  'plugins:changed': void
  /**
   * 装一个插件走到哪一步了。**只在一次安装期间推**,终态由
   * `plugins:installMarket` / `plugins:installPackage` 的返回值给。
   *
   * ★ 走全局广播而不是定向推:装插件是全局副作用,B 窗口的市场页也该看到
   * 那颗按钮在跑。代价是渲染层所有基于 `key` 的渲染都必须是**纯查表** ——
   * 「进度存在」不等于「是我这个窗口发起的」。
   */
  'plugins:installProgress': {
    /**
     * `market:<slug>` 或 `local:<路径>`。
     *
     * ★ 本地安装用固定值(比如 `'local'`)是不行的:多窗口、或者连着装两个
     * 本地包时,两条进度会挤在同一个 key 上互相覆盖。
     */
    key: string
    phase: 'preparing' | 'downloading' | 'installing' | 'done' | 'failed'
    /** `downloading` 阶段的已收字节 */
    received?: number
    /** content-length;拿不到就没有这个字段 → 渲染层走 indeterminate */
    total?: number
    /**
     * 只在 `failed` 上有值,而且是 **key 不是句子**(同 `plugins:message` 的规矩)。
     *
     * ★ 必须有:`emitToAll` 把事件推给所有窗口,但只有发起那个窗口能 catch 到
     * invoke 的 rejection。没有它,其他窗口看到的是进度条凭空消失。
     */
    messageKey?: string
  }
  /**
   * 插件要给用户看一条消息。
   *
   * ★ 带的是 **key + params**,不是渲染好的句子 —— 渲染层 `t()` 之后才是人话。
   * 传句子的话,那条通知永远是插件作者写死的那一种语言。
   */
  'plugins:message': {
    pluginId: string
    kind: 'info' | 'warn' | 'error'
    messageKey: string
    params: Record<string, string | number>
  }
  /**
   * 插件请求为某个文件打开它的自定义编辑器 Tab。
   *
   * ★ 主进程已经核过 viewType 是这个插件声明过的、path 落在工作区内
   * (见 `plugin/manager.ts` 的 `tabs.openCustomEditor`)——
   * 渲染层收到时只决定「放在哪一格」,不再做安全判断。
   */
  'plugins:openCustomEditor': { pluginId: string; viewType: string; path: string }
  /**
   * 插件请求打开一个网页 / 视图 Tab。
   *
   * ★ 与 `plugins:openCustomEditor` 同一条立场:主进程**只校验与转发**
   * (webapp 查清单、browser 查 `hostPermissions`、view 查视图声明),
   * 「放哪一格、要不要复用已经开着的那个」由 `stores/tabs.ts` 决定 ——
   * 那套布局规则抄进主进程一定会分叉。
   */
  'plugins:openTab': { pluginId: string; target: PluginTabTarget }
  /**
   * 插件要问用户一句话(选项 / 输入 / 确认),渲染层画、用户答、走
   * `plugins:interactionReply` 回去。
   *
   * ★ 文案全是 **l10n key**:主进程不产出用户可见的裸文本。
   */
  'plugins:interaction': { requestId: string; pluginId: string; request: PluginInteractionRequest }
  /**
   * 插件的长任务进度。`done: true` = 撤掉这一条。
   *
   * ★ 插件被禁用 / 应用退出时宿主会替它把每一条都撤掉 —— 否则状态栏上会留下
   * 一条永远转下去、而且没有主人的进度。
   */
  'plugins:progress': {
    pluginId: string
    id: string
    titleKey?: string
    fraction?: number
    messageKey?: string
    done?: boolean
  }
  /**
   * 编辑器画布打开的那个会话状态变了(Agent 改了、保存了、引擎崩了)。
   * ★ **定向**推给打开这个视图的窗口(`TargetedEventChannel`),不广播:别的窗口不该知道
   * 这边开着哪些文档。视图收到后拉取失效区域重画。
   */
  'documentEngine:changed': DocumentViewChanged
  'commands:changed': void
  'agents:changed': void
  'modes:changed': void
  'hooks:changed': void
  /** 已配对设备清单或连接状态变了(配对、切换、掉线、移除)。 */
  'device:changed': DevicesState
  /** 某条会话的目标变了（设立 / 轮数推进 / 清除）。`goal` 缺席 = 现在没有目标。 */
  'goal:changed': GoalChange
  'mcp:changed': { servers: McpServerStatus[] }
  'websearch:changed': { providers: SearchProviderStatus[] }
  'sessions:changed': SessionChange
  /** 一轮的文件改动集封包落盘完成(或被撤销/恢复)—— 卡片与审查 tab 据此重拉。 */
  'review:changed': { runId: string; sessionId: string; workspaceId: string }
  'browser:changed': BrowserChange
  'browser:cua': BrowserCuaEvent
  'browser:profilesChanged': BrowserProfile[]
  'scheduled:changed': { kind: 'task' | 'run'; taskId?: string; runId?: string; status?: ScheduledRun['status'] }
  'scheduled:focusRun': { runId: string }
  /** The persisted user catalogue changed. Built-in rows are bundled code. */
  'modelCatalog:changed': { custom: ModelCatalogDefinition[] }
  /** 同步价目变了(采用 / 清除)。带全量,渲染层直接替换。 */
  'pricing:changed': { synced: ModelPricing[] }
  /**
   * 上游供应商或别名变了。**两张表一起带**,因为设置页左列的每一行都是
   * 「供应商 + 它的主别名」—— 只推 providers 的话,改完供应商名字副标题还是旧的,
   * 而那需要渲染层再发一次 `provider:listModels` 才补得回来。
   */
  'provider:changed': { providers: UpstreamProvider[]; models: ModelAlias[] }
  /**
   * 登录流程走到哪一步了。**只在一次登录期间推**,终态由 `provider:startOAuth`
   * 的返回值给 —— 这条只负责让按钮上的字从「正在打开浏览器」变成「等待授权」。
   */
  'provider:authProgress': {
    providerId: string
    phase: 'opening' | 'waiting' | 'exchanging' | 'done' | 'failed' | 'cancelled'
    message?: string
    /**
     * ★★ 这次登录要用户**自己把回调地址粘回来**(没有回环端口可接)。
     *
     * 渲染层据此在 `waiting` 阶段画输入框而不是一个转圈的 spinner ——
     * 不给这个标志的话,它只能靠 issuer 去猜「哪几家要粘」,而那是
     * 规格表的知识,整张表是**故意**留在 main 的(见 `oauth/registry.ts` 文件头)。
     *
     * ★ 它描述的是**这一次流程**,不是那家供应商的静态属性 —— 所以走事件,
     * 不走 preset。
     */
    needsPastedCode?: boolean
    /**
     * 设备码流程(RFC 8628,Kimi 走的这条)的**配对码**,只在 `waiting` 上有值。
     *
     * ★★ 没有它,设备码登录在界面上就是一个永远转圈的 spinner:真正要用户做的事
     * (去浏览器里输这一串)**只存在于这个字段里**,厂商的授权页不会替我们告诉他。
     *
     * ★ 和 `needsPastedCode` 一样,它描述的是**这一次流程**,不是供应商的静态属性。
     */
    userCode?: string
    /** 输配对码的页面地址,给「浏览器没自动弹出来」时手动访问用。跟 `userCode` 同进同出 */
    verificationUri?: string
  }
  /**
   * 某家的登录态变了。
   *
   * ★★ **刷新 token 失败、把凭证标成 `needsReauth` 时也推这条** —— 那一刻用户
   * 可能正开着设置页,而他看到的还是「已登录」。不推的话,他要关掉再打开设置页
   * 才知道自己已经掉线了。
   *
   * ★ 不并进 `provider:changed`:登录态不是 providers / aliases 两张表的一部分,
   * 混进去会让每一次登录都触发一遍全应用的模型列表重算。
   */
  'provider:authChanged': { providerId: string; info: CredentialInfo }
  /**
   * 某家的**账号列表**变了:登录/删号/排序/停用,以及**限流落闸与解除**、额度快照更新。
   *
   * ★★ 限流那一半是这条事件存在的主要理由:落闸发生在一次请求失败的瞬间,
   * 而用户此刻多半正盯着聊天页。不推的话,他要关掉再打开设置页才知道
   * 「为什么换号了」—— 而那时闸门可能已经自己到期,他什么都看不到。
   *
   * ★ 整份列表下发,不发增量(和那几条写频道的返回值同一个形状、同一条理由)。
   */
  'provider:accountsChanged': { providerId: string; accounts: ProviderAccount[] }
  'clientAuth:changed': ClientAuthState
  'configSync:changed': SyncStatus
  'app:updateChanged': UpdateState
  /**
   * 导入状态变了 —— 作业进度、同步状态、新批次。
   *
   * ★ 带 `seq` 且**限频**:一次 100 会话的导入会产生上百次状态变化,
   * 每次都推一遍等于让设置页在导入期间每秒重渲染几十次。主进程侧按
   * 大约 200ms 合并,渲染层用 seq 判断自己手里那份是不是已经过期。
   *
   * ★ 不带快照正文:渲染层收到这条之后自己去 `imports:getState` 拉 ——
   * 把 `ImportJobStatus` 塞进来的话,一个没开设置页的窗口也要为每一次
   * 进度变化付一次结构化克隆。
   */
  'imports:changed': { sourceId: string; jobId?: string; seq: number }

  /**
   * 启动迁移的进度。★ 全量推 `MigrationState`,不是增量 delta。
   *
   * 理由和 `imports:changed` 刻意不同:那边一次导入上百次变化、必须限频省克隆,
   * 这里一次启动最多几十条,而**状态机本身很小**(几个枚举 + 两个数字),
   * 让渲染层自己做合并就是给一个「两份状态对不上」的 bug 留门 ——
   * 而它出现在启动路径上,坏了就是白屏。
   */
  'dataMigration:progress': MigrationState
}

// ═══════════════════════════════════════════════════════════════
// 五、频道白名单 —— preload 的运行时防线
// ═══════════════════════════════════════════════════════════════

/**
 * ★ Preload 是安全边界,必须在**运行时**校验频道(方案 §3 规则 6)。
 * 仅有编译期类型不够 —— 被攻破的渲染层可以 `invoke(任意字符串)`,
 * 而在本架构里那等价于经工具层获得任意文件系统访问。
 *
 * 下面三个 const 的 `satisfies Record<keyof XxxMap, 1>` 是**双向**约束:
 * - 少写一个 → Record 缺 key,编译报错
 * - 多写一个 → 对象字面量多余属性检查,编译报错
 *
 * 所以「由契约生成白名单」在这里是可执行的保证,而不是一句口号 ——
 * 往 map 里加频道却忘了加白名单,`npm run typecheck` 当场拦下。
 */
export const INVOKE_CHANNELS = {
  'app:getBootstrap': 1,
  'app:openExternal': 1,
  'app:checkForUpdates': 1,
  'app:updateCheck': 1,
  'app:updateDownload': 1,
  'app:updateInstall': 1,
  'app:updateGetState': 1,
  'app:copyText': 1,
  'device:getState': 1,
  'device:inspectInvitation': 1,
  'device:pair': 1,
  'device:select': 1,
  'device:useLocal': 1,
  'device:remove': 1,
  'device:setRelay': 1,
  'app:copyImage': 1,
  'app:saveTextFile': 1,
  'app:saveImageFile': 1,
  'app:openSessionWindow': 1,
  'clientAuth:getState': 1,
  'clientAuth:startLogin': 1,
  'clientAuth:selectTeam': 1,
  'clientAuth:useOffline': 1,
  'clientAuth:signOut': 1,
  'clientAuth:getUser': 1,
  'clientAuth:getUsage': 1,
  'referral:get': 1,
  'recharge:getOptions': 1,
  'recharge:checkout': 1,
  'recharge:getOrder': 1,
  'configSync:getStatus': 1,
  'configSync:setup': 1,
  'configSync:getConflicts': 1,
  'configSync:getPreview': 1,
  'configSync:confirmInitial': 1,
  'configSync:resolve': 1,
  'configSync:setUsage': 1,
  'settings:get': 1,
  'settings:update': 1,
  'theme:importImage': 1,
  'theme:saveImage': 1,
  'theme:listImages': 1,
  'theme:readImage': 1,
  'theme:deleteImage': 1,
  'theme:listProfiles': 1,
  'theme:saveProfile': 1,
  'theme:deleteProfile': 1,
  'theme:renameProfile': 1,
  'workspace:list': 1,
  'workspace:prepare': 1,
  'workspace:commitActivation': 1,
  'workspace:releaseActivation': 1,
  'workspace:createSsh': 1,
  'connection:list': 1,
  'connection:upsert': 1,
  'connection:remove': 1,
  'connection:connect': 1,
  'connection:disconnect': 1,
  'connection:cancel': 1,
  'connection:browse': 1,
  'connection:closeBrowse': 1,
  'connection:respond': 1,
  'connection:pickFile': 1,
  'workspace:pick': 1,
  'workspace:update': 1,
  'workspace:close': 1,
  'workspace:setActive': 1,
  'workspace:listDir': 1,
  'workspace:searchFiles': 1,
  'workspace:readFile': 1,
  'workspace:writeFile': 1,
  'workspace:mutateFile': 1,
  'workspace:revealFile': 1,
  'workspace:listOpenTargets': 1,
  'workspace:openWith': 1,
  'workspace:copyPath': 1,
  'workspace:saveFileAs': 1,
  'workspace:listRecovery': 1,
  'review:getChangeSet': 1,
  'review:getFileDiff': 1,
  'review:precheckUndo': 1,
  'review:undo': 1,
  'review:redo': 1,
  'browser:list': 1,
  'browser:open': 1,
  'browser:navigate': 1,
  'browser:close': 1,
  'browser:bind': 1,
  'browser:profiles': 1,
  'browser:createProfile': 1,
  'browser:deleteProfile': 1,
  'browser:exportCookies': 1,
  'browser:importCookies': 1,
  'browser:clearProfileState': 1,
  'git:getOverview': 1,
  'git:listBranches': 1,
  'git:listCommits': 1,
  'git:getDiff': 1,
  'git:getDiffSides': 1,
  'git:stage': 1,
  'git:unstage': 1,
  'git:commit': 1,
  'git:checkoutBranch': 1,
  'git:createBranch': 1,
  'git:pull': 1,
  'git:push': 1,
  'git:generateCommitMessage': 1,
  'composer:suggestNext': 1,
  'composer:history': 1,
  'composer:complete': 1,
  'composer:cancel': 1,
  'tabs:getInner': 1,
  'session:getInput': 1,
  'session:queue': 1,
  'session:reportBackground': 1,
  'attachment:upload': 1,
  'attachment:pick': 1,
  'attachment:remove': 1,
  'attachment:prepareWorkspaceUpload': 1,
  'attachment:completeWorkspaceUpload': 1,
  'attachment:cancelWorkspaceUpload': 1,
  'attachment:listBySession': 1,
  'sessions:list': 1,
  'sessions:get': 1,
  'sessions:replaceHistory': 1,
  'sessions:getPage': 1,
  'sessions:getSummary': 1,
  'sessions:lastAssistant': 1,
  'sessions:editMessage': 1,
  'sessions:deleteTurn': 1,
  'sessions:deleteReply': 1,
  'sessions:create': 1,
  'sessions:duplicate': 1,
  'sessions:branch': 1,
  'sessions:createSkillExtraction': 1,
  'sessions:rename': 1,
  'sessions:setMode': 1,
  'sessions:setModel': 1,
  'sessions:setArchived': 1,
  'sessions:setFavorited': 1,
  'sessions:delete': 1,
  'conversations:searchAll': 1,
  'agent:run': 1,
  'agent:attach': 1,
  'agent:unwatch': 1,
  'agent:abort': 1,
  'agent:backgroundSubagent': 1,
  'agent:interject': 1,
  'agent:setPermissionMode': 1,
  'agent:respondInteraction': 1,
  'agent:listInteractions': 1,
  'widget:getState': 1,
  'widget:setState': 1,
  'widget:submit': 1,
  'widget:runAction': 1,
  'widget:complete': 1,
  'widget:saveFile': 1,
  'agent:listTools': 1,
  'shell:stopToolCall': 1,
  'shell:backgroundToolCall': 1,
  'shell:listBackground': 1,
  'shell:peekBackground': 1,
  'shell:killBackground': 1,
  'terminal:create': 1,
  'terminal:prepare': 1,
  'terminal:approve': 1,
  'terminal:kill': 1,
  'terminal:list': 1,
  'terminal:getBuffer': 1,
  'mcp:list': 1,
  'mcp:upsert': 1,
  'mcp:remove': 1,
  'mcp:testConnection': 1,
  'mcp:setSecrets': 1,
  'mcp:getSecretsInfo': 1,
  'websearch:list': 1,
  'websearch:setEnabled': 1,
  'websearch:reorder': 1,
  'websearch:setCredential': 1,
  'websearch:clearCredential': 1,
  'websearch:test': 1,
  'websearch:testBuiltin': 1,
  'proxy:setPassword': 1,
  'proxy:clearPassword': 1,
  'proxy:getPasswordInfo': 1,
  'plugins:list': 1,
  'plugins:setEnabled': 1,
  'plugins:uninstall': 1,
  'plugins:pickPackage': 1,
  'plugins:installPackage': 1,
  'plugins:grantPermissions': 1,
  'plugins:revokePermissions': 1,
  'plugins:activity': 1,
  'plugins:runCommand': 1,
  'plugins:activateEditor': 1,
  'plugins:cardAction': 1,
  'plugins:interactionReply': 1,
  'plugins:openWebApp': 1,
  'plugins:setEditorDirty': 1,
  'plugins:confirmClose': 1,
  'plugins:marketList': 1,
  'plugins:marketCategories': 1,
  'plugins:marketDetail': 1,
  'plugins:installMarket': 1,
  'plugins:checkUpdates': 1,
  'plugins:updateAll': 1,
  'plugins:getConfiguration': 1,
  'documentEngine:open': 1,
  'documentEngine:render': 1,
  'documentEngine:input': 1,
  'documentEngine:command': 1,
  'documentEngine:list': 1,
  'documentEngine:headers': 1,
  'documentEngine:layout': 1,
  'documentEngine:state': 1,
  'documentEngine:save': 1,
  'documentEngine:close': 1,
  'plugins:setConfiguration': 1,
  'skills:list': 1,
  'skills:pickZip': 1,
  'skills:installZip': 1,
  'skills:installMarket': 1,
  'skills:uninstall': 1,
  'skills:marketList': 1,
  'skills:marketCategories': 1,
  'skills:marketDetail': 1,
  'skills:diagnostics': 1,
  'skills:setGlobalEnabled': 1,
  'skills:setWorkspaceActive': 1,
  'commands:list': 1,
  'commands:diagnostics': 1,
  'commands:listAll': 1,
  'commands:setEnabled': 1,
  'agents:list': 1,
  'agents:diagnostics': 1,
  'agents:setEnabled': 1,
  'agents:generate': 1,
  'modes:list': 1,
  'resource:get': 1,
  'resource:save': 1,
  'resource:delete': 1,
  'hooks:list': 1,
  'hooks:diagnostics': 1,
  'hooks:save': 1,
  'hooks:delete': 1,
  'hooks:setEnabled': 1,
  'hooks:test': 1,
  'goal:get': 1,
  'goal:set': 1,
  'goal:clear': 1,
  'video:listBySession': 1,
  'video:get': 1,
  'video:cancel': 1,
  'video:retryRetrieval': 1,
  'video:saveFile': 1,
  'provider:list': 1,
  'providers:listImportable': 1,
  'provider:upsert': 1,
  'provider:remove': 1,
  'provider:setCredential': 1,
  'provider:getCredentialInfo': 1,
  'provider:revealCredential': 1,
  'provider:export': 1,
  'provider:import': 1,
  'provider:startOAuth': 1,
  'provider:cancelOAuth': 1,
  'provider:submitOAuthCode': 1,
  'provider:signOut': 1,
  'provider:listAccounts': 1,
  'provider:addAccount': 1,
  'provider:reauthAccount': 1,
  'provider:removeAccount': 1,
  'provider:setAccountEnabled': 1,
  'provider:setAccountLabel': 1,
  'provider:setCurrentAccount': 1,
  'provider:reorderAccounts': 1,
  'provider:clearAccountLimit': 1,
  'provider:fetchQuota': 1,
  'provider:listModels': 1,
  'provider:fetchModels': 1,
  'provider:setAliases': 1,
  'provider:test': 1,
  'model:update': 1,
  'model:rename': 1,
  'model:remove': 1,
  'modelCatalog:list': 1,
  'modelCatalog:upsert': 1,
  'modelCatalog:remove': 1,
  'modelCatalog:syncPreview': 1,
  'modelCatalog:syncApply': 1,
  'pricing:listSynced': 1,
  'pricing:syncPreview': 1,
  'pricing:syncApply': 1,
  'pricing:clearSynced': 1,
  'usage:getSummary': 1,
  'usage:getRequestLogs': 1,
  'usage:getSessionAttempts': 1,
  'usage:getProviderStats': 1,
  'usage:getModelStats': 1,
  'usage:getDailySeries': 1,
  'usage:getActivityStats': 1,
  'gateway:getStatus': 1,
  'gateway:getKey': 1,
  'gateway:getAddresses': 1,
  'gateway:setEnabled': 1,
  'gateway:resetHealth': 1,
  'storage:getStats': 1,
  'storage:vacuum': 1,
  'storage:openDataDirectory': 1,
  'storage:export': 1,
  'storage:importPreview': 1,
  'storage:importApply': 1,
  'storage:chooseBackupDirectory': 1,
  'storage:getBackupStatus': 1,
  'storage:createBackup': 1,
  'storage:restoreBackup': 1,
  'storage:cleanupPreview': 1,
  'storage:cleanupAttachments': 1,
  'storage:cleanupByAge': 1,
  'storage:clearHistory': 1,
  'storage:clearLocalData': 1
  , 'imports:detect': 1
  , 'imports:chooseSource': 1
  , 'imports:getState': 1
  , 'imports:preview': 1
  , 'imports:previewItems': 1
  , 'imports:apply': 1
  , 'imports:status': 1
  , 'imports:cancel': 1
  , 'imports:history': 1
  , 'imports:historyItems': 1
  , 'imports:updateSync': 1
  , 'imports:syncNow': 1
  , 'imports:resolveConflict': 1
  , 'dataMigration:getState': 1
  , 'dataMigration:retry': 1
  , 'dataMigration:skip': 1
  , 'dataMigration:undoMerge': 1
  , 'dataMigration:openDataDirectory': 1
  , 'context:compact': 1
  , 'context:preview': 1
  , 'scheduled:listTasks': 1
  , 'scheduled:getTask': 1
  , 'scheduled:create': 1
  , 'scheduled:update': 1
  , 'scheduled:delete': 1
  , 'scheduled:setEnabled': 1
  , 'scheduled:runNow': 1
  , 'scheduled:listRuns': 1
  , 'scheduled:getRun': 1
  , 'scheduled:deleteRun': 1
} as const satisfies Record<keyof IpcInvokeMap, 1>

export const SEND_CHANNELS = {
  'terminal:write': 1,
  'terminal:resize': 1,
  'window:ready': 1,
  'window:control': 1,
  'app:quitConfirmed': 1,
  'tabs:persistOuter': 1,
  'tabs:persistInner': 1,
  'session:persistDraft': 1
} as const satisfies Record<keyof IpcSendMap, 1>

export const EVENT_CHANNELS = {
  'connection:status': 1,
  'connection:changed': 1,
  'connection:auth': 1,
  'agent:event': 1,
  'agent:activeRuns': 1,
  'session:queueChanged': 1,
  'session:subagentReport': 1,
  'session:inputPersistence': 1,
  'terminal:data': 1,
  'terminal:exit': 1,
  'window:maximized': 1,
  'window:visibility': 1,
  'gateway:status': 1,
  'gateway:failover': 1,
  'settings:changed': 1,
  'theme:changed': 1,
  'theme:libraryChanged': 1,
  'workspace:changed': 1,
  'skills:changed': 1,
  'skills:installProgress': 1,
  'plugins:changed': 1,
  'plugins:installProgress': 1,
  'plugins:message': 1,
  'plugins:openCustomEditor': 1,
  'plugins:openTab': 1,
  'plugins:interaction': 1,
  'plugins:progress': 1,
  'documentEngine:changed': 1,
  'commands:changed': 1,
  'agents:changed': 1,
  'modes:changed': 1,
  'hooks:changed': 1,
  'device:changed': 1,
  'goal:changed': 1,
  'mcp:changed': 1,
  'provider:changed': 1,
  'provider:authProgress': 1,
  'provider:authChanged': 1,
  'provider:accountsChanged': 1,
  'websearch:changed': 1,
  'sessions:changed': 1,
  'review:changed': 1,
  'browser:changed': 1,
  'browser:cua': 1,
  'browser:profilesChanged': 1,
  'modelCatalog:changed': 1,
  'pricing:changed': 1,
  'clientAuth:changed': 1
  ,'configSync:changed': 1
  ,'app:updateChanged': 1
  ,'imports:changed': 1
  ,'scheduled:changed': 1
  ,'scheduled:focusRun': 1
  ,'dataMigration:progress': 1
  // 视频任务状态变化(revision 单调递增,渲染层据此做增量合并)。
  ,'video:jobChanged': 1
  ,'shell:backgroundChanged': 1
} as const satisfies Record<keyof IpcEventMap, 1>

// ═══════════════════════════════════════════════════════════════
// 六、派生工具类型
// ═══════════════════════════════════════════════════════════════

export type InvokeChannel = keyof IpcInvokeMap
export type SendChannel = keyof IpcSendMap
export type EventChannel = keyof IpcEventMap

export type InvokeReq<K extends InvokeChannel> = IpcInvokeMap[K]['req']
export type InvokeRes<K extends InvokeChannel> = IpcInvokeMap[K]['res']

/** on() 必须返回退订函数(协议 §3.3 的硬约定,方案 §3 规则 4) */
export type Unsubscribe = () => void

export function isInvokeChannel(ch: string): ch is InvokeChannel {
  return Object.prototype.hasOwnProperty.call(INVOKE_CHANNELS, ch)
}

export function isSendChannel(ch: string): ch is SendChannel {
  return Object.prototype.hasOwnProperty.call(SEND_CHANNELS, ch)
}

export function isEventChannel(ch: string): ch is EventChannel {
  return Object.prototype.hasOwnProperty.call(EVENT_CHANNELS, ch)
}
