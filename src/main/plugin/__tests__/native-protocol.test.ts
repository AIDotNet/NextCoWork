/**
 * `ncw-plugin://` 不得把插件包里的原生区(`native/`)当静态资源发出去。
 *
 * 需求见 `shared/plugin/native-component.ts` 文件头第 1 条:原生引擎只能由主进程
 * 校验后 spawn。这里钉的是词法层与请求层两处拒绝,以及大小写 / 编码绕过。
 */
import { mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { handlePluginRequest, resolveInsidePackage } from '../protocol'

let root: string

beforeEach(() => { root = mkdtempSync(join(tmpdir(), 'ncw-native-proto-')) })
afterEach(() => { rmSync(root, { recursive: true, force: true }) })

describe('native/ is never served to plugin views', () => {
  it('rejects native paths lexically, case-insensitively', () => {
    for (const path of ['/native/helper', '/Native/lib.dylib', '/NATIVE/x/y.so', '/native']) {
      expect(resolveInsidePackage('/plugins/ncw.office-runtime', path), path).toBeNull()
    }
    expect(resolveInsidePackage('/plugins/ncw.office-runtime', '/dist/native.js')).toBe('/plugins/ncw.office-runtime/dist/native.js')
  })

  it('answers 403 for encoded native requests before touching the file system', async () => {
    const resolver = (): { root: string; main: string } => ({ root, main: 'dist/extension.js' })
    for (const path of ['/native/helper', '/%6Eative/helper', '/Native%2Fhelper']) {
      const response = await handlePluginRequest(new Request(`ncw-plugin://ncw.office-runtime${path}`), resolver)
      expect(response.status, path).toBe(403)
    }
  })

  it('answers 403 when a view file is a symlink into native/', async () => {
    mkdirSync(join(root, 'native'), { recursive: true })
    mkdirSync(join(root, 'dist'), { recursive: true })
    writeFileSync(join(root, 'native', 'helper'), 'binary')
    symlinkSync(join(root, 'native', 'helper'), join(root, 'dist', 'innocent.js'))
    const resolver = (): { root: string; main: string } => ({ root, main: 'dist/extension.js' })
    const response = await handlePluginRequest(new Request('ncw-plugin://ncw.office-runtime/dist/innocent.js'), resolver)
    expect(response.status).toBe(403)
  })
})
