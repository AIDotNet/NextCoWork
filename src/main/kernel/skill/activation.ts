/**
 * 「在某个工作区里启用 / 取消启用一条 Skill」之后,`activeSkillIds` 应该变成什么。
 *
 * 需求:设置页的开关(`ipc/skills.ts` 的 `setSkillWorkspaceActive`)和提炼会话写完
 * Skill 后的自动启用走**同一份**规则。两处各写一遍的话,改一处漏一处的症状是
 * 「手动开关正常,自动启用却把用户原来的选择清空了」—— 反之亦然。
 *
 * ★ 空清单 = 全都要。空清单时的任何改动都要先把「全都要」物化成一份显式清单,
 * 否则「关掉一条」是个空操作。完整理由与代价写在 `ipc/skills.ts` 的
 * `setSkillWorkspaceActive` 上(逻辑从那里抽出,说明留在原处)。
 */
export function nextActiveSkillIds(
  current: readonly string[],
  selectionMode: 'all' | 'explicit' | undefined,
  allIds: readonly string[],
  skillId: string,
  active: boolean
): string[] {
  // 隐式的「全都要」在这里物化,否则关掉一条会是个空操作
  const base = current.length === 0 && selectionMode !== 'explicit' ? allIds : current
  return active
    ? [...new Set([...base, skillId])]
    : base.filter((id) => id !== skillId)
}

/**
 * 判断一次设置更新是否真的改变了顺序敏感的 id 清单。
 *
 * 需求:自动启用路径不能用「长度变没变」判断是否需要写设置 —— 以后如果规则
 * 改成替换/整理顺序,同长度的变化会被静默丢掉,表现为 Skill 页显示已启用而
 * 下一轮仍拿旧快照。把判据和 `nextActiveSkillIds` 放在同一个纯模块,避免两处漂移。
 */
export function sameSkillIdList(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && a.every((id, index) => id === b[index])
}

/**
 * frontmatter 的 `name` 可能和目录名不一致(`load.ts` 明确以 name 为准)。
 *
 * 需求:自动启用要按**实际写入的目录**找到扫描结果,不能因为模型把 frontmatter
 * name 写成了另一个合法值就静默漏启用。`skillPath` 可以是本地/远程绝对路径,
 * 所以比较时统一分隔符并匹配路径尾部。
 */
export function matchesProjectSkillPath(skillPath: string, projectSkillsRel: string, directoryName: string): boolean {
  const normalized = skillPath.replace(/\\/g, '/').replace(/^\.\//, '')
  const expected = `${projectSkillsRel}/${directoryName}/SKILL.md`
  return normalized === expected || normalized.endsWith(`/${expected}`)
}

/**
 * 把「Skill 写盘后的异步激活」串在下一轮 run 之前。
 *
 * 需求:run_end 事件先到渲染层,而 `activateWrittenSkills` 还要异步扫描目录并写工作区设置。
 * 不满足会怎样:同一条提炼会话排队的下一轮会在激活完成前启动,继续拿不到刚写的 Skill。
 *
 * ★ 只等待 Skill 激活,不改变普通 run 的结束时序。任务失败也会收敛成已完成的 barrier,
 * 让文件已经落盘但激活失败时仍能继续对话,Skill 页随后可以手动处理。
 */
export class SkillActivationBarrier {
  private readonly pending = new Map<string, Map<Promise<void>, () => void>>()

  // 需求:在同步 beforeFinish 里先挂等待点,不能等 finally 才登记(此时 run_end 已发出)。
  begin(workspaceId: string): () => void {
    const group = this.pending.get(workspaceId) ?? new Map<Promise<void>, () => void>()
    let resolve!: () => void
    const promise = new Promise<void>((done) => { resolve = done })
    const release = (): void => {
      group.delete(promise)
      if (group.size === 0 && this.pending.get(workspaceId) === group) this.pending.delete(workspaceId)
      resolve()
    }
    group.set(promise, release)
    this.pending.set(workspaceId, group)
    return release
  }

  // 需求:等待过程中可能又有另一个子 run 收尾;重新取集合,不能漏掉后来登记的激活。
  async wait(workspaceId: string): Promise<boolean> {
    let waited = false
    for (;;) {
      const group = this.pending.get(workspaceId)
      if (group === undefined) return waited
      waited = true
      await Promise.all(group.keys())
    }
  }

  clear(): void {
    for (const group of this.pending.values()) {
      for (const release of group.values()) release()
    }
    this.pending.clear()
  }
}
