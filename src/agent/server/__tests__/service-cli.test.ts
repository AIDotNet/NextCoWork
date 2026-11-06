import { existsSync, mkdtempSync, readFileSync, realpathSync, rmSync, statSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { parseServiceArgs, runServiceCli, ServiceUsageError } from '../service-cli'

/*
  安装计划按目标平台校验路径:Linux 参数用例始终使用 POSIX 路径,即使在 Windows 上运行。
  只有真正落盘的 apply 用例使用本机平台和临时目录;realpath 展开 macOS 别名与 Windows 8.3 短名。
*/
const WINDOWS = process.platform === 'win32'
const env = { platform: 'linux' as const, nodePath: '/opt/node/bin/node', binPath: '/opt/ncw/bin.mjs', isAdmin: false }
const host = { platform: (WINDOWS ? 'win32' : 'linux') as 'win32' | 'linux',
  nodePath: WINDOWS ? 'C:\\node\\node.exe' : '/opt/node/bin/node',
  binPath: WINDOWS ? 'C:\\ncw\\bin.mjs' : '/opt/ncw/bin.mjs', isAdmin: false }
let dir = ''
const lines: string[] = []
const io = { out: (line: string) => { lines.push(line) }, err: (line: string) => { lines.push(line) } }
beforeEach(() => { dir = realpathSync.native(mkdtempSync(join(tmpdir(), 'ncw-service-cli-'))); lines.length = 0 })
afterEach(() => { rmSync(dir, { recursive: true, force: true }) })

describe('service command line', () => {
  it('parses strictly and requires a user and data dir', () => {
    expect(parseServiceArgs(['plan', '--user', 'ncw', '--data-dir', '/d', '--advertise', 'a.lan', '--advertise', 'b.lan'], env).options.advertise).toEqual(['a.lan', 'b.lan'])
    for (const bad of [[], ['launch'], ['plan'], ['plan', '--user', 'ncw'], ['plan', '--data-dir', '/d'], ['plan', '--user', 'ncw', '--data-dir', '/d', '--bogus'],
      ['plan', '--user', 'ncw', '--data-dir', '/d', '--port', 'x'], ['plan', '--user', 'ncw', '--data-dir', '/d', '--platform', 'beos']]) {
      expect(() => parseServiceArgs(bad, env)).toThrow(ServiceUsageError)
    }
  })

  it('plan prints files and commands but writes nothing and runs nothing', () => {
    runServiceCli(['plan', '--user', 'ncw', '--data-dir', '/srv/ncw/data'], io, env)
    const text = lines.join('\n')
    expect(text).toContain('# file: /etc/systemd/system/ncw-agent.service')
    expect(text).toContain('systemctl enable ncw-agent')
    expect(text).toContain('systemctl start ncw-agent')
    // 路径原样进 unit(每个 argv 各自被引号裹住),不被本机路径规则改写。
    expect(text).toContain('"/srv/ncw/data"')
    expect(existsSync('/etc/systemd/system/ncw-agent.service')).toBe(false)
  })

  it('apply refuses to write system paths without administrator rights', () => {
    expect(() => runServiceCli(['apply', '--user', 'ncw', '--data-dir', '/srv/ncw/data'], io, env)).toThrowError(expect.objectContaining({ code: 'forbidden' }))
  })

  it('apply writes under a chosen root and still enables/starts nothing', () => {
    runServiceCli(['apply', '--user', 'ncw', '--data-dir', join(dir, 'data'), '--root', join(dir, 'root')], io, host)
    // 平台不同,写出来的文件也不同(unit 里是 `User=`,WinSW 配置里是 `<username>`)。
    const relative = WINDOWS ? join('ncw', 'winsw', 'ncw-agent.xml') : join('etc', 'systemd', 'system', 'ncw-agent.service')
    const target = join(dir, 'root', relative)
    expect(readFileSync(target, 'utf8')).toContain(WINDOWS ? '<username>ncw</username>' : 'User=ncw')
    // `chmod` 在 Win32 上只翻转只读位,mode 在那里不是可断言的对象 —— 计划里的模式只在 POSIX 上验。
    if (!WINDOWS) expect(statSync(target).mode & 0o777).toBe(0o644)
    expect(lines.join('\n')).toContain('Nothing was enabled or started')
  })

  it('rejects a root user and prints uninstall commands', () => {
    expect(() => runServiceCli(['plan', '--user', 'root', '--data-dir', '/srv/ncw/data'], io, env)).toThrowError(expect.objectContaining({ code: 'invalid_request' }))
    runServiceCli(['uninstall-plan', '--user', 'ncw', '--data-dir', '/srv/ncw/data'], io, env)
    expect(lines.join('\n')).toContain('systemctl disable --now ncw-agent')
  })

  /*
    `--data-dir` 的绝对性按 `--platform` 判:Linux 的 unit 里写 `C:\…`、Windows 的 WinSW 配置里写
    `/var/lib/…` 都是注定起不来的,plan 阶段就该拦下,而不是等 systemd / 服务管理器报一个看不懂的错。
  */
  it('rejects a path that belongs to the other platform', () => {
    const linux = { ...env, platform: 'linux' as const }
    const windows = { ...host, platform: 'win32' as const }
    expect(() => runServiceCli(['plan', '--user', 'ncw', '--data-dir', 'C:\\ncw\\data'], io, linux))
      .toThrowError(expect.objectContaining({ code: 'invalid_request' }))
    expect(() => runServiceCli(['plan', '--user', 'ncw', '--data-dir', '/var/lib/ncw'], io, windows))
      .toThrowError(expect.objectContaining({ code: 'invalid_request' }))
    // 反过来:M 盘那样的 `/C:/…`(把 Windows 路径当 POSIX 路径拼出来)也拒掉。
    expect(() => runServiceCli(['plan', '--user', 'ncw', '--data-dir', '/C:/ncw/data'], io, linux))
      .toThrowError(expect.objectContaining({ code: 'invalid_request' }))
  })
})
