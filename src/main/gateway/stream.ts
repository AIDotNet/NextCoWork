/**
 * 需求：外部调用可随时接入正在运行的 Agent，断线后用快照恢复；不能因为一个
 * 慢客户端把 token 无界堆在主进程内存里。断线只释放订阅，不取消 Agent。
 */
import type { ServerResponse } from 'node:http'
import type { RunHandle } from '../kernel/run-registry'

const MAX_BUFFER = 1024 * 1024

export function streamRun(response: ServerResponse, handle: RunHandle, sinceSeq: number): void {
  response.writeHead(200, {
    'Content-Type': 'text/event-stream; charset=utf-8',
    'Cache-Control': 'no-cache, no-transform',
    'X-Accel-Buffering': 'no',
    'Connection': 'keep-alive'
  })
  let off: () => void = () => {}
  const heartbeat = setInterval(() => {
    if (!response.destroyed && response.writableLength < MAX_BUFFER) response.write(': heartbeat\n\n')
    else response.destroy()
  }, 15_000)
  heartbeat.unref()
  response.on('close', () => { off(); clearInterval(heartbeat) })

  const write = (kind: string, seq: number, data: unknown): void => {
    // 需求：有界缓冲超限时让客户端重连取快照；不能阻塞整个 Agent 等待 drain。
    if (response.destroyed || response.writableLength >= MAX_BUFFER) { response.destroy(); return }
    response.write(`id: ${seq}\nevent: ${kind}\ndata: ${JSON.stringify(data)}\n\n`)
  }
  // 订阅与快照在同一个同步块内完成，既不会丢首事件，也不会重复推历史。
  off = handle.on((event, seq) => {
    write('event', seq, { seq, event })
    if (event.type === 'run_end') { off(); clearInterval(heartbeat); response.end() }
  })
  const snapshot = handle.snapshot(sinceSeq)
  write('snapshot', snapshot.seq, snapshot)
  if (snapshot.status !== 'running') { off(); clearInterval(heartbeat); response.end() }
}
