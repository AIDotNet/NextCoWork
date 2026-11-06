import { describe, expect, it } from 'vitest'
import { IpcError, NotImplementedError, toAgentError } from '../errors'
import { DatabaseBusyError } from '../../db'

describe('toAgentError', () => {
  it('IpcError 的分类原样带过去', () => {
    expect(toAgentError(new IpcError('auth', 'API key 无效', 401))).toMatchObject({
      code: 'auth',
      message: 'API key 无效',
      status: 401
    })
  })

  it('IpcError 的本地化 key/params 原样带过去,别在 IPC 边界丢掉', () => {
    const e = toAgentError(
      new IpcError('unknown', 'internal error', undefined, {
        messageKey: 'attachment.error.tooLarge',
        messageParams: { name: 'photo.png', limit: 32 }
      })
    )
    expect(e).toMatchObject({
      code: 'unknown',
      message: 'internal error',
      messageKey: 'attachment.error.tooLarge',
      messageParams: { name: 'photo.png', limit: 32 },
      retryable: false
    })
  })

  it('异步事务占用数据库是可重试的冲突，不冒充未知故障', () => {
    expect(toAgentError(new DatabaseBusyError())).toMatchObject({ code: 'conflict', retryable: true })
  })

  it('NotImplementedError 是 unknown,但消息指向实施顺序', () => {
    const e = toAgentError(new NotImplementedError('mcp:list', '步骤 10'))
    expect(e.code).toBe('unknown')
    expect(e.message).toContain('步骤 10')
  })

  it('裸 Error 是 unknown,消息保留', () => {
    expect(toAgentError(new Error('炸了'))).toMatchObject({ code: 'unknown', message: '炸了' })
  })

  it('非 Error 抛出物也有个说法', () => {
    expect(toAgentError('一个字符串')).toMatchObject({ code: 'unknown', message: '一个字符串' })
    expect(toAgentError(undefined).code).toBe('unknown')
  })

  describe('中断不是故障', () => {
    it('DOMException 形状', () => {
      expect(toAgentError(new DOMException('aborted', 'AbortError')).code).toBe('aborted')
    })

    it('node 的 AbortError(不是 DOMException)', () => {
      const e = new Error('The operation was aborted')
      e.name = 'AbortError'
      expect(toAgentError(e).code).toBe('aborted')
    })

    /**
     * ★ 这条是把判断从 `err.name === 'AbortError'` 换成 `isAbortError` 的**全部理由**。
     *
     * undici 把中断包成 `TypeError: fetch failed`,真正的 AbortError 在 `cause` 上。
     * 只看 name 的话它会变成 `code: 'unknown'` —— 用户点了停止,却收到一个错误弹窗。
     */
    it('undici 包出来的形状:TypeError 套一个 AbortError cause', () => {
      const err = new TypeError('fetch failed', {
        cause: new DOMException('aborted', 'AbortError')
      })
      expect(toAgentError(err).code).toBe('aborted')
    })

    /** IpcError 的显式分类优先于中断嗅探 —— handler 主动拒绝时说了算 */
    it('显式分类过的 IpcError 不被当成中断', () => {
      expect(toAgentError(new IpcError('provider', '上游 abort 了这次请求')).code).toBe('provider')
    })
  })
})
