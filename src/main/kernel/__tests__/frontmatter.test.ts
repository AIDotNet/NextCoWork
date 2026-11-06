import { describe, expect, it } from 'vitest'
import { FM_LIMITS, fmBool, fmList, fmString, parseFrontmatter, serializeFrontmatter } from '../frontmatter'

/**
 * 前置块解析器的测试。
 *
 * 这个文件里有两类用例,分开看:
 *
 * - **能力**:支持的子集确实能解析出来。
 * - **防线**:不可信输入(SKILL.md 来自 zip / git)的那几条 —— 原型污染、
 *   尺寸闸门、控制字符。这几条不是「边界情况」,它们是这个模块**存在的理由**
 *   (否则直接 `import yaml from 'yaml'` 就完了)。
 */

describe('parseFrontmatter · 基本形状', () => {
  it('解析出键值和正文', () => {
    const fm = parseFrontmatter('---\nname: commit\ndescription: 写提交信息\n---\n正文第一行\n正文第二行')
    expect(fm.data.name).toBe('commit')
    expect(fm.data.description).toBe('写提交信息')
    expect(fm.body).toBe('正文第一行\n正文第二行')
  })

  it('... 也能收尾', () => {
    const fm = parseFrontmatter('---\nname: x\n...\nbody')
    expect(fm.data.name).toBe('x')
    expect(fm.body).toBe('body')
  })

  it('值里的冒号只按第一个分割', () => {
    const fm = parseFrontmatter('---\ndescription: 用法:先读再写\n---\n')
    expect(fm.data.description).toBe('用法:先读再写')
  })

  it('★ 行内 # 不当注释剥掉 —— CC 自己的 description 里就带 #', () => {
    const fm = parseFrontmatter('---\ndescription: 以 # 开头的行是注释\n---\n')
    expect(fm.data.description).toBe('以 # 开头的行是注释')
  })

  it('整行 # 是注释,静默跳过', () => {
    const fm = parseFrontmatter('---\n# 这是注释\nname: x\n---\n')
    expect(fm.data.name).toBe('x')
    expect(fm.skipped).toHaveLength(0)
  })

  it('空行不影响解析', () => {
    const fm = parseFrontmatter('---\nname: x\n\ndescription: y\n---\n')
    expect(fm.data.name).toBe('x')
    expect(fm.data.description).toBe('y')
  })
})

describe('parseFrontmatter · 块标量', () => {
  it('★ 字面块(|)保留换行,块结束后的键照常解析', () => {
    // 这条是 installSkillZip 那个「装不上」的回归保护:官方 Skill 的
    // description 就是这么写的,而安装器把 skipped 非空当硬失败。
    const fm = parseFrontmatter('---\ndescription: |\n  第一行\n  第二行\nname: x\n---\n正文')
    expect(fm.data.description).toBe('第一行\n第二行\n')
    expect(fm.data.name).toBe('x')
    expect(fm.skipped).toHaveLength(0)
    expect(fm.body).toBe('正文')
    // ★ 缩进的续行不能被当成「嵌套 map」再报一次,更不能被当成键值解析出来
    expect(fm.data).not.toHaveProperty('第一行')
  })

  it('折叠块(>)把换行折成空格,空行才是换行', () => {
    const fm = parseFrontmatter('---\nd: >\n  一\n  二\n\n  三\n---\n')
    expect(fm.data.d).toBe('一 二\n三\n')
  })

  it('★ 折叠块里更深缩进的行保持字面换行', () => {
    const fm = parseFrontmatter('---\nd: >\n  开头\n    缩进行\n  结尾\n---\n')
    expect(fm.data.d).toBe('开头\n  缩进行\n结尾\n')
  })

  it('截断指示:缺省留一个换行,- 全去掉,+ 原样留着', () => {
    expect(parseFrontmatter('---\nd: |\n  x\n\n\n---\n').data.d).toBe('x\n')
    expect(parseFrontmatter('---\nd: |-\n  x\n\n---\n').data.d).toBe('x')
    expect(parseFrontmatter('---\nd: |+\n  x\n\n---\n').data.d).toBe('x\n\n')
  })

  it('缩进指示两种顺序都认,且前导空格按它保留', () => {
    // `|2` 把块的缩进钉死在 2 列,于是第一行多出来的两个空格是内容的一部分
    expect(parseFrontmatter('---\nd: |2\n    缩进\n  顶格\n---\n').data.d).toBe('  缩进\n顶格\n')
    expect(parseFrontmatter('---\nd: |-2\n  x\n---\n').data.d).toBe('x')
    expect(parseFrontmatter('---\nd: |2-\n  x\n---\n').data.d).toBe('x')
  })

  it('块里的空行和 # 不当注释、不当分隔', () => {
    const fm = parseFrontmatter('---\nd: |\n  一\n\n  # 井号\nname: x\n---\n')
    expect(fm.data.d).toBe('一\n\n# 井号\n')
    expect(fm.data.name).toBe('x')
  })

  it('空块读成空值,下一个键不受影响', () => {
    const fm = parseFrontmatter('---\nd: |\nname: x\n---\n')
    expect(fmString(fm, 'd')).toBeUndefined()
    expect(fm.data.name).toBe('x')
    expect(fm.skipped).toHaveLength(0)
  })

  it('★ 块内容也走限长 —— 它同样会进系统提示词', () => {
    const fm = parseFrontmatter(`---\nd: |\n  ${'x'.repeat(FM_LIMITS.FM_VALUE_MAX + 100)}\n---\n`)
    expect((fm.data.d as string).length).toBe(FM_LIMITS.FM_VALUE_MAX)
    expect(fm.skipped.join()).toContain('截断')
  })
})

describe('parseFrontmatter · 一层嵌套', () => {
  it('★ 市场里真实的 SKILL.md —— metadata 块不再让整包装不上', () => {
    const fm = parseFrontmatter(
      [
        '---',
        'name: programming-tutor',
        'description: "全能 AI 编程导师"',
        'license: MIT',
        'metadata:',
        '  author: "Samuel Kahessay"',
        '  version: "1.0.1"',
        '  category: "education"',
        '---',
        '正文'
      ].join('\n')
    )
    expect(fm.skipped).toHaveLength(0)
    expect(fm.data.name).toBe('programming-tutor')
    expect(fm.data.license).toBe('MIT')
    expect(fm.data['metadata.author']).toBe('Samuel Kahessay')
    expect(fm.data['metadata.version']).toBe('1.0.1')
    expect(fm.body).toBe('正文')
    // 父键本身是一张 map,不该再留着那个空串占位
    expect(fm.data).not.toHaveProperty('metadata')
  })

  it('嵌套结束后顶层的键照常解析', () => {
    const fm = parseFrontmatter('---\nm:\n  a: 1\nname: x\n---\n')
    expect(fm.data['m.a']).toBe('1')
    expect(fm.data.name).toBe('x')
    expect(fm.skipped).toHaveLength(0)
  })

  it('子键下的列表,块式和流式都认', () => {
    const block = parseFrontmatter('---\nm:\n  tags:\n    - a\n    - b\n---\n')
    expect(block.data['m.tags']).toEqual(['a', 'b'])
    const flow = parseFrontmatter('---\nm:\n  tags: [a, b]\n---\n')
    expect(flow.data['m.tags']).toEqual(['a', 'b'])
  })

  it('子键的值也能是块标量', () => {
    const fm = parseFrontmatter('---\nm:\n  note: |\n    第一行\n    第二行\n  after: x\n---\n')
    expect(fm.data['m.note']).toBe('第一行\n第二行\n')
    expect(fm.data['m.after']).toBe('x')
  })

  it('★ 子键里的 __proto__ 照样被拒', () => {
    const fm = parseFrontmatter('---\nm:\n  __proto__: polluted\n---\n')
    expect(({} as Record<string, unknown>).polluted).toBeUndefined()
    expect(fm.data).not.toHaveProperty('m.__proto__')
    expect(fm.skipped.join()).toContain('安全')
  })

  it('空值的父键没有子键时,仍然是一个空值', () => {
    const fm = parseFrontmatter('---\nm:\nname: x\n---\n')
    expect(fm.data.m).toBe('')
    expect(fm.data.name).toBe('x')
  })
})

describe('parseFrontmatter · 引号', () => {
  it('单引号和双引号都去掉', () => {
    const fm = parseFrontmatter(`---\na: 'hello'\nb: "world"\n---\n`)
    expect(fm.data.a).toBe('hello')
    expect(fm.data.b).toBe('world')
  })

  it("单引号里的 '' 是一个字面单引号", () => {
    const fm = parseFrontmatter(`---\na: 'it''s'\n---\n`)
    expect(fm.data.a).toBe("it's")
  })

  it('双引号里的 \\n 变成真换行', () => {
    const fm = parseFrontmatter('---\na: "x\\ny"\n---\n')
    expect(fm.data.a).toBe('x\ny')
  })

  it('引号不成对时原样保留,不猜', () => {
    const fm = parseFrontmatter(`---\na: 'unterminated\n---\n`)
    expect(fm.data.a).toBe("'unterminated")
  })
})

describe('parseFrontmatter · 列表', () => {
  it('流式序列 [a, b]', () => {
    const fm = parseFrontmatter('---\ntools: [Read, Grep, Glob]\n---\n')
    expect(fm.data.tools).toEqual(['Read', 'Grep', 'Glob'])
  })

  it('流式序列里的引号也去掉', () => {
    const fm = parseFrontmatter(`---\ntools: ['Read', "Grep"]\n---\n`)
    expect(fm.data.tools).toEqual(['Read', 'Grep'])
  })

  it('块式序列', () => {
    const fm = parseFrontmatter('---\ntools:\n  - Read\n  - Grep\n---\n')
    expect(fm.data.tools).toEqual(['Read', 'Grep'])
  })

  it('块式序列不缩进也认', () => {
    const fm = parseFrontmatter('---\ntools:\n- Read\n- Grep\n---\n')
    expect(fm.data.tools).toEqual(['Read', 'Grep'])
  })

  it('块式序列之后还能接别的键', () => {
    const fm = parseFrontmatter('---\ntools:\n  - Read\nname: x\n---\n')
    expect(fm.data.tools).toEqual(['Read'])
    expect(fm.data.name).toBe('x')
  })

  it('没有对应键的顶层列表项被记下来', () => {
    const fm = parseFrontmatter('---\n- 孤儿\n---\n')
    expect(fm.skipped.join()).toContain('列表项')
  })

  it('空列表值保持空串,不是 undefined', () => {
    const fm = parseFrontmatter('---\ntools:\n---\n')
    expect(fm.data.tools).toBe('')
  })
})

describe('parseFrontmatter · 不支持的语法照实记下', () => {
  it('两层以上的嵌套被跳过', () => {
    const fm = parseFrontmatter('---\nname: x\na:\n  b:\n    c: 1\n---\n')
    expect(fm.data.name).toBe('x')
    expect(fm.data).not.toHaveProperty('a.b.c')
    expect(fm.skipped.join()).toContain('嵌套')
  })

  it('没有父键的缩进行被跳过', () => {
    const fm = parseFrontmatter('---\nname: x\n  stray: 1\n---\n')
    expect(fm.data.name).toBe('x')
    expect(fm.data).not.toHaveProperty('name.stray')
    expect(fm.skipped.join()).toContain('父键')
  })

  it('锚点和别名被跳过', () => {
    const fm = parseFrontmatter('---\na: &anchor v\nb: *anchor\n---\n')
    expect(fm.skipped.filter((s) => s.includes('锚点'))).toHaveLength(2)
    expect(fm.data).not.toHaveProperty('a')
  })

  it('不合法的键名被跳过', () => {
    const fm = parseFrontmatter('---\n9bad: v\nname: x\n---\n')
    expect(fm.data).not.toHaveProperty('9bad')
    expect(fm.data.name).toBe('x')
    expect(fm.skipped.join()).toContain('键名')
  })

  it('不是键值形状的行被跳过', () => {
    const fm = parseFrontmatter('---\n随便一行字\nname: x\n---\n')
    expect(fm.data.name).toBe('x')
    expect(fm.skipped.join()).toContain('键: 值')
  })
})

describe('parseFrontmatter · 没有前置块', () => {
  it('首行不是 --- 时,原文就是正文', () => {
    const src = '# 标题\n内容'
    const fm = parseFrontmatter(src)
    expect(fm.body).toBe(src)
    expect(Object.keys(fm.data)).toHaveLength(0)
  })

  it('空字符串不炸', () => {
    expect(parseFrontmatter('').body).toBe('')
  })

  /** ★ 吞掉正文的话,Skill 会变成「有 frontmatter 但没内容」—— 加载器判定它有效,模型收到一份空指令 */
  it('★ 没有结束标记时整块按正文处理,不把整个文件吞成前置块', () => {
    const src = '---\nname: x\n还有很多正文'
    const fm = parseFrontmatter(src)
    expect(fm.body).toBe(src)
    expect(fm.data).not.toHaveProperty('name')
    expect(fm.skipped.join()).toContain('结束标记')
  })

  it('★ 任何输入都不抛异常', () => {
    const nasty = [
      '---',
      '---\n',
      '---\n---',
      '---\n:\n---\n',
      '---\n: v\n---\n',
      '---\na:\n- \n---\n',
      '---\n[\n---\n',
      '-'.repeat(10_000),
      '---\n' + 'a: b\n'.repeat(5000) + '---\n'
    ]
    for (const s of nasty) expect(() => parseFrontmatter(s), JSON.stringify(s.slice(0, 20))).not.toThrow()
  })
})

describe('parseFrontmatter · BOM 与 CRLF', () => {
  it('BOM 不影响首行判断', () => {
    const fm = parseFrontmatter('\uFEFF---\nname: x\n---\nbody')
    expect(fm.data.name).toBe('x')
    expect(fm.body).toBe('body')
  })

  it('CRLF 的文件正常解析,且值里不留 \\r', () => {
    const fm = parseFrontmatter('---\r\nname: x\r\ndescription: y\r\n---\r\nbody\r\n')
    expect(fm.data.name).toBe('x')
    expect(fm.data.description).toBe('y')
    expect(fm.body).not.toContain('\r')
  })
})

// ────────────────────────── 防线 ──────────────────────────

describe('★ parseFrontmatter · 原型污染', () => {
  /**
   * ★ 这一条是这个模块存在的最直接理由。
   *
   * 一份从 git 装来的 SKILL.md 里写一行 `__proto__: polluted` 是零成本的。
   * 只靠 `Object.create(null)` 不够 —— `data` 会被下游拷进普通对象、展开进
   * 字面量、`JSON.parse(JSON.stringify(...))` 转一圈,只要中途落到一个
   * **有原型**的对象上,赋值就真的污染了全局。所以这三个键要显式拒绝。
   */
  it('★ __proto__ 不进 data,也没有污染任何东西', () => {
    const fm = parseFrontmatter('---\n__proto__: polluted\nname: x\n---\n')
    expect(fm.data).not.toHaveProperty('__proto__')
    expect(({} as Record<string, unknown>).polluted).toBeUndefined()
    expect(Object.prototype).not.toHaveProperty('polluted')
    // 正常的键不受影响
    expect(fm.data.name).toBe('x')
    expect(fm.skipped.join()).toContain('安全')
  })

  it('★ constructor 与 prototype 一样拒绝', () => {
    const fm = parseFrontmatter('---\nconstructor: x\nprototype: y\n---\n')
    expect(fm.data).not.toHaveProperty('constructor')
    expect(fm.data).not.toHaveProperty('prototype')
    expect(fm.skipped).toHaveLength(2)
  })

  it('★ 把 data 拷进普通对象之后也污染不了', () => {
    const fm = parseFrontmatter('---\n__proto__: polluted\n---\n')
    const copy: Record<string, unknown> = { ...fm.data }
    for (const [k, v] of Object.entries(fm.data)) copy[k] = v
    expect(({} as Record<string, unknown>).polluted).toBeUndefined()
  })

  it('data 本身没有原型 —— 双保险', () => {
    const fm = parseFrontmatter('---\nname: x\n---\n')
    expect(Object.getPrototypeOf(fm.data)).toBeNull()
  })
})

describe('★ parseFrontmatter · 尺寸闸门', () => {
  it(`超过 ${String(FM_LIMITS.FM_BLOCK_MAX / 1024)}KB 的前置块只解析前面一部分,正文位置仍然对`, () => {
    const filler = Array.from({ length: 400 }, (_, i) => `k${String(i)}: ${'v'.repeat(40)}`).join('\n')
    const fm = parseFrontmatter(`---\nname: first\n${filler}\nlast: 尾巴\n---\n真正的正文`)
    expect(fm.data.name).toBe('first')
    expect(fm.data).not.toHaveProperty('last')
    expect(fm.skipped.join()).toContain('KB')
    // ★ 正文没有被前置块吃掉
    expect(fm.body).toBe('真正的正文')
  })

  it(`键的数量封顶在 ${String(FM_LIMITS.FM_KEYS_MAX)} 个`, () => {
    const many = Array.from({ length: 100 }, (_, i) => `k${String(i)}: v`).join('\n')
    const fm = parseFrontmatter(`---\n${many}\n---\n`)
    expect(Object.keys(fm.data).length).toBeLessThanOrEqual(FM_LIMITS.FM_KEYS_MAX)
    expect(fm.skipped.join()).toContain('键的数量')
  })

  it(`单个值封顶在 ${String(FM_LIMITS.FM_VALUE_MAX / 1024)}KB`, () => {
    // 5000 字符:超过值上限(4KB),但整块仍在块上限(8KB)以内 —— 这样触发的是值那道闸
    const fm = parseFrontmatter(`---\ndescription: ${'x'.repeat(5000)}\n---\n`)
    expect(String(fm.data.description)).toHaveLength(FM_LIMITS.FM_VALUE_MAX)
    expect(fm.skipped.join()).toContain('截断')
  })

  /**
   * ★ 两道闸是有先后的,这一条把先后钉住。
   *
   * 一个 20KB 的单行值先撞上**块**上限(8KB),于是那一行整个不解析 ——
   * 而不是被截成 4KB 存进去。写成后者也说得通,但两种行为不能同时成立,
   * 而「块预算先生效」才和「前置块整体有个头」这个意图一致。
   */
  it('★ 单行就撑爆块预算时,那一行整个不解析(块闸在值闸之前)', () => {
    const fm = parseFrontmatter(`---\ndescription: ${'x'.repeat(20_000)}\nname: x\n---\n正文`)
    expect(fm.data).not.toHaveProperty('description')
    expect(fm.skipped.join()).toContain('KB')
    expect(fm.body).toBe('正文')
  })

  it(`列表封顶在 ${String(FM_LIMITS.FM_LIST_MAX)} 项`, () => {
    const items = Array.from({ length: 200 }, (_, i) => `- t${String(i)}`).join('\n')
    const fm = parseFrontmatter(`---\ntools:\n${items}\n---\n`)
    expect(fm.data.tools).toHaveLength(FM_LIMITS.FM_LIST_MAX)
  })

  it('没有结束标记的巨大文件也很快返回', () => {
    const t0 = Date.now()
    parseFrontmatter(`---\n${'a: b\n'.repeat(200_000)}`)
    expect(Date.now() - t0).toBeLessThan(1000)
  })
})

describe('★ parseFrontmatter · 控制字符', () => {
  /**
   * ★ description 直接进系统提示词。一个 ANSI 转义或 NUL 落在里面之后,
   * 谁也说不清下游(日志、终端、转录渲染)会拿它做什么。
   * 这里用 `String.fromCharCode` 造字符,不在源码里写字面控制字符 ——
   * 后者在编辑器和 diff 里都是隐形的。
   */
  it('★ 值里的控制字符被削掉', () => {
    const ESC = String.fromCharCode(27)
    const NUL = String.fromCharCode(0)
    const fm = parseFrontmatter(`---\ndescription: 前${ESC}[31m红${NUL}后\n---\n`)
    const d = String(fm.data.description)
    expect(d).not.toContain(ESC)
    expect(d).not.toContain(NUL)
    expect(d).toContain('红')
  })

  it('列表项里的控制字符也削', () => {
    const NUL = String.fromCharCode(0)
    const fm = parseFrontmatter(`---\ntools: [Re${NUL}ad]\n---\n`)
    expect((fm.data.tools as string[])[0]).toBe('Read')
  })
})

// ────────────────────────── 取值助手 ──────────────────────────

describe('fmString', () => {
  const fm = parseFrontmatter('---\na: hello\nb: [x]\nc: "   "\n---\n')

  it('取到字符串', () => {
    expect(fmString(fm, 'a')).toBe('hello')
  })

  it('★ 写成列表时返回 undefined —— 拼起来是猜,猜错比缺失更难查', () => {
    expect(fmString(fm, 'b')).toBeUndefined()
  })

  it('全是空白等于没有', () => {
    expect(fmString(fm, 'c')).toBeUndefined()
  })

  it('不存在的键返回 undefined', () => {
    expect(fmString(fm, 'nope')).toBeUndefined()
  })
})

describe('fmList', () => {
  /**
   * ★ 这一组钉的是「两种写法必须合流」。
   *
   * 用户会把 Claude Code 的 agent 文件原样粘过来,里面写的是
   * `tools: Read, Grep, Glob`(一个裸标量);而 `allowed-tools` 更常写成
   * `[Read, Grep]`。两种都得认成同一个东西 —— 不然子代理会带着
   * 一个叫 "Read, Grep, Glob" 的工具名去查表,查不到,然后带着空工具表跑起来。
   */
  it('★ YAML 数组与逗号分隔字符串给出同一个答案', () => {
    const a = parseFrontmatter('---\ntools: [Read, Grep, Glob]\n---\n')
    const b = parseFrontmatter('---\ntools: Read, Grep, Glob\n---\n')
    const c = parseFrontmatter('---\ntools:\n  - Read\n  - Grep\n  - Glob\n---\n')
    expect(fmList(a, 'tools')).toEqual(['Read', 'Grep', 'Glob'])
    expect(fmList(b, 'tools')).toEqual(fmList(a, 'tools'))
    expect(fmList(c, 'tools')).toEqual(fmList(a, 'tools'))
  })

  it('单项也是列表', () => {
    expect(fmList(parseFrontmatter('---\ntools: Read\n---\n'), 'tools')).toEqual(['Read'])
  })

  it('空值和不存在都返回 undefined —— 区分「没写」与「写了空的」不值得', () => {
    expect(fmList(parseFrontmatter('---\ntools:\n---\n'), 'tools')).toBeUndefined()
    expect(fmList(parseFrontmatter('---\na: b\n---\n'), 'tools')).toBeUndefined()
  })

  it('多余的逗号和空白被丢掉', () => {
    expect(fmList(parseFrontmatter('---\ntools: Read, , Grep,\n---\n'), 'tools')).toEqual([
      'Read',
      'Grep'
    ])
  })
})

describe('fmBool', () => {
  it('认得几种常见写法', () => {
    const fm = parseFrontmatter(
      '---\na: true\nb: false\nc: yes\nd: no\ne: TRUE\nf: on\ng: off\nh: 1\ni: 0\n---\n'
    )
    expect(fmBool(fm, 'a')).toBe(true)
    expect(fmBool(fm, 'b')).toBe(false)
    expect(fmBool(fm, 'c')).toBe(true)
    expect(fmBool(fm, 'd')).toBe(false)
    expect(fmBool(fm, 'e')).toBe(true)
    expect(fmBool(fm, 'f')).toBe(true)
    expect(fmBool(fm, 'g')).toBe(false)
    expect(fmBool(fm, 'h')).toBe(true)
    expect(fmBool(fm, 'i')).toBe(false)
  })

  it('★ 认不出返回 undefined,不当成 false', () => {
    // 当成 false 的话,`globalEnabled: maybe` 会静默变成「关闭」,
    // 而用户看到的是「我明明写了」。undefined 让调用方能选自己的默认值。
    expect(fmBool(parseFrontmatter('---\na: 随便\n---\n'), 'a')).toBeUndefined()
    expect(fmBool(parseFrontmatter('---\na: b\n---\n'), 'nope')).toBeUndefined()
  })

  it('解析时 true 仍然是字符串 —— coerce 只在 fmBool 里发生', () => {
    expect(parseFrontmatter('---\na: true\n---\n').data.a).toBe('true')
  })
})

/**
 * 序列化 —— 它存在的全部意义是「写回去还能原样读出来」，所以这一节几乎每条
 * 都是往返断言，而不是在比对生成的文本长什么样。
 *
 * ★ 断言生成文本的写法（`expect(out).toBe('---\\nname: x\\n---\\n')`）是脆的：
 *   它把缩进、引号策略、键序这些实现细节全锁死了，改一次策略就要重写一片测试，
 *   而真正要保的东西——**值不丢、不变形**——反而没被直接断言。
 */
describe('serializeFrontmatter · 往返', () => {
  /** 往返一次，回到 data。 */
  const round = (data: Record<string, string | string[]>, body = 'body'): Record<string, string | string[]> =>
    parseFrontmatter(serializeFrontmatter(data, body)).data as Record<string, string | string[]>

  it('★ 一层嵌套能往返 —— 编辑器保存不该吃掉 metadata 整块', () => {
    const data = {
      name: 'programming-tutor',
      'metadata.author': 'Samuel Kahessay',
      'metadata.version': '1.0.1',
      'metadata.tags': ['python', 'javascript']
    }
    expect(round(data)).toEqual(data)
    // 子键必须连成一片:写成两个 `metadata:` 块的话,读回来只剩后一个
    const text = serializeFrontmatter(data, '')
    expect(text.split('\n').filter((l) => l === 'metadata:')).toHaveLength(1)
  })

  it('同名的标量与 map 同在时,写 map 那一半', () => {
    // 两个都写出去的话读回来只会剩一个,而标量那半本来就是解析时的占位
    expect(round({ m: '', 'm.a': 'x' })).toEqual({ 'm.a': 'x' })
  })

  it('三层的键写不回去,直接不写 —— 写了也读不回来', () => {
    expect(round({ name: 'x', 'a.b.c': 'v' })).toEqual({ name: 'x' })
  })

  it('普通标量和列表原样回来', () => {
    const data = { name: 'commit', description: '写提交信息', tools: ['Read', 'Grep'] }
    expect(round(data)).toEqual(data)
  })

  it('★ 含字面反斜杠的值能往返', () => {
    // 这条是 `unquote` 那个链式 replace 顺序 bug 的回归保护：曾经 `a\nb`
    // （反斜杠 + 字母 n）写出去再读回来会变成 `a` + 反斜杠 + 真换行。
    const data = { re: 'a\\nb', win: 'C:\\Users\\me', tail: '结尾一个反斜杠\\' }
    expect(round(data)).toEqual(data)
  })

  it('换行、tab、引号都能往返', () => {
    const data = { multi: '第一行\n第二行', tabbed: 'a\tb', quoted: '他说"好"', both: 'x\n"y"\\z' }
    expect(round(data)).toEqual(data)
  })

  it('会被解析器当成别的东西的裸值，都能往返', () => {
    const data = {
      empty: '',
      spaced: '  前后有空格  ',
      looksList: '[a, b]',
      anchor: '&ref',
      alias: '*ref',
      block: '|',
      block2: '>-2',
      // ★ 缩进指示在前的写法也是块标量头,needsQuote 漏了它就会在读回来时吃掉后面几行
      block3: '|2-',
      block4: '>2',
      dash: '- 看着像列表项',
      hash: '#不是注释',
      colon: 'foo: bar',
      truthy: 'true',
      numeric: '123'
    }
    expect(round(data)).toEqual(data)
  })

  it('★ 列表项含逗号时退回块式 —— 流式是按逗号切的', () => {
    const data = { tools: ['a, b', 'c'] }
    expect(round(data)).toEqual(data)
    expect(serializeFrontmatter(data, '')).toContain('  - ')
  })

  it('列表项含方括号或首尾空白也退块式', () => {
    expect(round({ k: ['[x]', 'y'] })).toEqual({ k: ['[x]', 'y'] })
    expect(round({ k: [' 前后空格 ', 'y'] })).toEqual({ k: [' 前后空格 ', 'y'] })
  })

  it('空列表往返成空列表', () => {
    expect(round({ k: [] })).toEqual({ k: [] })
  })

  it('★ 正文不累积空行 —— 反复往返 body 必须收敛', () => {
    // 序列化时若在 `---` 后补一个空行，而 body 又来自上一次 parse，
    // 每存一次就会多一个前导换行，一直涨。
    const data = { a: 'x' }
    let body = '\n正文\n'
    for (let i = 0; i < 5; i++) body = parseFrontmatter(serializeFrontmatter(data, body)).body
    expect(body).toBe('\n正文\n')
  })

  it('CRLF 的正文归一成 LF', () => {
    expect(parseFrontmatter(serializeFrontmatter({ a: 'x' }, '一\r\n二')).body).toBe('一\n二')
  })

  it('未被识别的键原样带回来 —— 表单只覆盖它认识的那几个', () => {
    // 从 Claude Code 粘过来的 agent 文件里常有 `color: blue` 这类本应用不认识的键。
    const data = { description: '必填', color: 'blue', 'argument-hint': '<path>' }
    expect(round(data)).toEqual(data)
  })

  it('order 决定键序，其余按字母序 —— 每存一次换一次顺序会把 diff 变成噪音', () => {
    const out = serializeFrontmatter(
      { zeta: '1', description: '2', alpha: '3', name: '4' },
      '',
      { order: ['name', 'description'] }
    )
    const keys = out.split('\n').filter((l) => /^[a-z]/.test(l)).map((l) => l.slice(0, l.indexOf(':')))
    expect(keys).toEqual(['name', 'description', 'alpha', 'zeta'])
  })

  it('__proto__ 一类的键不写出去', () => {
    const out = serializeFrontmatter({ __proto__: 'polluted', ok: 'x' } as Record<string, string>, '')
    expect(out).not.toContain('polluted')
    expect(out).toContain('ok: x')
  })

  it('undefined 的键跳过，不写成空值', () => {
    const out = serializeFrontmatter({ a: 'x', b: undefined }, '')
    expect(out).toContain('a: x')
    expect(out).not.toContain('b:')
  })

  it('一个键都没有时不写前置块 —— 命令文件允许没有 frontmatter', () => {
    expect(serializeFrontmatter({}, '只有正文')).toBe('只有正文')
  })

  it('键名不合法的直接跳过', () => {
    const out = serializeFrontmatter({ 'bad key': 'x', good: 'y' } as Record<string, string>, '')
    expect(out).not.toContain('bad key')
    expect(out).toContain('good: y')
  })
})
