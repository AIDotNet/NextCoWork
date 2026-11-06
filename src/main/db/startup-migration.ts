/**
 * 启动迁移闸门:检查 → 需要就全屏跑完 → 放行。
 *
 * ## 为什么是「闸门」而不是「后台任务」
 *
 * 迁移必须在 `openDatabase()` **之前**跑完,而数据库是主进程所有 IPC 的底座。
 * 于是「阻止用户使用」不需要任何 UI 上的禁用逻辑 —— 迁移期间数据库根本没打开,
 * 渲染层调什么都失败,它自然只能显示那一屏进度。这是这套设计最省事的地方:
 * 要加一个「这次不让点」的控件就得记得在每处都加,而这里只有一个开关。
 *
 * ## 为什么检查要便宜到可以每次启动都跑
 *
 * 绝大多数启动什么都不用做。`probeLegacyDelta()` 只做几次 `COUNT`,不做任何
 * 写准备、不建目录、不动 `kv`。★ 所以**不要**在这里顺手加「顺便把设置读出来」
 * 之类的事:`getSettings()` 会打开内存兜底库,随后 `openDatabase()` 直接抛错。
 *
 * ## 这一步和它替掉的那段代码的关系
 *
 * `main/index.ts` 原先的 `prepareProjectDatabaseDirectory()` 是「目标库不存在
 * 就整体拷一份」,一次性的、做完就永久关门。本模块是它的补充:**目标库已经存在
 * 之后**那条路才走得到,而那条路原先会静默走开。
 *
 * 两段代码都留着,顺序是「先整体拷,拷不了再行级合并」——
 * 整体拷便宜且无损,行级合并贵且有判断,能用前者就不该用后者。
 */
import { existsSync } from 'node:fs'
import { join } from 'node:path'
import type {
  MigrationErrorCode,
  MigrationMergeSummary,
  MigrationState,
  MigrationStepKind
} from '../../shared/domain/data-migration'
import { DB_FILENAME } from './index'
import { migrateFlatLayout, rewriteMigratedPaths } from './flat-layout'
import {
  classifyMigrationError,
  collectAttachmentFiles,
  copyAttachmentFiles,
  hasAnythingToMerge,
  mergeLegacyRows,
  probeLegacyDelta,
  readMigrationClaim,
  readUndoManifest,
  rewriteMergedAttachmentPaths,
  undoMerge,
  writeUndoManifest,
  type LegacyDelta,
  type MergeResult
} from './legacy-merge'

/** 一个待处理的旧数据根。 */
export interface LegacySource {
  /**
   * 数据根的绝对路径。**只在主进程里出现**,不播给渲染层
   * (渲染层要开那个目录时走 `dataMigration:openDataDirectory`)。
   */
  root: string
  /** 库文件的绝对路径。 */
  databasePath: string
}

export interface MigrationInput {
  /** 当前数据根(`<userData>/data`)。 */
  dataRoot: string
  /** 当前库的路径。 */
  databasePath: string
  /** 按优先级排好的旧数据根。第一个确实有东西要合并的会被处理。 */
  sources: readonly LegacySource[]
  /** 扁平布局收拢(根层的库搬进 `data/`)。与 `main/index.ts` 原先那段同一件事。 */
  collapseFlatLayout?: { from: string; to: string }
  /** 进度回调。★ 每次状态变化都会调一次,主进程负责转成事件推给窗口。 */
  onChange?: (state: MigrationState) => void
  /** 每步之间的让出点。★ 生产里必须让出,见 `yieldToEventLoop`。 */
  yieldToEventLoop?: () => Promise<void>
}

export interface MigrationGate {
  /** 当前快照。渲染层握手时整份拿走。 */
  state(): MigrationState
  /** 检查并按需执行。失败后「重试」就是再调一次。 */
  run(): Promise<MigrationState>
  /** 放弃并继续启动。 */
  skip(): void
  /** 撤销本次合并。只在还有清单时有效。 */
  undo(): void
}

const IDLE_STATE: MigrationState = {
  phase: 'idle',
  steps: [],
  completed: [],
  current: null,
  ratio: null,
  failure: null,
  merged: null,
  undoAvailable: false,
  // 启动失败属于主进程编排状态，由 ipc/data-migration.ts 在流出前盖章。
  startupFailure: null,
  /*
    ★ 恒为 false,而且**故意**不在这里维护:闸门不知道主进程有没有跑完
    `registerIpc()`,那是启动序列的事。播给渲染层的那一份由 `ipc/data-migration.ts`
    的 `outward()` 盖章(见 `MigrationState.ipcReady`)。
  */
  ipcReady: false
}

/**
 * ★ 必须复制数组。`state()` 会把这份快照交给 IPC 序列化,而下面的 publish 会
 * 就地 `push` 到 `completed` 上 —— 不复制的话「已完成」列表会在渲染层拿到之后
 * 继续变,IPC 序列化撞上并发修改就是一个偶发的、只在迁移时出现的序列化错误。
 */
function freeze(state: MigrationState): MigrationState {
  return { ...state, steps: [...state.steps], completed: [...state.completed] }
}

/**
 * 建一个闸门。**这一步不做任何 I/O** —— 它只是把状态和输入装上。
 *
 * ★ 检查放在 `run()` 里而不是构造函数里:构造函数里跑 I/O 意味着「建闸门」这件事
 *   可能抛错,而调用方(启动路径)此时还没有任何可以显示错误的地方。
 */
export function createMigrationGate(input: MigrationInput): MigrationGate {
  let state: MigrationState = freeze(IDLE_STATE)
  let skipped = false
  let finished = false

  const publish = (next: MigrationState): void => {
    state = freeze(next)
    input.onChange?.(state)
  }

  const yieldNow =
    input.yieldToEventLoop ?? ((): Promise<void> => new Promise((resolve) => setImmediate(resolve)))

  const fail = (code: MigrationErrorCode, detail: string, stepKind: MigrationStepKind): void => {
    publish({ ...state, phase: 'failed', current: null, failure: { code, detail, stepKind } })
  }

  /**
   * 找第一个确实有东西要合并的旧根。
   *
   * ★ **目标库探测失败要一路抛出去**,不能当成「没有东西要搬」。见
   * `probeLegacyDelta` 的注释:把它降级成「无需迁移」正是这次丢数据的形态。
   * 这里让它抛,调用方会把它变成闸门上的一次可见失败。
   */
  const findPendingSource = (): { source: LegacySource; delta: LegacyDelta } | null => {
    for (const source of input.sources) {
      if (!existsSync(source.databasePath)) continue
      const delta = probeLegacyDelta(input.databasePath, source.databasePath)
      if (hasAnythingToMerge(delta)) return { source, delta }
    }
    return null
  }

  const run = async (): Promise<MigrationState> => {
    if (finished && state.phase === 'idle') return state
    finished = true
    skipped = false

    const steps: MigrationStepKind[] = []
    const completed: MigrationStepKind[] = []

    // ── 1. 扁平布局收拢 ──────────────────────────────────────────────
    /*
      与「目标库已存在」互斥:它只在目标库不存在时才有事可做。★ 判据是**目标库
      文件**不存在,不是目标目录不存在 —— 目录可能已经建好了但库还没落进去
      (或者那个位置上躺着别的东西),按目录判会静默跳过这一步,然后在后面
      以一个完全不相干的错误炸掉。

      ★ 失败时**要停在闸门上** —— 那时候目标库处于「搬了一半」的状态
      (rename 失败会自己逆序回滚,但回滚本身也可能失败),继续启动等于用一个坏库。
    */
    const collapse = input.collapseFlatLayout
    const collapsePending =
      collapse !== undefined &&
      existsSync(databasePathIn(collapse.from)) &&
      !existsSync(databasePathIn(collapse.to))
    if (collapsePending && collapse !== undefined) {
      steps.push('collapse-flat-layout')
      publish({
        ...state,
        phase: 'running',
        steps: [...steps],
        completed: [],
        current: { kind: 'collapse-flat-layout', done: 0, total: 0 },
        ratio: null,
        failure: null
      })
      await yieldNow()
      try {
        migrateFlatLayout(collapse.from, collapse.to)
        /*
          ★ 库里存着绝对路径(`attachments.path` / `sessions.root_path_at_creation`
          / `workspaces.json.rootPath`),搬完文件必须把它们改写到新根。
          漏掉这一步的表现是「迁移成功,但我的文件都没了」——
          而日志里一切正常。原先这段在 `main/index.ts` 里紧跟着 rename,
          抽到这里是为了让「搬布局」这件事只有一个入口。
        */
        rewriteMigratedPaths(input.databasePath, collapse.from, collapse.to)
      } catch (err) {
        fail(classifyMigrationError(err), describe(err), 'collapse-flat-layout')
        return state
      }
      completed.push('collapse-flat-layout')
    }

    // ── 2. 有没有旧库里有、当前库没有的会话 ─────────────────────────
    let pending: { source: LegacySource; delta: LegacyDelta } | null
    try {
      pending = findPendingSource()
    } catch (err) {
      /*
        ★ 探测**目标库**时炸了 —— 它是应用马上要打开的那个库,所以这是启动级
        问题,必须摆到闸门上让人看见。原先把这种情况降级成「没有东西要搬」,
        那正是这次丢数据的形态:探测失败 → 判定无需迁移 → 静默继续 →
        用户看到一份少了东西的数据,日志里一个字都没有。

        ★ 用 `target-corrupt` 而不是复用 `source-corrupt`:错误页的文案要说清
        **是哪一边**坏了,而这两个的下一步动作完全不同 —— 源库坏了可以跳过,
        目标库坏了跳过之后应用也打不开。
      */
      fail('target-corrupt', describe(err), 'merge-legacy-rows')
      return state
    }
    if (pending === null) {
      /*
        ★ 绝大多数启动走到这里。**不 publish `running`** —— 渲染层因此一帧都不会
        画迁移屏,多一层「正在检查」的过场就是一次白闪。

        已经 `completed` 过 `collapse-flat-layout` 时不覆盖状态:那一步的进度
        得留在屏幕上,不然用户会看到进度条走完又跳回空白。
      */
      if (state.phase !== 'running') {
        publish({ ...IDLE_STATE, undoAvailable: readUndoManifest(input.databasePath) !== null })
      } else {
        publish({ ...state, phase: 'idle', completed: [...completed], current: null, ratio: 1 })
      }
      return state
    }

    steps.push('merge-legacy-rows', 'copy-attachment-files')
    publish({
      ...state,
      phase: 'running',
      steps: [...steps],
      completed: [...completed],
      current: { kind: 'merge-legacy-rows', done: 0, total: pending.delta.sessions },
      ratio: 0,
      failure: null
    })
    await yieldNow()

    // ── 3. 行级合并 ─────────────────────────────────────────────────
    let wrote: MergeResult
    try {
      wrote = mergeLegacyRows({
        targetPath: input.databasePath,
        sourcePath: pending.source.databasePath,
        onProgress: (done, total) => {
          publish({
            ...state,
            phase: 'running',
            steps: [...steps],
            completed: [...completed],
            current: { kind: 'merge-legacy-rows', done, total },
            ratio: total === 0 ? null : done / total
          })
        }
      })
    } catch (err) {
      /*
        ★ 失败时错误页仍然不报「已合并 N 条」:那会让用户把半批数据误当成完整结果。
        每条会话及其归属清单已经在同一事务里提交，不能在这里另开连接补写；补写失败
        会让已提交的前半批永远无法被账户重连，正是这次迁移要避免的无声缺失。
      */
      fail(classifyMigrationError(err), describe(err), 'merge-legacy-rows')
      return state
    }
    const merged: MigrationMergeSummary = {
      sessions: wrote.sessions,
      messages: wrote.messages,
      attachments: wrote.attachments
    }
    completed.push('merge-legacy-rows')
    // 需求：重试时这次的 `createdSessions` 不含上次已提交的行。归属清单累计它们，
    // 附件补搬 / 路径改写必须按这份全集走，否则旧会话正文回来但图片永久碎掉。
    const attachmentSessionIds = readMigrationClaim(input.databasePath)?.sessions ?? wrote.createdSessions

    // ── 4. 附件文件 ─────────────────────────────────────────────────
    /*
      ★ 文件必须在**行已经提交之后**搬:反过来的话,一次失败的行合并会留下一批
      没有对应行的孤儿文件 —— 而重试会再拷一遍。这个顺序下最坏情况是
      「行在、文件少」,那是能看出来(碎图)且重试能补上的。

      ★ 拷文件这一步单独成一个步骤、单独报进度,而不是并进行合并里。
      附件文件在网络上、在慢盘上可能有好几百兆,没有进度条的那一段在用户眼里
      就是「卡死了」—— 而它恰恰是最长的一段。
    */
    const pairs = collectAttachmentFiles(
      pending.source.databasePath,
      pending.source.root,
      input.dataRoot,
      attachmentSessionIds
    )
    publish({
      ...state,
      phase: 'running',
      steps: [...steps],
      completed: [...completed],
      current: { kind: 'copy-attachment-files', done: 0, total: pairs.length },
      ratio: pairs.length === 0 ? null : 0
    })
    await yieldNow()
    const copiedFiles = copyAttachmentFiles(pairs, (done, total) => {
      publish({
        ...state,
        phase: 'running',
        steps: [...steps],
        completed: [...completed],
        current: { kind: 'copy-attachment-files', done, total },
        ratio: total === 0 ? null : done / total
      })
    })

    /*
      ★ **文件拷完之后必须改写附件行里的 `path`。** 行里存的是旧根下的绝对路径,
      而 `ncw://` 协议只认 `databaseDirectory()/attachments` —— 不改的话文件明明
      搬过来了,老会话里的图却是碎的,而库本身完全健康(见
      `legacy-merge.ts` 的 `rewriteMergedAttachmentPaths`)。

      这一步和文件拷贝合成同一个「附件」步骤:对用户来说是同一件事,
      分开报进度只会多一次没有意义的跳变。
    */
    const rewritten = rewriteMergedAttachmentPaths(
      input.databasePath,
      pending.source.root,
      input.dataRoot,
      attachmentSessionIds
    )
    if (rewritten > 0) console.log(`[migration] 改写 ${rewritten} 条附件行的路径到新数据根`)
    completed.push('copy-attachment-files')

    // ── 5. 撤销清单 ─────────────────────────────────────────────────
    /*
      ★ 清单写在合并之后、放行之前。写失败只降级成「没有撤销能力」,
      绝不让它把一次已经成功的合并变成启动失败 —— 撤销是安全网,不是必要条件。
    */
    try {
      writeUndoManifest(input.databasePath, {
        at: Date.now(),
        source: pending.source.root,
        sessions: wrote.createdSessions,
        workspaces: wrote.createdWorkspaces,
        files: copiedFiles
      })
    } catch (err) {
      console.warn('[migration] 撤销清单写入失败,本次合并将无法撤销:', err)
    }
    publish({
      ...state,
      phase: 'idle',
      steps: [...steps],
      completed: [...completed],
      current: null,
      ratio: 1,
      failure: null,
      merged,
      undoAvailable: readUndoManifest(input.databasePath) !== null
    })
    return state
  }

  const skip = (): void => {
    if (skipped) return
    skipped = true
    /*
      ★ 只把 `failed` 翻成 `skipped`,不动别的阶段。合并按会话提交,所以任何时刻
      的目标库都是一致的 —— 「跳过」不会开出一个坏库,它只是让用户带着一部分
      旧数据先启动。而**静默继续正是这次丢数据的成因**,所以这条路只能由用户
      在错误页上主动选,不能是默认行为。
    */
    if (state.phase === 'failed') {
      publish({ ...state, phase: 'skipped', current: null })
    }
  }

  const undo = (): void => {
    const manifest = readUndoManifest(input.databasePath)
    if (manifest === null) {
      publish({ ...state, undoAvailable: false })
      return
    }
    try {
      undoMerge(input.databasePath, manifest)
    } catch (err) {
      console.warn('[migration] 撤销失败:', err)
      publish({
        ...state,
        failure: {
          code: classifyMigrationError(err),
          detail: describe(err),
          stepKind: 'merge-legacy-rows'
        }
      })
      return
    }
    publish({ ...state, merged: null, undoAvailable: false, completed: [], steps: [], ratio: null })
  }

  return { state: () => state, run, skip, undo }
}

function describe(err: unknown): string {
  return err instanceof Error ? `${err.name}: ${err.message}` : String(err)
}

/** 库文件在某一份数据根下的位置。 */
export function databasePathIn(root: string): string {
  return join(root, DB_FILENAME)
}
