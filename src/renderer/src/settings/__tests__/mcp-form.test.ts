/**
 * MCP 表单纯逻辑的用例。
 *
 * 这些判断在弹窗里是看不见的 —— 用户只会看到「参数没生效」或者
 * 「这台服务器一直连不上」。所以每一条断言下面都写着它对应的那个症状。
 */
import { describe, expect, it } from 'vitest'
import type { McpServerConfig } from '../../../../shared/domain/mcp'
import {
  authorizationWarning,
  draftOf,
  emptyDraft,
  formatArgs,
  hasErrors,
  normalizeMcpConfig,
  parseArgs,
  parseSecretLines,
  secretValues,
  suggestId,
  toConfig,
  validateDraft
} from '../mcp-form'

describe('parseArgs', () => {
  it('按空白切,连续空白算一个', () => {
    expect(parseArgs('-y  @modelcontextprotocol/server-everything')).toEqual([
      '-y',
      '@modelcontextprotocol/server-everything'
    ])
  })

  it('空串和纯空白给空数组', () => {
    expect(parseArgs('')).toEqual([])
    expect(parseArgs('   \n\t ')).toEqual([])
  })

  /** ★ 不认引号的话,带空格的路径会被切碎,而症状是服务器启动后说找不到目录 */
  it('双引号里的空格不切', () => {
    expect(parseArgs('--dir "/Users/me/My Documents"')).toEqual([
      '--dir',
      '/Users/me/My Documents'
    ])
  })

  it('单引号同样认 —— 带 JSON 的参数靠它', () => {
    expect(parseArgs('--config \'{"a": 1}\'')).toEqual(['--config', '{"a": 1}'])
    expect(parseArgs("--msg 'hello world'")).toEqual(['--msg', 'hello world'])
  })

  /**
   * ★ **反斜杠不当转义符。** 代价是 `"{\"a\":1}"` 这种 shell 写法在这里
   * 解不出来(得改用单引号);收益是 Windows 路径 `"C:\Users\me"` 原样活着。
   * 后者更要紧 —— stdio 的 MCP 服务器在 Windows 上就是这么配的,
   * 而把用户的路径吃掉一半,报出来的错会指向完全无关的地方。
   */
  it('反斜杠原样保留,不当转义符', () => {
    expect(parseArgs('--dir "C:\\Users\\me\\Documents"')).toEqual([
      '--dir',
      'C:\\Users\\me\\Documents'
    ])
  })

  it('引号可以贴在参数中间', () => {
    expect(parseArgs('--path=/a" b"/c')).toEqual(['--path=/a b/c'])
  })

  /** 空引号是一个**空参数**,不是没有参数 —— `--flag ""` 是常见写法 */
  it('空引号产出一个空串参数', () => {
    expect(parseArgs('--flag ""')).toEqual(['--flag', ''])
  })

  it('没闭合的引号把剩下的全吃掉,不报错', () => {
    // 用户还在打字的中间态。报错会让输入框在打到一半时红一下
    expect(parseArgs('--msg "still typing')).toEqual(['--msg', 'still typing'])
  })

  it('换行和制表符也算分隔', () => {
    expect(parseArgs('-a\n-b\t-c')).toEqual(['-a', '-b', '-c'])
  })

  /** ★ 参数直接进 argv、不过 shell,所以这里不做也不该做变量展开 */
  it('$HOME 原样留着,不展开', () => {
    expect(parseArgs('--dir $HOME/x')).toEqual(['--dir', '$HOME/x'])
  })
})

describe('parseSecretLines', () => {
  it('逐行解析 KEY=VALUE', () => {
    expect(parseSecretLines('A=1\nB=2')).toEqual([
      { name: 'A', value: '1' },
      { name: 'B', value: '2' }
    ])
  })

  /** ★ 值里带 `=` 是常态(base64 的填充、连接串),只按第一个切 */
  it('只按第一个等号切', () => {
    expect(parseSecretLines('TOKEN=abc==')).toEqual([{ name: 'TOKEN', value: 'abc==' }])
  })

  it('等号两边的空格是排版,值内部的空格是值', () => {
    expect(parseSecretLines('Authorization = Bearer x y')).toEqual([
      { name: 'Authorization', value: 'Bearer x y' }
    ])
  })

  it('空行与 # 注释跳过', () => {
    expect(parseSecretLines('# 从 .env 粘来的\n\nA=1\n   \n#B=2')).toEqual([
      { name: 'A', value: '1' }
    ])
  })

  it('没有等号的行丢掉', () => {
    expect(parseSecretLines('A=1\n乱写的一行\nB=2')).toEqual([
      { name: 'A', value: '1' },
      { name: 'B', value: '2' }
    ])
  })

  it('以等号开头(没有键名)的行丢掉', () => {
    expect(parseSecretLines('=1')).toEqual([])
  })

  /** 值为空是**合法**的:编辑已有服务器时,界面就是这样显示已存键名的 */
  it('值为空保留,键名照样在', () => {
    expect(parseSecretLines('GITHUB_TOKEN=')).toEqual([{ name: 'GITHUB_TOKEN', value: '' }])
  })

  /**
   * 重复键名只留第一条。留最后一条也说得通,但那会让「我在上面改了值、
   * 下面还有一行旧的」变成静默生效 —— 而用户看得见的是上面那一行。
   */
  it('重复键名只留第一条', () => {
    expect(parseSecretLines('A=1\nA=2')).toEqual([{ name: 'A', value: '1' }])
  })
})

describe('secretValues', () => {
  /** ★ 空值 = 「别动已经存着的那个」。传空串上去会把已存的密钥清成空 */
  it('只带上真填了值的那几条', () => {
    const lines = parseSecretLines('A=1\nB=\nC=3')
    expect(secretValues(lines)).toEqual({ A: '1', C: '3' })
  })

  it('一条都没填时是空对象,不是 undefined', () => {
    expect(secretValues(parseSecretLines('A=\nB='))).toEqual({})
  })
})

describe('authorizationWarning', () => {
  /** ★ 这条测试对应的真实症状:填了裸 token,服务端报一个不相关的认证错误 */
  it('裸 token(不含空格)要警告', () => {
    expect(authorizationWarning(parseSecretLines('Authorization=abc123'))).toContain('Bearer abc123')
  })

  it('带方案前缀(含空格)不警告', () => {
    expect(authorizationWarning(parseSecretLines('Authorization=Bearer abc123'))).toBeNull()
  })

  it('键名大小写不敏感', () => {
    expect(authorizationWarning(parseSecretLines('authorization=abc123'))).not.toBeNull()
  })

  it('值为空(编辑已有服务器的常态)不警告', () => {
    expect(authorizationWarning(parseSecretLines('Authorization='))).toBeNull()
  })

  it('没有 Authorization 这一行不警告', () => {
    expect(authorizationWarning(parseSecretLines('X-API-Key=abc123'))).toBeNull()
  })
})

describe('suggestId', () => {
  it('从名字生成 slug', () => {
    expect(suggestId('GitHub MCP')).toBe('github-mcp')
  })

  it('非法字符成连字符,首尾的去掉', () => {
    expect(suggestId('  My @Server!  ')).toBe('my-server')
  })

  it('下划线原样保留', () => {
    expect(suggestId('my_server')).toBe('my_server')
  })

  /** ★ 猜不出来就空着,让用户自己填 —— id 会进工具名,编一个出来用户不会注意到 */
  it('纯中文名字猜不出来,返回空串', () => {
    expect(suggestId('文件系统')).toBe('')
  })

  it('空名字返回空串', () => {
    expect(suggestId('   ')).toBe('')
  })

  it('过长的名字截到 32 字符以内', () => {
    const id = suggestId('a'.repeat(80))
    expect(id).toHaveLength(32)
  })
})

describe('validateDraft', () => {
  const stdio = { ...emptyDraft(), name: 'X', id: 'x', command: 'npx' }

  it('填全了就没有报错', () => {
    expect(hasErrors(validateDraft(stdio, []))).toBe(false)
  })

  it('名字空着要报', () => {
    expect(validateDraft({ ...stdio, name: '  ' }, []).name).toBeDefined()
  })

  it('id 空着要报', () => {
    expect(validateDraft({ ...stdio, id: '' }, []).id).toBeDefined()
  })

  it('id 含非法字符要报', () => {
    expect(validateDraft({ ...stdio, id: 'my server' }, []).id).toBeDefined()
    expect(validateDraft({ ...stdio, id: 'a'.repeat(33) }, []).id).toBeDefined()
  })

  /** ★ 重名的 id 会覆盖掉另一台服务器 —— upsert 是按 id 落库的 */
  it('id 撞车要报', () => {
    expect(validateDraft(stdio, ['x']).id).toBeDefined()
    expect(validateDraft(stdio, ['y']).id).toBeUndefined()
  })

  it('stdio 缺命令要报', () => {
    expect(validateDraft({ ...stdio, command: '' }, []).command).toBeDefined()
  })

  it('stdio 不校验 url,http 不校验命令', () => {
    expect(validateDraft({ ...stdio, url: '这不是地址' }, []).url).toBeUndefined()
    const http = { ...stdio, transport: 'sse' as const, command: '', url: 'https://a.b/c' }
    expect(validateDraft(http, []).command).toBeUndefined()
    expect(hasErrors(validateDraft(http, []))).toBe(false)
  })

  it('http 缺地址、地址不合法都要报', () => {
    const http = { ...stdio, transport: 'streamable-http' as const, command: '' }
    expect(validateDraft({ ...http, url: '' }, []).url).toBeDefined()
    expect(validateDraft({ ...http, url: 'localhost:3000' }, []).url).toBeDefined()
  })

  /** ★ `file://` 能过 `new URL` 但对 MCP 的 HTTP 传输毫无意义,会连到一半才失败 */
  it('只放行 http 和 https', () => {
    const http = { ...stdio, transport: 'sse' as const, command: '' }
    expect(validateDraft({ ...http, url: 'file:///etc/passwd' }, []).url).toBeDefined()
    expect(validateDraft({ ...http, url: 'ws://a.b' }, []).url).toBeDefined()
    expect(validateDraft({ ...http, url: 'http://a.b' }, []).url).toBeUndefined()
  })
})

describe('toConfig', () => {
  it('stdio:命令、参数、环境变量键名', () => {
    const cfg = toConfig(
      {
        ...emptyDraft(),
        id: ' srv ',
        name: ' 我的服务器 ',
        transport: 'stdio',
        command: ' npx ',
        argsText: '-y pkg',
        secretsText: 'TOKEN=abc\nOTHER='
      },
      true
    )
    expect(cfg).toEqual({
      id: 'srv',
      name: '我的服务器',
      enabled: true,
      transport: 'stdio',
      command: 'npx',
      args: ['-y', 'pkg'],
      envNames: ['TOKEN', 'OTHER']
    })
  })

  /** ★ 密钥的**值**不能出现在这个类型里 —— 它会被序列化进 `mcp_servers.json` */
  it('产出的配置里一个密钥值都没有', () => {
    const cfg = toConfig(
      { ...emptyDraft(), id: 's', name: 'n', command: 'c', secretsText: 'TOKEN=super-secret' },
      true
    )
    expect(JSON.stringify(cfg)).not.toContain('super-secret')
  })

  it('可选字段空着就不出现,不是空串', () => {
    const cfg = toConfig({ ...emptyDraft(), id: 's', name: 'n', command: 'c' }, true)
    expect('description' in cfg).toBe(false)
    expect('cwd' in cfg).toBe(false)
  })

  it('描述和工作目录填了就带上', () => {
    const cfg = toConfig(
      { ...emptyDraft(), id: 's', name: 'n', command: 'c', description: ' 说明 ', cwd: ' /tmp ' },
      true
    )
    expect(cfg.description).toBe('说明')
    expect(cfg.transport === 'stdio' && cfg.cwd).toBe('/tmp')
  })

  it('http:地址与请求头键名', () => {
    const cfg = toConfig(
      {
        ...emptyDraft(),
        id: 's',
        name: 'n',
        transport: 'streamable-http',
        url: ' https://a.b/mcp ',
        secretsText: 'Authorization=Bearer x'
      },
      false
    )
    expect(cfg).toEqual({
      id: 's',
      name: 'n',
      enabled: false,
      transport: 'streamable-http',
      url: 'https://a.b/mcp',
      headerNames: ['Authorization']
    })
  })

  /** ★ enabled 由调用方给:改个描述不该把一台停用的服务器悄悄启用 */
  it('enabled 原样透传', () => {
    const d = { ...emptyDraft(), id: 's', name: 'n', command: 'c' }
    expect(toConfig(d, false).enabled).toBe(false)
    expect(toConfig(d, true).enabled).toBe(true)
  })
})

describe('draftOf', () => {
  const stdio: McpServerConfig = {
    id: 'srv',
    name: '服务器',
    description: '一句说明',
    enabled: true,
    transport: 'stdio',
    command: 'npx',
    args: ['-y', 'pkg'],
    envNames: ['TOKEN', 'OTHER'],
    cwd: '/tmp'
  }

  it('往返:配置 → 草稿 → 配置', () => {
    expect(toConfig(draftOf(stdio), true)).toEqual(stdio)
  })

  /**
   * ★ **取不回来的值显示成 `KEY=`。** 「凭证只写不读」的直接结果 ——
   * 这条测试在这里,是为了将来有人「顺手把值填回去」时立刻变红。
   */
  it('已存的密钥只回键名,等号后面是空的', () => {
    expect(draftOf(stdio).secretsText).toBe('TOKEN=\nOTHER=')
    expect(secretValues(parseSecretLines(draftOf(stdio).secretsText))).toEqual({})
  })

  it('参数用空格拼回去', () => {
    expect(draftOf(stdio).argsText).toBe('-y pkg')
  })

  it('http 配置往返', () => {
    const http: McpServerConfig = {
      id: 'h',
      name: 'H',
      enabled: false,
      transport: 'sse',
      url: 'https://a.b/sse',
      headerNames: ['Authorization']
    }
    expect(toConfig(draftOf(http), false)).toEqual(http)
    expect(draftOf(http).command).toBe('')
  })

  it('可选字段缺失时草稿里是空串,不是 undefined', () => {
    const bare: McpServerConfig = {
      id: 'b',
      name: 'B',
      enabled: true,
      transport: 'stdio',
      command: 'c',
      args: [],
      envNames: []
    }
    const d = draftOf(bare)
    expect(d.description).toBe('')
    expect(d.cwd).toBe('')
    expect(d.secretsText).toBe('')
  })
})

/**
 * ★ 白屏回归。库里的 JSON 原样存取、不校验形状,而编辑弹窗以前直接
 * `cfg.envNames.map` / `cfg.args.join` —— 任何一条缺字段的配置点「编辑」
 * 就是渲染期 TypeError,整个窗口白屏。这些形状都必须能打开。
 */
describe('draftOf 面对非规范形状', () => {
  const shapes: Record<string, unknown> = {
    '缺 envNames': { id: 'a', name: 'A', enabled: true, transport: 'stdio', command: 'npx', args: [] },
    '缺 args': { id: 'a', name: 'A', enabled: true, transport: 'stdio', command: 'npx', envNames: [] },
    'env 是对象': { id: 'a', name: 'A', enabled: true, transport: 'stdio', command: 'npx', args: [], env: { X: '1' } },
    '缺 headerNames': { id: 'a', name: 'A', enabled: true, transport: 'sse', url: 'https://x' },
    "transport 是 'http'、headers 是对象": { id: 'a', name: 'A', enabled: true, transport: 'http', url: 'https://x', headers: { A: 'b' } },
    '没有 transport、只有 url': { id: 'a', name: 'A', url: 'https://x' },
    'command 是数组': { id: 'a', name: 'A', command: ['npx', '-y', 'pkg'] },
    '缺 command': { id: 'a', name: 'A', transport: 'stdio' },
    '缺 name': { id: 'a', transport: 'stdio', command: 'x' },
    '完全不是对象': null
  }
  for (const [label, shape] of Object.entries(shapes)) {
    it(label, () => {
      expect(() => draftOf(shape as McpServerConfig)).not.toThrow()
    })
  }

  it('对象形式的 env / headers 只取键名,值(可能是明文 token)丢掉', () => {
    expect(draftOf(shapes['env 是对象'] as McpServerConfig).secretsText).toBe('X=')
    const http = draftOf(shapes["transport 是 'http'、headers 是对象"] as McpServerConfig)
    expect(http.transport).toBe('streamable-http')
    expect(http.secretsText).toBe('A=')
  })

  it('command 数组拆成命令 + 参数', () => {
    const d = draftOf(shapes['command 是数组'] as McpServerConfig)
    expect(d.transport).toBe('stdio')
    expect(d.command).toBe('npx')
    expect(d.argsText).toBe('-y pkg')
  })

  it('没有 transport 时按有没有 url 判断', () => {
    expect(draftOf(shapes['没有 transport、只有 url'] as McpServerConfig).url).toBe('https://x')
  })

  it('缺 name 时用 id 顶上', () => {
    expect(draftOf(shapes['缺 name'] as McpServerConfig).name).toBe('a')
  })
})

describe('normalizeMcpConfig', () => {
  it('规范形状原样返回', () => {
    const cfg: McpServerConfig = {
      id: 'x',
      name: 'X',
      enabled: false,
      workspaceId: 'w1',
      transport: 'stdio',
      command: 'npx',
      args: ['a'],
      envNames: ['K'],
      cwd: '/tmp'
    }
    expect(normalizeMcpConfig(cfg)).toEqual(cfg)
  })

  it('enabled 缺省视为开', () => {
    expect(normalizeMcpConfig({ id: 'x', name: 'X', transport: 'stdio', command: 'c' }).enabled).toBe(true)
  })
})

describe('formatArgs', () => {
  /** 症状:打开编辑什么都不改直接保存,带空格的路径被切成两个参数 */
  it('带空白的参数加引号,和 parseArgs 往返一致', () => {
    const args = ['--root', '/Users/me/My Docs', '', `{"a":1}`, "it's"]
    expect(parseArgs(formatArgs(args))).toEqual(args)
  })
})
