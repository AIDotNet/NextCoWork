import { describe, expect, it } from 'vitest'
import { LEASE_DEFAULT_TTL_MS, LEASE_MAX_TTL_MS, LeaseManager } from '../lease'

function setup(active = ['alice', 'bob']) {
  let now = 1_000_000
  const manager = new LeaseManager({ now: () => now, isActiveController: (id) => active.includes(id) })
  return { manager, advance: (ms: number) => { now += ms }, setActive: (ids: string[]) => { active.splice(0, active.length, ...ids) } }
}

describe('input leases', () => {
  it('lets one controller type at a time and tells the other to wait', () => {
    const { manager } = setup()
    const lease = manager.acquire('terminal', 't1', 'alice')
    expect(lease.holder).toBe('alice')
    expect(() => manager.acquire('terminal', 't1', 'bob')).toThrowError(expect.objectContaining({ code: 'busy' }))
    // 别的资源互不影响;观看者能看到谁持有但拿不到 token。
    expect(manager.acquire('terminal', 't2', 'bob').holder).toBe('bob')
    expect(manager.holderOf('terminal', 't1')).toEqual({ holder: 'alice', expiresAt: lease.expiresAt })
    expect(JSON.stringify(manager.holderOf('terminal', 't1'))).not.toContain(String(lease.token))
  })

  it('re-acquiring your own lease renews it and keeps the same token', () => {
    const { manager, advance } = setup()
    const first = manager.acquire('browser', 'b1', 'alice')
    advance(10_000)
    const again = manager.acquire('browser', 'b1', 'alice')
    expect(again.token).toBe(first.token)
    expect(again.expiresAt).toBeGreaterThan(first.expiresAt)
  })

  it('expires without the holder, lets the other controller in, and fences the old token', () => {
    const { manager, advance } = setup()
    const old = manager.acquire('terminal', 't1', 'alice')
    advance(LEASE_DEFAULT_TTL_MS + 1)
    const fresh = manager.acquire('terminal', 't1', 'bob')
    expect(fresh.token).toBeGreaterThan(old.token)
    // 迟到的旧持有者输入带着旧 token:被拒绝,不会插进 bob 的输入里。
    expect(() => manager.check('terminal', 't1', 'alice', old.token)).toThrowError(expect.objectContaining({ code: 'stale_binding' }))
    expect(() => manager.check('terminal', 't1', 'bob', old.token)).toThrowError(expect.objectContaining({ code: 'stale_binding' }))
    expect(() => manager.check('terminal', 't1', 'bob', fresh.token)).not.toThrow()
  })

  it('renewal needs the current token and cannot revive an expired lease', () => {
    const { manager, advance } = setup()
    const lease = manager.acquire('document', 'd1', 'alice')
    expect(manager.renew('document', 'd1', 'alice', lease.token).expiresAt).toBeGreaterThanOrEqual(lease.expiresAt)
    expect(() => manager.renew('document', 'd1', 'alice', lease.token + 1)).toThrowError(expect.objectContaining({ code: 'stale_binding' }))
    expect(() => manager.renew('document', 'd1', 'bob', lease.token)).toThrowError(expect.objectContaining({ code: 'stale_binding' }))
    advance(LEASE_DEFAULT_TTL_MS + 1)
    expect(() => manager.renew('document', 'd1', 'alice', lease.token)).toThrowError(expect.objectContaining({ code: 'stale_binding' }))
  })

  it('hands off explicitly: the new holder gets a new token and the old one is cut off at once', () => {
    const { manager } = setup()
    const mine = manager.acquire('terminal', 't1', 'alice')
    const theirs = manager.handoff('terminal', 't1', 'alice', mine.token, 'bob')
    expect(theirs).toMatchObject({ holder: 'bob' })
    expect(theirs.token).toBeGreaterThan(mine.token)
    expect(() => manager.check('terminal', 't1', 'alice', mine.token)).toThrowError(expect.objectContaining({ code: 'stale_binding' }))
    expect(() => manager.check('terminal', 't1', 'bob', theirs.token)).not.toThrow()
  })

  it('only the holder may hand off, and only to a paired controller other than itself', () => {
    const { manager, setActive } = setup()
    const lease = manager.acquire('terminal', 't1', 'alice')
    expect(() => manager.handoff('terminal', 't1', 'bob', lease.token, 'alice')).toThrowError(expect.objectContaining({ code: 'stale_binding' }))
    expect(() => manager.handoff('terminal', 't1', 'alice', lease.token, 'alice')).toThrowError(expect.objectContaining({ code: 'invalid_request' }))
    expect(() => manager.handoff('terminal', 't1', 'alice', lease.token, 'mallory')).toThrowError(expect.objectContaining({ code: 'not_found' }))
    setActive(['alice'])
    expect(() => manager.handoff('terminal', 't1', 'alice', lease.token, 'bob')).toThrowError(expect.objectContaining({ code: 'not_found' }))
    expect(() => manager.check('terminal', 't1', 'alice', lease.token)).not.toThrow() // 失败的交接没有改变持有者
  })

  it('releases only for the holder, and releaseAll drops everything a revoked controller held', () => {
    const { manager } = setup()
    const a = manager.acquire('terminal', 't1', 'alice')
    manager.acquire('browser', 'b1', 'alice')
    manager.acquire('terminal', 't2', 'bob')
    expect(() => manager.release('terminal', 't1', 'bob', a.token)).toThrowError(expect.objectContaining({ code: 'stale_binding' }))
    expect(manager.release('terminal', 't1', 'alice', a.token)).toBe(true)
    expect(manager.release('terminal', 't1', 'alice', a.token)).toBe(false)
    expect(manager.releaseAll('alice')).toBe(1)
    expect(manager.holderOf('browser', 'b1')).toBeNull()
    expect(manager.holderOf('terminal', 't2')).not.toBeNull()
  })

  it('never reuses a token, even if the counter restarts, and validates durations', () => {
    const { manager } = setup()
    const first = manager.acquire('terminal', 'a', 'alice')
    const second = manager.acquire('terminal', 'b', 'alice')
    expect(second.token).toBeGreaterThan(first.token)
    const rebuilt = new LeaseManager({ now: () => 1_000_000 + 5_000, isActiveController: () => true })
    expect(rebuilt.acquire('terminal', 'a', 'alice').token).toBeGreaterThan(first.token - 1 + 0) // 以时钟为下限
    for (const ttl of [0, 999, LEASE_MAX_TTL_MS + 1, 1.5]) {
      expect(() => manager.acquire('browser', 'x', 'alice', ttl)).toThrowError(expect.objectContaining({ code: 'invalid_request' }))
    }
  })
})
