import { describe, expect, it } from 'vitest'
import type { PermissionMode } from '../../../shared/agent/permission'
import { PERMISSION_MODES } from '../../../shared/agent/permission'
import { NETWORK_SWITCH_TOOLS, evaluate, permissionFacts } from '../permission-gate'
import { builtinTools } from '../tool/builtin'

/**
 * 权限闸门的**穷举**表。
 *
 * ★ 下面那张 `GOLDEN` 是**手写的期望值**,不是从 `evaluate()` 生成的。
 * 用一个「独立实现的期望函数」来对拍是没有意义的 —— 那只是把同一套 if 抄了两遍,
 * 两边一起改错的时候测试照绿。所以这里写死 48 行结果,改动策略必然改到这张表,
 * 而改表的人会被迫逐行看一眼自己在放开什么。
 *
 * 列:模式 / readOnly / destructive / needsNetwork / webSearch → 结果(1 = true)
 *
 * `readOnly` 与 `destructive` 同时为真是不合理的组合,但仍然列进来 ——
 * 穷举的价值就在于「不合理的输入也有确定的答案」,而这里的答案是「按只读放行」。
 */
const GOLDEN = `
ask  1 1 1 1 allow
ask  1 1 1 0 deny
ask  1 1 0 1 allow
ask  1 1 0 0 allow
ask  1 0 1 1 allow
ask  1 0 1 0 deny
ask  1 0 0 1 allow
ask  1 0 0 0 allow
ask  0 1 1 1 ask
ask  0 1 1 0 deny
ask  0 1 0 1 ask
ask  0 1 0 0 ask
ask  0 0 1 1 ask
ask  0 0 1 0 deny
ask  0 0 0 1 ask
ask  0 0 0 0 ask
auto 1 1 1 1 allow
auto 1 1 1 0 deny
auto 1 1 0 1 allow
auto 1 1 0 0 allow
auto 1 0 1 1 allow
auto 1 0 1 0 deny
auto 1 0 0 1 allow
auto 1 0 0 0 allow
auto 0 1 1 1 ask
auto 0 1 1 0 deny
auto 0 1 0 1 ask
auto 0 1 0 0 ask
auto 0 0 1 1 allow
auto 0 0 1 0 deny
auto 0 0 0 1 allow
auto 0 0 0 0 allow
full 1 1 1 1 allow
full 1 1 1 0 deny
full 1 1 0 1 allow
full 1 1 0 0 allow
full 1 0 1 1 allow
full 1 0 1 0 deny
full 1 0 0 1 allow
full 1 0 0 0 allow
full 0 1 1 1 allow
full 0 1 1 0 deny
full 0 1 0 1 allow
full 0 1 0 0 allow
full 0 0 1 1 allow
full 0 0 1 0 deny
full 0 0 0 1 allow
full 0 0 0 0 allow
`

interface Row {
  mode: PermissionMode
  readOnly: boolean
  destructive: boolean
  needsNetwork: boolean
  webSearch: boolean
  want: string
  label: string
}

const ROWS: Row[] = GOLDEN.trim()
  .split('\n')
  .map((line) => {
    const [mode, ro, de, ne, we, want] = line.trim().split(/\s+/)
    return {
      mode: mode as PermissionMode,
      readOnly: ro === '1',
      destructive: de === '1',
      needsNetwork: ne === '1',
      webSearch: we === '1',
      want: want ?? '',
      label: line.trim()
    }
  })

describe('PermissionGate · 穷举那张表', () => {
  it('表本身是完备的:3 档 × 4 个布尔 = 48 行,无重复无遗漏', () => {
    expect(ROWS).toHaveLength(48)
    const keys = new Set(ROWS.map((r) => `${r.mode}${String(r.readOnly)}${String(r.destructive)}${String(r.needsNetwork)}${String(r.webSearch)}`))
    expect(keys.size).toBe(48)
    expect(new Set(ROWS.map((r) => r.mode))).toEqual(new Set(PERMISSION_MODES))
    for (const r of ROWS) expect(['allow', 'deny', 'ask'], r.label).toContain(r.want)
  })

  for (const r of ROWS) {
    it(r.label, () => {
      expect(evaluate(r).kind, r.label).toBe(r.want)
    })
  }
})

/**
 * 上面 48 行已经把结果钉死了,这一组钉的是**为什么是这个结果** ——
 * 也就是那几行的**顺序**。顺序错了,上面某几行会跟着变,但从失败信息里
 * 看不出是顺序的问题;这几条的名字就是答案。
 */
describe('PermissionGate · 顺序即语义', () => {
  it('★ 联网开关排在只读放行之前 —— 只读的联网工具在开关关掉时仍然要拒', () => {
    const o = evaluate({
      mode: 'full',
      readOnly: true,
      destructive: false,
      needsNetwork: true,
      webSearch: false
    })
    expect(o.kind).toBe('deny')
  })

  it('★ full 档也放宽不了联网开关 —— 那是用户的硬开关,不是权限档位', () => {
    for (const mode of PERMISSION_MODES) {
      const o = evaluate({
        mode,
        readOnly: false,
        destructive: false,
        needsNetwork: true,
        webSearch: false
      })
      expect(o.kind, mode).toBe('deny')
    }
  })

  it('★ ask 档下的只读工具直接放行 —— 每读一个文件弹一次窗,用户会学会无脑点允许', () => {
    const o = evaluate({
      mode: 'ask',
      readOnly: true,
      destructive: true,
      needsNetwork: false,
      webSearch: false
    })
    expect(o.kind).toBe('allow')
  })

  it('auto 档只对破坏性操作发问,非破坏的写操作直接放行', () => {
    const base = { mode: 'auto' as const, readOnly: false, needsNetwork: false, webSearch: true }
    expect(evaluate({ ...base, destructive: true }).kind).toBe('ask')
    expect(evaluate({ ...base, destructive: false }).kind).toBe('allow')
  })

  it('needsNetwork 省略时等同 false —— 绝大多数工具不用写这个字段', () => {
    const o = evaluate({ mode: 'full', readOnly: false, destructive: true, webSearch: false })
    expect(o.kind).toBe('allow')
  })
})

describe('PermissionGate · 拒绝的说辞', () => {
  it('★ 联网被拒时要堵死「改用 Bash 里的 curl」这条路', () => {
    const o = evaluate({
      mode: 'full',
      readOnly: true,
      destructive: false,
      needsNetwork: true,
      webSearch: false
    })
    expect(o.kind === 'deny' && o.reason).toContain('curl')
    expect(o.kind === 'deny' && o.reason).toContain('user')
  })
})

describe('NETWORK_SWITCH_TOOLS', () => {
  /**
   * ★ 这条是**跨文件**的一致性检查。表里写错一个字(`webFetch` / `web_fetch`)
   * 的后果是:联网开关变成一个什么都不管的摆设,而所有单测照绿 ——
   * 因为 `evaluate()` 自己是对的,错的是没人往它手里递 `needsNetwork: true`。
   */
  it('★ 每一项都必须是真实注册了的 internalId', () => {
    const ids = new Set(builtinTools().map((t) => t.internalId))
    for (const id of NETWORK_SWITCH_TOOLS) {
      expect(ids, `NETWORK_SWITCH_TOOLS 里的 "${id}" 不是任何一个已注册工具的 internalId`).toContain(id)
    }
  })

  it('文件类工具不在表里 —— 在的话联网开关会顺手把读文件也关掉', () => {
    for (const id of ['Read', 'Write', 'Edit', 'LS', 'Glob', 'Grep', 'Bash', 'TodoWrite']) {
      expect(NETWORK_SWITCH_TOOLS.has(id), id).toBe(false)
    }
  })

  /**
   * ★★ 名单**精确等于**网页搜索 + 网页抓取两个 —— 用户的决定(见名单注释):
   * 「联网搜索」开关只管这两个。原先它是出网工具的下限表(连浏览器、生图、可视化卡片
   * 一起管),症状是「选好了生图模型,对话里却找不到工具」。
   *
   * 写成全等而不是「包含」:往这张表里加名字 = 让开关多管一个工具,是一次**行为改变**,
   * 必须改到这里、被人看一眼。反过来有人「顺手」把 `needsNetwork: true` 的工具
   * 同步进来(回到旧的下限表语义),也会在这里红。
   */
  it('★★ 名单精确等于 WebFetch + web_search —— 其余出网工具不受这颗开关管', () => {
    expect([...NETWORK_SWITCH_TOOLS].sort()).toEqual(['WebFetch', 'web_search'])
  })

  /**
   * ★ 这两个工具自己的 `needsNetwork` 也必须是 true —— 字段现在只是事实描述,
   * 但名单里的工具要是连「会出网」都没标,多半是名单指错了工具。
   */
  it('名单里的工具自己也标了 needsNetwork', () => {
    for (const t of builtinTools()) {
      if (!NETWORK_SWITCH_TOOLS.has(t.internalId)) continue
      expect(t.needsNetwork, t.internalId).toBe(true)
    }
  })

  /**
   * 反向:生图、浏览器、可视化卡片**仍然**如实标着 `needsNetwork: true`(事实没变),
   * 只是不在名单里 —— 钉住「字段是事实、名单是开关」这条分工,别有人为了让它们
   * 不被拦而把字段改成 false(那会让将来按出网加管控时丢掉事实)。
   */
  it('出网但不受开关管的工具:字段照实为 true,名单里没有', () => {
    for (const id of ['generate_image', 'browser_open', 'visualize_show_widget']) {
      const t = builtinTools().find((x) => x.internalId === id)
      expect(t?.needsNetwork, id).toBe(true)
      expect(NETWORK_SWITCH_TOOLS.has(id), id).toBe(false)
    }
  })
})

/**
 * `permissionFacts` —— 系统提示词 `# Environment` 里那几行。
 *
 * ★ 这一组钉的不是文案,是**它和上面那张表说同一件事**。漂移的表现最坏:
 * 提示词说「写盘会被拒」而闸门其实放行,模型就会**提前放弃**一件它做得成的事,
 * 而这中间不会有任何报错。
 */
describe('permissionFacts', () => {
  it('三档说三种话', () => {
    expect(permissionFacts('ask', true)).toContain('Permission mode: ask')
    expect(permissionFacts('ask', true)).toContain('wait for user approval')
    expect(permissionFacts('auto', true)).toContain('wait for user approval')
    expect(permissionFacts('full', true)).not.toContain('wait for user approval')
  })

  it('★ 读永远放行 —— 三档都不能把读说成要审批', () => {
    for (const mode of PERMISSION_MODES) {
      expect(permissionFacts(mode, true)).toContain('Reading and searching: run without asking')
    }
  })

  it('★ 联网开关连 full 档都放宽不了,而且要堵死 Bash 那条路', () => {
    const s = permissionFacts('full', false)

    expect(s).toContain('the network switch is off')
    expect(s).toContain('Bash')
  })

  it('★ 说的和那张表判的是同一件事', () => {
    for (const mode of PERMISSION_MODES) {
      for (const webSearch of [true, false]) {
        const said = permissionFacts(mode, webSearch)
        const write = evaluate({ mode, readOnly: false, destructive: true, webSearch })
        const line = said.split('\n').find((l) => l.startsWith('- Writing files')) ?? ''

        expect(line.includes('wait for user approval')).toBe(write.kind === 'ask')
        expect(line.includes('run without asking')).toBe(write.kind === 'allow')
      }
    }
  })
})
