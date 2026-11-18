import { describe, expect, it } from 'vitest'
import type { V3OfferState } from '../../../shared/domain/v3-upgrade'
import { V3OfferService, type V3OfferDeps } from '../v3-offer'

const body = (items: unknown[]): unknown => ({ code: 200, data: { channel: 'stable', items } })
const mac300 = { rid: 'osx-arm64', version: '3.0.0', downloads: [{ fileName: 'NextCoWork-stable-Setup.pkg' }] }

function rig(responses: Array<(() => Promise<{ ok: boolean; json: () => Promise<unknown> }>)>, platform = 'darwin', arch = 'arm64') {
  const emitted: V3OfferState[] = []
  const urls: string[] = []
  let call = 0
  const deps: V3OfferDeps = {
    fetch: (url) => { urls.push(url); return responses[Math.min(call++, responses.length - 1)]!() },
    platform, arch,
    emit: (state) => emitted.push(state)
  }
  return { service: new V3OfferService(deps), emitted, urls }
}
const ok = (json: unknown) => () => Promise.resolve({ ok: true, json: () => Promise.resolve(json) })

describe('V3OfferService', () => {
  it('starts unknown and becomes available when the backend lists an installer for this platform', async () => {
    const { service, emitted, urls } = rig([ok(body([mac300]))])
    expect(service.getState()).toEqual({ status: 'unknown' })
    expect(await service.refresh()).toEqual({ status: 'available', offer: { version: '3.0.0' } })
    expect(emitted).toEqual([{ status: 'available', offer: { version: '3.0.0' } }])
    expect(urls).toEqual(['https://nextco.work/api/client/updates/v3/latest?channel=stable'])
  })

  it('says "none" only when the backend answered and has nothing for this platform', async () => {
    const { service } = rig([ok(body([{ ...mac300, rid: 'win-x64' }]))])
    expect(await service.refresh()).toEqual({ status: 'none' })
  })

  it('stays unknown when the backend has no 3.x feed yet (404) or the network is down', async () => {
    const notFound = rig([() => Promise.resolve({ ok: false, json: () => Promise.resolve({}) })])
    expect(await notFound.service.refresh()).toEqual({ status: 'unknown' })
    expect(notFound.emitted).toEqual([])
    const offline = rig([() => Promise.reject(new Error('ENOTFOUND'))])
    expect(await offline.service.refresh()).toEqual({ status: 'unknown' })
  })

  it('keeps the last known offer when a later check fails, and only broadcasts real changes', async () => {
    const { service, emitted } = rig([ok(body([mac300])), () => Promise.reject(new Error('offline')), ok(body([mac300]))])
    await service.refresh()
    expect(await service.refresh()).toEqual({ status: 'available', offer: { version: '3.0.0' } })
    await service.refresh()
    expect(emitted).toHaveLength(1)
  })

  it('shares one request between concurrent checks', async () => {
    let release!: () => void
    const gate = new Promise<void>((r) => { release = r })
    const { service, urls } = rig([() => gate.then(() => ({ ok: true, json: () => Promise.resolve(body([mac300])) }))])
    const a = service.refresh()
    const b = service.refresh()
    release()
    expect(await a).toEqual(await b)
    expect(urls).toHaveLength(1)
  })

  it('does not ask the backend on a platform 3.x does not ship', async () => {
    const { service, urls } = rig([ok(body([mac300]))], 'win32', 'ia32')
    expect(await service.refresh()).toEqual({ status: 'none' })
    expect(urls).toEqual([])
  })
})
