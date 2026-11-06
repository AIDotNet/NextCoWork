/** 拉取 models.dev 的 api.json。定价同步与模型目录同步共用。 */
import { MODELS_DEV_URL } from '../shared/domain/models-dev'
import { getHost } from './runtime'

const FETCH_TIMEOUT_MS = 30_000
/** 实测约 5.6MB;32MB 防的是被劫持成无底洞,不是正常增长 */
const MAX_BYTES = 32 * 1024 * 1024

export async function fetchModelsDev(): Promise<unknown> {
  const response = await getHost().fetch(MODELS_DEV_URL, {
    headers: { accept: 'application/json' },
    signal: AbortSignal.timeout(FETCH_TIMEOUT_MS),
    redirect: 'error'
  })
  if (!response.ok) throw new Error(`models.dev 返回 HTTP ${response.status}。`)
  if (Number(response.headers.get('content-length') ?? 0) > MAX_BYTES) throw new Error('models.dev 响应过大,已放弃。')
  const text = await response.text()
  if (text.length > MAX_BYTES) throw new Error('models.dev 响应过大,已放弃。')
  try {
    return JSON.parse(text)
  } catch {
    throw new Error('models.dev 返回的不是有效 JSON。')
  }
}
