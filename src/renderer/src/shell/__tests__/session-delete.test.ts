import { describe, expect, it, vi } from 'vitest'

vi.mock('../../stores/toast', () => ({ toast: { error: vi.fn() } }))

import { AgentErrorException } from '../../services/ipc'
import { isSessionDeleteRunningError } from '../../services/sessions'
import { toast } from '../../stores/toast'
import { requestSessionDelete } from '../session-delete'

/**
 * 侧边栏删除:是否「在跑」听主进程的,不听渲染层那份可能陈旧的运行中索引。
 * 主进程说在跑 → 交给「停止并删除」确认框;别的失败 → toast,不吞。
 */

const running = new AgentErrorException({ code: 'conflict', message: 'busy', retryable: false, messageKey: 'session.deleteRunningMessage' })

describe('requestSessionDelete', () => {
  it('删成功就是 deleted', async () => {
    const del = vi.fn(async () => {})
    await expect(requestSessionDelete(del, 's1')).resolves.toBe('deleted')
    expect(del).toHaveBeenCalledWith('s1')
  })

  it('主进程说有 Agent 在跑 → running,不弹错误 toast', async () => {
    vi.mocked(toast.error).mockClear()
    await expect(requestSessionDelete(async () => { throw running }, 's1')).resolves.toBe('running')
    expect(toast.error).not.toHaveBeenCalled()
  })

  it('别的失败照旧 toast 并抛出', async () => {
    vi.mocked(toast.error).mockClear()
    await expect(requestSessionDelete(async () => { throw new Error('disk') }, 's1')).rejects.toThrow('disk')
    expect(toast.error).toHaveBeenCalledWith('disk', 'session-delete')
  })
})

describe('isSessionDeleteRunningError', () => {
  it('只认 conflict + 那一个 messageKey', () => {
    expect(isSessionDeleteRunningError(running)).toBe(true)
    expect(isSessionDeleteRunningError(new AgentErrorException({ code: 'conflict', message: 'x', retryable: true }))).toBe(false)
    expect(isSessionDeleteRunningError(new Error('session.deleteRunningMessage'))).toBe(false)
  })
})
