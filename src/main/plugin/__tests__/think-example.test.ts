/**
 * 需求:验证 think 插件的 ZIP 可由真实安装器接收,且确实零权限、单工具。
 * 打包产物不进版本库;没有 ZIP 的干净检出跳过,不误报为代码失败。
 */
import { existsSync, promises as fs, readdirSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import { installPluginZip } from '../installer'

const EXAMPLE = resolve(__dirname, '../../../../examples/acme.think')

/** 版本号会递增:按文件名模式取最新一份,与其它示例插件的验收测试同一写法。 */
function currentZip(): string | null {
  if (!existsSync(EXAMPLE)) return null
  const zip = readdirSync(EXAMPLE)
    .filter((name) => /^acme\.think-\d+\.\d+\.\d+\.zip$/.test(name))
    .sort()
    .pop()
  return zip === undefined ? null : join(EXAMPLE, zip)
}

const ZIP = currentZip()

describe.skipIf(ZIP === null)('think 插件包', () => {
  it('零权限,只贡献一个 reasoning 形态的 think 工具', async () => {
    if (ZIP === null) return
    const root = await fs.mkdtemp(join(tmpdir(), 'ncw-think-plugin-'))
    try {
      const installed = await installPluginZip(ZIP, root)
      expect(installed.manifest.id).toBe('acme.think')
      expect(installed.manifest.permissions).toEqual([])
      expect(installed.manifest.activationEvents).toEqual(['onTool:think'])
      const tools = installed.manifest.contributes.tools
      expect(tools.map((tool) => tool.name)).toEqual(['think'])
      expect(tools[0]?.shape).toBe('reasoning')
      // 图标随包走:安装器核对清单引用的每个路径都真实存在。
      expect(installed.manifest.icon).toBe('assets/icon.png')
      expect(existsSync(join(installed.target, 'assets/icon.png'))).toBe(true)
      expect(existsSync(join(installed.target, 'l10n/zh-CN.json'))).toBe(true)
      expect(existsSync(join(installed.target, 'l10n/en-US.json'))).toBe(true)
    } finally {
      await fs.rm(root, { recursive: true, force: true })
    }
  })
})
