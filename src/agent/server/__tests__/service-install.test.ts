import { describe, expect, it } from 'vitest'
import { planServiceInstall, type ServiceInstallOptions } from '../service-install'

const base: ServiceInstallOptions = {
  platform: 'linux', user: 'ncw', nodePath: '/opt/node-24/bin/node', binPath: '/opt/ncw/agent/bin.mjs', dataDir: '/var/lib/ncw',
  host: '127.0.0.1', port: 7001, pairingPort: 7002
}
const plan = (over: Partial<ServiceInstallOptions> = {}) => planServiceInstall({ ...base, ...over })

describe('systemd plan', () => {
  it('renders a system unit that runs as the ordinary user and never starts or enables anything itself', () => {
    const result = plan()
    expect(result.requiresRoot).toBe(true)
    const [file] = result.files
    expect(file?.path).toBe('/etc/systemd/system/ncw-agent.service')
    const unit = file!.content
    expect(unit).toContain('User=ncw')
    expect(unit).toContain('ExecStart="/opt/node-24/bin/node" "/opt/ncw/agent/bin.mjs" "--data-dir" "/var/lib/ncw" "--host" "127.0.0.1" "--port" "7001" "--pairing-port" "7002"')
    expect(unit).toContain('WantedBy=multi-user.target') // 开机无人登录也常驻
    expect(unit).toContain('NoNewPrivileges=true')
    expect(unit).not.toMatch(/User=root/)
    expect(result.install).toEqual([['systemctl', 'daemon-reload'], ['systemctl', 'enable', 'ncw-agent']])
    expect(result.start).toEqual([['systemctl', 'start', 'ncw-agent']])
    expect(result.uninstall.at(-1)).toEqual(['systemctl', 'daemon-reload'])
  })

  it('quotes paths with spaces, percent signs, dollars and quotes without breaking out of the line', () => {
    const unit = plan({ dataDir: '/srv/my data/100%/$HOME/"x"' }).files[0]!.content
    expect(unit).toContain('"/srv/my data/100%%/$$HOME/\\"x\\""')
    expect(unit.split('\n').filter((line) => line.startsWith('ExecStart='))).toHaveLength(1)
  })

  it('passes advertised hosts through', () => {
    const unit = plan({ host: '0.0.0.0', advertise: ['agent.lan', '10.0.0.5'] }).files[0]!.content
    expect(unit).toContain('"--advertise" "agent.lan" "--advertise" "10.0.0.5"')
  })
})

describe('launchd plan', () => {
  it('renders a LaunchDaemon with UserName and escaped arguments', () => {
    const result = plan({ platform: 'darwin', dataDir: '/Users/ncw/Library/Application Support/a&b', nodePath: '/usr/local/bin/node', binPath: '/opt/ncw/bin.mjs' })
    const file = result.files[0]!
    expect(file.path).toBe('/Library/LaunchDaemons/com.nextcowork.ncw-agent.plist')
    expect(file.content).toContain('<key>UserName</key><string>ncw</string>')
    expect(file.content).toContain('a&amp;b')
    expect(file.content).not.toContain('a&b')
    expect(file.content).toContain('<key>RunAtLoad</key><true/>')
    expect(result.install).toEqual([['launchctl', 'bootstrap', 'system', file.path]])
    expect(result.requiresRoot).toBe(true)
  })
})

describe('Windows (WinSW) plan', () => {
  it('renders a WinSW config for an ordinary account, with no password and an honest verification note', () => {
    const result = plan({ platform: 'win32', user: 'CORP\\ncw', nodePath: 'C:\\node\\node.exe', binPath: 'C:\\ncw\\bin.mjs', dataDir: 'C:\\ProgramData\\ncw data' })
    const file = result.files[0]!
    expect(file.path).toBe('C:\\ncw\\winsw\\ncw-agent.xml')
    expect(file.content).toContain('<username>CORP\\ncw</username>')
    expect(file.content).not.toMatch(/password/i)
    expect(file.content).toContain('&quot;C:\\ProgramData\\ncw data&quot;')
    expect(result.notes.join(' ')).toMatch(/not been verified on a Windows/)
    expect(result.install).toEqual([['C:\\ncw\\winsw\\ncw-agent.exe', 'install']])
  })
})

describe('input validation', () => {
  it('refuses to run as root or a system account', () => {
    for (const user of ['root', 'ROOT']) expect(() => plan({ user })).toThrowError(expect.objectContaining({ code: 'invalid_request' }))
    for (const user of ['SYSTEM', 'LocalSystem', 'Administrator', 'NT AUTHORITY\\SYSTEM']) {
      expect(() => plan({ platform: 'win32', user, nodePath: 'C:\\n.exe', binPath: 'C:\\b.mjs', dataDir: 'C:\\d' })).toThrow()
    }
  })

  it('rejects injection through user, name, paths and hosts', () => {
    const bad: Array<Partial<ServiceInstallOptions>> = [
      { user: 'ncw\nUser=root' }, { user: 'ncw;rm' }, { name: 'a b' }, { name: '../x' }, { name: 'Upper' },
      { dataDir: '/var/lib/ncw\nExecStartPre=/bin/evil' }, { dataDir: 'relative/path' }, { binPath: '/x\u0000y' }, { nodePath: '' },
      { host: '127.0.0.1\nExecStart=/bin/evil' }, { host: 'a b' }, { advertise: ['ok.lan', 'bad host'] },
      { port: 70000 }, { port: -1 }, { port: 1.5 }
    ]
    for (const over of bad) expect(() => plan(over), JSON.stringify(over)).toThrowError(expect.objectContaining({ code: 'invalid_request' }))
  })

  it('requires advertised hosts for a wildcard listener', () => {
    expect(() => plan({ host: '0.0.0.0' })).toThrowError(expect.objectContaining({ code: 'invalid_request' }))
    expect(() => plan({ host: '::', advertise: [] })).toThrow()
    expect(() => plan({ host: '0.0.0.0', advertise: ['agent.lan'] })).not.toThrow()
  })
})
