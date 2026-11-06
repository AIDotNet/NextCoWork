/**
 * 阻塞式人机往返不止一种 —— 方案 §4.6。
 *
 * 参考实现里有 `agent:tool-approval`(工具授权)和 `agent:ask-user`(Agent 反问),
 * 各自还配了一个重连恢复频道;再加上截图里「任务完成 / 权限审批 / **计划审批**」
 * 三类通知音效,说明还有第三种。
 *
 * **三种是同一个机制**:内核挂起 → 登记待决项 → UI 呈现 → 用户回答 → 内核恢复。
 * 分三套写就是三张待决表、三个重连恢复频道、三次 IPC 签名变更。
 * 现在合并成一个 union 是十几行。
 */
import type { PermissionDecision } from './permission'
import type { PlanApprovalAction } from '../domain/plan-file'
import type { WorkflowState } from './workflow'

/**
 * 工作流开跑前的三个去向。`revise` 必须带反馈 —— 「改一下」但不说改什么,
 * 模型只能原样再交一遍。
 */
export type WorkflowApprovalAction = 'run' | 'revise' | 'cancel'

/** 一道题里的一个候选项。`description` 是选项下面那行小字,模型可以不给。 */
export interface AskUserOption {
  label: string
  description?: string
}

/**
 * ★ **一次交互承载多道题,而不是一道** —— 模型在一个决策点上要问的往往不止一件事
 * (「改哪个模块」+「要不要顺带加测试」)。一道一问的话,内核会连续挂起两次,
 * 用户答完第一道才看到第二道,而这两道题本来是同一个决策;中间还夹着一次模型往返,
 * 用户改主意时已经无从回退。
 *
 * 三种题型压在同一个结构里,靠两个布尔量区分,而不是再开一个 union:
 * - 单选:`multiSelect: false` + `options`
 * - 多选:`multiSelect: true` + `options`
 * - 纯问答:`options` 为空(此时 `allowFreeform` 必须为真,否则这道题无法回答)
 *
 * `allowFreeform` 和 `options` 正交 —— 「给了选项,但也允许自己写」是最常用的一档,
 * 也就是界面上的「其它」。
 */
export interface AskUserQuestion {
  /**
   * 短标签(如「修复范围」),界面上是这道题的角标,回答回给模型时也用它作键 ——
   * 多道题的回答必须能被模型对上号,靠下标对是脆的(模型会重排)。
   */
  header: string
  question: string
  options: AskUserOption[]
  multiSelect: boolean
  allowFreeform: boolean
}

export type InteractionKind = PendingInteraction['kind']

export type PendingInteraction =
  | {
      kind: 'tool_permission'
      id: string
      runId: string
      callId: string
      toolName: string
      input: unknown
      readOnly: boolean
      destructive: boolean
      /**
       * 用户点「以后都允许」会被写进 `.next-cowork/settings.local.json` 的那条规则。
       *
       * ★ 由主进程算好带下来,渲染层**只负责显示**、不负责回送 —— 回送什么就写什么的话,
       * 这颗按钮等于开放了一个「往权限文件里写任意一行」的接口。
       */
      suggestedRule?: string
      createdAt: number
    }
  | {
      kind: 'ask_user'
      id: string
      runId: string
      /** 一次可以问多道题 —— 见 `AskUserQuestion` 上的注释 */
      questions: AskUserQuestion[]
      createdAt: number
    }
  | {
      kind: 'goal_proposal'
      id: string
      runId: string
      sessionId: string
      condition: string
      createdAt: number
    }
  | {
      kind: 'plan_approval'
      id: string
      runId: string
      plan: string
      planId: string
      path: string
      createdAt: number
    }
  | {
      kind: 'workflow_approval'
      id: string
      runId: string
      /** 哪一次 `Workflow` 调用在等 —— 卡片据此和对话里那张画布对上 */
      callId: string
      /** 全部 `pending` 的结构(`workflowFromInput`),卡片只画清单,不再画一遍画布 */
      workflow: WorkflowState
      createdAt: number
    }
  /**
   * 交互模式的 widget(`visualize_show_widget` 带 `interactive: true`)在等用户在它里面提交。
   *
   * ★ 题面不在这里 —— 它就是对话里那张 widget 本身。待决项只承担两件事:让 run 挂起
   * 有处可查(重载窗口后仍能恢复),以及给用户一个"不提交了"的出口(dismiss)。
   * 提交从 widget 里发起,按 `callId` 找到这一项(`ipc/widget.ts`)。
   */
  | {
      kind: 'widget_submit'
      id: string
      runId: string
      callId: string
      title: string
      createdAt: number
    }

export type InteractionResponse =
  | { id: string; kind: 'tool_permission'; decision: PermissionDecision }
  /**
   * null = dismiss(用户关掉了框,不是回答)。
   * 否则**每道题一组回答**,顺序与 `questions` 一一对应;单选题也是长度为 1 的数组,
   * 而不是裸字符串 —— 两种形状会让下游每处都得先判类型。
   */
  | { id: string; kind: 'ask_user'; answers: string[][] | null }
  | { id: string; kind: 'plan_approval'; action: PlanApprovalAction; feedback?: string }
  | { id: string; kind: 'goal_proposal'; approved: boolean }
  | { id: string; kind: 'workflow_approval'; action: WorkflowApprovalAction; feedback?: string }
  /** null = 用户关掉了,没有提交。否则是 widget `submit()` 的值的 JSON 文本 */
  | { id: string; kind: 'widget_submit'; value: string | null }

/** 待决项的最终去向。`aborted` 是中断路径写进去的(方案 §4.8 第 2 步)。 */
export type InteractionOutcome =
  | { status: 'answered'; response: InteractionResponse }
  | { status: 'aborted' }
  | { status: 'expired' }

/**
 * ★ 待决状态必须是**可查询的持久状态**,不能只是一个挂着的 Promise。
 * 失败模式很具体:渲染层重载 / 窗口关闭 / 崩溃 → promise 永不 settle →
 * 整个 run 无声挂死,握着一个打开的上游流和一个未完成的工具调用,UI 上什么都看不到。
 *
 * 所以 `agent:attach` 的返回值里带 `pendingInteractions`,
 * `agent:listInteractions` 是它的独立查询入口。
 */

/** 三类通知音效直接挂在这上面(方案 §4.6)。 */
export const INTERACTION_SOUND: Record<InteractionKind, 'approval' | 'plan' | 'ask'> = {
  tool_permission: 'approval',
  plan_approval: 'plan',
  workflow_approval: 'plan',
  ask_user: 'ask',
  goal_proposal: 'approval',
  widget_submit: 'ask'
}

export function interactionTitle(i: PendingInteraction): string {
  switch (i.kind) {
    case 'tool_permission':
      return `允许执行 ${i.toolName}?`
    case 'ask_user':
      return i.questions[0]?.question ?? '需要你的回答'
    case 'plan_approval':
      return '确认执行方案?'
    case 'goal_proposal':
      return i.condition
    case 'workflow_approval':
      return `运行工作流「${i.workflow.title}」?`
    case 'widget_submit':
      return i.title
  }
}
