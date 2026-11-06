/**
 * 旧转录里的内联图:读到哪一页、转哪一页 —— 走真实数据库的比较并替换。
 *
 * 钉的是:转过的图换成 `ncw://` 并落库(再读不再转)、会话忙时不动、转存期间消息被改写时
 * 放弃(不拿旧快照盖回去)、单张失败只保留那一张、每次有上限、坏 data URL 原样留着。
 */
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AgentMessage, ContentPart } from '../../shared/agent/message'
import { assistantMessage, userMessage } from '../../shared/agent/message'
import { closeDatabase, openDatabase } from '../db/index'
import * as repo from '../db/repo'
import { convertInlineImages, INLINE_CONVERT_MAX_IMAGES, type InlineImageDeps } from '../inline-images'

let dir = ''

beforeEach(() => {
  closeDatabase()
  dir = mkdtempSync(join(tmpdir(), 'nextcowork-inline-images-'))
  openDatabase(dir)
  repo.createSession({ id: 's', workspaceId: 'w', rootPathAtCreation: '/w' })
})

afterEach(() => {
  closeDatabase()
  rmSync(dir, { recursive: true, force: true })
})

const PNG = 'data:image/png;base64,iVBORw0KGgo='

function deps(overrides: Partial<InlineImageDeps> = {}): InlineImageDeps & { save: ReturnType<typeof vi.fn> } {
  let n = 0
  const save = vi.fn((_sessionId: string, _mime: string, _bytes: Uint8Array<ArrayBuffer>) => `ncw://attachments/sessions/s/img${String(++n)}.png`)
  return {
    isBusy: () => false,
    save,
    replaceIfUnchanged: (sessionId, messageId, expected, parts) => repo.replaceMessagePartsIfUnchanged(sessionId, messageId, expected, parts),
    log: () => undefined,
    ...overrides
  } as InlineImageDeps & { save: ReturnType<typeof vi.fn> }
}

function seed(): AgentMessage[] {
  const history = [
    userMessage('u1', [{ type: 'text', text: '看图' }, { type: 'image', mime: 'image/png', dataRef: PNG }], 10),
    assistantMessage('a1', [{ type: 'tool_call', callId: 'c1', name: 'browser_screenshot', input: {} }], 11),
    userMessage('r1', [{
      type: 'tool_result', callId: 'c1', isError: false,
      output: { content: 'captured', images: [{ mime: 'image/png', dataRef: PNG }] }
    }], 12),
    assistantMessage('b1', [{ type: 'text', text: '好' }], 13)
  ]
  repo.replaceHistory('s', history)
  return [...repo.getHistory('s')]
}

const refsOf = (messages: readonly AgentMessage[]): string[] => messages.flatMap((m) => m.parts.flatMap((p: ContentPart) =>
  p.type === 'image' ? [p.dataRef] : p.type === 'tool_result' ? (p.output.images ?? []).map((i) => i.dataRef) : []))

describe('convertInlineImages', () => {
  it('★ image part 与工具回执里的内联图都换成 ncw:// 并落库;再读一次不再转', () => {
    const page = seed()
    const d = deps()

    const out = convertInlineImages('s', page, d)

    expect(refsOf(out)).toEqual(['ncw://attachments/sessions/s/img1.png', 'ncw://attachments/sessions/s/img2.png'])
    expect(refsOf(repo.getHistory('s'))).toEqual(refsOf(out))
    // 原图字节原样交给附件仓,不压缩
    expect(Buffer.from(d.save.mock.calls[0]![2] as Uint8Array).toString('base64')).toBe('iVBORw0KGgo=')
    // 正文、回执文字不动
    expect(out[0]!.parts[0]).toEqual({ type: 'text', text: '看图' })

    const again = repo.getHistory('s') as AgentMessage[]
    expect(convertInlineImages('s', again, d)).toBe(again)
    expect(d.save).toHaveBeenCalledTimes(2)
  })

  it('会话忙时一张都不动', () => {
    const page = seed()
    const d = deps({ isBusy: () => true })

    expect(convertInlineImages('s', page, d)).toBe(page)
    expect(d.save).not.toHaveBeenCalled()
    expect(refsOf(repo.getHistory('s'))).toEqual([PNG, PNG])
  })

  it('★ 转存期间消息被改写:放弃这一次,库里保留新的那一份', () => {
    const page = seed()
    const d = deps({
      save: vi.fn(() => {
        // 转存的同时用户把这条提问改了
        repo.replaceMessagePartsIfUnchanged('s', 'u1', JSON.stringify(page[0]!.parts), [{ type: 'text', text: '改过了' }])
        return 'ncw://attachments/sessions/s/x.png'
      })
    })

    const out = convertInlineImages('s', page, d)

    expect(out[0]).toBe(page[0])
    expect(repo.getHistory('s')[0]!.parts).toEqual([{ type: 'text', text: '改过了' }])
  })

  it('一张存不下来只保留那一张,其余照转', () => {
    const page = seed()
    let calls = 0
    const log = vi.fn()
    const d = deps({
      log,
      save: vi.fn(() => {
        calls += 1
        if (calls === 1) throw new Error('disk full')
        return 'ncw://attachments/sessions/s/ok.png'
      })
    })

    const out = convertInlineImages('s', page, d)

    expect(refsOf(out)).toEqual([PNG, 'ncw://attachments/sessions/s/ok.png'])
    expect(refsOf(repo.getHistory('s'))).toEqual([PNG, 'ncw://attachments/sessions/s/ok.png'])
    expect(log).toHaveBeenCalled()
  })

  it('每次读页有张数上限,剩下的下次再转', () => {
    const many = Array.from({ length: INLINE_CONVERT_MAX_IMAGES + 3 }, (_, i) =>
      userMessage(`m${String(i)}`, [{ type: 'image', mime: 'image/png', dataRef: PNG }], i))
    repo.replaceHistory('s', many)
    const d = deps()

    const first = convertInlineImages('s', [...repo.getHistory('s')], d)
    expect(refsOf(first).filter((r) => r.startsWith('ncw://'))).toHaveLength(INLINE_CONVERT_MAX_IMAGES)

    const second = convertInlineImages('s', [...repo.getHistory('s')], d)
    expect(refsOf(second).every((r) => r.startsWith('ncw://'))).toBe(true)
  })

  it('形状不对的 data URL 原样留着,不调附件仓', () => {
    repo.replaceHistory('s', [userMessage('u1', [{ type: 'image', mime: 'image/png', dataRef: 'data:image/png,not-base64' }], 1)])
    const page = [...repo.getHistory('s')]
    const d = deps()

    expect(convertInlineImages('s', page, d)).toBe(page)
    expect(d.save).not.toHaveBeenCalled()
  })
})
