import { describe, expect, it } from 'vitest'
import { isAddressableUrl, isExternalOpenableUrl, resolveAddress } from '../address'

/**
 * 地址栏三个判断的边界表。纯函数，所以铺满。
 *
 * 需求背景：2026-09-22 放开 file://（见 address.ts 头注）。这里最容易回归的
 * 不是「file:// 放行了」，而是配套的两条边界：裸输入仍然补 https://（否则
 * 地址栏直接打域名会去解析 `example.com` 这个主机名），以及外部打开按钮
 * 仍然只认 http(s)（file:// 交出去等于让系统拿默认应用跑本地路径）。
 */

describe('resolveAddress', () => {
  it('裸域名补 https://', () => {
    expect(resolveAddress('example.com')).toBe('https://example.com/')
    expect(resolveAddress('example.com/a?b=c')).toBe('https://example.com/a?b=c')
  })

  it('http(s) 原样解析', () => {
    expect(resolveAddress('http://example.com/')).toBe('http://example.com/')
    expect(resolveAddress('https://example.com/x')).toBe('https://example.com/x')
  })

  it('★ file:// 原样通过，不被补成 https://file…', () => {
    expect(resolveAddress('file:///tmp/report.html')).toBe('file:///tmp/report.html')
    expect(resolveAddress('  file:///tmp/report.html  ')).toBe('file:///tmp/report.html')
  })

  it('空输入返回 null', () => {
    expect(resolveAddress('')).toBeNull()
    expect(resolveAddress('   ')).toBeNull()
  })

  it('★ 其余协议一律 null —— javascript: / data: / ncw:// 不进 webview', () => {
    expect(resolveAddress('javascript:alert(1)')).toBeNull()
    expect(resolveAddress('data:text/html,hi')).toBeNull()
    expect(resolveAddress('ncw://attachment/x')).toBeNull()
    expect(resolveAddress('ftp://example.com/x')).toBeNull()
  })

  it('带凭证的地址返回 null（和主进程的闸同一标准）', () => {
    expect(resolveAddress('https://user:pass@example.com/')).toBeNull()
  })

  it('解析不了的输入返回 null', () => {
    expect(resolveAddress('http://')).toBeNull()
    expect(resolveAddress('https://exa mple.com/')).toBeNull()
  })
})

describe('isAddressableUrl', () => {
  it('http(s) 与 file 回写地址栏，其余忽略', () => {
    expect(isAddressableUrl('https://example.com/')).toBe(true)
    expect(isAddressableUrl('http://example.com/')).toBe(true)
    expect(isAddressableUrl('file:///tmp/report.html')).toBe(true)
    expect(isAddressableUrl('about:blank')).toBe(false)
    expect(isAddressableUrl('ncw://attachment/x')).toBe(false)
    expect(isAddressableUrl('')).toBe(false)
  })
})

describe('isExternalOpenableUrl', () => {
  it('★ 只认 http(s) —— file:// 不能交给系统默认应用', () => {
    expect(isExternalOpenableUrl('https://example.com/')).toBe(true)
    expect(isExternalOpenableUrl('http://example.com/')).toBe(true)
    expect(isExternalOpenableUrl('file:///tmp/report.html')).toBe(false)
    expect(isExternalOpenableUrl('about:blank')).toBe(false)
  })
})
