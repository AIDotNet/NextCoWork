/**
 * `video` 形态的详情渲染器 —— 对话内生成视频(`generate_video`)的**专属视频卡**。
 *
 * 需求:
 *   1. 它是**异步**任务的界面:提交/排队/生成/下载/完成/失败各有各的样子,
 *      而不是一行通用的"运行中";
 *   2. 完成之后是 `<video controls>` —— 用 `ncw://` 地址播本地文件
 *      (Range 由协议处理,见 `net/attachment-protocol.ts`);
 *   3. **独立取消**:一颗按钮,与"停止聊天"无关;
 *   4. `not-stored`(云端成功、取回失败)给一颗**重试取回**,而且文案说清
 *      "不会重新生成" —— 否则用户会以为要再花一次钱;
 *   5. 播放器放不了这种编码时**照样允许下载**,并说明是播放器不支持,
 *      而不是把已生成的视频报成生成失败。
 * 「这一刻摆什么」全部由 `video-gen-view.ts` 算,这里只负责摆。
 *
 * ★★ **为什么不复用 `image` 那张卡**(判据是"用哪种渲染器",不是"产物是不是媒体"):
 * 图片卡的核心是"按 N 格出图 + 提示词区",而视频卡的核心是**一条任务的生命周期**
 * (排队/生成/下载/完成)+ 一个播放器。复用的结果是每张视频卡都长着一张
 * 永远空着的提示词区与一个不存在的"第 2 格"。
 *
 * ★ 任务状态从 store 读(`useVideoJobsStore`),不是从 `output` 里反解 ——
 * 工具调用早就结束了,而任务还在跑(见 `video-gen-view.ts` 文件头那段优先级)。
 */
import { AlertTriangle, Check, Copy, Download, Loader2, RotateCw, X } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { pick } from '../../../../shared/domain/tool-presenter'
import { ActionIconButton, useTransientStatus } from '../../components/ui/ActionIconButton'
import { Button } from '../../components/arc/button/button'
import { useI18n } from '../../i18n'
import { cn } from '../../lib/cn'
import { copyText } from '../../services/app'
import { cancelVideoJob, retryVideoRetrieval, saveVideoFile } from '../../services/video'
import { useVideoJobsStore } from '../../stores/video-jobs'
import { DETAIL_CARD_CLASS, DETAIL_CARD_DANGER_CLASS } from './detail-card'
import { OutputBlock, type DetailProps } from './ToolDetail'
import { videoActionOf, videoGenView } from './video-gen-view'

export function VideoGenDetail({ input, output, isError }: DetailProps): ReactNode {
  const { t } = useI18n()
  const jobId = typeof input === 'object' && input !== null && 'job_id' in input
    ? String((input as Record<string, unknown>)['job_id'] ?? '')
    : ''
  /*
    ★ 任务 id 有两条来源:入参里的 `job_id`(查询/取消),以及**回执正文**里那句
    "Job <id> was accepted…" —— 工具调用刚结束时 `output` 已经在了,而广播可能
    还没到;从回执里读出 id 才有得订阅(store 是按 id 查的)。
  */
  const fromOutput = output === undefined ? undefined : /Job (\S+) was accepted/u.exec(output.content)?.[1]
  const id = jobId !== '' ? jobId : fromOutput
  const job = useVideoJobsStore((s) => (id === undefined ? undefined : s.job(id)))

  if (isError) return <OutputBlock output={output} isError maxLines={20} />

  const view = videoGenView(input, output, job)
  const action = videoActionOf(input)
  const prompt = pick(input, 'prompt').trim()

  return (
    <div
      role="group"
      aria-label={t('videoGen.card.region')}
      data-testid="video-gen-card"
      data-video-gen-phase={view.phase}
      className={cn(DETAIL_CARD_CLASS, 'max-w-[560px] p-1.5')}
    >
      {view.phase === 'ready' && view.src !== undefined ? (
        <VideoPlayer src={view.src} mime={view.mime} />
      ) : (
        <StatusPanel view={view} action={action} />
      )}

      <div className="flex items-center gap-2 px-1 pt-1.5 pb-0.5" data-testid="video-gen-actions">
        <span className="min-w-0 flex-1 truncate text-[11.5px] text-fg-faint">
          {view.model ?? ''}
        </span>
        {view.cancellable && id !== undefined && (
          /* ★ 独立取消 —— 与"停止聊天"无关(见模块头第 3 条) */
          <CancelButton jobId={id} requested={view.cancelRequested} />
        )}
        {view.phase === 'not-stored' && id !== undefined && (
          /* ★ 重试**只取回**,绝不重新生成 —— 文案里明说 */
          <RetryRetrievalButton jobId={id} />
        )}
        {view.phase === 'ready' && id !== undefined && <SaveButton jobId={id} />}
      </div>

      {(prompt !== '' || view.reason !== undefined || view.stage !== undefined) && (
        <Footer prompt={prompt} reason={view.reason} stage={view.stage} phase={view.phase} />
      )}
    </div>
  )
}

/**
 * 播放器。
 *
 * ★ `preload="metadata"` 而不是 `auto`:一屏里可能有好几张已完成的卡,
 * 全部预载等于把几百兆一起读进解码器。`controls` + `playsInline`,
 * **不自动播放**(那既唐突又可能同时响起几段声音)。
 * ★ 还给了 `Download` 一颗 —— 本地解码器放不了某些编码(HEVC)时,
 * 用户仍然要能把文件取出来(见模块头第 5 条)。
 */
function VideoPlayer({ src, mime }: { src: string; mime?: string }): ReactNode {
  const { t } = useI18n()
  const [failed, setFailed] = useState(false)
  return (
    <div className="relative">
      <video
        src={src}
        controls
        playsInline
        preload="metadata"
        data-testid="video-gen-player"
        className="block max-h-[360px] w-full rounded-[8px] bg-black object-contain"
        onError={() => setFailed(true)}
      />
      {failed && (
        /*
          ★ 播放失败**不是生成失败**:视频就在本地,只是这台机器的解码器放不了。
          说清这件事,并指向那颗"保存"。
        */
        <p className="mt-1 px-1 text-[11.5px] leading-[1.6] text-fg-muted" data-testid="video-gen-decode-hint">
          {t('videoGen.card.decodeFailed', { mime: mime ?? 'video/mp4' })}
        </p>
      )}
    </div>
  )
}

/** 非成品阶段的那一格:图标 + 状态字 + **真实的**进度(没给就不编)。 */
function StatusPanel({ view, action }: { view: ReturnType<typeof videoGenView>; action: ReturnType<typeof videoActionOf> }): ReactNode {
  const { t } = useI18n()
  const phaseKey = {
    pending: 'videoGen.card.pending',
    running: 'videoGen.card.running',
    'not-stored': 'videoGen.card.notStored',
    failed: 'videoGen.card.failed',
    canceled: 'videoGen.card.canceled',
    paused: 'videoGen.card.paused',
    ready: 'videoGen.card.running'
  }[view.phase]
  const tone = view.phase === 'failed' ? 'danger' : view.phase === 'not-stored' || view.phase === 'paused' ? 'warn' : 'muted'
  return (
    <div
      role="status"
      data-testid="video-gen-status"
      className={cn(
        'flex aspect-video w-full flex-col items-center justify-center gap-2 rounded-[8px]',
        tone === 'danger' ? DETAIL_CARD_DANGER_CLASS : 'bg-tint',
        tone === 'danger' ? 'text-danger' : tone === 'warn' ? 'text-fg' : 'text-fg-faint'
      )}
    >
      {tone === 'danger' ? (
        <AlertTriangle size={22} aria-hidden />
      ) : tone === 'warn' ? (
        <Check size={22} aria-hidden />
      ) : (
        <Loader2 size={22} className="animate-spin motion-reduce:animate-none" aria-hidden />
      )}
      <span className="text-[12px]">{t(phaseKey)}</span>
      {/* ★ 只有供应商真给了百分比才画进度条 —— 编一个假的进度比没有更糟 */}
      {view.percent !== undefined && (
        <div className="h-1 w-40 overflow-hidden rounded-full bg-stroke" role="progressbar" aria-valuenow={view.percent} aria-valuemin={0} aria-valuemax={100}>
          <div className="h-full bg-accent transition-[width] duration-500 motion-reduce:transition-none" style={{ width: `${String(view.percent)}%` }} />
        </div>
      )}
      {/* ★ 动作词(生成/编辑/延长)让人知道这次调用**在做什么**,而不只是"在跑" */}
      <span className="text-[11px] text-fg-faint">
        {t('videoGen.card.action', { action: t(`videoGen.action.${action}` as 'videoGen.action.generate') })}
      </span>
    </div>
  )
}

function CancelButton({ jobId, requested }: { jobId: string; requested: boolean }): ReactNode {
  const { t } = useI18n()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  return (
    <>
      <Button
        type="button"
        variant="secondary"
        size="sm"
        disabled={busy || requested}
        onClick={() => {
          setBusy(true)
          setError(null)
          void cancelVideoJob(jobId)
            .then((result) => {
              /*
                ★ 只在**真的被拒**时才显示错误:`requested` 与 `already-done`
                都不是失败(前者"已请求",后者"它已经跑完了")。
              */
              if (!result.ok) setError(result.reason)
            })
            .catch((cause: unknown) => setError(cause instanceof Error ? cause.message : String(cause)))
            .finally(() => setBusy(false))
        }}
      >
        {busy ? <Loader2 size={12} className="animate-spin" /> : <X size={12} />}{requested ? t('videoGen.card.canceling') : t('videoGen.card.cancel')}
      </Button>
      {error !== null && (
        /* ★ 不支持取消的家会走到这里 —— 那句话必须**留在界面上**(而不是悬停提示) */
        <span className="max-w-[240px] shrink-0 truncate text-[10.5px] text-fg-faint" title={error} data-testid="video-gen-cancel-note">
          {error}
        </span>
      )}
    </>
  )
}

function RetryRetrievalButton({ jobId }: { jobId: string }): ReactNode {
  const { t } = useI18n()
  const [busy, setBusy] = useState(false)
  return (
    <Button
      type="button"
      variant="secondary"
      size="sm"
      disabled={busy}
      onClick={() => {
        setBusy(true)
        void retryVideoRetrieval(jobId).finally(() => setBusy(false))
      }}
    >
      {busy ? <Loader2 size={12} className="animate-spin" /> : <RotateCw size={12} />}{t('videoGen.card.retryRetrieval')}
    </Button>
  )
}

function SaveButton({ jobId }: { jobId: string }): ReactNode {
  const { t } = useI18n()
  const [status, setStatus] = useTransientStatus()
  return (
    <Button
      type="button"
      variant="secondary"
      size="sm"
      onClick={() => {
        void saveVideoFile(jobId)
          .then((saved) => {
            // ★ 用户在对话框里按了取消 → null。那不是失败,**不闪任何状态**
            if (saved !== null) setStatus('done')
          })
          .catch(() => setStatus('failed'))
      }}
    >
      {status === 'done' ? <Check size={12} /> : <Download size={12} />}{t('videoGen.card.save')}
    </Button>
  )
}

/** 卡片底部:提示词 + 阶段/原因。提示词是领域值(模型写的),不翻译。 */
function Footer({ prompt, reason, stage, phase }: { prompt: string; reason?: string; stage?: string; phase: ReturnType<typeof videoGenView>['phase'] }): ReactNode {
  const { t } = useI18n()
  const [copy, setCopy] = useTransientStatus()
  return (
    <div className="w-0 min-w-full px-1 pt-1 pb-0.5">
      {(reason !== undefined || stage !== undefined) && (
        <p
          className={cn('mb-1 text-[11px] leading-[1.6]', phase === 'failed' ? 'text-danger' : 'text-fg-faint')}
          data-testid="video-gen-reason"
        >
          {stage ?? ''}
          {reason === undefined ? '' : `${stage === undefined ? '' : ' · '}${reason}`}
        </p>
      )}
      {prompt !== '' && (
        <div className="flex min-h-6 items-start gap-2 text-[11.5px] text-fg-faint">
          <span className="shrink-0">{t('videoGen.card.prompt')}</span>
          <span className="selectable line-clamp-2 min-w-0 flex-1 whitespace-pre-wrap break-words text-fg-muted">{prompt}</span>
          <ActionIconButton
            label={t(copy === 'failed' ? 'videoGen.card.promptCopyFailed' : copy === 'done' ? 'videoGen.card.promptCopied' : 'videoGen.card.promptCopy')}
            testId="video-gen-prompt-copy"
            onClick={() => {
              void copyText(prompt).then(() => setCopy('done')).catch(() => setCopy('failed'))
            }}
          >
            {copy === 'done' ? <Check size={12} /> : <Copy size={12} />}
          </ActionIconButton>
        </div>
      )}
    </div>
  )
}
