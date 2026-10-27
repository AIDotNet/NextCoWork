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
import type { AgentError } from '../agent/error'
import type { ActiveRunEntry, AgentEvent, RunSnapshot } from '../agent/event'
import type { AgentMessage, ContentPart } from '../agent/message'
import type { ContextCheckpoint, ContextPreview } from '../agent/context-management'
import type { InteractionResponse, PendingInteraction } from '../agent/interaction'
import type { InterjectItem } from '../agent/interject'
import type { RunRequest, SessionMode } from '../agent/run-request'
import type { ToolInfo } from '../agent/tool'
import type { Bootstrap } from '../domain/bootstrap'
import type { DirListing, FileSuggestion } from '../domain/file-tree'
import type { ReviewChangeSet, ReviewFileDiff, ReviewMutationResult } from '../domain/review'
import type { McpSecretsInfo, McpServerConfig, McpServerStatus } from '../domain/mcp'
import type { ProxyPasswordInfo } from '../domain/proxy'
import type { PluginPermission } from '../plugin/permission'
import type { PluginActivity, PluginCatalog } from '../plugin/state'
import type { PluginInteractionRequest, PluginTabTarget } from '../plugin/ui-request'
import type { PluginMarketItem, PluginUpdate, PluginUpdateResult } from '../plugin/market'
import type {
  Attachment,
  AttachmentScope,
  AttachmentUploadRequest,
  PickedAttachment
} from '../domain/attachment'
import type { SessionInputState } from '../domain/queued-input'
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
import type { SearchHit, Session, SessionChange, SessionDetail, SessionListItem } from '../domain/session'
import type { AppSettings, AppSettingsPatch, ResolvedTheme, StorageStats } from '../domain/settings'
import type { InnerTabState, WindowKind, WindowTabState } from '../domain/tab'
import type { ImageTheme, ThemeProfile } from '../domain/theme'
import type { TerminalBuffer, TerminalCreateRequest, TerminalInfo, TerminalPreparation } from '../domain/terminal'
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
import type { BrowserChange, BrowserCuaEvent, BrowserProfile, BrowserTab } from '../domain/browser'
import type { GitBranchSummary, GitCommitSummary, GitDiff, GitOverview } from '../domain/git'
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
  /**
   * 存一段文本到用户挑的位置。路径由主进程的 showSaveDialog 产出 ——
   * 渲染层只给**文件名建议**,永不指定任意路径(方案 §9)。取消时返回 null。
   */
  'app:saveTextFile': { req: { defaultName: string; text: string }; res: { path: string } | null }
  'app:openSessionWindow': { req: { workspaceId: string; sessionId: string }; res: void }

  // ── NextCoWork 账户 ──
  'clientAuth:getState': { req: void; res: ClientAuthState }
  'clientAuth:startLogin': { req: void; res: ClientAuthState }
  'clientAuth:selectTeam': { req: { teamId: string }; res: ClientAuthState }
  'clientAuth:useOffline': { req: void; res: ClientAuthState }
  'clientAuth:signOut': { req: void; res: ClientAuthState }
  'clientAuth:getUser': { req: void; res: ClientAuthUser | null }
  'clientAuth:getUsage': { req: { from?: string; to?: string }; res: ClientUsageEntry[] }
  'configSync:getStatus': { req: void; res: SyncStatus }
  /** 密码只用于本次创建/解锁 vault，不进入设置或事件广播。 */
  'configSync:setup': { req: SyncSetupRequest; res: SyncStatus }
  'configSync:getConflicts': { req: void; res: SyncConflict[] }
  'configSync:getPreview': { req: void; res: SyncPreview }
  'configSync:confirmInitial': { req: void; res: void }
  'configSync:resolve': { req: { id: string; useRemote: boolean }; res: void }

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
  'sessions:create': { req: { workspaceId: string; title?: string; sessionId?: string; mode?: SessionMode }; res: Session }
  'sessions:duplicate': { req: { sessionId: string; title: string }; res: Session }
  /** 从某一轮「分支」：只克隆到这一轮为止的转录，之后的内容不带过去。 */
  'sessions:branch': { req: { sessionId: string; uptoMessageId: string; title: string }; res: Session }
  'sessions:rename': { req: { sessionId: string; title: string }; res: void }
  'sessions:setMode': { req: { sessionId: string; mode: SessionMode }; res: void }
  /**
   * 记住这条会话选中的模型。★ 和 `setMode` 同理:模型是**每条会话自己的**记忆,
   * 在另一条会话里换模型不该把这条会话也换掉(工作区默认值只影响新会话)。
   */
  'sessions:setModel': { req: { sessionId: string; model: string; modelProviderId?: string }; res: void }
  'sessions:setArchived': { req: { sessionId: string; archived: boolean }; res: void }
  'sessions:setFavorited': { req: { sessionId: string; favorited: boolean }; res: void }
  'sessions:delete': { req: { sessionId: string }; res: void }
  /** 全局入口,但结果可按工作区筛(同「定时任务」页那个筛选器) */
  'conversations:searchAll': {
    req: { q: string; workspaceId?: string; limit: number }
    res: SearchHit[]
  }
  'context:list': { req: { sessionId: string }; res: ContextCheckpoint[] }
  'context:updateCheckpoint': {
    req: { checkpointId: string; note: string; revision: number }
    res: ContextCheckpoint
  }
  /**
   * 手动压缩上下文 —— 让模型总结一遍旧历史,落一个新检查点。
   *
   * ★ 只在会话**空闲**时可用:跑着的那个 run 已经把自己的投影冻在内存里,
   * 此时落检查点不会影响它,却会让界面上的读数和模型实际看到的对不上。
   */
  'context:compact': {
    req: { sessionId: string }
    res: { checkpoint: ContextCheckpoint; inputTokens: number }
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
  /** ★ runId 由调用方传入,不由这里返回(方案 §3 规则 2) */
  'agent:run': { req: RunRequest; res: void }
  'agent:attach': { req: { runId: string; sinceSeq: number }; res: RunSnapshot }
  'agent:abort': { req: { runId: string; cascade: boolean }; res: void }
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
  /** ★ 审批 / 反问 / 计划确认三种 kind 共用这一个(方案 §4.6) */
  'agent:respondInteraction': { req: InteractionResponse; res: void }
  'agent:listInteractions': { req: { runId?: string; sessionId?: string }; res: PendingInteraction[] }
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
  'plugins:runCommand': { req: { pluginId: string; commandId: string }; res: void }
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
  'plugins:marketList': { req: { q?: string; category?: string }; res: PluginMarketItem[] }
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

  // ── 使用统计 ──
  'usage:getSummary': { req: UsageWindow; res: UsageSummary }
  'usage:getRequestLogs': { req: UsageRequestLogsQuery; res: UsageRequestLogsPage }
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
    ★ 这四条是**全应用唯一能在数据库打开之前调用的 IPC**。

    它们必须能通,因为闸门存在的那段时间里数据库还没打开 —— 渲染层这时候只有
    一个窗口、一屏进度,别的什么都调不了(这也是「阻止用户使用」的实现方式:
    不是禁用控件,而是后端根本没起来)。

    ★ 所以这四条的实现里**一个字节的库读写都不能有**。谁哪天顺手往
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
   * 未发出的输入落盘。★ **`immediate` 分两档,这是与 Tab 布局唯一的不同**:
   *
   * - 草稿每次按键都变 → `false`,走同一个 500ms 防抖,丢失窗口 ≤500ms,代价是半个词。
   * - 入队/插话/编辑/删除是离散低频动作 → `true`,立即写。丢一整条排队消息的代价
   *   远高于丢半个词,而这类操作的频率低到根本不值得防抖。
   *
   * 强杀(kill -9)拦不住任何一档 —— 这正是队列走立即写、只让草稿承担
   * 那 500ms 风险窗口的原因。
   */
  'session:persistInput': {
    sessionId: string
    state: SessionInputState
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
  'terminal:data': { id: string; seq: number; chunk: string }
  'terminal:exit': { id: string; code: number }
  /**
   * 最大化状态。**每窗口一份**,所以走 `windows.emitTo` 定向推 —— 它被登记进
   * `TargetedEventChannel`,`emitToAll` 在编译期就拿不到它。自绘的中间那颗按钮
   * (`shell/WindowControls.tsx`)据此在「□」和「双叠框」之间换字形。
   */
  'window:maximized': { maximized: boolean }
  'gateway:status': GatewayStatus
  'gateway:failover': FailoverEvent
  'settings:changed': AppSettings
  'theme:changed': { resolved: ResolvedTheme }
  'theme:libraryChanged': { profiles: ThemeProfile[]; images: ImageTheme[] }
  'workspace:changed': { workspaces: Workspace[] }
  'skills:changed': void
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
  'commands:changed': void
  'agents:changed': void
  'modes:changed': void
  'hooks:changed': void
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
  'app:saveTextFile': 1,
  'app:openSessionWindow': 1,
  'clientAuth:getState': 1,
  'clientAuth:startLogin': 1,
  'clientAuth:selectTeam': 1,
  'clientAuth:useOffline': 1,
  'clientAuth:signOut': 1,
  'clientAuth:getUser': 1,
  'clientAuth:getUsage': 1,
  'configSync:getStatus': 1,
  'configSync:setup': 1,
  'configSync:getConflicts': 1,
  'configSync:getPreview': 1,
  'configSync:confirmInitial': 1,
  'configSync:resolve': 1,
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
  'git:stage': 1,
  'git:unstage': 1,
  'git:commit': 1,
  'git:checkoutBranch': 1,
  'git:createBranch': 1,
  'git:pull': 1,
  'git:push': 1,
  'git:generateCommitMessage': 1,
  'tabs:getInner': 1,
  'session:getInput': 1,
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
  'sessions:create': 1,
  'sessions:duplicate': 1,
  'sessions:branch': 1,
  'sessions:rename': 1,
  'sessions:setMode': 1,
  'sessions:setModel': 1,
  'sessions:setArchived': 1,
  'sessions:setFavorited': 1,
  'sessions:delete': 1,
  'conversations:searchAll': 1,
  'agent:run': 1,
  'agent:attach': 1,
  'agent:abort': 1,
  'agent:interject': 1,
  'agent:respondInteraction': 1,
  'agent:listInteractions': 1,
  'agent:listTools': 1,
  'shell:stopToolCall': 1,
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
  'usage:getSummary': 1,
  'usage:getRequestLogs': 1,
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
  , 'context:list': 1
  , 'context:updateCheckpoint': 1
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
  'session:persistInput': 1
} as const satisfies Record<keyof IpcSendMap, 1>

export const EVENT_CHANNELS = {
  'connection:status': 1,
  'connection:changed': 1,
  'connection:auth': 1,
  'agent:event': 1,
  'agent:activeRuns': 1,
  'terminal:data': 1,
  'terminal:exit': 1,
  'window:maximized': 1,
  'gateway:status': 1,
  'gateway:failover': 1,
  'settings:changed': 1,
  'theme:changed': 1,
  'theme:libraryChanged': 1,
  'workspace:changed': 1,
  'skills:changed': 1,
  'plugins:changed': 1,
  'plugins:installProgress': 1,
  'plugins:message': 1,
  'plugins:openCustomEditor': 1,
  'plugins:openTab': 1,
  'plugins:interaction': 1,
  'plugins:progress': 1,
  'commands:changed': 1,
  'agents:changed': 1,
  'modes:changed': 1,
  'hooks:changed': 1,
  'goal:changed': 1,
  'mcp:changed': 1,
  'provider:changed': 1,
  'provider:authProgress': 1,
  'provider:authChanged': 1,
  'websearch:changed': 1,
  'sessions:changed': 1,
  'review:changed': 1,
  'browser:changed': 1,
  'browser:cua': 1,
  'browser:profilesChanged': 1,
  'modelCatalog:changed': 1,
  'clientAuth:changed': 1
  ,'configSync:changed': 1
  ,'app:updateChanged': 1
  ,'imports:changed': 1
  ,'scheduled:changed': 1
  ,'scheduled:focusRun': 1
  ,'dataMigration:progress': 1
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
