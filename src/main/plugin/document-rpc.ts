/**
 * 插件 `documents.*` RPC 的安全边界与 session 租约。
 *
 * 需求：办公插件的预览、Agent 修改和保存必须落到同一个受控文档会话；插件只能
 * 访问所属工作区中的普通文件，并且只能使用清单允许的文档引擎。这里故意不把
 * 排版、单元格计算或 PDF 语义复制进插件宿主——这些属于 `DocumentEngineProvider`。
 * 不满足会怎样：如果直接把插件传来的路径交给引擎，软链能把读写带出工作区；如果
 * 只按 sessionId 查而不记插件租约，另一个插件可以探测或操作别人的活动文档。
 *
 * ## 为什么所有入口走同一条串行队列
 *
 * `open` / `close` / 空闲清扫 / 插件禁用都可能落在同一个文件上交错发生（用户点关闭
 * 的同时定时器在扫空闲租约，插件被禁用的同时它的工具正在打开文档）。这里把**全部**
 * 入口串到一条队列上，让每次调用在入口处同步冻结账户作用域，队列轮到它时再与当前
 * 账户比一次。不满足会怎样：表现为「关掉编辑器后引擎还在跑」或者「切账户之后旧账户
 * 的文档又被写了一次」，两者都不报错。
 *
 * ## 租约的两个不变式
 *
 * 1. **每个 `插件 + 账户 + 工作区 + 规范路径` 最多一个租约。** 重复 `open` 直接复用
 *    已有的租约（不叠加视图）—— 视图是「谁在用这个会话」的记账，同一个插件开同一个
 *    文件两次不该记两笔。
 * 2. **释放一律非 force。** 脏会话由 `DocumentSessionManager` 保留（`closed:false,
 *    dirty:true`），未保存的改动归用户；要不要丢由 `assertCanRelease` 那道闸决定，
 *    不在这里静默丢。到期 / 禁用只移除**租约**，不碰会话里的内容。
 *
 * ## 路径
 *
 * 词法门复用 `capabilities.ts` 的 `narrowWorkspacePath`，落点由**逐段 lstat** 确认：
 * 任何一级是软链就拒。工作区根用 `realpath` 归一（macOS 的 `/var` → `/private/var`
 * 就是这种根），归一后的路径才是会话路径 —— 否则同一个目录的两个名字能让「导出不许
 * 覆盖正在编辑的文件」这条检查失效。
 */
import type { Stats } from 'node:fs'
import { lstat, realpath } from 'node:fs/promises'
import { isAbsolute, join, relative, resolve, sep } from 'node:path'
import {
  DocumentEngineError,
  documentFormatOf,
  isDocumentFormat
} from '../../shared/document-engine/protocol'
import type { DocumentCapabilities } from '../../shared/document-engine/protocol'
import type { DocumentSessionSnapshot } from '../../shared/document-engine/session'
import type { PluginErrorCode, PluginMethod } from '../../shared/plugin/protocol'
import { satisfiesEngine, type PluginManifest } from '../../shared/plugin/manifest'
import type { DocumentEngineProvider, DocumentSessionManager } from '../document-engine/manager'
import { splitProviderId } from '../document-engine/provider-registry'
import { narrowWorkspacePath } from './capabilities'

export interface DocumentCallScope {
  workspaceId: string
  workspaceRoot: string
  /**
   * 这次调用的中止信号(`manager.handleRequest` 传进来,与 `ctx.signal` 同一个)。
   *
   * ★ 可选,是因为这个类型也被纯内存单测直接构造。缺席 = 不检查 —— 旧行为,
   * 那些测试关心的不是取消。
   */
  signal?: AbortSignal
  /**
   * 「这一次调用已经越过可安全重试的边界」—— 与 `CapabilityContext.markSideEffectStarted`
   * **同一个回调**,由 manager 传下来。导出 / 保存原文件之前调一次,让一次已经
   * 写盘之后的取消报 `[result_unknown]` 而不是「可以安全重试」。
   */
  markSideEffectStarted?: () => void
}

export interface DocumentChanged {
  path: string
  kind: 'created' | 'modified'
}

export interface PluginDocumentResponse {
  data: unknown
  summary: string
  changed?: DocumentChanged
}

export interface PluginDocumentsBridge {
  handle: (
    pluginId: string,
    manifest: PluginManifest,
    method: PluginMethod,
    params: unknown,
    scope: DocumentCallScope
  ) => Promise<PluginDocumentResponse>
  releasePlugin: (pluginId: string) => Promise<void>
  /**
   * 可选：放行 / 拦住「把这个插件的文档租约放掉」这件事。
   *
   * 需求：禁用、卸载、升级插件之前要先问一句「它还有没有没保存的东西」，有就不能放行
   * —— 放行之后 `releasePlugin` 会（非 force 地）收回视图，脏会话虽然留在会话表里，
   * 但**没有任何人能再把它存回去**：那个插件已经不在了。
   *
   * 无 id = 问「全部文档都能放下吗」（退出应用 / 切账户走这条）；
   * 有 id = 只看这个插件的租约，以及 providerId 以 `<pluginId>/` 开头的会话 ——
   * 用别人（比如引擎插件）的引擎打开的脏文档，也会被那个插件卸载时连累。
   * 有脏文档时抛 `PluginCapabilityError('rejected', …unsaved…)`。
   */
  assertCanRelease?: (pluginId?: string) => Promise<void>
  /** 仅受信宿主在用户明确确认丢弃后调用；不暴露成插件 RPC。 */
  discardAll?: () => Promise<void>
}

interface PluginDocumentsOptions {
  sessions: DocumentSessionManager
  ensureProvider: (providerId: string) => DocumentEngineProvider | null
  /** 生产装配提供当前插件版本；纯内存 fake provider 可省略。 */
  engineVersion?: (pluginId: string) => string | null
  retireEngines: (pluginId: string) => Promise<void>
  accountScope: () => string
  leaseIdleMs?: number
  now?: () => number
}

interface DocumentLease {
  pluginId: string
  sessionId: string
  workspaceId: string
  /** 建租约时冻结的账户作用域。★ 之后每一次 session 调用都用它，不用「当时」的当前账户 */
  accountScope: string
  /**
   * 规范工作区根（`realpath` 之后）。★ 比较与重算路径都必须用规范根：同一个目录经
   * 软链有第二个名字时，词法根算出来的路径与租约里的路径对不上，父子关系也就判不出来了。
   */
  workspaceRoot: string
  /** 规范绝对路径（会话的实际落点） */
  absolutePath: string
  /** 工作区相对路径，`/` 分隔，只用于回给插件的展示 */
  path: string
  providerId: string
  viewId: string
  lastUsed: number
}

interface SessionOwner {
  pluginIds: Set<string>
  providerId: string
}

/**
 * 主窗口里一个编辑器画布对文档会话的占用(见 `openEditorView`)。
 *
 * 需求:画布与插件逻辑 / Agent 工具要落到**同一个**会话,但画布的生命周期跟 Tab 走,
 * 不跟插件 RPC 的租约走 —— 租约 5 分钟不用就会被 `sweepIdle` 收掉,而用户盯着一页
 * 文档看五分钟不动是常态;画布若挂在租约上,下一次重画就是 session_closed。
 * 所以它是一个独立的会话视图,只在 Tab 关闭 / 窗口销毁时释放。
 */
export interface DocumentEditorView {
  pluginId: string
  sessionId: string
  viewId: string
  workspaceId: string
  /** 打开时冻结的账户作用域;之后每次调用都要与当前账户相同 */
  accountScope: string
  /** 规范工作区根(`realpath` 之后) */
  workspaceRoot: string
  /** 规范绝对路径 */
  absolutePath: string
  /** 工作区相对路径,`/` 分隔 */
  path: string
  providerId: string
}

/** 保存前重验路径门所需的最小信息 —— 租约与编辑器视图共用 */
type SaveTarget = Pick<DocumentEditorView, 'sessionId' | 'workspaceId' | 'accountScope' | 'workspaceRoot' | 'absolutePath' | 'path'>

export class PluginCapabilityError extends Error {
  constructor(readonly code: PluginErrorCode, message: string) {
    super(message)
    this.name = 'PluginCapabilityError'
  }
}

export class PluginDocuments {
  private readonly leases = new Map<string, DocumentLease>()
  /**
   * 规范路径 → 租约。★ 重复 `open` 靠它去重，而不是靠 sessionId：sessionId 要等
   * `sessions.open` 返回才知道，而那一次调用**已经给会话加了一个视图**。
   */
  private readonly leasesByPath = new Map<string, DocumentLease>()
  /**
   * 每个**曾经打开过**的会话的归属。★ 租约可能已经不在表里（到期 / 已 close），
   * 会话却还脏着；`assertCanRelease` 只有靠这张表才认得出来是谁的东西。
   */
  private readonly owners = new Map<string, SessionOwner>()
  private readonly leaseIdleMs: number
  private readonly now: () => number
  /** 全部入口共用的一条队列（见文件头「为什么所有入口走同一条串行队列」） */
  private queue: Promise<unknown> = Promise.resolve()

  constructor(private readonly options: PluginDocumentsOptions) {
    this.leaseIdleMs = options.leaseIdleMs ?? 5 * 60_000
    this.now = options.now ?? Date.now
  }

  async handle(
    pluginId: string,
    manifest: PluginManifest,
    method: PluginMethod,
    rawParams: unknown,
    scope: DocumentCallScope
  ): Promise<PluginDocumentResponse> {
    /*
      需求：账户作用域在**入口同步**冻结（`accountScope()` 在任何 await 之前调用），
      队列轮到这次调用时再与当前账户比一次；不符就当会话不存在拒绝。
      不满足会怎样：请求在队列里排了一会儿，期间用户切了账户，这次调用会拿着新账户的
      作用域去读写旧账户的会话 —— 表现为「切账户之后还能读到上一个账户的文档」。
    */
    const accountScope = this.options.accountScope()
    return this.enqueue(async () => {
      if (this.options.accountScope() !== accountScope) throw sessionClosed()
      /*
        需求：**排队的动作在真正执行之前再查一次「这次调用还算不算数」。**
        串行队列可能让一次请求等上几百毫秒到几秒（另一次 open 在起引擎 / 复制
        工作副本），这期间 RPC 可能已经超时、插件被禁用、产生它的工具被中止。
        不查的话，那次调用会越过自己的 deadline 去改文档 —— 而插件那边早就收到
        失败并以为可以重试。★ 检查放在 `enqueue` 里面（真正轮到它时），不是入口：
        入口那次冻结的是账户，不是「还活着」。
        ★ 报 `rejected`（与 manager 那句「the call was cancelled」同一口径），
        让 abort 的两个来源（超时 / 取消）由上层按 reason 分类，这里不猜。
      */
      if (scope.signal?.aborted === true) throw new PluginCapabilityError('rejected', 'the call was cancelled')
      try {
        switch (method) {
          case 'documents.open': return await this.open(pluginId, manifest, rawParams, scope, accountScope)
          case 'documents.apply': return await this.apply(pluginId, rawParams, scope, accountScope)
          case 'documents.save': return await this.save(pluginId, rawParams, scope, accountScope)
          case 'documents.export': return await this.export(pluginId, rawParams, scope, accountScope)
          case 'documents.getState': return await this.getState(pluginId, rawParams, scope, accountScope)
          case 'documents.query': return await this.query(pluginId, rawParams, scope, accountScope)
          case 'documents.getOperation': return await this.getOperation(pluginId, rawParams, scope, accountScope)
          case 'documents.close': return await this.close(pluginId, rawParams, scope, accountScope)
          default: throw new PluginCapabilityError('unknown_method', `unknown method: ${method}`)
        }
      } catch (error) {
        throw toCapabilityError(error)
      }
    })
  }

  async sweepIdle(): Promise<void> {
    await this.enqueue(async () => {
      const cutoff = this.now() - this.leaseIdleMs
      for (const lease of [...this.leases.values()]) {
        if (lease.lastUsed > cutoff) continue
        /*
          需求：到期只**移除租约**。释放是非 force 的，脏会话因此留在会话表里
          （下一次 open 会把它找回来）；这里图省事 force 一下，丢的就是用户还没保存的输入。
          释放本身失败（会话已被别处收掉）也要把租约删掉：留着它只会让这个文件永远开不了。
        */
        await this.releaseLease(lease).catch(() => undefined)
        this.dropLease(lease)
      }
    })
  }

  async releasePlugin(pluginId: string): Promise<void> {
    await this.enqueue(async () => {
      for (const lease of [...this.leases.values()]) {
        if (lease.pluginId !== pluginId) continue
        /*
          需求：禁用 / 卸载时**只做非 force 释放**。脏会话该不该放下由 `assertCanRelease`
          决定（调用方在进这里之前问）；到了这一步还 force，就等于把用户没保存的东西删了。
        */
        await this.releaseLease(lease).catch(() => undefined)
        this.dropLease(lease)
      }
      /*
        需求：只退**自己**的引擎。依赖的引擎归依赖方管 —— 这里递归进去等于把别人
        （可能还有别人正在用的）helper 一起收掉。
      */
      await this.options.retireEngines(pluginId)
    })
  }

  /**
   * 见 `PluginDocumentsBridge.assertCanRelease` 的注释（为什么存在）。
   * 走同一条队列：要问的是「队列排空之后还脏不脏」，绕开队列会读到半个状态。
   */
  async assertCanRelease(pluginId?: string): Promise<void> {
    await this.enqueue(async () => {
      const dirty = this.options.sessions.dirtySessions()
      this.forgetOwnersOfSettledSessions(dirty.map((snapshot) => snapshot.sessionId))
      for (const snapshot of dirty) {
        if (!this.ownsSession(snapshot.sessionId, pluginId)) continue
        throw new PluginCapabilityError('rejected', `[unsaved] document ${snapshot.sessionId} still has unsaved changes`)
      }
    })
  }

  /** 需求：崩溃后的脏模型无法保存，用户必须能明确放弃它，而不是只能强杀应用。 */
  async discardAll(): Promise<void> {
    await this.enqueue(async () => {
      await this.options.sessions.closeAll()
      this.leases.clear()
      this.leasesByPath.clear()
      this.owners.clear()
    })
  }

  /**
   * 为主窗口里的编辑器画布打开 Tab 绑定的那个文件(不建租约,见 `DocumentEditorView`)。
   *
   * 引擎只取 `customEditors[viewType].documentEngine`:画布是那个编辑器的 UI,
   * 不应该退到插件别的引擎上。路径门、依赖版本门、格式过滤与插件 RPC 的 `open` 相同,
   * 走同一条串行队列、在入口冻结账户作用域(理由见 `handle`)。
   * 会话记入归属表:画布里没保存的改动,在禁用 / 卸载这个插件时同样要被 `assertCanRelease` 拦下。
   */
  async openEditorView(
    pluginId: string,
    manifest: PluginManifest,
    viewType: string,
    path: string,
    scope: DocumentCallScope
  ): Promise<{ view: DocumentEditorView; snapshot: DocumentSessionSnapshot; capabilities: DocumentCapabilities }> {
    const accountScope = this.options.accountScope()
    return this.enqueue(async () => {
      if (this.options.accountScope() !== accountScope) throw sessionClosed()
      try {
        const editor = manifest.contributes.customEditors.find((item) => item.viewType === viewType)
        if (editor?.documentEngine === undefined) throw invalidArgument(`custom editor ${viewType} is not bound to a document engine`)
        const providerId = qualifyEngine(pluginId, editor.documentEngine)
        if (splitProviderId(providerId) === null) throw invalidArgument('engine is invalid')
        assertEngineDependency(pluginId, manifest, providerId)
        const target = await resolveWorkspacePath(scope, requiredString(path, 'path'), 'read')
        const provider = this.ensureProvider(pluginId, manifest, providerId)
        if (provider === null) throw engineUnavailable(providerId)
        const format = documentFormatOf(target.absolutePath)
        if (format === null || !provider.formats.includes(format)) throw new DocumentEngineError('unsupported_format', `document engine ${providerId} cannot open this file type`)
        const managerScope = { accountScope, workspaceId: scope.workspaceId }
        const opened = await this.options.sessions.open({ scope: managerScope, absolutePath: target.absolutePath, providerId })
        // 需求同 `open`:打开期间账户被切走,新视图属于已易主的账户,放掉再拒绝
        if (this.options.accountScope() !== accountScope) {
          await this.options.sessions.release({ sessionId: opened.snapshot.sessionId, scope: managerScope, viewId: opened.viewId }).catch(() => undefined)
          throw sessionClosed()
        }
        const owner = this.owners.get(opened.snapshot.sessionId)
        if (owner === undefined) this.owners.set(opened.snapshot.sessionId, { pluginIds: new Set([pluginId]), providerId })
        else owner.pluginIds.add(pluginId)
        const view: DocumentEditorView = {
          pluginId,
          sessionId: opened.snapshot.sessionId,
          viewId: opened.viewId,
          workspaceId: scope.workspaceId,
          accountScope,
          workspaceRoot: target.root,
          absolutePath: target.absolutePath,
          path: target.relative,
          providerId
        }
        return { view, snapshot: opened.snapshot, capabilities: opened.capabilities }
      } catch (error) {
        throw toCapabilityError(error)
      }
    })
  }

  /** 保存编辑器画布打开的文档。与插件 RPC 的 `save` 同一套写回前路径重验 */
  async saveEditorView(view: DocumentEditorView): Promise<DocumentSessionSnapshot> {
    const accountScope = this.options.accountScope()
    return this.enqueue(async () => {
      if (accountScope !== view.accountScope || this.options.accountScope() !== accountScope) throw sessionClosed()
      try {
        return (await this.saveResolved(view)).snapshot
      } catch (error) {
        throw toCapabilityError(error)
      }
    })
  }

  /**
   * 放掉编辑器画布的那个会话视图。★ 非 force:脏会话留在会话表里(重开能找回),
   * 与租约释放同一条规矩。会话已经不在(崩溃后被收、账户已切)不算错误。
   */
  async closeEditorView(view: DocumentEditorView): Promise<{ closed: boolean; dirty: boolean }> {
    return this.enqueue(async () => {
      try {
        return await this.options.sessions.release({
          sessionId: view.sessionId,
          scope: { accountScope: view.accountScope, workspaceId: view.workspaceId },
          viewId: view.viewId
        })
      } catch (error) {
        if (error instanceof DocumentEngineError && error.code === 'session_closed') return { closed: true, dirty: false }
        throw toCapabilityError(error)
      }
    })
  }

  private ensureProvider(pluginId: string, manifest: PluginManifest, providerId: string): DocumentEngineProvider | null {
    const owner = splitProviderId(providerId)?.pluginId
    // 需求：依赖声明不仅是名字，升级成不兼容版本后不能继续向旧协议下发操作。
    if (owner !== undefined && owner !== pluginId && this.options.engineVersion !== undefined) {
      const version = this.options.engineVersion(owner)
      const range = manifest.dependencies[owner]
      if (version === null || range === undefined || !satisfiesEngine(range, version)) return null
    }
    return this.options.ensureProvider(providerId)
  }

  private async open(
    pluginId: string,
    manifest: PluginManifest,
    rawParams: unknown,
    scope: DocumentCallScope,
    accountScope: string
  ): Promise<PluginDocumentResponse> {
    const params = objectParams(rawParams)
    const path = requiredString(params.path, 'path')
    const target = await resolveWorkspacePath(scope, path, 'read')
    const explicitEngine = params.engine
    let qualifiedEngine: string | undefined
    if (explicitEngine !== undefined) {
      if (typeof explicitEngine !== 'string' || explicitEngine === '') throw invalidArgument('engine is invalid')
      qualifiedEngine = qualifyEngine(pluginId, explicitEngine)
      if (splitProviderId(qualifiedEngine) === null) throw invalidArgument('engine is invalid')
      assertEngineDependency(pluginId, manifest, qualifiedEngine)
    }
    const reusable = this.leasesByPath.get(pathKey(pluginId, accountScope, scope.workspaceId, target.absolutePath))
    if (reusable !== undefined) {
      // 需求：重复 open 仍需遵守这次清单和显式引擎，不能用缓存绕过已撤销的依赖声明。
      assertEngineDependency(pluginId, manifest, reusable.providerId)
      if (qualifiedEngine !== undefined && qualifiedEngine !== reusable.providerId) throw engineUnavailable(qualifiedEngine)
      /*
        需求：复用之前**也**要确认引擎还在（插件可能刚被禁用 / 升级，宿主会把它的 provider 撤掉）。
        不满足会怎样：插件拿到一个指向已退休 helper 的会话句柄，之后每一次操作都失败，
        而错误发生在几步之后，看不出根因在这一次 open。
      */
      if (this.ensureProvider(pluginId, manifest, reusable.providerId) === null) throw engineUnavailable(reusable.providerId)
      const state = this.readLeaseState(reusable)
      if (state !== null) {
        /*
          需求：同一个插件重复 open 同一个文件时**复用租约**，不新建视图。
          不满足会怎样：每多开一次就多一个 viewId，而插件只记得最后一个；被遗忘的视图
          永远不会 release，这个会话也就永远关不掉（表现为「关掉编辑器后引擎还在跑」）。
        */
        reusable.lastUsed = this.now()
        return {
          data: {
            sessionId: reusable.sessionId,
            viewId: reusable.viewId,
            path: reusable.path,
            snapshot: state.snapshot,
            capabilities: state.capabilities
          },
          summary: `opened ${reusable.path}`
        }
      }
      // 会话已被别处收掉（原生编辑器关掉最后一个视图、provider 被撤销）：陈旧租约不能挡住重开
      this.dropLease(reusable)
    }

    const candidates = engineCandidates(pluginId, manifest)
    let providerId: string | undefined
    if (qualifiedEngine !== undefined) {
      const qualified = qualifiedEngine
      /*
        需求：显式指定的引擎必须**当场**确认宿主拿得到它，而不是把请求递进会话管理器再收一条
        `engine_unavailable`。不满足会怎样：插件分不清「清单没声明」和「引擎没装」，只能重试。
      */
      if (this.ensureProvider(pluginId, manifest, qualified) === null) throw engineUnavailable(qualified)
      providerId = qualified
    } else {
      const format = documentFormatOf(target.absolutePath)
      for (const candidate of candidates) {
        if (!isAllowedEngine(pluginId, manifest, candidate)) continue
        const provider = this.ensureProvider(pluginId, manifest, candidate)
        /*
          需求：自动候选按**引擎实际支持的格式**过滤。清单里绑定了引擎、但那个引擎打不开
          这个扩展名时不能选中它 —— 否则用户看到的是一条来自引擎内部的 unsupported_format。
        */
        if (provider === null) continue
        if (format !== null && !provider.formats.includes(format)) continue
        providerId = candidate
        break
      }
      if (providerId === undefined) throw new DocumentEngineError('engine_unavailable', '[engine_unavailable] no declared document engine is available')
    }

    scope.signal?.throwIfAborted()
    const managerScope = { accountScope, workspaceId: scope.workspaceId }
    const opened = await this.options.sessions.open({ scope: managerScope, absolutePath: target.absolutePath, providerId })
    /*
      需求：`sessions.open` 可能要跑几秒（起 helper、复制工作副本），这期间账户可能被切走。
      这时新拿到的视图属于一个已经易主的账户，必须自己放掉（非 force）再拒绝。
      不满足会怎样：表现为切换账户之后，某份文档的视图永远留在会话表里，谁也关不掉它。
    */
    if (this.options.accountScope() !== accountScope || scope.signal?.aborted === true) {
      await this.options.sessions.release({ sessionId: opened.snapshot.sessionId, scope: managerScope, viewId: opened.viewId }).catch(() => undefined)
      scope.signal?.throwIfAborted()
      throw sessionClosed()
    }

    const lease: DocumentLease = {
      pluginId,
      sessionId: opened.snapshot.sessionId,
      workspaceId: scope.workspaceId,
      accountScope,
      workspaceRoot: target.root,
      absolutePath: target.absolutePath,
      path: target.relative,
      providerId,
      viewId: opened.viewId,
      lastUsed: this.now()
    }
    this.leases.set(leaseKey(lease.pluginId, lease.sessionId, lease.workspaceId), lease)
    this.leasesByPath.set(pathKey(lease.pluginId, lease.accountScope, lease.workspaceId, lease.absolutePath), lease)
    // 需求：同一模型可被多个插件加入，后来者不能覆盖已关闭租约的早期编辑者归属。
    const owner = this.owners.get(lease.sessionId)
    if (owner === undefined) this.owners.set(lease.sessionId, { pluginIds: new Set([pluginId]), providerId })
    else owner.pluginIds.add(pluginId)
    return {
      data: {
        sessionId: lease.sessionId,
        viewId: lease.viewId,
        path: lease.path,
        snapshot: opened.snapshot,
        capabilities: opened.capabilities
      },
      summary: `opened ${lease.path}`
    }
  }

  private async apply(
    pluginId: string,
    rawParams: unknown,
    scope: DocumentCallScope,
    accountScope: string
  ): Promise<PluginDocumentResponse> {
    const params = objectParams(rawParams)
    const sessionId = requiredString(params.sessionId, 'sessionId')
    const lease = await this.requireLease(pluginId, sessionId, scope, accountScope)
    const generation = requiredInteger(params.generation, 'generation')
    const modelRevision = requiredInteger(params.modelRevision, 'modelRevision')
    const operationId = requiredString(params.operationId, 'operationId')
    scope.signal?.throwIfAborted()
    scope.markSideEffectStarted?.()
    const result = await this.options.sessions.apply({
      sessionId,
      scope: { accountScope: lease.accountScope, workspaceId: lease.workspaceId },
      operationId,
      generation,
      modelRevision,
      operations: params.operations
    })
    lease.lastUsed = this.now()
    return { data: result, summary: `applied ${operationId} to ${lease.path}` }
  }

  private async save(
    pluginId: string,
    rawParams: unknown,
    scope: DocumentCallScope,
    accountScope: string
  ): Promise<PluginDocumentResponse> {
    const params = objectParams(rawParams)
    const sessionId = requiredString(params.sessionId, 'sessionId')
    const lease = await this.requireLease(pluginId, sessionId, scope, accountScope)
    const { snapshot, changed } = await this.saveResolved(lease, scope)
    lease.lastUsed = this.now()
    return {
      data: { snapshot },
      summary: `saved ${lease.path}`,
      ...(changed === undefined ? {} : { changed })
    }
  }

  /**
   * 写回原文件。租约(插件 RPC)与编辑器画布共用这一份,路径重验只写一次。
   * (原先内联在 `save` 里;画布保存加进来之后抽出,两处复制的话迟早只改一份。)
   */
  private async saveResolved(target: SaveTarget, scope?: DocumentCallScope): Promise<{ snapshot: DocumentSessionSnapshot; changed?: DocumentChanged }> {
    /*
      需求：写回原文件之前，把租约里的相对路径**重新走一遍路径门**，并确认算出来的规范
      路径与租约里的逐字相同。打开与保存之间父目录可能被换成软链（或目标被换成软链 /
      非普通文件）—— 只信打开时那一次的结果，保存就会写穿链接，落到工作区外面。
      不满足会怎样：表现为「保存成功」而工作区里的文件纹丝不动，内容出现在别的目录。
    */
    const recheck = await resolveWorkspacePath({ workspaceId: target.workspaceId, workspaceRoot: target.workspaceRoot }, target.path, 'read')
    if (recheck.absolutePath !== target.absolutePath) throw invalidArgument('document path changed on disk')
    /*
      需求：**过了路径重验、动手写回之前**结算一次。这会话可能刚从队列里排出来
      （另一次 open 在起引擎），期间 RPC 已经超时 / 插件已被禁用 / 工具被中止 ——
      越过去才写的画，插件那边已经收到失败，重试就会把这一次的写又做一遍。
      ★ 只检查，不回滚：`sessions.save` 一旦开始就取消不了（见 export 的同款说明）。
    */
    if (scope !== undefined) {
      scope.signal?.throwIfAborted()
      scope.markSideEffectStarted?.()
    }
    const managerScope = { accountScope: target.accountScope, workspaceId: target.workspaceId }
    const before = this.options.sessions.snapshot(target.sessionId, managerScope)
    const snapshot = await this.options.sessions.save({ sessionId: target.sessionId, scope: managerScope })
    const changed = before.modelRevision !== before.savedRevision
      ? { path: target.path, kind: 'modified' as const }
      : undefined
    return { snapshot, ...(changed === undefined ? {} : { changed }) }
  }

  private async export(
    pluginId: string,
    rawParams: unknown,
    scope: DocumentCallScope,
    accountScope: string
  ): Promise<PluginDocumentResponse> {
    const params = objectParams(rawParams)
    const sessionId = requiredString(params.sessionId, 'sessionId')
    const lease = await this.requireLease(pluginId, sessionId, scope, accountScope)
    const path = requiredString(params.path, 'path')
    const format = params.format
    if (!isDocumentFormat(format)) throw invalidArgument('format is invalid')
    if (params.overwrite !== undefined && typeof params.overwrite !== 'boolean') throw invalidArgument('overwrite is invalid')
    const overwrite = params.overwrite === true
    const target = await resolveWorkspacePath(scope, path, 'write')
    if (target.absolutePath === lease.absolutePath) throw invalidArgument('use documents.save to write the open document')
    /*
      需求：默认不许覆盖已存在的导出目标（「另存为」盖掉别人放在那儿的文件是静默丢数据）。
      要覆盖得由调用方显式说 `overwrite: true`。
    */
    if (target.existed && !overwrite) throw invalidArgument('export target already exists; pass overwrite to replace it')
    /*
      需求：即使目标存在且允许覆盖，也不许写到**这个会话正在编辑的文件**上 —— 换个名字
      （硬链 / 另一个拼写）也不行，那绕过会话的修订号直接改盘，之后保存的冲突检查会对不上。
    */
    if (target.existed && await sameFile(target.absolutePath, lease.absolutePath)) {
      throw invalidArgument('use documents.save to write the open document')
    }
    /*
      需求：**导出引擎是一段我们取消不了的外部工作**（解码 / 重排 / 写盘都在
      helper 里）。一旦发起，取消只能变成「我们不再等它」—— 它可能已经把文件
      写到一半。所以越过这一点之前的失败是普通拒绝（路径门 / 覆盖门 / 排队），
      之后的取消必须报 result_unknown，让插件不要把导出做第二遍。
    */
    scope.signal?.throwIfAborted()
    scope.markSideEffectStarted?.()
    const result = await this.options.sessions.exportDocument({
      sessionId,
      scope: { accountScope: lease.accountScope, workspaceId: lease.workspaceId },
      outputPath: target.absolutePath,
      format,
      overwrite
    })
    lease.lastUsed = this.now()
    const changed: DocumentChanged = { path: target.relative, kind: target.existed ? 'modified' : 'created' }
    /*
      需求：响应里**不带 `outputPath`**（那是宿主的绝对路径）。插件要的是「导出到了哪个
      工作区文件」，绝对路径只会泄漏宿主的目录结构。
    */
    return {
      data: { path: target.relative, snapshot: result.snapshot },
      summary: `exported ${lease.path} to ${target.relative}`,
      changed
    }
  }

  private async getState(
    pluginId: string,
    rawParams: unknown,
    scope: DocumentCallScope,
    accountScope: string
  ): Promise<PluginDocumentResponse> {
    const params = objectParams(rawParams)
    const sessionId = requiredString(params.sessionId, 'sessionId')
    const lease = await this.requireLease(pluginId, sessionId, scope, accountScope)
    const snapshot = this.options.sessions.snapshot(sessionId, { accountScope: lease.accountScope, workspaceId: lease.workspaceId })
    lease.lastUsed = this.now()
    return { data: snapshot, summary: `read state for ${lease.path}` }
  }

  /**
   * 只读查询（正文 / 单元格 / 大纲 / 版面）。
   * 需求：Agent 改文档前必须先读到**活动模型**里的内容（含未保存的修改）；普通 Read 工具对
   * OOXML 只会报二进制，读盘上文件又会漏掉用户还没保存的输入。
   * `request` 原样交给会话管理器，由 `validateQuery` 统一收窄，这里不另写一份规则。
   */
  private async query(
    pluginId: string,
    rawParams: unknown,
    scope: DocumentCallScope,
    accountScope: string
  ): Promise<PluginDocumentResponse> {
    const params = objectParams(rawParams)
    const sessionId = requiredString(params.sessionId, 'sessionId')
    const lease = await this.requireLease(pluginId, sessionId, scope, accountScope)
    const result = await this.options.sessions.query({
      sessionId,
      scope: { accountScope: lease.accountScope, workspaceId: lease.workspaceId },
      request: params.request
    })
    lease.lastUsed = this.now()
    return { data: result, summary: `queried ${lease.path}` }
  }

  private async getOperation(
    pluginId: string,
    rawParams: unknown,
    scope: DocumentCallScope,
    accountScope: string
  ): Promise<PluginDocumentResponse> {
    const params = objectParams(rawParams)
    const sessionId = requiredString(params.sessionId, 'sessionId')
    const operationId = requiredString(params.operationId, 'operationId')
    /*
      需求：先过租约门再看回执表。回执表是按 operationId 平铺的，不先确认「这个 sessionId
      是这个插件、这个账户的」，一次猜中的 operationId 就能读到别人的操作结果。
    */
    const lease = await this.requireLease(pluginId, sessionId, scope, accountScope)
    const operation = this.options.sessions.getOperation(operationId)
    lease.lastUsed = this.now()
    if (operation === undefined || operation.sessionId !== sessionId) return { data: { status: 'missing' }, summary: `operation ${operationId} is missing` }
    return { data: operation, summary: `read operation ${operationId}` }
  }

  private async close(
    pluginId: string,
    rawParams: unknown,
    scope: DocumentCallScope,
    accountScope: string
  ): Promise<PluginDocumentResponse> {
    const params = objectParams(rawParams)
    const sessionId = requiredString(params.sessionId, 'sessionId')
    const lease = await this.requireLease(pluginId, sessionId, scope, accountScope)
    const result = await this.releaseLease(lease)
    /*
      需求：显式 close 之后租约**立刻失效**，哪怕会话因为脏而保留了下来（`dirty:true`）——
      下一次 open 会重新建租约、重新开视图并把改动找回来。
      不满足会怎样：插件以为关掉了，getState 却还能读到内容，「关闭」变成一句说了不做的话。
    */
    this.dropLease(lease)
    return { data: result, summary: `closed ${lease.path}` }
  }

  /**
   * 取出租约，并确认它现在真的还能用。
   *
   * ★ 账户 / 工作区根不符与「不存在」报**同一个**错：区分开的话，一个插件可以通过
   * 错误码探测别的账户、别的工作区里打开了哪些文档（同 `DocumentSessionManager.require`）。
   */
  private async requireLease(
    pluginId: string,
    sessionId: string,
    scope: DocumentCallScope,
    accountScope: string
  ): Promise<DocumentLease> {
    const lease = this.leases.get(leaseKey(pluginId, sessionId, scope.workspaceId))
    if (lease === undefined || lease.accountScope !== accountScope) throw sessionClosed()
    let root: string
    try {
      root = await canonicalWorkspaceRoot(scope.workspaceRoot)
    } catch {
      // 根都解析不出来（工作区被删 / 账户切走换了数据目录）：当作会话不存在
      throw sessionClosed()
    }
    if (root !== lease.workspaceRoot) throw sessionClosed()
    /*
      需求：租约还在不代表会话还在 —— 别的消费者会收掉会话（原生编辑器关掉最后一个视图、
      provider 被撤销、账户退出）。这里用一次 snapshot 验证存活，让 getOperation / close
      这些「不直接打引擎」的方法也拿不到一个已经消失的会话的内容。
    */
    this.options.sessions.snapshot(lease.sessionId, { accountScope: lease.accountScope, workspaceId: lease.workspaceId })
    return lease
  }

  /**
   * 放掉这个租约的那一个视图。★ 永远非 force：脏会话由会话管理器保留，
   * 由 `assertCanRelease` 决定要不要放行。
   */
  private releaseLease(lease: DocumentLease): Promise<{ closed: boolean; dirty: boolean }> {
    return this.options.sessions.release({
      sessionId: lease.sessionId,
      scope: { accountScope: lease.accountScope, workspaceId: lease.workspaceId },
      viewId: lease.viewId
    })
  }

  private dropLease(lease: DocumentLease): void {
    this.leases.delete(leaseKey(lease.pluginId, lease.sessionId, lease.workspaceId))
    this.leasesByPath.delete(pathKey(lease.pluginId, lease.accountScope, lease.workspaceId, lease.absolutePath))
  }

  /** 租约对应的会话还活着时返回它的当前快照与能力；会话已消失返回 `null` */
  private readLeaseState(lease: DocumentLease): { snapshot: DocumentSessionSnapshot; capabilities: DocumentCapabilities } | null {
    const scope = { accountScope: lease.accountScope, workspaceId: lease.workspaceId }
    try {
      return {
        snapshot: this.options.sessions.snapshot(lease.sessionId, scope),
        capabilities: this.options.sessions.capabilities(lease.sessionId, scope)
      }
    } catch (error) {
      // 需求：崩溃不等于关闭；不能丢掉仍属于本插件的 view，造成后续清理泄漏。
      if (error instanceof DocumentEngineError && error.code === 'session_closed') return null
      throw error
    }
  }

  /** 这个脏会话算不算 `pluginId` 的东西（无 id 时一律算） */
  private ownsSession(sessionId: string, pluginId: string | undefined): boolean {
    if (pluginId === undefined) return true
    const owner = this.owners.get(sessionId)
    if (owner !== undefined && (owner.pluginIds.has(pluginId) || owner.providerId.startsWith(`${pluginId}/`))) return true
    for (const lease of this.leases.values()) {
      if (lease.pluginId === pluginId && lease.sessionId === sessionId) return true
    }
    return false
  }

  /**
   * 清掉「既不脏、也没有租约」的会话归属记录。
   *
   * 需求：归属表要认得**无租约的脏会话**，所以它不能跟着租约一起删；但也不能无上限地
   * 长下去（每开过一次文档就留一条）。这里借 `assertCanRelease`（禁用 / 退出 / 卸载时才调）
   * 的时机做一次收敛。判据是「不脏且无租约」= 那份文档此刻没有任何可以丢掉的东西。
   */
  private forgetOwnersOfSettledSessions(dirtySessionIds: readonly string[]): void {
    const live = new Set(dirtySessionIds)
    for (const lease of this.leases.values()) live.add(lease.sessionId)
    for (const sessionId of [...this.owners.keys()]) {
      if (!live.has(sessionId)) this.owners.delete(sessionId)
    }
  }

  /**
   * 串行队列。★ 前一项失败不能卡住后一项：用 `then(run, run)` 接续（同
   * `document-engine/manager.ts` 的 `enqueue`），返回给调用方的仍是这一项自己的结果 / 错误。
   */
  private enqueue<T>(task: () => Promise<T>): Promise<T> {
    const run = this.queue.then(task, task)
    this.queue = run.then(() => undefined, () => undefined)
    return run
  }
}

export function engineCandidates(pluginId: string, manifest: PluginManifest): string[] {
  const candidates: string[] = []
  for (const editor of manifest.contributes.customEditors) {
    const engine = editor.documentEngine
    if (engine !== undefined) addCandidate(candidates, qualifyEngine(pluginId, engine))
  }
  for (const engine of manifest.contributes.documentEngines ?? []) addCandidate(candidates, `${pluginId}/${engine.id}`)
  return candidates
}

export function toCapabilityError(error: unknown): PluginCapabilityError {
  if (error instanceof PluginCapabilityError) return error
  if (error instanceof DocumentEngineError) {
    const code: PluginErrorCode = documentErrorToPluginCode(error.code)
    return new PluginCapabilityError(code, `[${error.code}] ${error.message}`)
  }
  if (error instanceof Error) return new PluginCapabilityError('internal_error', error.message)
  return new PluginCapabilityError('internal_error', 'document request failed')
}

function documentErrorToPluginCode(code: DocumentEngineError['code']): PluginErrorCode {
  switch (code) {
    case 'invalid_operation':
    case 'unsupported_operation':
    case 'unsupported_format':
    case 'unsupported_environment':
      return 'invalid_argument'
    case 'stale_revision':
    case 'stale_generation':
    case 'disk_conflict':
    case 'session_closed':
    case 'result_unknown':
    case 'busy':
      return 'rejected'
    case 'engine_unavailable':
    case 'engine_crashed':
    case 'timeout':
    case 'io':
    case 'macro_denied':
      return 'internal_error'
  }
}

function qualifyEngine(pluginId: string, engine: string): string {
  return engine.includes('/') ? engine : `${pluginId}/${engine}`
}

function assertEngineDependency(pluginId: string, manifest: PluginManifest, providerId: string): void {
  if (isAllowedEngine(pluginId, manifest, providerId)) return
  throw invalidArgument('engine must be declared in dependencies')
}

function isAllowedEngine(pluginId: string, manifest: PluginManifest, providerId: string): boolean {
  const parts = splitProviderId(providerId)
  if (parts === null) return false
  if (parts.pluginId === pluginId) return (manifest.contributes.documentEngines ?? []).some((engine) => engine.id === parts.engineId)
  return Object.hasOwn(manifest.dependencies, parts.pluginId)
}

function addCandidate(candidates: string[], value: string): void {
  if (!candidates.includes(value)) candidates.push(value)
}

function objectParams(raw: unknown): Record<string, unknown> {
  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) throw invalidArgument('params must be an object')
  return raw as Record<string, unknown>
}

function requiredString(value: unknown, name: string): string {
  if (typeof value !== 'string' || value === '' || value.includes('\0')) throw invalidArgument(`${name} is required`)
  return value
}

function requiredInteger(value: unknown, name: string): number {
  if (typeof value !== 'number' || !Number.isInteger(value) || value < 0) throw invalidArgument(`${name} is invalid`)
  return value
}

function invalidArgument(message: string): PluginCapabilityError {
  return new PluginCapabilityError('invalid_argument', message)
}

function sessionClosed(): PluginCapabilityError {
  return new PluginCapabilityError('rejected', '[session_closed] no such document session')
}

function engineUnavailable(providerId: string): DocumentEngineError {
  return new DocumentEngineError('engine_unavailable', `document engine ${providerId} is not installed or not enabled`)
}

/** 路径门的结果：规范根、规范绝对路径、工作区相对路径（`/` 分隔）、目标此前是否已存在 */
interface ResolvedWorkspacePath {
  root: string
  absolutePath: string
  relative: string
  existed: boolean
}

/**
 * 插件传来的工作区相对路径 → 可以交给引擎的规范绝对路径。
 *
 * 两道门，缺一不可：
 * - 词法（`narrowWorkspacePath`）：只收相对路径、拒 `..` / NUL / 超长，不碰文件系统；
 * - 落点（**逐段 lstat**）：工作区里任何一级是软链就拒。只对最终路径做一次 `realpath`
 *   挡不住「父目录是软链、里面的文件是普通文件」—— 那条路会把读写带出工作区。
 *
 * `access = 'read'`：目标必须存在且是普通文件（打开 / 保存前重验走这条）；
 * `access = 'write'`：目标可以不存在（导出），存在就必须是普通文件、父目录必须存在。
 */
async function resolveWorkspacePath(
  scope: DocumentCallScope,
  rawPath: string,
  access: 'read' | 'write'
): Promise<ResolvedWorkspacePath> {
  if (scope.workspaceRoot === '') throw invalidArgument('[unsupported_environment] document operations require a local workspace')
  const narrowed = narrowWorkspacePath(scope.workspaceRoot, rawPath)
  if (!narrowed.ok) throw invalidArgument(narrowed.reason)
  const lexical = relative(resolve(scope.workspaceRoot), narrowed.value)
  if (lexical === '' || lexical.startsWith(`..${sep}`) || lexical === '..' || isAbsolute(lexical)) {
    throw invalidArgument('path must stay inside the workspace')
  }
  const root = await canonicalWorkspaceRoot(scope.workspaceRoot)
  const parts = lexical.split(sep)
  let current = root
  for (const [index, part] of parts.entries()) {
    current = join(current, part)
    const last = index === parts.length - 1
    let info: Stats
    try {
      info = await lstat(current)
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw invalidArgument('cannot inspect the document path')
      if (!last) throw invalidArgument('document parent does not exist')
      if (access === 'read') throw invalidArgument('document does not exist')
      return { root, absolutePath: current, relative: parts.join('/'), existed: false }
    }
    if (info.isSymbolicLink()) throw invalidArgument('workspace document paths cannot contain symlinks')
    if (!last) {
      if (!info.isDirectory()) throw invalidArgument('document parent must be a directory')
      continue
    }
    if (!info.isFile()) throw invalidArgument(access === 'read' ? 'document must be a regular file' : 'export target must be a regular file')
  }
  return { root, absolutePath: current, relative: parts.join('/'), existed: true }
}

/**
 * 工作区根的规范路径。★ 根本身可以是软链（macOS 的 `/var` → `/private/var`），
 * 所以**只归一根本身、不拒绝它**；归一的结果才是所有会话路径的基准。
 */
async function canonicalWorkspaceRoot(workspaceRoot: string): Promise<string> {
  try {
    return await realpath(resolve(workspaceRoot))
  } catch {
    throw invalidArgument('workspace root does not exist')
  }
}

/** 两个路径是否指向同一个文件：先比 inode（挡硬链），再比规范路径；任一解析不了就算不同 */
async function sameFile(a: string, b: string): Promise<boolean> {
  try {
    const [left, right] = await Promise.all([lstat(a), lstat(b)])
    if (left.dev === right.dev && left.ino === right.ino) return true
  } catch {
    // 有一边不在盘上：退化成规范路径比较
  }
  try {
    const [left, right] = await Promise.all([realpath(a), realpath(b)])
    return left === right
  } catch {
    return false
  }
}

function leaseKey(pluginId: string, sessionId: string, workspaceId: string): string {
  return `${pluginId}\0${sessionId}\0${workspaceId}`
}

function pathKey(pluginId: string, accountScope: string, workspaceId: string, absolutePath: string): string {
  return `${pluginId}\0${accountScope}\0${workspaceId}\0${absolutePath}`
}
