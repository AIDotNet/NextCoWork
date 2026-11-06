import { dialog } from 'electron'

/**
 * 「选择 Skill ZIP」系统对话框。
 *
 * ★ 单独一个文件:`ipc/skills.ts` 的其余逻辑(列出、安装、启停、卸载)不碰任何窗口/对话框,
 *   放在一起会让无头设备服务为了一个只有桌面才有的文件选择器而把 Electron 拖进来。
 */
export async function pickSkillZip(): Promise<{ path: string; name: string } | null> {
  const result = await dialog.showOpenDialog({ title: '选择 Skill ZIP', properties: ['openFile'], filters: [{ name: 'Skill ZIP', extensions: ['zip'] }] })
  if (result.canceled || !result.filePaths[0]) return null
  const path = result.filePaths[0]
  return { path, name: path.split(/[\\/]/).pop() ?? 'skill.zip' }
}
