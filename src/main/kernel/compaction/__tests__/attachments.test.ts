/**
 * 压缩后重附件(`buildPostCompactAttachments`)的用例。
 *
 * 需求:摘要写的是「做过什么」,不是「文件现在长什么样」。压完之后模型手里只剩
 * 摘要的话,它的第一个动作几乎一定是把刚才在改的那几个文件重新 Read 一遍 ——
 * 白白多一轮往返;更糟的是在那之前它会照着摘要里的旧片段去 Edit,old_string 对不上。
 *
 * ★ 这一组同时承接了一条从 `todo-derive.test.ts` 搬过来的不变式:
 * **压缩之后模型不能忘记自己的进度表**。原先它靠「机械压缩保留 tool_call」成立,
 * 机械压缩删除后改由重附件承担 —— 判据变了,要守的东西没变。
 */
import { describe, expect, it } from 'vitest'
import type { AgentMessage } from '../../../../shared/agent/message'
import { assistantMessage, userMessage } from '../../../../shared/agent/message'
import {
  buildPostCompactAttachments,
  recentFilePaths,
  truncateToTokens,
  POST_COMPACT_MAX_FILES
} from '../attachments'

const NOW = Date.UTC(2026, 8, 4, 12, 0, 0)
const TOOLS = { file: new Set(['Read', 'Edit']), todo: 'TodoWrite', skill: 'Skill' }

/** 一轮工具往返。 */
function call(id: string, name: string, input: unknown, opts: { output?: string; isError?: boolean } = {}): AgentMessage[] {
  return [
    assistantMessage(`a-${id}`, [{ type: 'tool_call', callId: id, name, input }], NOW),
    userMessage(`r-${id}`, [{
      type: 'tool_result', callId: id,
      output: { content: opts.output ?? 'ok' },
      isError: opts.isError ?? false
    }], NOW)
  ]
}

describe('recentFilePaths', () => {
  it('新的在前,重复路径只留一次', () => {
    const messages = [
      ...call('c1', 'Read', { file_path: '/ws/a.ts' }),
      ...call('c2', 'Read', { file_path: '/ws/b.ts' }),
      ...call('c3', 'Edit', { file_path: '/ws/a.ts' })
    ]
    expect(recentFilePaths(messages, TOOLS.file)).toEqual(['/ws/a.ts', '/ws/b.ts'])
  })

  /** ★ 失败的 Read(文件不存在)重附一次只会再失败一次,而且挤掉一个名额。 */
  it('★ 失败的调用不算「碰过」', () => {
    const messages = [
      ...call('c1', 'Read', { file_path: '/ws/gone.ts' }, { isError: true }),
      ...call('c2', 'Read', { file_path: '/ws/a.ts' })
    ]
    expect(recentFilePaths(messages, TOOLS.file)).toEqual(['/ws/a.ts'])
  })

  /**
   * ★ 工具名从注册表查来的**外部名**,不是字面量:撞名时 `ToolNamer` 会加哈希后缀。
   * 这里钉的是「名字对不上就一个文件都不附」—— 那是静默退化,不是报错。
   */
  it('★ 只认传进来的那几个工具名', () => {
    const messages = call('c1', 'Read_a1b2', { file_path: '/ws/a.ts' })
    expect(recentFilePaths(messages, TOOLS.file)).toEqual([])
    expect(recentFilePaths(messages, new Set(['Read_a1b2']))).toEqual(['/ws/a.ts'])
  })
})

describe('buildPostCompactAttachments', () => {
  /**
   * ★★ **内容现读,不从转录里抄。** 转录里的是当时的快照,之后多半已经被 Edit 过 ——
   * 抄旧快照会让模型以为文件还是旧样子,下一次 Edit 的 old_string 对不上,
   * 而错误信息指向的是工具,不是这里。
   */
  it('★★ 文件内容来自 readFile,不是转录里的那份', async () => {
    const messages = call('c1', 'Read', { file_path: '/ws/a.ts' }, { output: '旧快照' })
    const out = await buildPostCompactAttachments({
      messages, tools: TOOLS, readFile: async () => '磁盘上的新内容'
    })
    expect(out.restoredFiles).toEqual(['/ws/a.ts'])
    expect(out.texts.join('\n')).toContain('磁盘上的新内容')
    expect(out.texts.join('\n')).not.toContain('旧快照')
  })

  /** ★ 读失败静默跳过:重附是锦上添花,不能让整次压缩失败。 */
  it('★ readFile 抛异常或返回 undefined 都只是少附一个文件', async () => {
    const messages = [
      ...call('c1', 'Read', { file_path: '/ws/boom.ts' }),
      ...call('c2', 'Read', { file_path: '/ws/ok.ts' })
    ]
    const out = await buildPostCompactAttachments({
      messages,
      tools: TOOLS,
      readFile: async (path) => {
        if (path === '/ws/boom.ts') throw new Error('EACCES')
        return '内容'
      }
    })
    expect(out.restoredFiles).toEqual(['/ws/ok.ts'])
  })

  it('最多重附 POST_COMPACT_MAX_FILES 个文件', async () => {
    const messages = Array.from({ length: POST_COMPACT_MAX_FILES + 3 }, (_, i) =>
      call(`c${i}`, 'Read', { file_path: `/ws/f${i}.ts` })
    ).flat()
    const out = await buildPostCompactAttachments({ messages, tools: TOOLS, readFile: async () => '内容' })
    expect(out.restoredFiles.length).toBe(POST_COMPACT_MAX_FILES)
  })

  /**
   * ★★ 从 `todo-derive.test.ts` 搬来的那条:压缩之后模型不能忘记自己的进度表。
   * 忘了的表现是它重新写一份清单,把之前的进度**抹掉** —— 而界面上的 todo 面板
   * 照旧显示(它读的是完整转录),所以这个 bug 从界面上根本看不出来。
   */
  it('★★ 当前 todo 列表被重新附上', async () => {
    const messages = call('c1', 'TodoWrite', {
      todos: [
        { content: '读代码', status: 'completed', activeForm: '正在读代码' },
        { content: '写代码', status: 'in_progress', activeForm: '正在写代码' }
      ]
    })
    const out = await buildPostCompactAttachments({ messages, tools: TOOLS, readFile: async () => undefined })
    const text = out.texts.join('\n')
    expect(text).toContain('读代码')
    expect(text).toContain('写代码')
    expect(text).toContain('TodoWrite')
  })

  it('没有 todo 时不附一段空清单', async () => {
    const out = await buildPostCompactAttachments({
      messages: call('c1', 'Read', { file_path: '/ws/a.ts' }),
      tools: TOOLS,
      readFile: async () => undefined
    })
    expect(out.texts).toEqual([])
  })

  /** 技能正文在压缩后仍然生效 —— 它是这段对话的规则,不是一次性输出。 */
  it('用过的技能正文重新附上', async () => {
    const messages = call('c1', 'Skill', { name: 'plugin-builder' }, { output: '技能正文:先读 manifest' })
    const out = await buildPostCompactAttachments({ messages, tools: TOOLS, readFile: async () => undefined })
    expect(out.texts.join('\n')).toContain('plugin-builder')
    expect(out.texts.join('\n')).toContain('先读 manifest')
  })

  /** 工具名没传(注册表里没有这个工具)时那一档整个不出现,而不是附一段空的。 */
  it('没给 todo / skill 工具名时不产出对应段落', async () => {
    const messages = [
      ...call('c1', 'TodoWrite', { todos: [{ content: 'x', status: 'pending', activeForm: '正在 x' }] }),
      ...call('c2', 'Skill', { name: 's' }, { output: '正文' })
    ]
    const out = await buildPostCompactAttachments({
      messages, tools: { file: new Set(['Read']) }, readFile: async () => undefined
    })
    expect(out.texts).toEqual([])
  })

  /** 已经取消的压缩不该再去读一堆文件。 */
  it('signal 已取消时不再读文件', async () => {
    const controller = new AbortController()
    controller.abort()
    const out = await buildPostCompactAttachments({
      messages: call('c1', 'Read', { file_path: '/ws/a.ts' }),
      tools: TOOLS,
      readFile: async () => '内容',
      signal: controller.signal
    })
    expect(out.restoredFiles).toEqual([])
  })
})

describe('truncateToTokens', () => {
  it('没超预算时原样返回', () => {
    expect(truncateToTokens('短文本', 100)).toBe('短文本')
  })

  /**
   * ★ 截断处必须留一句说明。不留的话模型会把半个文件当成整个文件,
   * 接着对着不存在的行号提 Edit —— 而它完全不知道自己看到的是截断版。
   */
  it('★ 超预算时截断并留下「用 Read 看全文」的说明', () => {
    const out = truncateToTokens('x'.repeat(100_000), 100)
    expect(out.length).toBeLessThan(100_000)
    expect(out).toContain('Use the Read tool')
  })

  /** ★ 估算对 CJK 和拉丁字符的系数不同,按字符比例直接切会切过头或切不够。 */
  it('★ 中文正文同样能截到预算之内', () => {
    const out = truncateToTokens('中'.repeat(50_000), 100)
    expect(out.length).toBeLessThan(50_000)
    expect(out).toContain('Use the Read tool')
  })
})
