/** 需求：真实关窗前的预检必须可否决、去重，显式丢弃不能变成默认行为。 */
import { describe, expect, it, vi } from 'vitest'
import { DocumentQuitGuard } from '../quit-guard'

function setup(): {
  guard: DocumentQuitGuard
  acquire: ReturnType<typeof vi.fn<(discard: boolean) => Promise<(() => void) | null>>>
  confirmDiscard: ReturnType<typeof vi.fn<(error: unknown) => Promise<boolean>>>
  begin: ReturnType<typeof vi.fn<() => void>>
  release: ReturnType<typeof vi.fn<() => void>>
} {
  const release = vi.fn<() => void>()
  const acquire = vi.fn<(discard: boolean) => Promise<(() => void) | null>>(async () => release)
  const confirmDiscard = vi.fn<(error: unknown) => Promise<boolean>>(async () => false)
  const begin = vi.fn<() => void>()
  const guard = new DocumentQuitGuard({ isQuitting: () => false, acquire, confirmDiscard, begin })
  return { guard, acquire, confirmDiscard, begin, release }
}

describe('DocumentQuitGuard', () => {
  it('does not close windows when unsaved state is refused and the user cancels', async () => {
    const { guard, acquire, confirmDiscard, begin } = setup()
    acquire.mockRejectedValue(new Error('unsaved'))
    await guard.request()
    expect(acquire).toHaveBeenCalledWith(false)
    expect(confirmDiscard).toHaveBeenCalledOnce()
    expect(begin).not.toHaveBeenCalled()
    expect(guard.acquired).toBe(false)
  })

  it('discards only after an explicit positive confirmation', async () => {
    const { guard, acquire, confirmDiscard, begin } = setup()
    acquire.mockRejectedValueOnce(new Error('unsaved'))
    confirmDiscard.mockResolvedValue(true)
    await guard.request()
    expect(acquire.mock.calls).toEqual([[false], [true]])
    expect(guard.acquired).toBe(true)
    expect(begin).toHaveBeenCalledOnce()
  })

  it('does not retry a synchronously vetoed close and restores editing', async () => {
    const { guard, begin, release } = setup()
    begin.mockImplementation(() => { guard.veto() })
    await guard.request()
    expect(begin).toHaveBeenCalledOnce()
    expect(release).toHaveBeenCalledOnce()
    expect(guard.acquired).toBe(false)
    begin.mockImplementation(() => undefined)
    await guard.request()
    expect(begin).toHaveBeenCalledTimes(2)
  })

  it('deduplicates pending requests and releases a late result after a veto', async () => {
    const { guard, acquire, begin, release } = setup()
    let finish: (release: () => void) => void = () => undefined
    acquire.mockImplementation(() => new Promise((resolve) => { finish = resolve }))
    const first = guard.request()
    await guard.request()
    expect(acquire).toHaveBeenCalledOnce()
    guard.veto()
    finish(release)
    await first
    expect(release).toHaveBeenCalledOnce()
    expect(begin).not.toHaveBeenCalled()
  })

  it('releases the guard if beginning the close throws', async () => {
    const { guard, begin, release } = setup()
    begin.mockImplementation(() => { throw new Error('window failed') })
    await expect(guard.request()).rejects.toThrow('window failed')
    expect(release).toHaveBeenCalledOnce()
    expect(guard.acquired).toBe(false)
  })
})
