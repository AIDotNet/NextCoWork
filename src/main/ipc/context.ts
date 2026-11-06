import type { ContextPreview } from '../../shared/agent/context-management'
import { effectiveContextWindow } from '../../shared/agent/context-management'
import { messagesForModel } from '../../shared/agent/compaction'
import { normalizeEnvironmentRef } from '../../shared/domain/environment'
import type { ContextPreviewRequest } from '../../shared/ipc/contract'
import { resolveMaxOutputTokens } from '../../shared/agent/run-request'
import type { AgentMessage } from '../../shared/agent/message'
import { userMessage } from '../../shared/agent/message'
import { ulid } from '../../shared/util/id'
import { agentRegistry } from '../kernel/agent/registry'
import { modePromptFor, modeRegistry } from '../kernel/mode/registry'
import { skillRegistry } from '../kernel/skill/registry'
import { taskTool } from '../kernel/tool/builtin/task'
import { ToolRegistry } from '../kernel/tool/registry'
import { resolveAnywhere } from '../kernel/tool/path-guard'
import { assemble, estimateMessages } from '../kernel/context-assembler'
import { compactConversation } from '../kernel/compaction/compact'
import { resolveCompactBinding } from '../kernel/compaction/binding'
import { readAttachableFile, type AttachmentToolNames } from '../kernel/compaction/attachments'
import { connectedWorkspaceMcpTools, getHost, getRouter, getTools, loadInstructions } from '../runtime'
import { store } from '../state/store'
import { runs } from '../kernel/run-registry'
import { currentConfigScope } from '../db/config-profile'
import { IpcError } from './errors'

const SUMMARY_TIMEOUT_MS = 180_000

/**
 * 手动压缩(/compact)—— `AgentSession.compact` 那条自动路径的同胞,区别只在触发者
 * 是用户、可以带一句「重点保留什么」,以及续接语不要求模型接着干(用户会自己发下一句)。
 *
 * ★ 摘要提示词、PTL 重试、重附件、边界消息的形状全部来自 `kernel/compaction/compact.ts`,
 * 和自动那条读同一份 —— 原先这里有旧摘要提示词和 digest 的一整套副本,两份各自演化。
 *
 * ★ 结果是一条**提交进转录**的边界消息,不再是另一张表里的检查点:下一次 run 从
 * `messagesForModel` 读到的就是它,重开会话也一样;完整转录仍在,界面照常画出边界之前的对话。
 *
 * ★ 只在会话空闲时调用(界面在生成中禁用了入口):跑着的 run 把上下文冻在内存里,
 * 此时插一条边界会和它随后提交的消息交错。
 */
export async function compactContext(req: { sessionId: string; instructions?: string }): Promise<{
  message: AgentMessage
  inputTokens: number
}> {
  const release = runs.acquireSessionOperation(req.sessionId)
  try { return await compactSessionContext(req) } finally { release() }
}

async function compactSessionContext(req: { sessionId: string; instructions?: string }): Promise<{
  message: AgentMessage
  inputTokens: number
}> {
  const session = store.getSession(req.sessionId)
  if (session === undefined) throw new Error('会话不存在')
  const history = store.getHistory(req.sessionId)
  if (history.length === 0) throw new Error('这段对话还没有可压缩的内容')
  const scope = currentConfigScope()
  const revision = JSON.stringify(history)

  const signal = AbortSignal.timeout(SUMMARY_TIMEOUT_MS)
  const root = session.rootPathAtCreation
  const workspace = store.getWorkspace(session.workspaceId)
  // 远端工作区的文件在另一台机器上,这条菜单路径不为重附件去租一条 SSH 连接 —— 少附文件,摘要照常。
  const local = workspace === undefined || normalizeEnvironmentRef(workspace.environment).kind !== 'connection'

  /*
    需求:压缩可以配成另一个模型、另一个思考档位(设置 › 通用 › Agent,工作区可覆盖)。
    解析、回落(配置的模型当前解析不到就静默换回会话模型)与档位归一化和自动压缩
    **共用同一份** —— `kernel/compaction/binding.ts`。各写一份的话,两条路径会分头演化,
    而症状是「菜单里压出来的摘要和自动压出来的不是一个模型写的」,没有任何报错。

    ★ `'inherit'` 在这条路径上落到 `session.thinking`:手动 /compact 时没有 run,
    「本轮档位」这个东西不存在,会话记录上那个才是用户此刻选的。
  */
  const settings = store.getSettings()
  const binding = resolveCompactBinding({
    resolveModel: (model, modelProviderId) => getRouter().resolveModel(model, modelProviderId),
    settings: {
      model: settings.compactModel,
      ...(settings.compactModelProviderId === undefined ? {} : { modelProviderId: settings.compactModelProviderId }),
      thinking: settings.compactThinking
    },
    ...(workspace === undefined ? {} : { workspace: workspace.settings }),
    session: {
      model: session.model,
      ...(session.modelProviderId === undefined ? {} : { modelProviderId: session.modelProviderId }),
      thinking: session.thinking
    },
    maxOutputTokens: settings.maxOutputTokens,
    warn: (message) => { getHost().logger.warn(message) }
  })
  if (binding === undefined) throw new Error('压缩模型不可用')

  const result = await compactConversation({
    messages: history,
    trigger: 'manual',
    ...(req.instructions === undefined ? {} : { instructions: req.instructions }),
    preTokens: estimateMessages(messagesForModel(history)),
    autoContinue: false,
    // 协议窗口:摘要请求只受**压缩模型**真实上限约束,和「最大上下文」计费开关无关。
    protocolWindow: binding.protocolWindow,
    summaryMaxOutputTokens: binding.maxOutputTokens,
    summaryThinking: binding.thinking,
    summaryModel: {
      model: binding.model,
      ...(binding.modelProviderId === undefined ? {} : { modelProviderId: binding.modelProviderId }),
      thinking: binding.thinking,
      fellBack: binding.fellBack
    },
    send: (request) => getRouter().stream(
      {
        model: binding.model,
        /*
          摘要**默认**和正文走同一家:它读的是同一段对话,漂到另一家既换了口径也换了账单。
          这条理由没变 —— 变的是它现在由配置表达(`compactModel` 出厂是空串 = 跟随会话),
          只有用户显式选了别的压缩模型,这里才会是另一家。
        */
        ...(binding.modelProviderId === undefined ? {} : { modelProviderId: binding.modelProviderId }),
        system: request.system,
        messages: request.messages,
        tools: [],
        maxOutputTokens: request.maxOutputTokens,
        thinkingLevel: request.thinkingLevel
      },
      signal,
      { workspaceId: session.workspaceId, runId: `${req.sessionId}:compact:manual`, sessionId: req.sessionId }
    ),
    attachments: {
      tools: attachmentTools(),
      readFile: async (path) => {
        if (!local) return undefined
        try {
          return await readAttachableFile(getHost().fs, resolveAnywhere(root, path).abs)
        } catch {
          return undefined
        }
      }
    },
    newId: () => ulid(),
    now: getHost().clock.now(),
    signal
  })
  if (!result.ok) throw new Error(result.error.message)
  // 目标状态、导入同步等并非 run，也可能改写历史。旧摘要不能隐藏这期间的新消息。
  if (currentConfigScope() !== scope || store.getSession(req.sessionId)?.workspaceId !== session.workspaceId
    || JSON.stringify(store.getHistory(req.sessionId)) !== revision) {
    throw new IpcError('conflict', '摘要期间会话内容已变化，请重新压缩上下文')
  }
  store.commitMessage(req.sessionId, result.message)
  return { message: result.message, inputTokens: result.boundary.postTokens }
}

/** 同 `AgentSession.attachmentTools`:外部名从注册表查,撞名加后缀时字面量会静默失配。 */
function attachmentTools(): AttachmentToolNames {
  const registry = getTools()
  const name = (id: string): string | undefined => registry.byInternalId(id)?.externalName
  const file = new Set([name('Read'), name('Write'), name('Edit')].filter((n): n is string => n !== undefined))
  const todo = name('TodoWrite')
  const skill = name('Skill')
  return { file, ...(todo === undefined ? {} : { todo }), ...(skill === undefined ? {} : { skill }) }
}

/**
 * 还没发过请求时的占用归因 —— 把这一轮**会**发出去的东西装配一遍,但不发。
 *
 * ★ **一个副作用都不留。** 它读的全是进程里已经有的那份:全局工具注册表、
 * 技能注册表当前的内容、这个工作区**已经连上**的 MCP。三件真正会动东西的事
 * 一件都不做 —— 不 `refreshSkills`(那会 `replaceAll` 这个工作区那一份注册表,父 run
 * 跑到一半时把它换掉),不 `prepareWorkspaceMcp`(那会去连服务器),不租环境。
 * 理由很朴素:这条通道是用户点开一个菜单时被调的,而一个菜单不该拉起子进程。
 *
 * ★ 代价说清楚:**MCP 还没连上时那一档就是 0**,等它连上再点开才有数。
 * 这比「为了画个百分比先把所有服务器拉起来」要诚实,也比「装作没有这一档」要有用。
 *
 * ★ `messages` 那一档不是估的空值,是**从库里读的真历史** —— 所以重开一个聊过
 * 很久的老会话,这张卡当场就是对的,不必再发一条消息去把它唤醒。
 */
export async function previewContext(req: ContextPreviewRequest): Promise<ContextPreview | undefined> {
  const workspace = store.getWorkspace(req.workspaceId)
  if (workspace === undefined) return undefined

  /*
    ★ 复刻 `runtime.ts` 的 `snapshotRunTools`,但**不要求一个环境**。
    远程与否只影响两件事(去掉 MCP/浏览器、换掉 Bash 的描述),而前者从
    工作区的环境引用就能判断,后者只是几十个 token 的措辞差 —— 为它去租一条
    SSH 连接是本末倒置。
  */
  const remote = normalizeEnvironmentRef(workspace.environment).kind === 'connection'
  const registry = new ToolRegistry()
  for (const tool of getTools().snapshot()) {
    if (remote && (tool.source.kind === 'mcp' || tool.internalId.startsWith('browser_'))) continue
    registry.register(tool)
  }
  registry.register(taskTool(agentRegistry(req.workspaceId).list()))
  if (!remote) {
    for (const tool of connectedWorkspaceMcpTools(req.workspaceId)?.snapshot() ?? []) registry.register(tool)
  }

  const disabled = new Set(store.getDisabledSkillIds())
  const skills = skillRegistry(req.workspaceId).list().filter((s) => !disabled.has(s.id) && !s.unavailableReason)

  /*
    AGENTS.md 算进去 —— `loadInstructions` 不需要一个已经租好的环境,本地工作区
    就是两次文件读。★ 读不到不算失败:远程工作区没连上时它会抛,而「少一档
    说明文字」远远好过「整张卡打不开」。
  */
  let projectInstructions = ''
  try { projectInstructions = await loadInstructions(req.workspaceId) } catch {
    // 远程工作区没连上时保持为空字符串
  }

  const alias = getRouter().resolveModel(req.model, req.modelProviderId)
  const mode = modeRegistry(req.workspaceId).resolve(req.mode)
  const tools = registry.snapshot({
    ...(mode.tools === undefined ? {} : { allowList: mode.tools }),
    network: req.webSearch
  }).map(({ execute: _execute, ...info }) => info)

  /*
    ★ 空会话必须塞一条**占位的空用户消息**,否则 AGENTS.md 根本不会被算进去:
    `decorate()` 把说明块注入的是「数组里第一条 user 消息」,一条都没有时它
    `return messages` 直接走人(见那个 `i === -1`)。于是一个写了两千字 AGENTS.md
    的仓库,在预览里那一档是 0 —— 而真发送时它一定在,因为那时至少有用户这一句。
    预览要回答的是「我下一条发出去会占多少」,所以把那条消息先摆上是**更准**不是更假;
    它自己只贡献一份消息开销(几个 token),落在 `messages` 档里,也是真花的。
  */
  // 只算最后一条压缩边界之后的那段 —— 那才是下一次请求真会发出去的。
  const history = req.sessionId === '' ? [] : messagesForModel(store.getHistory(req.sessionId))
  const messages = history.length > 0
    ? history
    : [userMessage('preview', [{ type: 'text', text: '' }], 0)]

  const { usage } = assemble({
    messages,
    tools,
    skills,
    ...(store.getSettings().personalization !== undefined
      ? { personalization: store.getSettings().personalization }
      : {}),
    mode: mode.id,
    modePrompt: modePromptFor(mode),
    thinking: req.thinking,
    model: req.model,
    ...(req.modelProviderId === undefined ? {} : { modelProviderId: req.modelProviderId }),
    workspaceRoot: workspace.rootPath,
    now: getHost().clock.now(),
    platform: getHost().platform,
    permissionMode: req.permissionMode,
    webSearch: req.webSearch,
    contextWindow: effectiveContextWindow(alias?.contextWindow, req.maxContext === true),
    // 需求：预览装配与正文共用同一个全局输出额度设置；当前返回值不暴露 shouldCompact，但不能让两条装配口径分叉。
    maxOutputTokens: resolveMaxOutputTokens(store.getSettings().maxOutputTokens, alias?.contextWindow),
    supportsThinking: alias?.capabilities.thinking ?? false,
    ...(alias?.reasoningEfforts !== undefined ? { reasoningEfforts: alias.reasoningEfforts } : {}),
    ...(alias?.thinkingConfig !== undefined ? { thinkingConfig: alias.thinkingConfig } : {}),
    /*
      ★ 不给 git 上下文 —— 它要 `environment.spawn`,而那是这个函数唯一拒绝付的代价。
      少掉的是 reminder 里几行分支名和状态,落在 `instructions` 那一档里,
      量级上可以忽略;真发送时它会回来。
      `todoToolName` 同理不给:空会话里推不出 todo,老会话里它只是个名字。
    */
    ...(projectInstructions === '' ? {} : { reminder: { projectInstructions } })
  })

  return { used: usage.used, window: usage.window, segments: usage.segments ?? [] }
}
