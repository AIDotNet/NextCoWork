import { describe, expect, it } from 'vitest'
import { modelListRequest, parseModelList } from '../../../main/kernel/upstream/model-list'
import {
  importRows,
  modelListAvailability
} from '../../../renderer/src/settings/pages/model/import-models'
import {
  isPresetAdded,
  providerFromPreset,
  seedModelsForPreset
} from '../../../renderer/src/settings/pages/model/provider-edit'
import { previewUrl } from '../baseurl'
import { findPreset } from '../presets'

describe('API Route preset setup', () => {
  it('creates a provider record that can be saved and recognized as already added', () => {
    const preset = findPreset('api-route')!
    const provider = providerFromPreset(preset)!
    expect(provider.protocol).toBe('openai-chat')
    expect(provider.baseUrl).toBe('https://global.api-route.com/v1')
    expect(provider.credentialRef).toBe('provider:api-route')
    expect(isPresetAdded(preset, [provider])).toBe(true)
    expect(previewUrl(provider.baseUrl, provider.protocol)).toBe(
      'https://global.api-route.com/v1/chat/completions'
    )
  })

  it('requires a key for discovery and avoids seeding a stale fallback catalog', () => {
    const preset = findPreset('api-route')!
    const provider = providerFromPreset(preset)!
    expect(modelListAvailability(provider)).toEqual({
      hint: null,
      needsKey: true
    })
    expect(seedModelsForPreset(preset, provider.protocol)).toEqual([])
    expect(modelListRequest(provider.protocol, provider.baseUrl, 'test-key')).toEqual({
      url: 'https://global.api-route.com/v1/models',
      headers: { accept: 'application/json', authorization: 'Bearer test-key' }
    })
  })

  it('preserves unprefixed upstream model IDs through discovery and import rows', () => {
    const fetched = parseModelList('openai-chat', {
      data: [{ id: 'gpt-6.1-sol' }, { id: 'claude-fable-5-1' }]
    })
    expect(importRows(fetched, []).map((row) => row.id)).toEqual([
      'gpt-6.1-sol',
      'claude-fable-5-1'
    ])
    expect(findPreset('api-route')?.recommended).toBeUndefined()
  })
})
