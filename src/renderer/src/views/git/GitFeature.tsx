/**
 * Git 管理面板 —— 侧边栏那一格点开的外层 feature Tab。
 *
 * 结构照 `scheduled/ScheduledFeature.tsx`:左栏列表 + 右栏详情、顶部标题栏带关闭。
 * 远程工作区的降级照 `browser/BrowserFeature.tsx` —— 但**判断不在这里做**:
 * 主进程已经把「能不能用」算成了 `GitOverview.available`,这里只负责把四种
 * 不可用各自说清楚。渲染层重复判一遍 `isLocalEnvironment` 只会多一个会和
 * 主进程说法不一致的真相源。
 *
 * ★ **主进程抛上来的 message 有两种。** 一种是我们自己的 i18n 键
 *   (`git.invalidBranch` / `git.unavailable.*`),一种是 git 自己的 stderr
 *   (「Your local changes would be overwritten…」)。后者**恰恰是最该原样显示的**
 *   —— 换成一句「操作失败」等于把 git 唯一一次把话说明白的机会扔掉。
 *   所以这里只对**已知键**做翻译,其余照搬。`Translate` 不支持探测键是否存在,
 *   白名单是唯一能在编译期站住的办法。
 *
 * ★ **文件图标复用 `lib/file-icon.ts`,不另起一套。** 那套彩色图标是文件树在用的,
 *   同一个 `.ts` 在树里和在 Git 面板里必须长得一样 —— 两处各自维护一张扩展名表,
 *   迟早会分叉成「文件树认得 .mjs、Git 面板不认得」。
 *
 * ★ **提交框不用 `TextArea`。** 那个组件的草稿只在失焦时 commit(`useDraft`),
 *   而这里要三件它给不了的事:AI 生成后**从外面**把文本填进来(`useDraft` 只在
 *   未聚焦时同步外部值)、提交按钮的禁用态跟着当前字数走、⌘↵ 直接提交。
 *   所以就地写了一个受控的 `CommitBox`,类名照抄 `TextArea` 保持外观一致。
 *
 * ★ **自动刷新只能靠轮询。** 仓库这边没有 FS 监听 —— `services/workspace-files`
 *   的 `WorkspaceFilesChanged` 是渲染层自己的事件总线,只在应用自己改过文件后
 *   才响,看不见用户在终端里 `git add` 了什么。而用户多半正是从终端切回来的,
 *   所以窗口重新获得焦点时立刻刷一次,可见时再低频兜底;隐藏的标签页不刷。
 *
 * ★ **AI 写提交信息不传模型。** 主进程读设置里的默认模型(同「AI 生成子代理」)。
 *   渲染层这一侧根本拿不到设置:它们是 `App.tsx` 的 state,而 Git 面板走
 *   `FeatureView`,不在那条 props 链上。
 *
 * ★ **diff 的文本解析不在这里,在 `components/diff/parse-unified.ts`。** 行号推算和
 *   「哪一行算正文」是这个面板里唯一算得上算法的一段,留在组件里就只能靠肉眼验收 ——
 *   而它错了不会报错,只会让行号整体串位。抽出去之后有
 *   `components/diff/__tests__/parse-unified.test.ts`;画行的那一份也一并挪去了
 *   `components/diff/DiffLines.tsx`,现在和聊天里的 diff 是同一个组件。
 */
import {
  ArrowDown,
  ArrowUp,
  GitBranch,
  History,
  Minus,
  Plus,
  RefreshCw,
  Sparkles,
  X
} from 'lucide-react'
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import type {
  GitBranchSummary,
  GitCommitSummary,
  GitDiff,
  GitDiffSides,
  GitFileChange,
  GitOverview,
  GitUnavailableReason
} from '../../../../shared/domain/git'
import { Button } from '../../components/arc/button/button'
import { EmptyState } from '../../components/arc/empty-state/empty-state'
import { IconButton } from '../../components/ui/IconButton'
import SegmentedControl from '../../components/arc/segmented-control/segmented-control'
import { Select } from '../../components/ui/Select'
import { Spinner } from '../../components/ui/Spinner'
import { TextInput } from '../../components/ui/TextInput'
import { Switch } from '../../components/arc/switch/switch'
import { useI18n, type Translate } from '../../i18n'
import { cn } from '../../lib/cn'
import { iconFor } from '../../lib/file-icon'
import {
  checkoutGitBranch,
  commitGit,
  createGitBranch,
  generateGitCommitMessage,
  getGitDiff,
  getGitDiffSides,
  getGitOverview,
  listGitBranches,
  listGitCommits,
  pullGit,
  pushGit,
  stageGitPaths,
  unstageGitPaths
} from '../../services/git'
import { FeatureFrame } from '../../shell/FeatureFrame'
import { useWindowStore } from '../../stores/window'
import { DiffLines } from '../../components/diff/DiffLines'
import { CodeDiffViewer } from '../../components/diff/CodeDiffViewer'
import { parseUnifiedDiff } from '../../components/diff/parse-unified'
import { languageOf } from '../../components/code'

/** 主进程会原样抛回来的 i18n 键。不在表里的一律当作 git 的原话显示。 */
const KNOWN_ERROR_KEYS = [
  'git.invalidBranch',
  'git.nothingStaged',
  'git.aiNoModel',
  'git.aiTimeout',
  'git.aiFailed',
  'git.aiUnparsable',
  'git.aiTruncated',
  'git.invalidPath',
  'git.detachedPush',
  'git.emptyMessage',
  'git.unavailable.remote-workspace',
  'git.unavailable.workspace-unavailable',
  'git.unavailable.git-missing',
  'git.unavailable.not-a-repository'
] as const

type KnownErrorKey = (typeof KNOWN_ERROR_KEYS)[number]

/** 有补充说明的那三种不可用。`git-missing` 故意没有:标题本身已经是全部信息。 */
type HintedReason = Exclude<GitUnavailableReason, 'git-missing'>

type Pane = 'changes' | 'history'
/** 哪一颗按钮该转圈。别的写操作很快,不值得一个指示器。 */
type Pending = 'pull' | 'push'
type Selection = { path: string; staged: boolean }

const HISTORY_LIMIT = 50

/**
 * diff 最多渲染这么多行。
 *
 * ★ 主进程已经按 200KB 截断过,但 200KB 的源码仍然是好几千行,而每一行都是一个
 *   带 className 的 `<div>`。**一次 rebase 后的大 diff 能让选中文件这个动作卡住
 *   半秒**,而超过一屏的部分没有人会滚着读完 —— 要读整份的人用的是编辑器。
 */
const DIFF_LINE_LIMIT = 1200

/** 可见时的兜底轮询间隔。见文件头 ★:没有 FS 监听。 */
const POLL_MS = 15_000

function errorText(error: unknown, t: Translate): string {
  const message = error instanceof Error ? error.message : String(error)
  const known = (KNOWN_ERROR_KEYS as readonly string[]).includes(message)
  return known ? t(message as KnownErrorKey) : message
}

/** 列表里那一格状态字母。未跟踪的 worktree 是 `?`,直接拿来用。 */
function statusLetter(file: GitFileChange, staged: boolean): string {
  const letter = staged ? file.index : file.worktree
  return letter === '.' || letter === '' ? ' ' : letter
}

/**
 * 状态字母的语义:一个颜色 + 一句 tooltip。
 *
 * ★ **M 故意是中性色。** 改动是列表里最常见的一种,给它上色等于给整张列表上色,
 *   结果是「新增」和「删除」这两个真正需要一眼认出来的反而淹了。
 */
function statusMeta(letter: string, t: Translate): { className: string; title: string } {
  if (letter === 'A') return { className: 'text-accent', title: t('git.status.added') }
  if (letter === '?') return { className: 'text-accent', title: t('git.status.untracked') }
  if (letter === 'D') return { className: 'text-danger', title: t('git.status.deleted') }
  if (letter === '!') return { className: 'text-danger', title: t('git.conflicted') }
  if (letter === 'R') return { className: 'text-accent-soft', title: t('git.status.renamed') }
  if (letter === 'C') return { className: 'text-accent-soft', title: t('git.status.copied') }
  if (letter === 'T') return { className: 'text-accent-soft', title: t('git.status.typeChanged') }
  if (letter === 'M') return { className: 'text-fg-muted', title: t('git.status.modified') }
  return { className: 'text-fg-faint', title: '' }
}

export function GitFeature({ onClose }: { onClose?: () => void } = {}): ReactNode {
  const { t } = useI18n()
  const workspaceId = useWindowStore((state) => state.activeWorkspaceId)
  const [overview, setOverview] = useState<GitOverview | null>(null)
  const [branches, setBranches] = useState<GitBranchSummary[]>([])
  const [commits, setCommits] = useState<GitCommitSummary[]>([])
  const [pane, setPane] = useState<Pane>('changes')
  const [selection, setSelection] = useState<Selection | null>(null)
  const [diff, setDiff] = useState<GitDiff | null>(null)
  /** 两侧全文。`null` = 没法按全文对比(二进制 / 超限 / 冲突),退回 unified 文本 */
  const [sides, setSides] = useState<GitDiffSides | null>(null)
  const [message, setMessage] = useState('')
  const [creating, setCreating] = useState(false)
  const [newBranch, setNewBranch] = useState('')
  const [newCheckout, setNewCheckout] = useState(true)
  const [busy, setBusy] = useState(false)
  /** 正在跑的是哪一个写操作 —— 只为了把转圈画在**被点的那颗按钮**里。 */
  const [pending, setPending] = useState<Pending | null>(null)
  const [generating, setGenerating] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  // 并发刷新的版本号:切工作区 / 连点刷新时,只有最后一次的结果准落地
  const loadVersion = useRef(0)
  // 轮询回调里读 busy —— 放进 effect 的依赖会让定时器每次置忙都重建一遍
  const busyRef = useRef(false)
  /*
    改动列表的指纹 + 修订号。轮询回来的 `overview` 每次都是新对象,但内容多半
    没变;只有指纹真的变了才 bump `revision`,不然当前打开的 diff 会每 15 秒
    无谓地重取一次(而且会打断正在进行的文本选择)。
  */
  const signature = useRef('')
  const [revision, setRevision] = useState(0)

  // 摘出布尔量而不是直接把 `overview` 挂进依赖:轮询每次都换一个新对象,
  // 挂对象的话历史面板会每 15 秒重跑一次 `git log`
  const available = overview !== null && overview.available

  /** `silent` 是给轮询用的:后台刷新失败不该往界面上糊一条用户没招来的红字。 */
  const refresh = useCallback(
    async (silent = false): Promise<void> => {
      if (workspaceId === null) {
        setOverview(null)
        return
      }
      const version = ++loadVersion.current
      try {
        const next = await getGitOverview(workspaceId)
        if (version !== loadVersion.current) return
        setOverview(next)
        const sign = next.available
          ? `${next.branch}\u0000${next.files.map((f) => `${f.path}${f.index}${f.worktree}`).join('\n')}`
          : ''
        if (sign !== signature.current) {
          signature.current = sign
          setRevision((v) => v + 1)
        }
        if (!next.available) {
          setBranches([])
          setCommits([])
          return
        }
        const list = await listGitBranches(workspaceId)
        if (version !== loadVersion.current) return
        setBranches(list)
      } catch (e) {
        if (version !== loadVersion.current || silent) return
        setError(errorText(e, t))
      }
    },
    [workspaceId, t]
  )

  useEffect(() => {
    setSelection(null)
    setDiff(null)
    setCommits([])
    setPane('changes')
    signature.current = ''
    void refresh()
  }, [refresh])

  /*
    自动刷新。焦点回来时立刻刷(用户刚从终端切回来),可见时再低频兜一次。
    ★ 隐藏的标签页**不刷** —— 后台每 15 秒跑一趟 `git status` 是在替一个没人看
      的面板烧电;`visibilitychange` 那一跳负责在切回来的瞬间补上。
  */
  useEffect(() => {
    if (workspaceId === null) return
    const tick = (): void => {
      if (document.hidden || busyRef.current) return
      void refresh(true)
    }
    const timer = window.setInterval(tick, POLL_MS)
    window.addEventListener('focus', tick)
    document.addEventListener('visibilitychange', tick)
    return () => {
      window.clearInterval(timer)
      window.removeEventListener('focus', tick)
      document.removeEventListener('visibilitychange', tick)
    }
  }, [workspaceId, refresh])

  // 历史是按需拉的:大仓库上 `git log` 不该在每次开面板时都跑一趟
  useEffect(() => {
    if (pane !== 'history' || workspaceId === null || !available) return
    let alive = true
    listGitCommits(workspaceId, HISTORY_LIMIT)
      .then((list) => {
        if (alive) setCommits(list)
      })
      .catch((e: unknown) => {
        if (alive) setError(errorText(e, t))
      })
    return () => {
      alive = false
    }
  }, [pane, workspaceId, available, revision, t])

  useEffect(() => {
    if (selection === null || workspaceId === null) {
      setDiff(null)
      setSides(null)
      return
    }
    let alive = true
    /*
      unified 文本和两侧全文一起取、一起落地:前者判「二进制 / 没有文本改动」,
      后者喂对比视图。分开落地的话,中间那一帧会先画一次兜底的 unified 视图再换掉。
      全文那一路失败不算错 —— 退回 unified 文本就是了。
    */
    Promise.all([
      getGitDiff(workspaceId, selection.path, selection.staged),
      getGitDiffSides(workspaceId, selection.path, selection.staged).catch(() => null)
    ])
      .then(([next, nextSides]) => {
        if (!alive) return
        setDiff(next)
        setSides(nextSides)
      })
      .catch((e: unknown) => {
        if (!alive) return
        setDiff(null)
        setSides(null)
        setError(errorText(e, t))
      })
    return () => {
      alive = false
    }
  }, [selection, workspaceId, revision, t])

  /**
   * 所有写操作的统一外壳:置忙、清提示、失败留话、成功后刷新。
   *
   * ★ `tag` 只影响转圈画在哪 —— `busy` 仍然是全局的(同时 pull 和 commit 没有
   *   任何意义),但 pull / push 一等就是几十秒,把指示器画在别处等于没画。
   */
  const run = async (action: () => Promise<void>, tag?: Pending): Promise<void> => {
    if (busy) return
    setBusy(true)
    busyRef.current = true
    setPending(tag ?? null)
    setError(null)
    setNotice(null)
    try {
      await action()
      await refresh()
    } catch (e) {
      setError(errorText(e, t))
    } finally {
      setBusy(false)
      busyRef.current = false
      setPending(null)
    }
  }

  const files = overview !== null && overview.available ? overview.files : []
  const staged = useMemo(() => files.filter((f) => f.staged), [files])
  const unstaged = useMemo(() => files.filter((f) => f.unstaged || f.untracked), [files])

  if (workspaceId === null) {
    return (
      <Shell t={t} onClose={onClose} onRefresh={null}>
        <EmptyState
          className="m-auto"
          icon={<GitBranch size={26} />}
          title={t('git.noWorkspace')}
          description={t('git.noWorkspaceHint')}
        />
      </Shell>
    )
  }

  if (overview === null) {
    return (
      <Shell t={t} onClose={onClose} onRefresh={() => void refresh()}>
        <p role="status" className="m-auto p-6 text-[13px] text-fg-muted">
          {t('git.loading')}
        </p>
      </Shell>
    )
  }

  if (!overview.available) {
    const reason = overview.reason
    return (
      <Shell t={t} onClose={onClose} onRefresh={() => void refresh()}>
        <EmptyState
          className="m-auto"
          icon={<GitBranch size={26} />}
          title={t(`git.unavailable.${reason}`)}
          description={
            reason === 'git-missing'
              ? ''
              : t(`git.unavailable.${reason as HintedReason}Hint`)
          }
        />
      </Shell>
    )
  }

  const branchLabel = overview.detached
    ? t('git.branchDetached')
    : overview.unborn
      ? `${overview.branch} · ${t('git.branchUnborn')}`
      : overview.branch

  const branchOptions = branches.map((b) => ({ value: b.name, label: b.name }))
  const selectValue = branches.some((b) => b.name === overview.branch) ? overview.branch : ''

  const commit = (): void => {
    const text = message.trim()
    // 按钮在空信息时就是禁用的,这里兜的是 ⌘↵ —— 快捷键绕不过键盘
    if (text === '' || busy || generating) return
    void run(async () => {
      const created = await commitGit(workspaceId, text)
      setMessage('')
      setSelection(null)
      setNotice(t('git.commitDone', { hash: created.shortHash }))
    })
  }

  /**
   * 让模型照暂存区写一条草稿,**填进输入框等人过目** —— 不直接提交。
   *
   * ★ 不走 `run`:它会在成功后刷一遍仓库,而这次请求什么都没改。生成期间
   *   照样允许轮询和翻历史,只有两颗提交相关的按钮停下来。
   */
  const generate = (): void => {
    if (busy || generating) return
    setGenerating(true)
    setError(null)
    setNotice(null)
    generateGitCommitMessage(workspaceId)
      .then((result) => setMessage(result.message))
      .catch((e: unknown) => setError(errorText(e, t)))
      .finally(() => setGenerating(false))
  }

  const createBranch = (): void => {
    const name = newBranch.trim()
    if (name === '') {
      setError(t('git.invalidBranch'))
      return
    }
    void run(async () => {
      await createGitBranch(workspaceId, name, newCheckout)
      setNewBranch('')
      setCreating(false)
    })
  }

  return (
    <Shell t={t} onClose={onClose} onRefresh={() => void refresh()}>
      <section className="flex w-[340px] shrink-0 flex-col border-r border-hairline">
        {/* ── 分支与远端 ── */}
        <div className="border-b border-hairline px-4 py-3">
          <div className="flex items-center gap-2">
            <GitBranch size={14} className="shrink-0 text-fg-faint" />
            <span className="truncate text-[13px] font-medium text-fg" title={branchLabel}>
              {branchLabel}
            </span>
          </div>
          <div className="mt-1.5 flex items-center gap-2 text-[11px] text-fg-faint">
            {overview.upstream === '' ? (
              <span>{t('git.noUpstream')}</span>
            ) : (
              <>
                <span className="truncate" title={overview.upstream}>
                  {overview.upstream}
                </span>
                {overview.ahead > 0 && <span>{t('git.ahead', { count: overview.ahead })}</span>}
                {overview.behind > 0 && <span>{t('git.behind', { count: overview.behind })}</span>}
              </>
            )}
          </div>
          <div className="mt-2.5 flex items-center gap-2">
            <Button
              type="button"
              size="sm"
              variant="secondary"
              disabled={busy}
              onClick={() => void run(() => pullGit(workspaceId), 'pull')}
            >
              {pending === 'pull' ? <Spinner size="sm" /> : <ArrowDown size={13} />}
              {t('git.pull')}
            </Button>
            <Button
              type="button"
              size="sm"
              variant="secondary"
              disabled={busy}
              onClick={() => void run(() => pushGit(workspaceId), 'push')}
            >
              {pending === 'push' ? <Spinner size="sm" /> : <ArrowUp size={13} />}
              {t('git.push')}
            </Button>
            <IconButton
              label={t('git.newBranch')}
              className="ml-auto"
              active={creating}
              disabled={busy}
              onClick={() => setCreating((v) => !v)}
            >
              <Plus size={14} />
            </IconButton>
          </div>
          {branchOptions.length > 0 && (
            <Select
              className="mt-2 w-full"
              value={selectValue}
              options={branchOptions}
              ariaLabel={t('git.switchBranch')}
              disabled={busy}
              onValueChange={(name) => {
                if (name === overview.branch) return
                void run(() => checkoutGitBranch(workspaceId, name))
              }}
            />
          )}
          {creating && (
            <div className="mt-2 flex flex-col gap-2">
              <TextInput
                value={newBranch}
                onChange={setNewBranch}
                placeholder={t('git.newBranchPlaceholder')}
                ariaLabel={t('git.newBranchName')}
                disabled={busy}
              />
              <div className="flex items-center justify-between">
                <span className="flex items-center gap-2 text-[12px] text-fg-muted">
                  <Switch
                    checked={newCheckout}
                    onCheckedChange={setNewCheckout}
                    aria-label={t('git.newBranchCheckout')}
                  />
                  {t('git.newBranchCheckout')}
                </span>
                <Button type="button" size="sm" variant="primary" disabled={busy} onClick={createBranch}>
                  {t('git.create')}
                </Button>
              </div>
            </div>
          )}
        </div>

        {/* ── 改动列表 ── */}
        <div className="min-h-0 flex-1 overflow-y-auto px-2 py-2">
          {overview.filesTruncated && (
            <p className="px-2 pb-2 text-[11px] text-fg-faint">
              {t('git.filesTruncated', { count: files.length })}
            </p>
          )}
          {files.length === 0 ? (
            <EmptyState
              className="py-8"
              title={t('git.clean')}
              description={t('git.cleanHint')}
            />
          ) : (
            <>
              <FileGroup
                t={t}
                title={t('git.staged')}
                files={staged}
                staged
                busy={busy}
                selection={selection}
                actionLabel={t('git.unstage')}
                bulkLabel={t('git.unstageAll')}
                onSelect={setSelection}
                onAction={(paths) => void run(() => unstageGitPaths(workspaceId, paths))}
              />
              <FileGroup
                t={t}
                title={t('git.unstaged')}
                files={unstaged}
                staged={false}
                busy={busy}
                selection={selection}
                actionLabel={t('git.stage')}
                bulkLabel={t('git.stageAll')}
                onSelect={setSelection}
                onAction={(paths) => void run(() => stageGitPaths(workspaceId, paths))}
              />
            </>
          )}
        </div>

        {/* ── 提交 ── */}
        <div className="border-t border-hairline px-4 py-3">
          <CommitBox
            value={message}
            placeholder={t('git.commitPlaceholder')}
            ariaLabel={t('git.commitMessage')}
            disabled={busy || generating}
            onChange={setMessage}
            onSubmit={commit}
          />
          <div className="mt-2 flex items-center justify-between gap-2">
            <span className="truncate text-[11px] text-fg-faint">
              {generating
                ? t('git.aiGenerating')
                : staged.length === 0
                  ? t('git.nothingStaged')
                  : null}
            </span>
            <div className="flex shrink-0 items-center gap-1.5">
              <IconButton
                label={t('git.aiGenerate')}
                size={28}
                disabled={busy || generating || staged.length === 0}
                onClick={generate}
              >
                {generating ? <Spinner size="sm" /> : <Sparkles size={14} />}
              </IconButton>
              <Button
                type="button"
                size="sm"
                variant="primary"
                disabled={busy || generating || staged.length === 0 || message.trim() === ''}
                onClick={commit}
              >
                {t('git.commit')}
              </Button>
            </div>
          </div>
        </div>
      </section>

      {/* ── 右栏:diff / 历史 ── */}
      <section className="flex min-w-0 flex-1 flex-col">
        <div className="flex items-center gap-3 border-b border-hairline px-4 py-2.5">
          <SegmentedControl
            value={pane}
            label={t('git.title')}
            options={[
              { value: 'changes', label: t('git.tab.changes') },
              { value: 'history', label: t('git.tab.history') }
            ]}
            onValueChange={(v) => setPane(v as Pane)}
          />
          {pane === 'changes' && selection !== null && (
            <span className="truncate text-[12px] text-fg-muted" title={selection.path}>
              {selection.path}
            </span>
          )}
          {pane === 'history' && (
            <span className="flex items-center gap-1.5 text-[12px] text-fg-muted">
              <History size={13} />
              {t('git.historyOf')}
            </span>
          )}
        </div>

        {(error !== null || notice !== null) && (
          <div className="px-4 pt-3">
            {error !== null && (
              <div className="flex items-start justify-between gap-3 rounded-[8px] bg-danger/10 px-3 py-2 text-[12px] text-danger">
                <span className="min-w-0 whitespace-pre-wrap break-words">{error}</span>
                <IconButton label={t('git.operationFailed')} size={20} onClick={() => setError(null)}>
                  <X size={12} />
                </IconButton>
              </div>
            )}
            {notice !== null && (
              <div className="mt-2 rounded-[8px] bg-tint px-3 py-2 text-[12px] text-fg-muted">
                {notice}
              </div>
            )}
          </div>
        )}

        <div className="flex min-h-0 flex-1 flex-col overflow-auto">
          {pane === 'history' ? (
            commits.length === 0 ? (
              <EmptyState className="m-auto" title={t('git.noCommits')} description="" />
            ) : (
              <ul className="px-4 py-3">
                {commits.map((c) => (
                  <li key={c.hash} className="border-b border-hairline py-2.5 last:border-b-0">
                    <p className="truncate text-[13px] text-fg" title={c.subject}>
                      {c.subject}
                    </p>
                    <p className="mt-1 flex items-center gap-2 text-[11px] text-fg-faint">
                      <code className="font-mono">{c.shortHash}</code>
                      <span className="truncate">{c.author}</span>
                      <span>{new Date(c.timestamp).toLocaleString()}</span>
                    </p>
                  </li>
                ))}
              </ul>
            )
          ) : selection === null ? (
            <EmptyState className="m-auto" title={t('git.selectFile')} description="" />
          ) : diff === null ? (
            <p role="status" className="p-6 text-[13px] text-fg-muted">
              {t('git.loading')}
            </p>
          ) : diff.binary ? (
            <EmptyState className="m-auto" title={t('git.diffBinary')} description="" />
          ) : diff.text === '' || (sides !== null && sides.original === sides.modified) ? (
            // 后一种:只改了文件模式之类 —— git 有输出,但两侧正文一字不差
            <EmptyState className="m-auto" title={t('git.diffEmpty')} description="" />
          ) : sides !== null ? (
            <CodeDiffViewer original={sides.original} modified={sides.modified} path={diff.path} />
          ) : (
            <DiffView t={t} diff={diff} />
          )}
        </div>
      </section>
    </Shell>
  )
}

/** 标题栏 + 画布。五种状态(无工作区 / 加载中 / 四种不可用 / 正常)共用一个外框。 */
function Shell({
  t,
  onClose,
  onRefresh,
  children
}: {
  t: Translate
  onClose?: () => void
  onRefresh: (() => void) | null
  children: ReactNode
}): ReactNode {
  return (
    /*
      52px 标题栏、`app-drag`、右端给自绘窗口按钮让位、左端 `SidebarReveal` ——
      四条都归 `shell/FeatureFrame` 管,原委见那个文件的头。
    */
    <FeatureFrame
      header={
        <>
          <span className="text-[14px] font-medium text-fg">{t('git.title')}</span>
          <span className="truncate text-[12px] text-fg-faint">{t('git.description')}</span>
          <div className="ml-auto flex items-center gap-1">
            {onRefresh !== null && (
              <IconButton label={t('git.refresh')} onClick={onRefresh}>
                <RefreshCw size={14} />
              </IconButton>
            )}
            {onClose !== undefined && (
              <IconButton label={t('git.close')} onClick={onClose}>
                <X size={15} />
              </IconButton>
            )}
          </div>
        </>
      }
    >
      <div className="flex min-h-0 flex-1">{children}</div>
    </FeatureFrame>
  )
}

function FileGroup({
  t,
  title,
  files,
  staged,
  busy,
  selection,
  actionLabel,
  bulkLabel,
  onSelect,
  onAction
}: {
  t: Translate
  title: string
  files: GitFileChange[]
  staged: boolean
  busy: boolean
  selection: Selection | null
  actionLabel: string
  bulkLabel: string
  onSelect: (s: Selection) => void
  onAction: (paths: string[]) => void
}): ReactNode {
  if (files.length === 0) return null
  // 冲突文件不参与「全部暂存」:把冲突标记连同标记一起 add 进去,是在用一次
  // 误操作把冲突「解决」掉。它只能一个个来,而且得先真的解决。
  const actionable = files.filter((f) => !f.conflicted).map((f) => f.path)
  return (
    <div className="mb-2">
      <div className="flex items-center justify-between px-2 py-1.5">
        <span className="text-[11px] font-medium uppercase tracking-wide text-fg-faint">
          {title} · {files.length}
        </span>
        {actionable.length > 0 && (
          <button
            type="button"
            disabled={busy}
            onClick={() => onAction(actionable)}
            className="rounded-[6px] px-1.5 py-0.5 text-[11px] text-fg-faint hover:bg-tint-hover hover:text-fg disabled:opacity-40"
          >
            {bulkLabel}
          </button>
        )}
      </div>
      {files.map((file) => {
        const active = selection !== null && selection.path === file.path && selection.staged === staged
        // 路径一律是仓库相对的 POSIX 形式(主进程 `core.quotePath=false` + 斜杠),
        // 所以这里按 '/' 切就够,不必过 path 模块
        const slash = file.path.lastIndexOf('/')
        const name = slash === -1 ? file.path : file.path.slice(slash + 1)
        const dir = slash === -1 ? '' : file.path.slice(0, slash)
        const { Icon, className: iconClass } = iconFor(name, 'file')
        const letter = file.conflicted ? '!' : statusLetter(file, staged)
        const status = statusMeta(letter, t)
        return (
          <div
            key={`${staged ? 's' : 'u'}:${file.path}`}
            className={cn(
              'group flex items-center gap-1.5 rounded-[8px] px-2 py-1.5',
              active ? 'bg-tint-strong' : 'hover:bg-tint-hover'
            )}
          >
            <button
              type="button"
              onClick={() => onSelect({ path: file.path, staged })}
              className="flex min-w-0 flex-1 items-center gap-2 text-left"
              title={file.renamedFrom === undefined ? file.path : t('git.renamedFrom', { from: file.renamedFrom })}
            >
              <Icon size={14} className={cn('shrink-0', iconClass)} />
              <span
                className={cn(
                  'truncate text-[13px]',
                  active ? 'text-fg' : 'text-fg-muted group-hover:text-fg'
                )}
              >
                {name}
              </span>
              {/* 目录挂在文件名后面、更淡更小 —— 照 VS Code 的 SCM 列表:
                  一屏文件里真正要读的是 basename,路径只在重名时才需要 */}
              {dir !== '' && (
                <span className="min-w-0 shrink truncate text-[11px] text-fg-faint">{dir}</span>
              )}
            </button>
            <code
              className={cn('w-3 shrink-0 text-center font-mono text-[11px]', status.className)}
              title={status.title}
            >
              {letter}
            </code>
            <IconButton
              label={file.conflicted ? t('git.conflictedHint') : actionLabel}
              size={20}
              disabled={busy || file.conflicted}
              onClick={() => onAction([file.path])}
            >
              {staged ? <Minus size={12} /> : <Plus size={12} />}
            </IconButton>
          </div>
        )
      })}
    </div>
  )
}

/**
 * diff 正文的**兜底**画法 —— 只在拿不到两侧全文时用(二进制以外的那几种:
 * 单侧超过 1MB、冲突中、子模块,见主进程 `getGitDiffSides`)。正常情况画的是
 * `components/diff/CodeDiffViewer`,那边有横向滚动、并排对比和折叠。
 *
 * ★ **封顶在 `DIFF_LINE_LIMIT` 行**,见那个常量上的注释。超出的部分给一句说明
 *   而不是一个「展开全部」—— 展开之后卡的还是同一下,只是换成用户自己按的。
 *
 * ★ 解析放在 `useMemo` 里:面板每次 setState(轮询、忙碌态、输入框敲字)都会
 *   重渲染这棵树,而 diff 本身几乎不变。它同时也是 `DiffLines` 的 prop,
 *   每帧新建一份等价数组会让语法高亮跟着每帧重算。
 *
 * ★ **正文换行而不是横向滚动**(`wrap`)。横向滚动会把左边的行号栏一起推走 ——
 *   而行号恰恰是滚到一半时最需要的那个东西;这一栏还可能被左侧 340px 挤得很窄,
 *   长行在这里是常态而非例外。
 *
 * 行怎么画不在这里:`components/diff/DiffLines.tsx` 是全仓库唯一那一份,聊天里的
 * 工具卡和「改动审查」画的是同一个组件(原先这边是另一套行样式和另一条行号栏)。
 */
function DiffView({ t, diff }: { t: Translate; diff: GitDiff }): ReactNode {
  const parsed = useMemo(() => parseUnifiedDiff(diff.text, DIFF_LINE_LIMIT), [diff.text])
  return (
    <div className="pb-4">
      {/* 统计条:进来第一眼要的是「动了多大」,不是第一行改了什么 */}
      <div className="sticky top-0 z-10 flex items-center gap-3 border-b border-hairline bg-canvas px-4 py-2 text-[11px]">
        <span className="font-mono text-accent" title={t('git.diffAdded', { count: parsed.added })}>
          +{parsed.added}
        </span>
        <span
          className="font-mono text-danger"
          title={t('git.diffRemoved', { count: parsed.removed })}
        >
          −{parsed.removed}
        </span>
        {diff.truncated && <span className="text-fg-faint">{t('git.diffTruncated')}</span>}
      </div>
      {/* 语法高亮的语言从路径推:diff 文本自己不带这个信息 */}
      <DiffLines
        lines={parsed.lines}
        language={languageOf(diff.path)}
        lineNumbers
        className="text-[12px] leading-[1.6]"
      />
      {parsed.lines.length < parsed.total && (
        <p className="px-4 pt-2 text-[11px] text-fg-faint">
          {t('git.diffLinesTruncated', { shown: parsed.lines.length, total: parsed.total })}
        </p>
      )}
    </div>
  )
}

/**
 * 提交信息输入框。受控、逐键上抛,见文件头 ★ 为什么不是 `TextArea`。
 *
 * 类名逐条照抄 `TextArea` —— 两个框在同一个界面里出现,长得不一样就是 bug。
 */
function CommitBox({
  value,
  placeholder,
  ariaLabel,
  disabled,
  onChange,
  onSubmit
}: {
  value: string
  placeholder: string
  ariaLabel: string
  disabled: boolean
  onChange: (v: string) => void
  onSubmit: () => void
}): ReactNode {
  return (
    <textarea
      value={value}
      rows={3}
      disabled={disabled}
      aria-label={ariaLabel}
      placeholder={placeholder}
      spellCheck={false}
      onChange={(e) => onChange(e.target.value)}
      onKeyDown={(e) => {
        // ⌘↵ / Ctrl↵ 提交。裸 ↵ 不行:提交信息的正文本来就是多行的
        if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) {
          e.preventDefault()
          onSubmit()
        }
      }}
      className={cn(
        'apple-field app-no-drag selectable w-full resize-none rounded-[8px] border border-border',
        'bg-surface-field px-2.5 py-2 text-[13px] leading-[1.6] text-fg outline-none',
        'transition-colors placeholder:text-fg-faint focus:border-fg-faint disabled:opacity-40'
      )}
    />
  )
}
