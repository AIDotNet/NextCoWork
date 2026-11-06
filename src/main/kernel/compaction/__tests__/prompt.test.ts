/**
 * 压缩提示词的输入/输出两头。
 *
 * 需求:压完之后模型手里只剩这份摘要,所以「模型写歪了怎么办」必须是**已决定**的行为,
 * 不能碰运气。这里钉的两件事都直接决定一次压缩成不成立:
 * 草稿有没有被剥干净(留着就把省下的 token 吃回去一半),
 * 以及模型没按格式写时我们取什么(取空 = 整次压缩判失败,而摘要其实是有的)。
 */
import { describe, expect, it } from 'vitest'
import { compactPrompt, continuationText, formatCompactSummary } from '../prompt'

describe('compactPrompt', () => {
  /**
   * ★ 九节结构是这套机制能不能用的一半:漏了「用户原话」或「当前工作」,
   * 下一轮模型就会重新问一遍、或者把已经做完的事再做一遍。
   * 逐节钉住,防止后来者「顺手精简」。
   */
  it('★ 九节标题一节不少', () => {
    const prompt = compactPrompt()
    for (const section of [
      'Primary Request and Intent',
      'Key Technical Concepts',
      'Files and Code Sections',
      'Errors and fixes',
      'Problem Solving',
      'All user messages',
      'Pending Tasks',
      'Current Work',
      'Optional Next Step'
    ]) {
      expect(prompt, section).toContain(section)
    }
  })

  /**
   * ★ 禁用工具的那段话必须在**最前面**。压缩请求不下发工具 schema,但历史里全是
   * tool_call,有的模型会照着历史的样子「继续调用工具」,产出一段伪造的调用文本
   * 而不是摘要 —— 那时 `formatCompactSummary` 拿到的是垃圾,压缩静默地变成一次浪费。
   */
  it('★ 开头和结尾都有「不要调用工具」', () => {
    const prompt = compactPrompt()
    expect(prompt.startsWith('CRITICAL: Respond with TEXT ONLY')).toBe(true)
    expect(prompt.trimEnd().endsWith('followed by a <summary> block.')).toBe(true)
  })

  it('/compact 的补充指令接在正文之后', () => {
    expect(compactPrompt('重点保留权限那一段')).toContain('Additional Instructions:\n重点保留权限那一段')
    expect(compactPrompt('   ')).not.toContain('Additional Instructions')
    expect(compactPrompt()).not.toContain('Additional Instructions')
  })
})

describe('formatCompactSummary', () => {
  it('取 summary 块,剥掉 analysis', () => {
    expect(formatCompactSummary('<analysis>草稿</analysis>\n<summary>正文</summary>')).toBe('正文')
  })

  /**
   * ★ 模型没写 `<summary>` 时退回「剥完草稿的全文」,而不是判空。
   * 判空的代价是整次压缩失败、白烧一次请求 —— 而摘要其实好端端地在那儿,
   * 只是少了一对标签。宁可带一点格式噪音。
   */
  it('★ 没有 summary 标签时取剥完草稿的全文', () => {
    expect(formatCompactSummary('<analysis>草稿</analysis>\n1. 意图: 重构')).toBe('1. 意图: 重构')
  })

  /**
   * ★ 输出被 `maxOutputTokens` 截断时 `</analysis>` 可能根本没出现 ——
   * 那时后面没有 summary 可取,整段都是草稿。不特判的话草稿会原样进上下文,
   * 下一轮模型读到的是自己的思考过程,而不是结论。
   */
  it('★ analysis 没闭合(输出被截断)时不把草稿当摘要', () => {
    expect(formatCompactSummary('<analysis>草稿写到一半就被截断了')).toBe('')
  })

  it('连续空行压成一个,首尾空白去掉', () => {
    expect(formatCompactSummary('<summary>\n\n一\n\n\n\n二\n\n</summary>')).toBe('一\n\n二')
  })
})

describe('continuationText', () => {
  it('自动压缩要求接着干,手动压缩不要求', () => {
    expect(continuationText('摘要', true)).toContain('without asking the user any further questions')
    expect(continuationText('摘要', false)).not.toContain('without asking the user any further questions')
    expect(continuationText('摘要', false)).toContain('摘要')
  })
})
