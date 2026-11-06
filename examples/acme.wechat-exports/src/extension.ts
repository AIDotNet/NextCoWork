/**
 * 需求：给 Agent 提供受限、只读的微信主动导出文件检索工具。
 * 只注册三个具名工具；模型只能看到其明确读取的少量内容，无法选择目录外路径。
 * ★ 输入 schema 不由宿主验证，所以每个 invoke 自行收窄参数与返回尺寸。
 */
import * as ncw from 'nextcowork'
import { excerpt, listExports, readExport } from './exports'

const listSchema = { type: 'object', properties: {}, additionalProperties: false }
const pathSchema = {
  type: 'object',
  properties: {
    path: { type: 'string', description: 'Path returned by list_exports under wechat-exports/' },
    limit: { type: 'integer', minimum: 1, maximum: 20 }
  },
  required: ['path'], additionalProperties: false
}
const searchSchema = {
  type: 'object',
  properties: {
    query: { type: 'string', minLength: 2, description: 'Literal substring to find in exported chat text' },
    limit: { type: 'integer', minimum: 1, maximum: 20 }
  },
  required: ['query'], additionalProperties: false
}

function limitOf(value: unknown): number {
  return typeof value === 'number' && Number.isInteger(value) ? Math.min(20, Math.max(1, value)) : 10
}

function pathOf(input: unknown): string {
  if (input === null || typeof input !== 'object') throw new Error('Missing export path')
  const path = (input as { path?: unknown }).path
  if (typeof path !== 'string') throw new Error('Missing export path')
  return path
}

export function activate(context: ncw.ExtensionContext): void {
  context.subscriptions.push(
    ncw.tools.registerTool<Record<string, unknown>>('list_exports', {
      description: 'List user-exported WeChat TXT/ZIP archives in the current workspace wechat-exports/ directory. Never reads the WeChat database.',
      inputSchema: listSchema,
      readOnly: true,
      async invoke() {
        const files = await listExports()
        return { content: [{ text: JSON.stringify({ directory: 'wechat-exports/', files }) }] }
      }
    }),
    ncw.tools.registerTool<Record<string, unknown>>('read_export', {
      description: 'Read bounded excerpts of an exported WeChat chat TXT/ZIP from wechat-exports/. User must have exported the file; returned text is visible to the model.',
      inputSchema: pathSchema,
      readOnly: true,
      async invoke({ input }) {
        const path = pathOf(input)
        const limit = limitOf(input.limit)
        const transcripts = await readExport(path)
        return { content: [{ text: JSON.stringify({
          path, transcripts: transcripts.slice(0, 8).map(({ name, text }) => ({ name, excerpt: text.slice(0, limit * 500), truncated: text.length > limit * 500 }))
        }) }] }
      }
    }),
    ncw.tools.registerTool<Record<string, unknown>>('search_exports', {
      description: 'Search literal text in user-exported WeChat TXT/ZIP archives under wechat-exports/; returns bounded matches only, never reads live WeChat.',
      inputSchema: searchSchema,
      readOnly: true,
      async invoke({ input }) {
        const query = input.query
        if (typeof query !== 'string' || query.trim().length < 2 || query.length > 100) throw new Error('Query must be 2–100 characters')
        const limit = limitOf(input.limit)
        const matches: { path: string; name: string; lines: string[] }[] = []
        let remaining = limit
        for (const file of await listExports()) {
          if (remaining <= 0) break
          for (const transcript of await readExport(file.path)) {
            const lines = excerpt(transcript.text, query, remaining)
            if (lines.length > 0) matches.push({ path: file.path, name: transcript.name, lines })
            remaining -= lines.length
            if (remaining <= 0) break
          }
        }
        return { content: [{ text: JSON.stringify({ query, matches }) }] }
      }
    })
  )
}
