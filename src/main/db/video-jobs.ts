/**
 * 视频任务的持久化 —— schema 第 28 条那张表的读写。
 *
 * ★★ 与 `repo.ts` 里那些"整行读写"的表同一档,所以它**也能只落一列 JSON** ——
 * 但那三列(`cloud` / `retrieval` / `credential_fingerprint`)是真列,理由是
 * 查询形状(schema 第 28 条上写了)。这里的所有查询都按那三列过滤。
 *
 * ★ 这一层**没有任何网络或调度逻辑**:它只负责"把一份任务状态存下来、读回来"。
 * 后台轮询、取消、下载全在 `video-generation/manager.ts` —— 分开是因为
 * 那些逻辑要能在纯内核测试里用假 store 跑,而不必碰 SQLite。
 */
import type { VideoJob } from '../../shared/domain/video-generation'
import { needsWorker } from '../../shared/domain/video-generation'
import { stmt } from './index'

function parseJob(raw: string): VideoJob | null {
  try {
    const parsed: unknown = JSON.parse(raw)
    if (typeof parsed !== 'object' || parsed === null) return null
    const job = parsed as VideoJob
    return typeof job.id === 'string' ? job : null
  } catch {
    // 坏行不该让整个恢复流程挂掉 —— 跳过它就是"这条任务丢失",而抛出去是"所有任务都恢复不了"。
    return null
  }
}

export function listVideoJobsForSession(sessionId: string): VideoJob[] {
  return stmt('SELECT json FROM video_generation_jobs WHERE session_id = ? ORDER BY created_at DESC')
    .all(sessionId)
    .flatMap((row) => {
      const job = parseJob(String((row as Record<string, unknown>)['json'] ?? ''))
      return job === null ? [] : [job]
    })
}

export function getVideoJob(id: string): VideoJob | undefined {
  const row = stmt('SELECT json FROM video_generation_jobs WHERE id = ?').get(id) as Record<string, unknown> | undefined
  if (row === undefined) return undefined
  return parseJob(String(row['json'] ?? '')) ?? undefined
}

/**
 * 启动恢复用:本配置作用域下**还需要 worker** 的任务。
 *
 * ★ 判据用 `needsWorker`(领域函数),不在这里重写一遍"哪些状态算未完成"——
 * 两份判断迟早分叉,而分叉的表现是"某类任务永远停在处理中、重启也不动"。
 */
export function listRecoverableVideoJobs(configProfile: string): VideoJob[] {
  const rows = stmt(
    `SELECT json FROM video_generation_jobs
      WHERE config_profile = ?
        AND (cloud NOT IN ('succeeded', 'failed', 'canceled')
             OR (cloud = 'succeeded' AND retrieval IN ('waiting', 'downloading', 'retryable_error')))
      ORDER BY updated_at ASC`
  ).all(configProfile)
  return rows.flatMap((row) => {
    const job = parseJob(String((row as Record<string, unknown>)['json'] ?? ''))
    return job === null || !needsWorker(job) ? [] : [job]
  })
}

/** 会话被删时它的任务行会被外键带走;这个函数给"删会话前先把本机 worker 停掉"用。 */
export function listVideoJobIdsForSession(sessionId: string): string[] {
  return stmt('SELECT id FROM video_generation_jobs WHERE session_id = ?')
    .all(sessionId)
    .map((row) => String((row as Record<string, unknown>)['id']))
}

export function putVideoJob(job: VideoJob, json: string): void {
  stmt(
    `INSERT INTO video_generation_jobs
       (id, session_id, workspace_id, config_profile, cloud, retrieval, credential_fingerprint, upstream_id, created_at, updated_at, revision, json)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
     ON CONFLICT (id) DO UPDATE SET
       cloud = excluded.cloud,
       retrieval = excluded.retrieval,
       credential_fingerprint = excluded.credential_fingerprint,
       upstream_id = excluded.upstream_id,
       updated_at = excluded.updated_at,
       revision = excluded.revision,
       json = excluded.json`
  ).run(
    job.id,
    job.sessionId,
    job.workspaceId,
    job.configProfile,
    job.cloud,
    job.retrieval,
    job.credentialFingerprint ?? null,
    job.upstreamId ?? null,
    job.createdAt,
    job.updatedAt,
    job.revision,
    json
  )
}

export function removeVideoJob(id: string): void {
  stmt('DELETE FROM video_generation_jobs WHERE id = ?').run(id)
}
