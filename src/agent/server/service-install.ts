/**
 * 常驻服务的安装计划生成(纯函数,不碰系统)。
 *
 * ## 立场
 * - **普通用户运行。** `user` 必填且不得是 root / SYSTEM / LocalSystem / Administrator:
 *   服务只需要读写它自己的数据根,不该拿到机器的全部权限。
 * - **提权单独提示。** 这里只产出「要写哪些文件、之后要跑哪些命令」;写系统目录需要 root 的
 *   事实由 `requiresRoot` 明示,真正的写入与命令由操作者(或 `service-cli` 的 apply)显式执行,
 *   本模块从不自己 `sudo`、不启动、不启用服务。
 * - **开机无人登录也常驻。** Linux 用 system 级 unit(带 `User=`),macOS 用 LaunchDaemon(带
 *   `UserName`),Windows 用服务封装(WinSW)—— 不拿随登录退出的 user service / LaunchAgent 冒充。
 * - **注入防护。** 所有会进入配置文件的值(用户名、路径、参数)按各格式转义,含控制字符/换行/
 *   引号破坏结构的输入直接拒绝,而不是靠转义赌运气。
 *
 * ★ Windows:Node 脚本不能直接当 Windows 服务。这里生成 WinSW(https://github.com/winsw/winsw)
 *   的配置;WinSW 可执行文件不随本项目分发,需由操作者安装。这一路径**未在 Windows 上验证**。
 */
import { posix, win32 } from 'node:path'
import { DeviceProtocolError } from '../../shared/device/errors'

export type ServicePlatform = 'linux' | 'darwin' | 'win32'

export interface ServiceInstallOptions {
  platform: ServicePlatform
  /** 运行服务的普通用户。 */
  user: string
  /** 绝对路径:node 可执行文件(要求 Node 24+)。 */
  nodePath: string
  /** 绝对路径:构建出来的 `bin.mjs`。 */
  binPath: string
  /** 绝对路径:数据根(该用户可写)。 */
  dataDir: string
  host?: string
  port?: number
  pairingPort?: number
  advertise?: string[]
  /** 服务名,默认 `ncw-agent`。 */
  name?: string
}

export interface PlannedFile { path: string; content: string; mode: number }
export interface InstallPlan {
  platform: ServicePlatform
  name: string
  files: PlannedFile[]
  /** 写完文件之后由操作者执行的命令(每条一个 argv),此处从不自动执行。 */
  install: string[][]
  start: string[][]
  stop: string[][]
  status: string[][]
  uninstall: string[][]
  /** 写入/启用这些文件是否需要管理员权限。 */
  requiresRoot: boolean
  notes: string[]
}

const FORBIDDEN_USERS = new Set(['root', 'system', 'localsystem', 'nt authority\\system', 'administrator', 'administrators'])
const USER = /^[A-Za-z_][A-Za-z0-9_.-]{0,31}$/
const NAME = /^[a-z][a-z0-9-]{0,40}$/
const HOST = /^(?:[A-Za-z0-9.-]{1,253}|\[?[0-9A-Fa-f:.]{2,45}\]?)$/

function reject(message: string): never { throw new DeviceProtocolError('invalid_request', message) }
// eslint-disable-next-line no-control-regex -- 恰恰是要拒绝控制字符
const CONTROL = /[\u0000-\u001f\u007f]/

function requireAbsolute(value: string, label: string, platform: ServicePlatform): string {
  if (typeof value !== 'string' || value.length === 0 || value.length > 1024 || CONTROL.test(value)) reject(`${label} is invalid`)
  const windows = platform === 'win32'
  const ok = windows ? win32.isAbsolute(value) : posix.isAbsolute(value)
  /*
    `win32.isAbsolute` 也认 `/d` 与 `/C:/x` —— 它们会被 `win32.resolve` 解析成「当前盘符下的路径」,
    写进 WinSW 配置之后启动必然失败。按 `--platform win32` 时要求明确指出盘符或 UNC 根。
  */
  const okWindows = win32.isAbsolute(value) && /^(?:[A-Za-z]:[\\/]|\\\\)/.test(value)
  if (!(windows ? okWindows : ok) || (!windows && /^\/[A-Za-z]:/.test(value))) reject(`${label} must be an absolute path`)
  return value
}

/** 校验并规范化输入。 */
function validate(options: ServiceInstallOptions): Required<Omit<ServiceInstallOptions, 'advertise'>> & { advertise: string[] } {
  const name = options.name ?? 'ncw-agent'
  if (!NAME.test(name)) reject('Service name must be lowercase letters, digits and dashes')
  if (!USER.test(options.user) && !(options.platform === 'win32' && /^[A-Za-z0-9_.-]{1,64}(?:\\[A-Za-z0-9_.-]{1,64})?$/.test(options.user))) reject('User name is invalid')
  if (FORBIDDEN_USERS.has(options.user.toLowerCase())) reject('The service must run as an ordinary user, not root/SYSTEM')
  const port = (value: number | undefined, label: string): number => {
    if (value === undefined) return 0
    if (!Number.isInteger(value) || value < 0 || value > 65535) reject(`${label} must be a port number`)
    return value
  }
  const host = options.host ?? '127.0.0.1'
  if (!HOST.test(host)) reject('Host is invalid')
  const advertise = options.advertise ?? []
  for (const item of advertise) if (!HOST.test(item)) reject('Advertised host is invalid')
  if (['0.0.0.0', '::', '[::]'].includes(host) && advertise.length === 0) reject('A wildcard host requires advertised hosts')
  return {
    platform: options.platform, name, user: options.user,
    nodePath: requireAbsolute(options.nodePath, 'nodePath', options.platform),
    binPath: requireAbsolute(options.binPath, 'binPath', options.platform),
    dataDir: requireAbsolute(options.dataDir, 'dataDir', options.platform),
    host, port: port(options.port, 'port'), pairingPort: port(options.pairingPort, 'pairingPort'), advertise
  }
}

function argv(o: ReturnType<typeof validate>): string[] {
  const args = [o.binPath, '--data-dir', o.dataDir, '--host', o.host, '--port', String(o.port), '--pairing-port', String(o.pairingPort)]
  for (const host of o.advertise) args.push('--advertise', host)
  return args
}

// ── 各格式的转义 ──────────────────────────────────────────────

/** systemd:双引号包裹;`\`、`"` 转义;`%` 与 `$` 在 unit 里有特殊含义,各自写成两个。 */
function systemdQuote(value: string): string {
  return `"${value.replace(/\\/g, '\\\\').replace(/"/g, '\\"').replace(/%/g, '%%').replace(/\$/g, '$$$$')}"`
}
const xml = (value: string): string =>
  value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&apos;')

function linux(o: ReturnType<typeof validate>): InstallPlan {
  const unit = `/etc/systemd/system/${o.name}.service`
  const exec = [o.nodePath, ...argv(o)].map(systemdQuote).join(' ')
  const content = `[Unit]
Description=NextCoWork device service
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=${o.user}
WorkingDirectory=${systemdQuote(o.dataDir)}
ExecStart=${exec}
# SIGUSR2 = 排空后停止(升级用):拒绝新任务,等在跑的任务自然结束。
ExecReload=/bin/kill -USR2 $MAINPID
KillSignal=SIGTERM
TimeoutStopSec=60
Restart=on-failure
RestartSec=5
# 以普通用户运行,并去掉不需要的能力。
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ProtectHome=read-only
ReadWritePaths=${systemdQuote(o.dataDir)}

[Install]
WantedBy=multi-user.target
`
  return {
    platform: 'linux', name: o.name, files: [{ path: unit, content, mode: 0o644 }],
    install: [['systemctl', 'daemon-reload'], ['systemctl', 'enable', o.name]],
    start: [['systemctl', 'start', o.name]], stop: [['systemctl', 'stop', o.name]], status: [['systemctl', 'status', o.name]],
    uninstall: [['systemctl', 'disable', '--now', o.name], ['rm', unit], ['systemctl', 'daemon-reload']],
    requiresRoot: true,
    notes: [
      'ProtectHome=read-only: workspaces under the home directory are read-only for the service unless listed in ReadWritePaths. Edit the unit to add the project directories the agent must write.',
      'Run `systemctl reload` to drain and stop before an upgrade.'
    ]
  }
}

function darwin(o: ReturnType<typeof validate>): InstallPlan {
  const label = `com.nextcowork.${o.name}`
  const plist = `/Library/LaunchDaemons/${label}.plist`
  const args = [o.nodePath, ...argv(o)].map((item) => `    <string>${xml(item)}</string>`).join('\n')
  const content = `<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>${xml(label)}</string>
  <key>UserName</key><string>${xml(o.user)}</string>
  <key>ProgramArguments</key>
  <array>
${args}
  </array>
  <key>WorkingDirectory</key><string>${xml(o.dataDir)}</string>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
  <key>ExitTimeOut</key><integer>60</integer>
  <key>StandardOutPath</key><string>${xml(posix.join(o.dataDir, 'service.log'))}</string>
  <key>StandardErrorPath</key><string>${xml(posix.join(o.dataDir, 'service.log'))}</string>
</dict>
</plist>
`
  return {
    platform: 'darwin', name: o.name, files: [{ path: plist, content, mode: 0o644 }],
    install: [['launchctl', 'bootstrap', 'system', plist]],
    start: [['launchctl', 'kickstart', '-k', `system/${label}`]],
    stop: [['launchctl', 'kill', 'SIGTERM', `system/${label}`]],
    status: [['launchctl', 'print', `system/${label}`]],
    uninstall: [['launchctl', 'bootout', `system/${label}`], ['rm', plist]],
    requiresRoot: true,
    notes: ['LaunchDaemon plists must be owned by root:wheel with mode 0644; the daemon itself runs as the configured user.']
  }
}

function windows(o: ReturnType<typeof validate>): InstallPlan {
  const dir = win32.join(win32.dirname(o.binPath), 'winsw')
  const config = win32.join(dir, `${o.name}.xml`)
  const wrapper = win32.join(dir, `${o.name}.exe`)
  const args = argv(o).map((item) => (/[\s"]/.test(item) ? `"${item.replace(/"/g, '\\"')}"` : item)).join(' ')
  const content = `<service>
  <id>${xml(o.name)}</id>
  <name>NextCoWork device service</name>
  <description>NextCoWork device service (runs as an ordinary user)</description>
  <executable>${xml(o.nodePath)}</executable>
  <arguments>${xml(args)}</arguments>
  <workingdirectory>${xml(o.dataDir)}</workingdirectory>
  <serviceaccount>
    <username>${xml(o.user)}</username>
    <allowservicelogon>true</allowservicelogon>
  </serviceaccount>
  <onfailure action="restart" delay="5 sec"/>
  <stoptimeout>60 sec</stoptimeout>
  <log mode="roll-by-size"><sizeThreshold>10240</sizeThreshold><keepFiles>5</keepFiles></log>
</service>
`
  return {
    platform: 'win32', name: o.name, files: [{ path: config, content, mode: 0o644 }],
    install: [[wrapper, 'install']], start: [[wrapper, 'start']], stop: [[wrapper, 'stop']], status: [[wrapper, 'status']],
    uninstall: [[wrapper, 'stop'], [wrapper, 'uninstall']],
    requiresRoot: true,
    notes: [
      `Copy the WinSW executable (not bundled with this project) to ${wrapper} next to the config before running the install command.`,
      'Windows requires the service account password at install time (WinSW prompts for it); it is never written to the config.',
      'This Windows path has not been verified on a Windows machine.'
    ]
  }
}

export function planServiceInstall(options: ServiceInstallOptions): InstallPlan {
  const o = validate(options)
  switch (o.platform) {
    case 'linux': return linux(o)
    case 'darwin': return darwin(o)
    case 'win32': return windows(o)
    default: return reject('Unsupported platform')
  }
}
