/**
 * 本地历史补全用的指令清单,按工作区缓存在渲染层。
 *
 * ★ 模块级缓存而不是挂在组件上:同一个工作区开三个对话 Tab,读库一次就够;
 *   切 Tab 重挂载也不该每次都去主进程扫一遍。60 秒过期,别的窗口发过的指令迟早能看到。
 * ★ 两份分开存:`loaded` 是库里读来的(每次重读整份替换),`local` 是这个窗口里
 *   用户**原样打出来**的草稿 —— 库里的斜杠命令是展开后的整份模板,而用户下次要补全的
 *   是 `/review foo` 这几个字,所以本地这份不随重读丢掉。
 */
import { listPromptHistory } from '../../services/composer'

const TTL_MS = 60_000
const LIMIT = 500
const LOCAL_LIMIT = 100

interface Entry {
  at: number
  loaded: string[]
  local: string[]
  prompts: string[]
  pending?: Promise<void>
}

const cache = new Map<string, Entry>()

function entryOf(workspaceId: string): Entry {
  return cache.get(workspaceId) ?? { at: 0, loaded: [], local: [], prompts: [] }
}

export function promptHistory(workspaceId: string): readonly string[] {
  return cache.get(workspaceId)?.prompts ?? []
}

/** 过期或没读过才去主进程读;读失败就保留旧的 —— 补全拿不到历史不是错误。 */
export function loadPromptHistory(workspaceId: string, now: number = Date.now()): Promise<void> {
  const entry = entryOf(workspaceId)
  if (entry.pending !== undefined) return entry.pending
  if (entry.at !== 0 && now - entry.at < TTL_MS) return Promise.resolve()
  const pending = listPromptHistory(workspaceId)
    .then(({ prompts }) => {
      const cur = entryOf(workspaceId)
      cache.set(workspaceId, { at: Date.now(), loaded: prompts, local: cur.local, prompts: merge(cur.local, prompts) })
    })
    .catch(() => {
      const cur = entryOf(workspaceId)
      cache.set(workspaceId, { ...cur, at: Date.now(), pending: undefined })
    })
  cache.set(workspaceId, { ...entry, pending })
  return pending
}

/** 刚发出去的那句放到最前面。多行和过长的同库里的规则一样不要。 */
export function rememberPrompt(workspaceId: string, raw: string): void {
  const text = raw.trim()
  if (text === '' || text.length > 500 || text.split('\n').length > 3) return
  const entry = entryOf(workspaceId)
  const local = [text, ...entry.local.filter((p) => p !== text)].slice(0, LOCAL_LIMIT)
  cache.set(workspaceId, { ...entry, local, prompts: merge(local, entry.loaded) })
}

/** 本地刚发的(更新)在前,库里的在后,去重 */
function merge(local: readonly string[], loaded: readonly string[]): string[] {
  const seen = new Set<string>()
  const out: string[] = []
  for (const p of [...local, ...loaded]) {
    if (seen.has(p)) continue
    seen.add(p)
    out.push(p)
    if (out.length >= LIMIT) break
  }
  return out
}

export function resetPromptHistoryForTest(): void {
  cache.clear()
}
