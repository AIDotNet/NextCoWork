/**
 * 插件契约层的测试 —— 清单校验、能力上界、菜单合并、`when` 求值。
 *
 * 这四样都是**纯函数**,而且每一样都守着一条「错了不会报错、只会悄悄变形」
 * 的规则。所以用例的写法统一是:先造一个**看起来正常**的输入,再改坏一处,
 * 断言它被挡在了哪一层。
 */
import { describe, expect, it } from 'vitest'
import {
  hasNewerVersion,
  matchesHostPermission,
  parsePluginManifest,
  parseRange,
  satisfiesEngine
} from '../manifest'
import {
  addedPermissions,
  canRequest,
  grantPermissions,
  hasPermission,
  permissionEscalated,
  sortPermissions
} from '../permission'
import {
  MAX_ITEMS_PER_PLUGIN,
  evaluateWhen,
  mergeMenuItems,
  normalizeMenuIcon,
  parseMenuGroup,
  type TabMenuItem
} from '../contribution'
import { PLUGIN_METHOD_PERMISSION, isPluginMethod } from '../protocol'

const VALID = {
  name: 'excalidraw',
  publisher: 'acme',
  displayName: 'Excalidraw',
  description: '画图',
  version: '1.0.0',
  engines: { nextcowork: '^0.2.0' },
  main: './dist/extension.js',
  l10n: './l10n',
  activationEvents: ['onCommand:excalidraw.new'],
  permissions: ['workspace.read', 'workspace.write'],
  optionalPermissions: ['net'],
  hostPermissions: ['https://libraries.excalidraw.com/*'],
  contributes: {
    commands: [{ command: 'excalidraw.new', title: '%cmd.new%', icon: 'pen-tool' }],
    menus: { 'tabBar/new': [{ command: 'excalidraw.new', group: 'create@20' }] }
  }
}

const parse = (over: Record<string, unknown> = {}): ReturnType<typeof parsePluginManifest> =>
  parsePluginManifest({ ...VALID, ...over })

describe('清单校验', () => {
  it('一份正常的清单解析得出 publisher.name 的身份', () => {
    const result = parse()
    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.manifest.id).toBe('acme.excalidraw')
    expect(result.manifest.contributes.commands[0]?.title).toBe('%cmd.new%')
  })

  it('★ 贡献点的 title 必须是 %key% —— 裸文案会绕开整个 i18n 层', () => {
    const result = parse({ contributes: { commands: [{ command: 'x', title: '新建绘图' }] } })
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.errors.some((e) => e.field.endsWith('.title'))).toBe(true)
  })

  it('★ 名字与 id 的形状被钉死 —— 大写、点、斜杠都进不来', () => {
    for (const name of ['Excalidraw', 'ex.calidraw', '../evil', '']) {
      expect(parse({ name }).ok, name).toBe(false)
    }
  })

  it('命令的 iconFile 必须是包内相对的 svg/png —— 品牌图标走这条并行通道', () => {
    const ok = parse({ contributes: { commands: [{ command: 'x', title: '%cmd.x%', iconFile: 'assets/claude.svg' }] } })
    expect(ok.ok).toBe(true)
    if (ok.ok) expect(ok.manifest.contributes.commands[0]?.iconFile).toBe('assets/claude.svg')
    for (const iconFile of ['/etc/logo.svg', '../evil.svg', 'assets/logo.gif']) {
      const result = parse({ contributes: { commands: [{ command: 'x', title: '%cmd.x%', iconFile }] } })
      expect(result.ok, iconFile).toBe(false)
      if (result.ok) continue
      expect(result.errors.some((e) => e.field.endsWith('.iconFile')), iconFile).toBe(true)
    }
  })

  it('★ main 必须是包内相对路径的单文件 ESM', () => {
    for (const main of ['/etc/passwd', '../../x.js', 'C:\\x.js', './dist/extension.cjs', '']) {
      expect(parse({ main }).ok, main).toBe(false)
    }
  })

  it('★ 认不出的能力整份拒绝,而不是悄悄忽略', () => {
    const result = parse({ permissions: ['workspace.read', 'root'] })
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.errors[0]?.message).toContain('unknown permission')
  })

  it('★ 没有通配能力 —— `*` 不是一条能力', () => {
    expect(parse({ permissions: ['*'] }).ok).toBe(false)
  })

  it('认不出的激活事件被拒', () => {
    expect(parse({ activationEvents: ['onLanguage:ts'] }).ok).toBe(false)
    expect(parse({ activationEvents: ['onStartup'] }).ok).toBe(true)
  })

  it('engines 必填且必须是认得的 range', () => {
    expect(parse({ engines: {} }).ok).toBe(false)
    expect(parse({ engines: { nextcowork: '>=1.0 <2.0' } }).ok).toBe(false)
  })

  it('hostPermissions 没有 net 时只是警告,不拒绝整份', () => {
    const result = parse({ permissions: ['workspace.read'], optionalPermissions: [] })
    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.warnings.some((w) => w.field === 'hostPermissions')).toBe(true)
  })

  /*
    `allowedCommands` —— `process.exec` 的命令白名单。

    ★ 这个字段是**后补的**:`rpc.ts` 的注释一直说「清单里可选的命令白名单」,
    但清单里从来没有这个字段,`manager.ts` 那一侧写死成 `[]`,于是任何插件的
    exec 都在参数门被静默拒死。补上之后,校验必须和 `capabilities.ts` 的归一
    规则**对齐**:那边比的是去掉目录与 `.exe/.cmd/.bat/.ps1` 之后的 stem,
    所以作者写 `/usr/bin/git` 永远匹配不上 —— 与其让它静默失效,不如在装载时
    就报错。
  */
  it('裸可执行名通过', () => {
    const result = parse({ permissions: ['process'], allowedCommands: ['git', 'cargo', 'node'] })
    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.manifest.allowedCommands).toEqual(['git', 'cargo', 'node'])
  })

  it('★ 带路径或带 .exe 的条目让整份清单作废 —— 不默默剥掉,也不默默丢掉', () => {
    // 这里是**错误**而不是警告(对比上面的 hostPermissions):默默丢掉的话,
    // 作者只会在运行时看到一句「命令不在白名单里」,而清单上明明写着。
    for (const command of ['/usr/bin/git', 'bin\\git', 'git.exe', 'git.cmd']) {
      const result = parse({ permissions: ['process'], allowedCommands: [command] })
      expect(result.ok, command).toBe(false)
      if (result.ok) continue
      expect(result.errors.some((e) => e.field === 'allowedCommands'), command).toBe(true)
    }
  })

  it('★ 声明了 allowedCommands 却没声明 process 能力 —— 警告,不作废整份', () => {
    const result = parse({ allowedCommands: ['git'] })
    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.warnings.some((w) => w.field === 'allowedCommands')).toBe(true)
  })

  it('没写 allowedCommands 时是空数组,不是 undefined —— 空数组 = 一条都不许跑', () => {
    const result = parse()
    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.manifest.allowedCommands).toEqual([])
  })

  it('认得字段名但没实现的贡献点进 unsupported,不报错', () => {
    const result = parse({ contributes: { chatRenderers: [{ id: 'x' }] } })
    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.manifest.contributes.unsupported).toContain('chatRenderers')
  })

  it('工具贡献解析 shape 与 card 模板', () => {
    const result = parse({
      contributes: {
        tools: [{ name: 'make_thing', title: '%tool.make%', shape: 'mutate', card: { title: '%tool.make.card%', summary: '%tool.make.sum%' } }]
      }
    })
    expect(result.ok).toBe(true)
    if (!result.ok) return
    const tool = result.manifest.contributes.tools[0]
    expect(tool?.shape).toBe('mutate')
    expect(tool?.card).toEqual({ title: '%tool.make.card%', summary: '%tool.make.sum%' })
  })

  it('★ 未知 shape 与裸文案 card 模板被拒', () => {
    expect(parse({ contributes: { tools: [{ name: 't', title: '%t%', shape: 'weird' }] } }).ok).toBe(false)
    expect(parse({ contributes: { tools: [{ name: 't', title: '%t%', card: { title: '造东西' } }] } }).ok).toBe(false)
  })

  it('cardViews 解析 viewType + 包内相对路径,越界路径被拒', () => {
    const ok = parse({ contributes: { cardViews: [{ viewType: 'task.card', path: './dist/card.html' }] } })
    expect(ok.ok).toBe(true)
    if (!ok.ok) return
    expect(ok.manifest.contributes.cardViews[0]).toEqual({ viewType: 'task.card', path: './dist/card.html' })
    expect(parse({ contributes: { cardViews: [{ viewType: 'x', path: '../evil.html' }] } }).ok).toBe(false)
  })

  it('dependencies 解析 pluginId→range;自依赖 / 坏 id / 坏 range 被拒', () => {
    const ok = parse({ dependencies: { 'acme.other': '^1.0.0' } })
    expect(ok.ok).toBe(true)
    if (!ok.ok) return
    expect(ok.manifest.dependencies).toEqual({ 'acme.other': '^1.0.0' })
    expect(parse({ dependencies: { 'acme.excalidraw': '^1.0.0' } }).ok).toBe(false) // 自依赖(VALID 的 id)
    expect(parse({ dependencies: { 'Bad Id': '^1.0.0' } }).ok).toBe(false)
    expect(parse({ dependencies: { 'acme.other': 'not-a-range' } }).ok).toBe(false)
  })

  it('不是对象的输入不会把解析器打崩', () => {
    for (const raw of [null, 42, 'x', []]) expect(parsePluginManifest(raw).ok).toBe(false)
  })
})

describe('engines range', () => {
  it('^0.x 锁到 minor —— 1.0 之前 API 明确可 break', () => {
    expect(satisfiesEngine('^0.2.0', '0.2.9')).toBe(true)
    expect(satisfiesEngine('^0.2.0', '0.3.0')).toBe(false)
    expect(satisfiesEngine('^0.2.0', '0.1.9')).toBe(false)
  })

  it('^1.x 锁到 major', () => {
    expect(satisfiesEngine('^1.2.0', '1.9.9')).toBe(true)
    expect(satisfiesEngine('^1.2.0', '2.0.0')).toBe(false)
  })

  it('~ 锁到 minor,>= 只比大小,精确就是精确', () => {
    expect(satisfiesEngine('~1.2.0', '1.2.7')).toBe(true)
    expect(satisfiesEngine('~1.2.0', '1.3.0')).toBe(false)
    expect(satisfiesEngine('>=1.2.0', '9.0.0')).toBe(true)
    expect(satisfiesEngine('1.2.0', '1.2.1')).toBe(false)
  })

  it('★ 读不懂的 range 返回 false,不是放行', () => {
    expect(parseRange('latest')).toBeNull()
    expect(satisfiesEngine('latest', '1.0.0')).toBe(false)
  })
})

describe('有没有新版本', () => {
  it('高的才算新,相等和降级都不算', () => {
    expect(hasNewerVersion('0.1.2', '0.1.3')).toBe(true)
    expect(hasNewerVersion('0.1.3', '0.2.0')).toBe(true)
    expect(hasNewerVersion('0.1.3', '0.1.3')).toBe(false)
    expect(hasNewerVersion('0.1.4', '0.1.3')).toBe(false)
  })

  it('★ 手上拿着预发布版 → 正式版算更新', () => {
    // parseSemVer 丢掉 `-beta`,所以这两个在 compare 眼里是相等的 ——
    // 而这恰恰是最该提示更新的一种情况。
    expect(hasNewerVersion('1.0.0-beta.1', '1.0.0')).toBe(true)
    // 反过来不算:正式版的用户不该被劝退回预发布
    expect(hasNewerVersion('1.0.0', '1.0.0-beta.1')).toBe(false)
  })

  it('★ 读不懂 / 没有版本号一律 false —— 宁可不提示,也不提示一次必然失败的更新', () => {
    expect(hasNewerVersion('0.1.2', 'latest')).toBe(false)
    expect(hasNewerVersion('nightly', '0.1.3')).toBe(false)
    expect(hasNewerVersion('0.1.2', null)).toBe(false)
    expect(hasNewerVersion('0.1.2', undefined)).toBe(false)
  })
})

describe('hostPermissions 匹配', () => {
  const patterns = ['https://api.example.com/v1/*', 'https://cdn.example.com/*']

  it('域名与路径前缀都要对上', () => {
    expect(matchesHostPermission(patterns, 'https://api.example.com/v1/items')).toBe(true)
    expect(matchesHostPermission(patterns, 'https://api.example.com/v2/items')).toBe(false)
    expect(matchesHostPermission(patterns, 'https://cdn.example.com/a/b/c')).toBe(true)
  })

  it('★ 子域名不算命中 —— 前缀匹配主机名是最常见的一种越权', () => {
    expect(matchesHostPermission(patterns, 'https://evil.api.example.com/v1/x')).toBe(false)
    expect(matchesHostPermission(patterns, 'https://api.example.com.evil.com/v1/x')).toBe(false)
  })

  it('★ 明文 http 一律不匹配', () => {
    expect(matchesHostPermission(['https://api.example.com/*'], 'http://api.example.com/x')).toBe(false)
  })

  it('读不懂的 URL 不会抛', () => {
    expect(matchesHostPermission(patterns, 'not a url')).toBe(false)
  })
})

describe('能力状态', () => {
  const state = { required: ['workspace.read'] as const, optional: ['net'] as const, granted: ['workspace.read'] as const }

  it('★ request 只能要清单上界之内的 —— 之外的直接拒,不弹窗', () => {
    expect(canRequest(state, ['net'])).toBe(true)
    expect(canRequest(state, ['process'])).toBe(false)
    expect(canRequest(state, ['net', 'process'])).toBe(false)
  })

  it('授予时越界的那些被丢掉,不会因为一条越界就整体失败', () => {
    const next = grantPermissions(state, ['net', 'process'])
    expect(hasPermission(next, 'net')).toBe(true)
    expect(hasPermission(next, 'process')).toBe(false)
  })

  it('要多条时缺一条就是没有', () => {
    expect(hasPermission(state, ['workspace.read'])).toBe(true)
    expect(hasPermission(state, ['workspace.read', 'net'])).toBe(false)
  })

  it('★ 必选能力变大 = 扩权,要重新批;可选变大不算', () => {
    expect(permissionEscalated(['workspace.read'], ['workspace.read', 'process'])).toBe(true)
    expect(permissionEscalated(['workspace.read', 'process'], ['workspace.read'])).toBe(false)
    expect(addedPermissions(['workspace.read'], ['workspace.read', 'process'])).toEqual(['process'])
  })

  it('排序稳定 —— 授权弹窗里的条目不该换位置', () => {
    expect(sortPermissions(['net', 'workspace.read'])).toEqual(['workspace.read', 'net'])
    expect(sortPermissions(['workspace.read', 'net'])).toEqual(['workspace.read', 'net'])
  })
})

describe('菜单合并', () => {
  const builtin: TabMenuItem[] = [
    { id: 'builtin.chat', titleKey: 'tabMenu.chat', icon: 'message-square', group: 'create', order: 10, action: { kind: 'openTab', tabKind: 'chat' } },
    { id: 'builtin.terminal', titleKey: 'tabMenu.terminal', icon: 'terminal', group: 'tools', order: 10, action: { kind: 'openTab', tabKind: 'terminal' } }
  ]

  const contributed = (n: number, group = 'create@1'): TabMenuItem[] =>
    Array.from({ length: n }, (_, i) => ({
      id: `acme.demo:cmd${String(i)}`,
      titleKey: `plugin.acme.demo.cmd${String(i)}`,
      icon: 'puzzle' as const,
      pluginId: 'acme.demo',
      ...parseMenuGroup(group),
      action: { kind: 'command' as const, commandId: `cmd${String(i)}` }
    }))

  it('★ 插件项排在同组内置项之后 —— 写 create@1 也抢不走第一位', () => {
    const merged = mergeMenuItems(builtin, contributed(1))
    expect(merged.items[0]?.id).toBe('builtin.chat')
    expect(merged.items.map((i) => i.id)).toEqual(['builtin.chat', 'acme.demo:cmd0', 'builtin.terminal'])
  })

  it('★ 单插件最多 3 项,溢出的折叠而不是丢掉', () => {
    const merged = mergeMenuItems(builtin, contributed(5))
    expect(merged.items.filter((i) => i.pluginId === 'acme.demo')).toHaveLength(MAX_ITEMS_PER_PLUGIN)
    expect(merged.overflow[0]?.items).toHaveLength(2)
  })

  it('认不出的 group 归到 plugin 组,不是消失', () => {
    expect(parseMenuGroup('creat@20').group).toBe('plugin')
    expect(parseMenuGroup(undefined).group).toBe('plugin')
    expect(parseMenuGroup('create@20')).toEqual({ group: 'create', order: 20 })
  })

  it('★ 图标是闭集 —— 认不出的回落到拼图块,不是留空也不是任意字符串', () => {
    expect(normalizeMenuIcon('pen-tool')).toBe('pen-tool')
    expect(normalizeMenuIcon('<svg onload=alert(1)>')).toBe('puzzle')
    expect(normalizeMenuIcon(undefined)).toBe('puzzle')
  })

  it('同组同序时按 id 定序 —— 菜单不能跟着目录遍历顺序抖', () => {
    const a = mergeMenuItems(builtin, contributed(2)).items.map((i) => i.id)
    const b = mergeMenuItems(builtin, [...contributed(2)].reverse()).items.map((i) => i.id)
    expect(a).toEqual(b)
  })
})

describe('when 求值', () => {
  const context = { pane: 'right', resourceExtname: '.excalidraw', isDirty: true }

  it('支持 == != && || ! 与括号', () => {
    expect(evaluateWhen('pane == right', context)).toBe(true)
    expect(evaluateWhen("pane == 'main'", context)).toBe(false)
    expect(evaluateWhen('pane != main', context)).toBe(true)
    expect(evaluateWhen('pane == right && resourceExtname == .excalidraw', context)).toBe(true)
    expect(evaluateWhen('pane == main || isDirty', context)).toBe(true)
    expect(evaluateWhen('!isDirty', context)).toBe(false)
    expect(evaluateWhen('(pane == main || pane == right) && isDirty', context)).toBe(true)
  })

  it('空表达式 = 无条件显示', () => {
    expect(evaluateWhen(undefined, context)).toBe(true)
    expect(evaluateWhen('   ', context)).toBe(true)
  })

  it('★ 读不懂就返回 false —— 宁可少一项,不要让被条件挡住的操作露出来', () => {
    for (const expression of ['pane ==', '((pane', 'pane =~ /x/', 'a ? b : c']) {
      expect(evaluateWhen(expression, context), expression).toBe(false)
    }
  })

  it('★ 不走 new Function —— 表达式里的代码不会被执行', () => {
    const marker = { hit: false }
    ;(globalThis as unknown as { __whenProbe?: () => void }).__whenProbe = () => { marker.hit = true }
    evaluateWhen('__whenProbe()', context)
    expect(marker.hit).toBe(false)
    delete (globalThis as unknown as { __whenProbe?: () => void }).__whenProbe
  })

  it('查不到的 key 当作假', () => {
    expect(evaluateWhen('nonexistent', context)).toBe(false)
    expect(evaluateWhen('nonexistent == x', context)).toBe(false)
  })
})

describe('RPC 白名单', () => {
  it('★ 每个方法都明确声明了它要哪条能力', () => {
    for (const [method, permission] of Object.entries(PLUGIN_METHOD_PERMISSION)) {
      expect(permission === null || typeof permission === 'string', method).toBe(true)
    }
  })

  it('★ 不在表里的方法名一律认不出 —— 拼错方法名会得到 unknown_method', () => {
    expect(isPluginMethod('workspace.readFile')).toBe(true)
    expect(isPluginMethod('workspace.readfile')).toBe(false)
    expect(isPluginMethod('__proto__')).toBe(false)
    expect(isPluginMethod('toString')).toBe(false)
  })

  it('写类方法挂的是写能力,不是读能力', () => {
    expect(PLUGIN_METHOD_PERMISSION['workspace.writeFile']).toBe('workspace.write')
    expect(PLUGIN_METHOD_PERMISSION['workspace.deleteFile']).toBe('workspace.write')
    expect(PLUGIN_METHOD_PERMISSION['process.exec']).toBe('process')
    expect(PLUGIN_METHOD_PERMISSION['net.fetch']).toBe('net')
  })
})
