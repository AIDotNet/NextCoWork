/**
 * 文件四件套:`Read` / `Write` / `Edit` / `LS`。
 *
 * ★ 名字、参数名、描述的写法**全部对齐 Claude Code**,不是审美选择:
 *
 * 1. 模型见过海量 `Read(file_path=...)` / `Edit(old_string=..., new_string=...)`
 *    的训练数据。换成 `read_file(path=...)` 它照样能用,但会更频繁地传错参数名、
 *    更少地遵守「先读后写」这类只在 CC 描述里写过的纪律。
 * 2. 用户会把 CC 的 agent 定义文件(`tools: Read, Grep, Glob`)原样粘过来。
 *    名字一致的话,那张别名表就只是兜底,而不是正确性的前提。
 *
 * 四个工具共同守着三条线:
 *
 * - **路径一律过 `resolvePath`**。工作区不是围栏 —— 「谁能碰哪个文件」由权限档位决定
 *   (见 `path-guard.ts` 文件头),但归一化(`..`、软链、大小写、`/var`)只有那一份实现。
 * - **已有文件必须先 `Read` 过才能写**(见 `read-tracker.ts`)。
 * - **失败要说清楚下一步做什么**。模型是照着 `toolFail` 的正文决定怎么重试的:
 *   「文件不存在」和「文件不存在,先用 Glob 找找」产出的后续行为完全不同。
 */
import { z } from 'zod'
import { DIR_LISTING_LIMIT, sortEntries, type FileEntry } from '../../../../shared/domain/file-tree'
import { toolFail, toolOk } from '../../../../shared/agent/tool'
import { defineTool } from '../define'
import type { ToolContext, ToolRegistration } from '../registry'
import { compileGlob, normalizeGlobPath } from './glob-match'
import { humanSize, looksBinary, relOf, resolvePath } from './paths'
import { markRead, wasRead } from './read-tracker'
import { recordChange } from './change-recorder'

/** 单次读取的上限。超过就让模型改用 `Grep`,或 `Bash` 里的 `sed -n`。 */
const MAX_READ_BYTES = 8 * 1024 * 1024
/** 单行截断长度。和 CC 一致 —— 压缩产物一行能有几十万字符。 */
const MAX_LINE_CHARS = 2000
/** 不带 `limit` 时默认读多少行。和 CC 一致。 */
const DEFAULT_READ_LIMIT = 2000
/** 二进制探测的取样长度 */
const SNIFF_BYTES = 4096

/** 「没读过就想写」的统一说辞。两个写工具必须给出同一句话。 */
function mustReadFirst(rel: string): string {
  return (
    `You have not read ${rel} in this session yet. Read it first, then edit.` +
    `This exists so you cannot overwrite a file from memory and silently wipe out what you never saw.`
  )
}

export function restrictedWrite(ctx: ToolContext, abs: string, rel: string) {
  if (ctx.writeFileRestriction === undefined || abs === ctx.writeFileRestriction) return undefined
  return toolFail(
    `Plan mode may only modify its active Markdown plan. ${rel} is outside that file; no changes were made.`
  )
}

// ────────────────────────────── Read ──────────────────────────────

const ReadInput = z.object({
  file_path: z
    .string()
    .min(1)
    .describe(
      'Absolute path of the file to read. A relative path is resolved against the workspace root; an absolute path may point outside the workspace'
    ),
  offset: z
    .number()
    .int()
    .min(1)
    .optional()
    .describe('Line number to start reading from (1-based). Only pass it when the file is large enough to need paging'),
  limit: z
    .number()
    .int()
    .min(1)
    .max(50_000)
    .optional()
    .describe('Maximum number of lines to read. Only pass it when the file is large enough to need paging')
})

/**
 * ★ 行号用 `cat -n` 的形式(右对齐 6 位 + 制表符),和 CC 逐字节一致。
 *
 * 不是审美:`Edit` 要求模型给出**原文**。换一种它不熟悉的前缀,它会把前缀一起
 * 抄进 `old_string`,然后每一次编辑都匹配不上 —— 而错误信息看起来只是「没找到」。
 */
function withLineNumbers(lines: string[], startLine: number): string {
  return lines
    .map((line, i) => {
      const n = String(startLine + i).padStart(6, ' ')
      const body =
        line.length > MAX_LINE_CHARS ? `${line.slice(0, MAX_LINE_CHARS)}…[line truncated]` : line
      return `${n}\t${body}`
    })
    .join('\n')
}

export const readTool: ToolRegistration = defineTool({
  internalId: 'Read',
  description:
    'Reads a file from disk. Paths inside the workspace are the normal case, but an absolute path ' +
    'outside it works too.\n\n' +
    'Usage:\n' +
    '- file_path must be an absolute path, not a relative one\n' +
    '- By default it reads up to 2000 lines from the start of the file\n' +
    '- You can pass offset and limit to read a slice of a long file, but read the whole file when you can — do not paginate for no reason\n' +
    '- Lines longer than 2000 characters are truncated\n' +
    '- Output is in `cat -n` format, with line numbers starting at 1\n' +
    '- You can call multiple tools in one reply. Speculatively reading several related files at once is much faster than one round trip each\n' +
    '- Binary files are rejected; for files over 8MB use Grep, or `sed -n` through Bash, to pull out a slice\n' +
    '- You MUST read an existing file with this tool before you change it — Write and Edit will fail outright otherwise',
  schema: ReadInput,
  readOnly: true,
  destructive: false,
  needsNetwork: false,
  async run(input, ctx) {
    const r = await resolvePath(ctx, input.file_path)
    if (!r.ok) return r.result
    const { fs } = ctx.host
    const rel = relOf(ctx, r.abs)

    if (!(await fs.exists(r.abs))) {
      return toolFail(`File does not exist: ${rel}. Use LS to see what is in the directory, or Glob to find it by pattern.`)
    }

    const st = await fs.stat(r.abs)
    if (st.isDir) return toolFail(`${rel} is a directory, not a file. Use LS to list its contents.`)
    if (st.size > MAX_READ_BYTES) {
      return toolFail(
        `${rel} is too large (${humanSize(st.size)}; the limit is ${humanSize(MAX_READ_BYTES)}). ` +
          `Use Grep to search for what you need, or run \`sed -n 'START,ENDp'\` through Bash to pull out a slice.`
      )
    }

    if (st.size === 0) {
      // ★ 空文件也算「读过」—— 否则模型永远没法用 Write 往一个占位空文件里填内容
      markRead(ctx.runId, r.abs)
      return toolOk(`${rel} exists but is empty (0 bytes).`)
    }

    // ★ 探测在解码之前 —— 否则一个 300MB 的 .pack 会先被解成一堆 U+FFFD
    if (looksBinary(await fs.readFileBytes(r.abs, SNIFF_BYTES))) {
      return toolFail(`${rel} looks like a binary file (${humanSize(st.size)}); this tool only reads text.`)
    }

    ctx.emit({ callId: ctx.callId, message: `Reading ${rel} (${humanSize(st.size)})` })

    const all = (await fs.readFile(r.abs)).split('\n')
    const start = (input.offset ?? 1) - 1
    if (start >= all.length) {
      return toolFail(`${rel} has only ${String(all.length)} lines; offset=${String(input.offset)} is past the end.`)
    }
    const limit = input.limit ?? DEFAULT_READ_LIMIT
    const slice = all.slice(start, start + limit)

    markRead(ctx.runId, r.abs)

    const shown = start + slice.length
    const note =
      shown < all.length
        ? `\n\n[Showing lines ${String(start + 1)}–${String(shown)} of ${String(all.length)}. ` +
          `Pass offset=${String(shown + 1)} to continue.]`
        : ''
    return toolOk(withLineNumbers(slice, start + 1) + note)
  }
})

// ────────────────────────────── Write ──────────────────────────────

const WriteInput = z.object({
  file_path: z
    .string()
    .min(1)
    .describe(
      'Absolute path of the file to write. Parent directories are created for you. It may point outside the workspace'
    ),
  content: z.string().describe('The complete contents to write. This OVERWRITES the existing file in full')
})

export const writeTool: ToolRegistration = defineTool({
  internalId: 'Write',
  description:
    'Writes a file to disk. Paths inside the workspace are the normal case, but an absolute path ' +
    'outside it works too.\n\n' +
    'Usage:\n' +
    '- If a file already exists at that path, this tool OVERWRITES it completely\n' +
    '- You MUST read an existing file with Read before writing over it, or this call fails\n' +
    '- ALWAYS prefer editing an existing file. Do not create a new one unless it is genuinely needed\n' +
    '- NEVER create documentation files (*.md) or a README on your own initiative. Only write docs when the user explicitly asks\n' +
    '- NEVER write emoji into a file unless the user explicitly asks for it\n' +
    '- Use Edit, not this tool, to change part of a file — overwriting a file you have not read in full will almost always drop something you did not know was there',
  schema: WriteInput,
  readOnly: false,
  destructive: true,
  needsNetwork: false,
  async run(input, ctx) {
    const r = await resolvePath(ctx, input.file_path)
    if (!r.ok) return r.result
    const { fs } = ctx.host
    const rel = relOf(ctx, r.abs)
    const blocked = restrictedWrite(ctx, r.abs, rel)
    if (blocked !== undefined) return blocked

    const existed = await fs.exists(r.abs)
    if (existed) {
      const st = await fs.stat(r.abs)
      if (st.isDir) return toolFail(`${rel} is a directory; it cannot be written as a file.`)
      // ★ 只对**已有**文件要求先读。新建文件当然没得读。
      if (!wasRead(ctx.runId, r.abs)) return toolFail(mustReadFirst(rel))
    }

    // ★ 覆盖前补读旧内容 —— 采集撤销所需的 before。新建文件 before = null。
    //   (Edit 天然读了 before,Write 原本不读,这里为审查/撤销补上。)
    const before = existed ? await fs.readFile(r.abs) : null

    await fs.mkdirp(r.abs)
    await fs.writeFile(r.abs, input.content)
    // 写完就等于知道当下内容,后续的 Edit 不该再被拦
    markRead(ctx.runId, r.abs)
    recordChange(ctx.runId, {
      abs: r.abs,
      relPath: rel,
      before,
      after: input.content,
      inWorkspace: !r.outside
    })

    const lines = input.content === '' ? 0 : input.content.split('\n').length
    return toolOk(
      `${existed ? 'Overwrote' : 'Created'} ${rel} (${String(lines)} lines, ${humanSize(input.content.length)}).`
    )
  }
})

// ────────────────────────────── Edit ──────────────────────────────

const EditInput = z.object({
  file_path: z.string().min(1).describe('Absolute path of the file to edit. It may point outside the workspace'),
  old_string: z.string().min(1).describe('The exact text to replace'),
  new_string: z
    .string()
    .describe('What to replace it with (must differ from old_string). An empty string deletes the text'),
  replace_all: z
    .boolean()
    .optional()
    .describe(
      'Replace every occurrence of old_string. Defaults to false — a non-unique match fails instead, so that you add context'
    )
})

export const editTool: ToolRegistration = defineTool({
  internalId: 'Edit',
  description:
    'Performs an exact string replacement inside a file.\n\n' +
    'Usage:\n' +
    '- You MUST read the file with Read before editing it, or this call fails outright\n' +
    '- When you copy text out of Read output, preserve the exact indentation (tabs/spaces) AFTER the line-number prefix. ' +
    'That prefix is "spaces + line number + a tab"; only what follows the tab is real file content. ' +
    'NEVER include any part of the line-number prefix in old_string or new_string\n' +
    '- The edit FAILS if old_string is not unique in the file. Either add surrounding lines until it is unique, ' +
    'or pass replace_all to change every occurrence\n' +
    '- Use replace_all for whole-file replacements such as renaming a variable\n' +
    '- ALWAYS prefer editing an existing file. Do not create a new one unless it is genuinely needed\n' +
    '- NEVER add emoji to a file unless the user explicitly asks for it',
  schema: EditInput,
  readOnly: false,
  destructive: true,
  needsNetwork: false,
  async run(input, ctx) {
    if (input.old_string === input.new_string) {
      return toolFail('old_string and new_string are identical, so this edit would change nothing.')
    }

    const r = await resolvePath(ctx, input.file_path)
    if (!r.ok) return r.result
    const { fs } = ctx.host
    const rel = relOf(ctx, r.abs)
    const blocked = restrictedWrite(ctx, r.abs, rel)
    if (blocked !== undefined) return blocked

    if (!(await fs.exists(r.abs))) {
      return toolFail(`File does not exist: ${rel}. Use Write to create a new file.`)
    }
    const st = await fs.stat(r.abs)
    if (st.isDir) return toolFail(`${rel} is a directory, not a file.`)
    if (!wasRead(ctx.runId, r.abs)) return toolFail(mustReadFirst(rel))

    const before = await fs.readFile(r.abs)
    const count = before.split(input.old_string).length - 1

    if (count === 0) {
      /*
        ★ **不要**把文件内容附在错误里。模型会以为那是它该抄的原文,
        而更要紧的是:一次失败的编辑不该把整个文件塞进上下文窗。
        给它一条可执行的下一步就够了。
      */
      return toolFail(
        `old_string was not found in ${rel}. Common causes: the indentation does not match (tabs vs spaces), ` +
          `trailing whitespace, or you copied the line-number prefix out of Read output — ` +
          `the number and the tab after it are not part of the file. Read the file again to get the exact text.`
      )
    }
    if (count > 1 && input.replace_all !== true) {
      return toolFail(
        `old_string appears ${String(count)} times in ${rel}, so it is not unique and nothing was changed. ` +
          `Add surrounding lines until the match is unique; pass replace_all: true only if you really do mean every occurrence.`
      )
    }

    const after =
      input.replace_all === true
        ? before.split(input.old_string).join(input.new_string)
        : before.replace(input.old_string, input.new_string)

    await fs.writeFile(r.abs, after)
    recordChange(ctx.runId, { abs: r.abs, relPath: rel, before, after, inWorkspace: !r.outside })
    return toolOk(`Edited ${rel}: replaced ${String(input.replace_all === true ? count : 1)} occurrence(s).`)
  }
})

// ────────────────────────────── LS ──────────────────────────────

const LsInput = z.object({
  path: z
    .string()
    .min(1)
    .describe('Absolute path of the directory to list, not a relative path. It may point outside the workspace'),
  ignore: z
    .array(z.string())
    .max(64)
    .optional()
    .describe('Glob patterns to skip, e.g. ["*.log", "__pycache__"]')
})

export const lsTool: ToolRegistration = defineTool({
  internalId: 'LS',
  description:
    'Lists the files and subdirectories in a directory (NOT recursive), directories first. ' +
    'path must be an absolute path, not a relative one. You can pass ignore with glob patterns to skip entries. ' +
    'You should generally prefer Glob (search by filename) and Grep (search by content) when you already know ' +
    'what you are looking for — LS is for getting a feel for what a directory holds.',
  schema: LsInput,
  readOnly: true,
  destructive: false,
  needsNetwork: false,
  async run(input, ctx) {
    const r = await resolvePath(ctx, input.path)
    if (!r.ok) return r.result
    const { fs } = ctx.host
    const rel = relOf(ctx, r.abs)

    if (!(await fs.exists(r.abs))) return toolFail(`Directory does not exist: ${rel}`)
    const st = await fs.stat(r.abs)
    if (!st.isDir) return toolFail(`${rel} is a file, not a directory. Use Read to read it.`)

    // ignore 里的模式只比名字,不比路径 —— LS 不递归,路径匹配在这里没有意义
    const ignores = (input.ignore ?? []).map((p) => compileGlob(p))
    const items = (await fs.readDir(r.abs)).filter(
      (it) => !ignores.some((re) => re.test(normalizeGlobPath(it.name)))
    )
    const truncated = items.length > DIR_LISTING_LIMIT

    const entries: FileEntry[] = []
    for (const it of items.slice(0, DIR_LISTING_LIMIT)) {
      let size: number | undefined
      if (!it.isDir) {
        try {
          size = (await fs.stat(`${r.abs}/${it.name}`)).size
        } catch {
          // 断链软链 / 刚被删掉 —— 仍然列出来,只是没有大小
        }
      }
      entries.push({
        name: it.name,
        path: rel === '.' ? it.name : `${rel}/${it.name}`,
        kind: it.isDir ? 'dir' : 'file',
        hidden: it.name.startsWith('.'),
        ...(size !== undefined ? { size } : {})
      })
    }

    if (entries.length === 0) return toolOk(`${rel} is an empty directory.`)

    // ★ 复用 shared 的排序:文件树和工具输出必须给出同一个顺序,
    //   否则用户在树里看到的和模型报告的对不上号
    const body = sortEntries(entries)
      .map((e) =>
        e.kind === 'dir'
          ? `${e.name}/`
          : `${e.name}${e.size === undefined ? '' : `  (${humanSize(e.size)})`}`
      )
      .join('\n')

    const note = truncated
      ? `\n\n[The directory has ${String(items.length)} entries; showing the first ${String(DIR_LISTING_LIMIT)}]`
      : ''
    return toolOk(`${rel}:\n${body}${note}`)
  }
})

/** 给测试用的常量出口,避免测试里再抄一份魔法数字 */
export const FS_LIMITS = {
  MAX_READ_BYTES,
  MAX_LINE_CHARS,
  DEFAULT_READ_LIMIT
} as const
