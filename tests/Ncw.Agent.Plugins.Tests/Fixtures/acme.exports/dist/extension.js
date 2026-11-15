// 移植自 2.x 的 examples/acme.wechat-exports(去掉 ZIP 与 fflate,只读 TXT;其余结构、收窄方式、返回尺寸上限照旧)。
import * as ncw from 'nextcowork'

const DIRECTORY = 'chat-exports'
const MAX_TXT_BYTES = 2 * 1024 * 1024

const listSchema = { type: 'object', properties: {}, additionalProperties: false }
const pathSchema = {
  type: 'object',
  properties: {
    path: { type: 'string', description: 'Path returned by list_exports under chat-exports/' },
    limit: { type: 'integer', minimum: 1, maximum: 20 }
  },
  required: ['path'],
  additionalProperties: false
}
const searchSchema = {
  type: 'object',
  properties: {
    query: { type: 'string', minLength: 2, description: 'Literal substring to find in exported chat text' },
    limit: { type: 'integer', minimum: 1, maximum: 20 }
  },
  required: ['query'],
  additionalProperties: false
}

function limitOf(value) {
  return typeof value === 'number' && Number.isInteger(value) ? Math.min(20, Math.max(1, value)) : 10
}

// ★ 输入 schema 不由宿主验证,所以每个 invoke 自行收窄参数与返回尺寸
function pathOf(input) {
  if (input === null || typeof input !== 'object' || typeof input.path !== 'string') throw new Error('Missing export path')
  if (!input.path.startsWith(DIRECTORY + '/') || !input.path.endsWith('.txt')) throw new Error('Only .txt files under ' + DIRECTORY + '/ can be read')
  return input.path
}

async function listExports() {
  const paths = await ncw.workspace.findFiles(DIRECTORY + '/*.txt', 50)
  const files = []
  for (const path of paths) {
    const stat = await ncw.workspace.fs.stat(path)
    if (stat.kind === 'file' && stat.size <= MAX_TXT_BYTES) files.push({ path, size: stat.size })
  }
  return files
}

export function activate(context) {
  context.subscriptions.push(
    ncw.tools.registerTool('list_exports', {
      description: 'List user-exported chat TXT files in the workspace chat-exports/ directory.',
      inputSchema: listSchema,
      readOnly: true,
      async invoke() {
        return { content: [{ text: JSON.stringify({ directory: DIRECTORY + '/', files: await listExports() }) }] }
      }
    }),
    ncw.tools.registerTool('read_export', {
      description: 'Read a bounded excerpt of an exported chat TXT file from chat-exports/.',
      inputSchema: pathSchema,
      readOnly: true,
      async invoke({ input }) {
        const path = pathOf(input)
        const limit = limitOf(input.limit)
        const { data } = await ncw.workspace.fs.readFile(path)
        return { content: [{ text: JSON.stringify({ path, excerpt: data.slice(0, limit * 500), truncated: data.length > limit * 500 }) }] }
      }
    }),
    ncw.tools.registerTool('search_exports', {
      description: 'Search literal text in the exported chat TXT files; returns bounded matches only.',
      inputSchema: searchSchema,
      readOnly: true,
      async invoke({ input }) {
        const query = input.query
        if (typeof query !== 'string' || query.trim().length < 2 || query.length > 100) throw new Error('Query must be 2-100 characters')
        let remaining = limitOf(input.limit)
        const matches = []
        for (const file of await listExports()) {
          if (remaining <= 0) break
          const { data } = await ncw.workspace.fs.readFile(file.path)
          const lines = data.split('\n').filter((line) => line.includes(query)).slice(0, remaining)
          if (lines.length > 0) matches.push({ path: file.path, lines })
          remaining -= lines.length
        }
        return { content: [{ text: JSON.stringify({ query, matches }) }] }
      }
    })
  )
}
