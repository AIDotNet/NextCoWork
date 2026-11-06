/**
 * `ncw-agent takeover ...` —— 把桌面数据根原地交给独立服务(可被测试直接调用)。
 *
 *   takeover begin    --data-dir <d>                          检查旧进程 → 一致性备份 → 验证(可重入)
 *   takeover commit   --data-dir <d>                          创建服务身份、追加 ownership 标记
 *   takeover rollback --data-dir <d> [--acknowledge-data-loss]  恢复备份;提交后必须显式确认
 *   takeover status   --data-dir <d>                          打印当前阶段
 *
 * 只输出阶段与备份位置,不输出路径以外的任何内容(没有密钥/凭据/行内容)。
 */
import { beginTakeover, commitTakeover, readJournal, rollbackTakeover, type TakeoverJournal } from '../../main/migration/takeover'

export class TakeoverUsageError extends Error {}
export interface TakeoverCliIo { out(line: string): void; err(line: string): void }

const COMMANDS = new Set(['begin', 'commit', 'rollback', 'status'])

export function runTakeoverCli(argv: readonly string[], io: TakeoverCliIo, options: { isAlive?: (pid: number) => boolean } = {}): void {
  const command = argv[0]
  if (command === undefined || !COMMANDS.has(command)) throw new TakeoverUsageError('command must be begin, commit, rollback or status')
  let dataDir: string | undefined
  let acknowledge = false
  for (let i = 1; i < argv.length; i++) {
    const flag = argv[i]!
    if (flag === '--acknowledge-data-loss') { acknowledge = true; continue }
    if (flag !== '--data-dir') throw new TakeoverUsageError(`Unknown option: ${flag}`)
    dataDir = argv[++i]
    if (dataDir === undefined || dataDir.startsWith('--')) throw new TakeoverUsageError('--data-dir needs a value')
  }
  if (dataDir === undefined) throw new TakeoverUsageError('--data-dir is required')
  if (acknowledge && command !== 'rollback') throw new TakeoverUsageError('--acknowledge-data-loss only applies to rollback')
  const shared = options.isAlive !== undefined ? { isAlive: options.isAlive } : {}
  const show = (journal: TakeoverJournal): void => {
    io.out(`takeover ${journal.id}: ${journal.phase}`)
    io.out(`backup: ${journal.backupDir}`)
    if (journal.postTakeoverCopy !== undefined) io.out(`data written after the takeover was kept at: ${journal.postTakeoverCopy}`)
  }
  switch (command) {
    case 'begin': show(beginTakeover(dataDir, shared)); break
    case 'commit': show(commitTakeover(dataDir, shared)); break
    case 'rollback': show(rollbackTakeover(dataDir, { ...shared, acknowledgeDataLoss: acknowledge })); break
    default: {
      const journal = readJournal(dataDir)
      io.out(journal === null ? 'no takeover in progress' : `takeover ${journal.id}: ${journal.phase}`)
    }
  }
}
