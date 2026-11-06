/**
 * 需求：验证微信只读插件的 ZIP 可由真实安装器接收，且只有工作区读能力。
 * 打包产物不进版本库；没有 ZIP 的干净检出跳过，不误报为代码失败。
 */
import { existsSync, promises as fs, readdirSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import { installPluginZip } from '../installer'

const EXAMPLE = resolve(__dirname, '../../../../examples/acme.wechat-exports')

/** 版本号会递增：按文件名模式取最新一份，与其它示例插件的验收测试同一写法。 */
function currentZip(): string | null {
  if (!existsSync(EXAMPLE)) return null
  const zip = readdirSync(EXAMPLE)
    .filter((name) => /^acme\.wechat-exports-\d+\.\d+\.\d+\.zip$/.test(name))
    .sort()
    .pop()
  return zip === undefined ? null : join(EXAMPLE, zip)
}

const ZIP = currentZip()

describe.skipIf(ZIP === null)('微信导出文件插件包', () => {
  it('仅贡献三个只读工具和 workspace.read 权限', async () => {
    if (ZIP === null) return
    const root = await fs.mkdtemp(join(tmpdir(), 'ncw-wechat-plugin-'))
    try {
      const installed = await installPluginZip(ZIP, root)
      expect(installed.manifest.id).toBe('acme.wechat-exports')
      expect(installed.manifest.permissions).toEqual(['workspace.read'])
      expect(installed.manifest.contributes.tools.map((tool) => tool.name)).toEqual([
        'list_exports', 'read_export', 'search_exports'
      ])
      expect(installed.manifest.activationEvents).toEqual([
        'onTool:list_exports', 'onTool:read_export', 'onTool:search_exports'
      ])
      // 图标随包走：市场条目的 logo 由服务端从包里提取，缺它就是一块拼图占位。
      expect(installed.manifest.icon).toBe('assets/icon.png')
      expect(existsSync(join(installed.target, 'assets/icon.png'))).toBe(true)
    } finally {
      await fs.rm(root, { recursive: true, force: true })
    }
  })
})
