/**
 * `SaveImage` —— 按地址取字节、原样落进工作区。
 *
 * 真临时目录、真写盘(同 `fs-tools.test.ts` 的理由:覆盖判定、目录 vs 文件、
 * exclusive 写只有真文件系统才测得出来)。会话图片仓用假实现 —— 它自己的安全校验
 * (`resolveImageDataRef`)由 `upstream/__tests__/images.test.ts` 负责。
 */
import { existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { nodeHost, type KernelHost } from '../../../host'
import type { SessionImageStore } from '../../../session-images'
import type { ToolContext } from '../../registry'
import { saveImageTool } from '../save-image'

/** 1×1 PNG */
const PNG = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
  'base64'
)
const URL_A = 'ncw://attachments/sessions/s1/01ABC.png'

let root = ''

function images(over: Partial<SessionImageStore> = {}): SessionImageStore {
  return {
    save: vi.fn(async (image) => image),
    read: vi.fn(async () => ({ mime: 'image/png' as const, bytes: new Uint8Array(PNG) })),
    ...over
  }
}

function ctx(over: Partial<ToolContext> = {}): ToolContext {
  return {
    workspaceRoot: root,
    signal: new AbortController().signal,
    permissionMode: 'auto',
    depth: 0,
    callId: 'call_1',
    runId: 'run_1',
    host: nodeHost(),
    emit: () => {},
    sessionImages: images(),
    ...over
  }
}

beforeEach(() => {
  root = join(realpathSync.native(mkdtempSync(join(tmpdir(), 'ncw-save-image-'))), 'ws')
  mkdirSync(root)
})

afterEach(() => {
  rmSync(join(root, '..'), { recursive: true, force: true })
})

describe('SaveImage · 元数据与下发', () => {
  it('与 Write 同档:非只读、破坏性 —— 权限闸门按写工具对待它', () => {
    expect(saveImageTool.readOnly).toBe(false)
    expect(saveImageTool.destructive).toBe(true)
    expect(saveImageTool.needsNetwork).toBe(true)
  })

  it('没有会话图片仓、或宿主写不了二进制时不下发', () => {
    expect(saveImageTool.isEnabled?.(ctx())).toBe(true)
    const withoutStore = ctx()
    delete withoutStore.sessionImages
    expect(saveImageTool.isEnabled?.(withoutStore)).toBe(false)
    const base = nodeHost()
    const fs = { ...base.fs }
    delete fs.writeBytes
    expect(saveImageTool.isEnabled?.(ctx({ host: { ...base, fs } }))).toBe(false)
  })
})

describe('SaveImage · 写入', () => {
  it('ncw 地址 → 会话图片仓取字节,原样写进工作区,回执带格式与大小', async () => {
    const store = images()
    const result = await saveImageTool.execute({ url: URL_A, file_path: 'assets/logo.png' }, ctx({ sessionImages: store }))
    expect(result.isError).toBe(false)
    expect(readFileSync(join(root, 'assets', 'logo.png'))).toEqual(PNG)
    expect(result.output.content).toMatch(/^Saved assets\/logo\.png \(PNG image, /u)
    expect(store.read).toHaveBeenCalledWith(URL_A, expect.any(AbortSignal))
  })

  it('已有文件且没传 overwrite:拒绝,原文件不动,连字节都不去取', async () => {
    writeFileSync(join(root, 'logo.png'), 'keep me')
    const store = images()
    const result = await saveImageTool.execute({ url: URL_A, file_path: 'logo.png' }, ctx({ sessionImages: store }))
    expect(result.isError).toBe(true)
    expect(result.output.content).toContain('overwrite: true')
    expect(readFileSync(join(root, 'logo.png'), 'utf8')).toBe('keep me')
    expect(store.read).not.toHaveBeenCalled()
  })

  it('overwrite: true 覆盖已有文件,回执写 Overwrote', async () => {
    writeFileSync(join(root, 'logo.png'), 'old')
    const result = await saveImageTool.execute({ url: URL_A, file_path: 'logo.png', overwrite: true }, ctx())
    expect(result.isError).toBe(false)
    expect(result.output.content).toMatch(/^Overwrote logo\.png/u)
    expect(readFileSync(join(root, 'logo.png'))).toEqual(PNG)
  })

  it('目标是目录:失败并提示带上文件名', async () => {
    mkdirSync(join(root, 'assets'))
    const result = await saveImageTool.execute({ url: URL_A, file_path: 'assets', overwrite: true }, ctx())
    expect(result.isError).toBe(true)
    expect(result.output.content).toMatch(/is a directory/u)
  })

  it('扩展名与实际格式不符时照写不转码,但回执提醒', async () => {
    const result = await saveImageTool.execute({ url: URL_A, file_path: 'logo.jpg' }, ctx())
    expect(result.isError).toBe(false)
    expect(readFileSync(join(root, 'logo.jpg'))).toEqual(PNG)
    expect(result.output.content).toContain('the image is PNG but the file extension suggests JPEG')
  })

  it('会话图片仓拒绝(跨会话 / 越界):失败带原因,什么都不写', async () => {
    const store = images({ read: vi.fn(async () => { throw new Error('Image attachment does not belong to this session') }) })
    const result = await saveImageTool.execute(
      { url: 'ncw://attachments/sessions/other/x.png', file_path: 'x.png' },
      ctx({ sessionImages: store })
    )
    expect(result.isError).toBe(true)
    expect(result.output.content).toContain('does not belong to this session')
    expect(existsSync(join(root, 'x.png'))).toBe(false)
  })

  it('http(s) 地址走 downloadImage 下载后落盘', async () => {
    const fetchMock = vi.fn(async (): Promise<Response> => new Response(new Uint8Array(PNG), { status: 200 }))
    const host: KernelHost = { ...nodeHost(), fetch: fetchMock as unknown as typeof globalThis.fetch }
    const result = await saveImageTool.execute({ url: 'https://cdn.example.com/pic.png', file_path: 'pic.png' }, ctx({ host }))
    expect(result.isError).toBe(false)
    expect(readFileSync(join(root, 'pic.png'))).toEqual(PNG)
  })

  it('认不出的地址(file:// / 裸路径):失败并说清能传什么,什么都不写', async () => {
    for (const url of ['file:///etc/passwd', '/tmp/a.png']) {
      const result = await saveImageTool.execute({ url, file_path: 'a.png' }, ctx())
      expect(result.isError, url).toBe(true)
      expect(result.output.content, url).toContain('Unsupported image URL')
    }
    expect(existsSync(join(root, 'a.png'))).toBe(false)
  })

  it('Plan 模式的写入围栏照样生效:计划文件之外一律不写', async () => {
    const result = await saveImageTool.execute(
      { url: URL_A, file_path: 'logo.png' },
      ctx({ writeFileRestriction: join(root, 'plan.md') })
    )
    expect(result.isError).toBe(true)
    expect(result.output.content).toMatch(/Plan mode/u)
    expect(existsSync(join(root, 'logo.png'))).toBe(false)
  })
})
