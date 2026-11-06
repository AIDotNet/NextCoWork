/**
 * 需求：只在用户主动放入工作区 wechat-exports/ 的导出文件中查询聊天文本。
 * 不访问微信数据库/App Group，不解密、不联网、不发送消息，也不把附件交给模型。
 * ★ ZIP 解压前用 entry 元数据限制展开规模；否则几 KB 压缩包可耗尽插件进程内存。
 */
import * as ncw from 'nextcowork'
import { unzipSync } from 'fflate'

const ROOT = 'wechat-exports/'
const MAX_FILES = 100
const MAX_TEXT_BYTES = 2 * 1024 * 1024
const MAX_ARCHIVE_BYTES = 8 * 1024 * 1024
const MAX_EXPANDED_BYTES = 8 * 1024 * 1024
const decoder = new TextDecoder('utf-8', { fatal: true })

export interface ExportFile { path: string; size: number }
export interface Transcript { path: string; name: string; text: string }

/** 需求：只列出用户指定目录中的文件，所有后续读取均重新校验路径。 */
export async function listExports(): Promise<ExportFile[]> {
  const paths = await ncw.workspace.findFiles(`${ROOT}**`, MAX_FILES)
  const files: ExportFile[] = []
  for (const path of paths) {
    if (!validPath(path)) continue
    const info = await ncw.workspace.fs.stat(path)
    if (info.kind === 'file') files.push({ path, size: info.size })
  }
  return files.sort((a, b) => a.path.localeCompare(b.path))
}

export function validPath(path: string): boolean {
  return path.startsWith(ROOT) && !path.includes('\\') &&
    path.slice(ROOT.length).split('/').every((part) => part !== '' && part !== '.' && part !== '..') &&
    /\.(zip|txt)$/i.test(path)
}

/** 需求：ZIP 仅提取已限额的 UTF-8 TXT，忽略媒体；原始内容保持在工作区。 */
export async function readExport(path: string): Promise<Transcript[]> {
  if (!validPath(path)) throw new Error('Only TXT/ZIP files under wechat-exports/ are allowed')
  const stat = await ncw.workspace.fs.stat(path)
  if (stat.kind !== 'file' || stat.size > MAX_ARCHIVE_BYTES) throw new Error('Export is missing or exceeds the 8 MB read limit')
  if (/\.txt$/i.test(path)) {
    if (stat.size > MAX_TEXT_BYTES) throw new Error('TXT exceeds the 2 MB text limit')
    const file = await ncw.workspace.fs.readFile(path, 'utf8')
    return [{ path, name: path.split('/').pop() ?? path, text: file.data }]
  }
  const file = await ncw.workspace.fs.readFile(path, 'base64')
  const binary = atob(file.data)
  const bytes = Uint8Array.from(binary, (char) => char.charCodeAt(0))
  let expanded = 0
  let count = 0
  const contents = unzipSync(bytes, {
    filter(entry) {
      if (!/\.txt$/i.test(entry.name) || entry.name.startsWith('/') || entry.name.includes('\\') ||
          entry.name.split('/').some((part) => part === '..' || part === '.')) return false
      count += 1
      expanded += entry.originalSize
      if (count > MAX_FILES || entry.originalSize > MAX_TEXT_BYTES || expanded > MAX_EXPANDED_BYTES) {
        throw new Error('ZIP transcript limits exceeded')
      }
      return true
    }
  })
  return Object.entries(contents).map(([name, content]) => ({
    path, name, text: decoder.decode(content)
  })).sort((a, b) => a.name.localeCompare(b.name))
}

/** 需求：模型每轮只能得到少量文字，避免一次查询将整份私密归档送进上下文。 */
export function excerpt(text: string, query: string, limit: number): string[] {
  const needle = query.toLocaleLowerCase()
  const lines = text.split(/\r?\n/)
  const found: string[] = []
  for (let i = 0; i < lines.length && found.length < limit; i += 1) {
    const line = lines[i] ?? ''
    if (line.toLocaleLowerCase().includes(needle)) {
      found.push(`${i + 1}: ${line.slice(0, 320)}`)
    }
  }
  return found
}
