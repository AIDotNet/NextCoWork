import { describe, expect, it } from 'vitest'
import { literalPrefilter, requiredLiteral } from '../grep-prefilter'

/**
 * `Grep` 整文件预筛的测试。
 *
 * ★ 这里守的是「只许误报,不许漏报」:预筛说「不可能命中」的文件会被整个跳过,
 * 漏报一次就是模型拿到一句看起来很可信的「仓库里没有」。最后那条对照测试
 * 断言的正是这一点 —— 原正则能命中的文本,预筛必须放行。
 */

describe('requiredLiteral', () => {
  it('extracts a plain identifier as-is', () => {
    expect(requiredLiteral('GiftBalance')).toBe('GiftBalance')
  })

  it('treats escaped punctuation as the literal character', () => {
    expect(requiredLiteral('foo\\.bar\\(')).toBe('foo.bar(')
  })

  it('picks the longest run between metacharacters', () => {
    expect(requiredLiteral('log.*ErrorHandler')).toBe('ErrorHandler')
    expect(requiredLiteral('function\\s+\\w+')).toBe('function')
  })

  it('drops the character made optional by ?, * or {0,n}', () => {
    expect(requiredLiteral('colou?r')).toBe('colo')
    expect(requiredLiteral('abcx*yz')).toBe('abc')
    expect(requiredLiteral('abcd{0,2}')).toBe('abc')
  })

  it('keeps a +-quantified character but breaks adjacency after it', () => {
    expect(requiredLiteral('abcd+efg')).toBe('abcd')
  })

  it('refuses any pattern with top-level alternation', () => {
    expect(requiredLiteral('fooBar|bazQux')).toBeNull()
  })

  it('does not descend into groups or character classes', () => {
    expect(requiredLiteral('(?:optionalThing)?rest')).toBe('rest')
    expect(requiredLiteral('[abc]+xy')).toBeNull()
  })

  it('treats a malformed brace as a literal, like V8 does', () => {
    expect(requiredLiteral('{foo}')).toBe('{foo}')
  })

  it('returns null when nothing long enough is required', () => {
    expect(requiredLiteral('\\w+\\s*=')).toBeNull()
    expect(requiredLiteral('a.b')).toBeNull()
  })
})

describe('literalPrefilter', () => {
  it('is case-sensitive unless -i is set', () => {
    expect(literalPrefilter('GiftBalance', false)?.('giftbalance')).toBe(false)
    expect(literalPrefilter('GiftBalance', true)?.('x giftbalance y')).toBe(true)
  })

  it('escapes the literal before building the -i regex', () => {
    expect(literalPrefilter('a\\.b\\(c', true)?.('A.B(C')).toBe(true)
    expect(literalPrefilter('a\\.b\\(c', true)?.('AxB(C')).toBe(false)
  })

  it('never rejects a text the original regex matches', () => {
    const patterns = [
      'GiftBalance', 'colou?r', 'log.*Error', 'function\\s+\\w+', 'abcd+efg', 'a\\.b\\(c',
      '(?:pre)?fix_value', 'x{2}yz', '\\bimport\\b', 'end$', '^start', 'lazy+?tail', '{foo}', 'k{0}abc'
    ]
    const texts = [
      'const GiftBalance = 1', 'color colour', 'log: Error here', 'function  foo()', 'abcddddefg',
      'a.b(c', 'fix_value prefix_value', 'xxyz', 'import x', 'the end', 'start here', 'lazyyytail',
      '{foo}', 'abc', 'KABC', 'COLOR', ''
    ]
    for (const p of patterns) {
      for (const ignoreCase of [false, true]) {
        const pre = literalPrefilter(p, ignoreCase)
        if (pre === null) continue
        const re = new RegExp(p, ignoreCase ? 'i' : '')
        for (const t of texts) {
          if (re.test(t)) expect(pre(t), `${p} /${ignoreCase ? 'i' : ''} on "${t}"`).toBe(true)
        }
      }
    }
  })
})
