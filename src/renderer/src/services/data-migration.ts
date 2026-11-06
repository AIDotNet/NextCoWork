/**
 * 启动迁移闸门的渲染层入口。
 *
 * ★ 除无状态的窗口控制外，这是**唯一可以在首屏之前调用**的业务 service ——
 * 闸门存在的那段时间里数据库还没打开,别的 service 调了都会失败。所以它不依赖
 * `app:getBootstrap`,也不需要 `window:ready` 先到。
 *
 * ★ 五个函数都用 `invoke`(失败抛 `AgentErrorException`)而不是 `tryInvoke`。
 * 闸门期间的失败**没有「正常结果的失败分支」** —— 拿不到状态就是拿不到,
 * 调用方要看到它。
 */
import type { MigrationState } from '../../../shared/domain/data-migration'
import type { Unsubscribe } from '../../../shared/ipc/contract'
import { invoke, on } from './ipc'

export function getMigrationState(): Promise<MigrationState> {
  return invoke('dataMigration:getState', undefined)
}

export function retryMigration(): Promise<MigrationState> {
  return invoke('dataMigration:retry', undefined)
}

export function skipMigration(): Promise<MigrationState> {
  return invoke('dataMigration:skip', undefined)
}

export function undoMigration(): Promise<MigrationState> {
  return invoke('dataMigration:undoMerge', undefined)
}

export function openMigrationDataDirectory(): Promise<void> {
  return invoke('dataMigration:openDataDirectory', undefined)
}

/** ★ 返回值必须进 useEffect 的 cleanup,否则 HMR 每次热更叠一层监听器。 */
export function onMigrationProgress(cb: (state: MigrationState) => void): Unsubscribe {
  return on('dataMigration:progress', cb)
}
