/**
 * 受管导入副本的落点 —— **纯路径 helper,零重依赖。**
 *
 * ★★ **它为什么单独一个文件:为了断掉 `imports/service ⇄ runtime` 的值依赖环。**
 * `managedInstructionsPath` 本来长在 `imports/service.ts` 里,而那个文件拖着整条
 * 导入流水线(各来源的扫描器、store、db …)。`runtime.ts` 只在
 * `loadManagedInstructions` 里用它拼一条路径,却因此把整条导入链拉进了 runtime。
 *
 * 抽到这里之后 runtime 只依赖这个叶子模块(`node:path` + `../db` 的
 * `databaseDirectory`),`imports/service.ts` 反过来 import 它 —— 同一方向,
 * 环不存在了。
 *
 * ★ 两个常量与 `service.ts` 里 `applyInstructions` 用的**必须逐字相同**:
 * 写路径和读路径是同一个东西的两半,分叉的表现是「导入成功了,但模型读不到」。
 */
import { join } from 'node:path'
import { databaseDirectory } from '../db'

/** 受管说明副本的落点。★ 不覆盖、不改写原生 `AGENTS.md`,见 `loadManagedInstructions`。 */
export const MANAGED_INSTRUCTIONS_DIR = 'imports'

const SOURCE_KIND = 'claude-code' as const

/**
 * 受管说明副本的路径。
 *
 * ★ 全局那一份叫 `global.md`,工作区那一份按 workspaceId 命名 —— 两级作用域
 * 不能互相覆盖(见 `loadManagedInstructions` 的读取顺序)。
 */
export function managedInstructionsPath(sourceId: string, workspaceId: string): string {
  return join(
    databaseDirectory(),
    MANAGED_INSTRUCTIONS_DIR,
    SOURCE_KIND,
    sourceId,
    'instructions',
    workspaceId === '' ? 'global.md' : `${workspaceId}.md`
  )
}
