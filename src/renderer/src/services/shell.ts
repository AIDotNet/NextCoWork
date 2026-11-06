/**
 * Agent 的 shell —— 服务层(组件不碰频道字符串,协议 §9)。
 *
 * 需求:工具卡片上那颗停止按钮只想掐掉**这一条**命令。它和 `abortRun`
 * (停整轮)是两条不同的意图,所以也不放进 `services/agent.ts` —— 放在一起的话,
 * 下一个人很容易顺手在停止按钮上改调 `abortRun`,而那会把模型写到一半的回复
 * 一起停掉,且没有任何报错。
 */
import type { BackgroundShellInfo } from '../../../shared/domain/shell'
import { invoke, on } from './ipc'

/**
 * 停掉某次工具调用正在跑的命令。
 *
 * 返回 `false` = 它已经不在跑了(点击与收尾撞在同一刻)。**这不是错误**,
 * 调用方不必提示:用户要的结果已经达成。
 */
export function stopToolCall(runId: string, callId: string): Promise<boolean> {
  return invoke('shell:stopToolCall', { runId, callId })
}

/**
 * 把某次工具调用正在跑的命令转去后台:不杀它,只是不再等它。
 *
 * 返回 `false` = 没转成(已经跑完 / 环境不支持 / 后台名额已满)。同样不是错误。
 */
export function backgroundToolCall(runId: string, callId: string): Promise<boolean> {
  return invoke('shell:backgroundToolCall', { runId, callId })
}

/** 这个工作区里 agent 开过的后台 shell(含已结束的)。之后的变化订阅 `onBackgroundShellsChanged`。 */
export function listBackgroundShells(workspaceId: string): Promise<BackgroundShellInfo[]> {
  return invoke('shell:listBackground', { workspaceId })
}

/** 看一眼输出尾巴,**不消费**模型那份 `BashOutput` 缓冲。`null` = 已被淘汰。 */
export function peekBackgroundShell(id: string): Promise<{ info: BackgroundShellInfo; output: string } | null> {
  return invoke('shell:peekBackground', { id })
}

/** 停掉一个后台 shell,与模型调 `KillShell` 是同一件事。`null` = 已被淘汰。 */
export function killBackgroundShell(id: string): Promise<BackgroundShellInfo | null> {
  return invoke('shell:killBackground', { id })
}

/** 后台 shell 列表变了(全量、所有工作区)。返回退订函数。 */
export function onBackgroundShellsChanged(callback: (shells: BackgroundShellInfo[]) => void): () => void {
  return on('shell:backgroundChanged', ({ shells }) => callback(shells))
}
