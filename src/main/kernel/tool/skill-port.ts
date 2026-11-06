/**
 * Skill 工具的**遥测端口** —— 「这条 Skill 被取用过一次」这件事的出口。
 *
 * ★★ **它为什么存在:`kernel/tool/builtin/skill.ts` 原来是整个内核里唯一一处
 * 直接 import `state/store` 的地方。** 内核(尤其 `kernel/tool/**`)对 electron
 * 与 store 零依赖,是它能在无头 Node 里被完整单测的前提。
 *
 * ★ **遥测必须是可选、且永不阻断的。** 一条 Skill 正文能不能取回来,和
 * 「我们记不记得住它被用过」是两件事 —— 端口没装、或它内部抛错,都只该被吞掉,
 * 不能影响这次 `Skill` 调用的结果。工具里那句 `try/catch` 就是这条承诺的落点。
 *
 * 安装点在 `runtime.ts` 的 `installSkillToolPorts`(生产)或测试里自行装。
 */
export interface SkillToolPorts {
  /** 记一次「这条 Skill 被取用了」。`workspaceId` 可缺(无头调用没有工作区)。 */
  recordSkillTrigger(skillId: string, workspaceId?: string): void
}

let ports: SkillToolPorts | undefined

export function installSkillToolPorts(next: SkillToolPorts): void {
  ports = next
}

/** 测试专用:清掉注入的端口,免得跨用例泄漏。 */
export function resetSkillToolPortsForTest(): void {
  ports = undefined
}

/** 取用一次。端口没装时是一次 no-op —— 见文件头那条「遥测永不阻断」。 */
export function recordSkillTrigger(skillId: string, workspaceId?: string): void {
  ports?.recordSkillTrigger(skillId, workspaceId)
}
