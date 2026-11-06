import { describe, expect, it } from 'vitest'
import { messagesFor } from '../../i18n'
import {
  DEFAULT_SETTINGS_PAGE,
  matchPages,
  matchRows,
  resolveSettingsPage,
  SETTINGS_INDEX,
  SETTINGS_PAGES,
  visibleSettingsPages,
  type SettingsPageId
} from '../nav'

const PAGE_IDS = new Set<SettingsPageId>(SETTINGS_PAGES.map((p) => p.id))

describe('导航表与行目录的自洽', () => {
  /**
   * 参考图那十项 + 「导入」。
   *
   * ★ 断言的是**逐个 id**,不只是长度。原来那条只数个数,而它想守的其实是
   * 「没人悄悄加一页、也没人悄悄删一页」—— 只数个数的话,把「钱包」换成
   * 「导入」照样是绿的,而那正是这条测试该拦下的那种改动。
   */
  it('十项照参考图,外加「导入」「使用统计」与控制端本地的「设备」', () => {
    expect(SETTINGS_PAGES.map((p) => p.id)).toEqual([
      'account',
      'wallet',
      'general',
      'import',
      'preference',
      'model',
      'usage',
      'review',
      'connection',
      'devices',
      'computer',
      'data',
      'experimental',
      'about'
    ])
  })

  it('行目录里的 page 全都存在', () => {
    for (const r of SETTINGS_INDEX) expect(PAGE_IDS.has(r.page)).toBe(true)
  })

  /** 表和界面漂移是这套搜索唯一的失效方式 —— 这条断言守的是它的一半 */
  it('行目录里的 sub 必须是那一页真有的子 Tab', () => {
    for (const r of SETTINGS_INDEX) {
      if (r.sub === undefined) continue
      const page = SETTINGS_PAGES.find((p) => p.id === r.page)
      expect(
        page?.subs?.some((s) => s.id === r.sub),
        `${r.page}/${r.sub}`
      ).toBe(true)
    }
  })

  it('有子 Tab 的页面,它的行必须指明是哪个子 Tab', () => {
    for (const p of SETTINGS_PAGES) {
      if (p.subs === undefined) continue
      for (const r of SETTINGS_INDEX.filter((x) => x.page === p.id)) {
        expect(r.sub, `${p.id}:${r.title}`).toBeDefined()
      }
    }
  })

  it('默认页在表里', () => {
    expect(PAGE_IDS.has(DEFAULT_SETTINGS_PAGE)).toBe(true)
  })

  /**
   * ★ `SettingsOverlay` 的 `pageLabel` / `subLabel` 是**拼字符串**取键的
   * (`t(\`settings.sub.${id}\`)`),而 `t()` 查不到时返回键本身 —— 漏一条翻译
   * 不会报错,只会让那颗药丸上写着 `settings.sub.personalization`。
   * 加一页、加一个子 Tab 却忘了配文案,是这里唯一会发生的事故。
   */
  it('★ 每一页、每一个子 Tab 都有中英文案', () => {
    const zh = messagesFor('zh-CN')
    const en = messagesFor('en-US')
    for (const p of SETTINGS_PAGES) {
      expect(zh[`settings.page.${p.id}`], p.id).toBeDefined()
      expect(en[`settings.page.${p.id}`], p.id).toBeDefined()
      for (const s of p.subs ?? []) {
        expect(zh[`settings.sub.${s.id}`], `${p.id}/${s.id}`).toBeDefined()
        expect(en[`settings.sub.${s.id}`], `${p.id}/${s.id}`).toBeDefined()
      }
    }
  })

  /**
   * ★ 「执行 Shell」那一行是**唯一**这一页上不靠 `settings.*` 取标题的行 ——
   * 标题是域名词(`Shell`),只有它的描述和「跟随系统」那一项走 i18n。
   * 漏配一条不报错,只会让描述那一栏写着 `general.shellHint`。
   */
  it('「执行 Shell」行的界面文案中英两套都在', () => {
    const zh = messagesFor('zh-CN')
    const en = messagesFor('en-US')
    for (const key of ['general.shell', 'general.shellSystem', 'general.shellHint']) {
      expect(zh[key], key).toBeDefined()
      expect(en[key], key).toBeDefined()
    }
  })
})

describe('matchRows', () => {
  it('空查询返回空 —— 不是返回全部', () => {
    expect(matchRows('')).toEqual([])
    expect(matchRows('   ')).toEqual([])
  })

  it('命中行标题', () => {
    expect(matchRows('期望端口').map((r) => r.title)).toEqual(['期望端口'])
  })

  it('命中英文关键词', () => {
    const titles = matchRows('proxy').map((r) => r.title)
    expect(titles).toContain('启用代理')
    // 三段拆开之后这一行叫「代理服务器」了(协议 / 地址 / 端口 在同一行里)
    expect(titles).toContain('代理服务器')
  })

  it('命中页名 —— 打「连接」应该把这一页的行全带出来', () => {
    const rows = matchRows('连接')
    expect(rows.length).toBeGreaterThan(0)
    expect(rows.every((r) => r.page === 'connection')).toBe(true)
  })

  it('大小写与前后空格无关', () => {
    expect(matchRows('  THEME  ')).toEqual(matchRows('theme'))
    expect(matchRows('theme').length).toBeGreaterThan(0)
  })

  it('无命中返回空(驱动空态)', () => {
    expect(matchRows('zzzz没有这一项')).toEqual([])
  })

  /**
   * ★ 「执行 Shell」那一行的标题是域名词,而用户打的十有八九是 shell 自己的
   * 名字或「终端」。搜不到就等于这一项不存在 —— 八个取值一个都不能漏。
   */
  it('shell 相关查询都指到通用 › Agent', () => {
    const queries = ['shell', 'sheel', 'bash', 'zsh', 'fish', 'sh', 'powershell', 'pwsh', 'cmd', 'terminal', '终端', '命令']
    for (const query of queries) {
      const rows = matchRows(query)
      expect(
        rows.some((r) => r.page === 'general' && r.sub === 'agent' && r.title === 'Shell'),
        query
      ).toBe(true)
    }
  })
})

describe('matchPages', () => {
  it('页名命中', () => {
    expect(matchPages('关于', true).map((p) => p.id)).toEqual(['about'])
  })
  it('空查询返回空', () => {
    expect(matchPages('', true)).toEqual([])
  })
  it('未登录时搜不到钱包页 —— 导航里看不见的页不能从搜索进去', () => {
    expect(matchPages('钱包', false)).toEqual([])
    expect(matchPages('钱包', true).map((p) => p.id)).toEqual(['wallet'])
  })
})

describe('需要登录的页', () => {
  it('未登录时导航里只少钱包这一页，顺序不变', () => {
    const all = SETTINGS_PAGES.map((p) => p.id)
    expect(visibleSettingsPages(true).map((p) => p.id)).toEqual(all)
    expect(visibleSettingsPages(false).map((p) => p.id)).toEqual(all.filter((id) => id !== 'wallet'))
  })

  it('未登录时请求钱包页会落到账户页（那里有登录入口），其余页原样', () => {
    expect(resolveSettingsPage('wallet', false)).toBe('account')
    expect(resolveSettingsPage('wallet', true)).toBe('wallet')
    expect(resolveSettingsPage('general', false)).toBe('general')
  })
})
