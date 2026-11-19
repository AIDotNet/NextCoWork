/**
 * 2.9 在应用内下载并安装 NextCoWork 3.0(和 2.x 自己的「下载 → 重启并安装」同一种体验)。
 *
 * 两步,中间有状态(`V3InstallState`):
 *  1. `download()`:从 nextco.work 下载这个平台的 3.0 包 → 校验 sha256 → (macOS)解开并验签。成功 = `ready`。
 *  2. `install()`:起一个**脱离本进程的小脚本**,等 2.x 退出后再换 / 装 3.0,然后启动它;调用方随后走正常退出流程。
 *
 * ★ 为什么脚本要「等 2.x 退出」:3.0 在 2.x 还开着时拒绝接管数据;用户在退出流程里取消(未保存的文档等)时,
 *   脚本等满超时就放弃,什么都没动,2.x 照常用。
 * ★ macOS 上 3.0 和 2.x 是同名同 id 的 `NextCoWork.app`,所以「安装」就是原地替换正在运行的这个 bundle;
 *   旧的 2.x 移到 `~/Library/Application Support/NextCoWork-2.x-backup/`,换失败时原样放回。
 * ★ 只信 nextco.work、只信后端清单里的 sha256;任何一步不对都落到 `error`,界面退回「打开下载页」。
 *
 * Electron 相关的(网络、进程、退出)全部由调用方注入,本文件可以在 Node 里直接测。
 */
import { createHash } from 'node:crypto'
import { accessSync, appendFileSync, constants, createWriteStream, existsSync, mkdirSync, renameSync, rmSync, writeFileSync, chmodSync, copyFileSync } from 'node:fs'
import { dirname, join, sep } from 'node:path'
import { v3FileKindOf, type V3File, type V3InstallErrorCode, type V3InstallState } from '../../shared/domain/v3-upgrade'

export const V3_ORIGIN = 'https://nextco.work'

/** 脚本等 2.x 退出的上限(秒)。用户在退出确认里犹豫的时间也算在内。 */
export const WAIT_FOR_EXIT_SECONDS = 180

export interface FetchedBody {
  ok: boolean
  body: { getReader: () => { read: () => Promise<{ done: boolean; value?: Uint8Array }> } } | null
}

export interface InstallerDeps {
  fetch: (url: string, signal: AbortSignal) => Promise<FetchedBody>
  /** 同步执行外部命令(`ditto` / `codesign`),失败抛错 */
  run: (command: string, args: string[]) => Promise<void>
  /** 起脱离本进程的后台进程。返回「放弃等待」的函数(退出被取消时用它结束还在等 2.x 退出的脚本)。 */
  spawnDetached: (command: string, args: string[], onError: (error: Error) => void) => () => void
  platform: string
  /** 本进程的可执行文件(定位正在运行的 .app) */
  execPath: string
  pid: number
  home: string
  /** 下载 / 解包用的临时目录(每次下载前清空) */
  workDir: string
  onState: (state: V3InstallState) => void
}

class InstallError extends Error {
  constructor(readonly code: V3InstallErrorCode, message: string) {
    super(message)
  }
}

/** 相对路径按 nextco.work 解析;解析出来不是 nextco.work 的 https 地址一律拒绝。 */
export function resolveFeedUrl(url: string): string | null {
  try {
    const resolved = new URL(url, V3_ORIGIN)
    return resolved.origin === V3_ORIGIN ? resolved.toString() : null
  } catch {
    return null
  }
}

/** `/Applications/NextCoWork.app/Contents/MacOS/NextCoWork` → `/Applications/NextCoWork.app`。不在 .app 里返回 null。 */
export function runningBundleOf(execPath: string): string | null {
  const marker = `.app${sep}Contents${sep}`
  const i = execPath.indexOf(marker)
  return i < 0 ? null : execPath.slice(0, i + '.app'.length)
}

/** sh 引号:整个值包进单引号,值里的单引号写成 '\'' */
export function shq(value: string): string {
  return `'${value.replace(/'/g, `'\\''`)}'`
}

/** PowerShell 单引号字符串:单引号写成两个 */
export function psq(value: string): string {
  return `'${value.replace(/'/g, "''")}'`
}

/**
 * macOS:等 2.x 退出 → 把旧 bundle 挪去备份 → 把已验过的新 bundle 换进来 → 打开。
 * 任何一步失败都把旧的放回去。`stage` 和 `target` 在同一个目录里,所以 `mv` 是改名,不是拷贝。
 */
export function macSwapScript(): string {
  return `#!/bin/sh
PID="$1"; STAGE="$2"; TARGET="$3"; BACKUP="$4"
i=0
while kill -0 "$PID" 2>/dev/null; do
  i=$((i+1))
  if [ "$i" -gt ${WAIT_FOR_EXIT_SECONDS * 2} ]; then exit 1; fi
  sleep 0.5
done
[ -d "$STAGE" ] || exit 1
rm -rf "$BACKUP"
mkdir -p "$(dirname "$BACKUP")" || exit 1
mv "$TARGET" "$BACKUP" || exit 1
if ! mv "$STAGE" "$TARGET"; then
  mv "$BACKUP" "$TARGET"
  exit 1
fi
open "$TARGET"
`
}

/** Linux:等 2.x 退出 → 启动 AppImage。 */
export function linuxLaunchScript(): string {
  return `#!/bin/sh
PID="$1"; APPIMAGE="$2"
i=0
while kill -0 "$PID" 2>/dev/null; do
  i=$((i+1))
  if [ "$i" -gt ${WAIT_FOR_EXIT_SECONDS * 2} ]; then exit 1; fi
  sleep 0.5
done
exec "$APPIMAGE"
`
}

/** Windows:等 2.x 退出 → 跑 Velopack 的 Setup.exe(它装完会自己启动 3.0)。 */
export function windowsInstallCommand(pid: number, setupPath: string, logPath: string): string {
  return [
    `$log = ${psq(logPath)}`,
    `function L($m) { Add-Content -LiteralPath $log -Value ((Get-Date -Format o) + ' [ps] ' + $m) }`,
    `L 'script started, waiting for 2.x (pid ${pid}) to exit'`,
    `Wait-Process -Id ${pid} -Timeout ${WAIT_FOR_EXIT_SECONDS} -ErrorAction SilentlyContinue`,
    `if (Get-Process -Id ${pid} -ErrorAction SilentlyContinue) { L 'gave up: 2.x did not exit in time'; exit 1 }`,
    `L 'starting the installer'`,
    `try { Start-Process -FilePath ${psq(setupPath)} -ErrorAction Stop; L 'installer started' } catch { L ('installer failed to start: ' + $_) }`
  ].join('; ')
}

export class V3Installer {
  private state: V3InstallState = { phase: 'idle' }
  private inflight: AbortController | null = null
  /** 下载好的文件(`ready` 之后才有) */
  private artifact: { file: string; stage?: string; target?: string } | null = null
  /** 正在等 2.x 退出的安装脚本的「放弃」开关 */
  private abandonScript: (() => void) | null = null

  constructor(private readonly deps: InstallerDeps) {}

  getState(): V3InstallState {
    return this.state
  }

  /** 新版本出现时丢掉旧的进度(不删磁盘上的文件,下次下载前会清空工作目录) */
  reset(): void {
    this.inflight?.abort()
    this.inflight = null
    this.artifact = null
    this.set({ phase: 'idle' })
  }

  async download(file: V3File): Promise<void> {
    if (this.state.phase === 'downloading' || this.state.phase === 'verifying' || this.state.phase === 'installing') return
    const controller = new AbortController()
    this.inflight = controller
    this.artifact = null
    try {
      await this.doDownload(file, controller.signal)
    } catch (error) {
      if (controller.signal.aborted) return
      const code: V3InstallErrorCode = error instanceof InstallError ? error.code : 'network'
      console.warn('[v3] 下载 / 准备 3.0 失败:', error)
      this.set({ phase: 'error', code })
    } finally {
      if (this.inflight === controller) this.inflight = null
    }
  }

  private async doDownload(file: V3File, signal: AbortSignal): Promise<void> {
    const { deps } = this
    const kind = v3FileKindOf(deps.platform)
    if (kind === null || kind !== file.kind) throw new InstallError('unsupported', `platform ${deps.platform} cannot install ${file.kind}`)
    const url = resolveFeedUrl(file.url)
    if (url === null) throw new InstallError('unsupported', `refusing to download from ${file.url}`)

    // macOS 要原地换 bundle:换不了(目录不可写 / 不是从 .app 里跑的)就别白下 90MB
    let target: string | undefined
    if (deps.platform === 'darwin') {
      const bundle = runningBundleOf(deps.execPath)
      if (bundle === null) throw new InstallError('permission', 'not running from an .app bundle')
      try { accessSync(dirname(bundle), constants.W_OK) } catch { throw new InstallError('permission', `${dirname(bundle)} is not writable`) }
      target = bundle
    }

    rmSync(deps.workDir, { recursive: true, force: true })
    mkdirSync(deps.workDir, { recursive: true })
    const partial = join(deps.workDir, `${file.fileName}.part`)
    const final = join(deps.workDir, file.fileName)

    this.set({ phase: 'downloading', received: 0, total: file.size })
    const response = await deps.fetch(url, signal)
    if (!response.ok || response.body === null) throw new InstallError('network', 'download request failed')
    const hash = createHash('sha256')
    const out = createWriteStream(partial)
    let received = 0
    let lastPercent = -1
    try {
      const reader = response.body.getReader()
      for (;;) {
        const { done, value } = await reader.read()
        if (done) break
        if (value === undefined) continue
        hash.update(value)
        if (!out.write(value)) await new Promise<void>((resolve) => out.once('drain', resolve))
        received += value.byteLength
        const percent = Math.min(99, Math.floor((received / file.size) * 100))
        if (percent !== lastPercent) {
          lastPercent = percent
          this.set({ phase: 'downloading', received, total: file.size })
        }
      }
    } finally {
      await new Promise<void>((resolve) => out.end(resolve))
    }
    if (received !== file.size) throw new InstallError('network', `expected ${file.size} bytes, got ${received}`)

    this.set({ phase: 'verifying' })
    if (hash.digest('hex') !== file.sha256.toLowerCase()) throw new InstallError('checksum', 'sha256 mismatch')
    renameSync(partial, final)

    if (deps.platform === 'darwin' && target !== undefined) {
      const stage = join(dirname(target), '.ncw-v3-stage')
      rmSync(stage, { recursive: true, force: true })
      mkdirSync(stage, { recursive: true })
      try {
        await deps.run('/usr/bin/ditto', ['-x', '-k', final, stage])
      } catch {
        throw new InstallError('extract', 'ditto failed')
      }
      const app = join(stage, 'NextCoWork.app')
      // Velopack 装出来的 3.x 才有 sq.version —— 和 2.x 的 bundle 区分开,也防止解出一个别的东西来
      if (!existsSync(join(app, 'Contents', 'MacOS', 'sq.version'))) throw new InstallError('extract', 'not a NextCoWork 3.x bundle')
      try {
        await deps.run('/usr/bin/codesign', ['--verify', '--deep', '--strict', app])
      } catch {
        throw new InstallError('signature', 'codesign verification failed')
      }
      this.artifact = { file: final, stage: app, target }
    } else {
      this.artifact = { file: final }
    }
    this.set({ phase: 'ready' })
  }

  /**
   * 起脱离本进程的安装脚本。调用方在这之后走正常退出流程;脚本等 2.x 退出才动手。
   * 返回 false = 没有可安装的东西(没 ready)。
   */
  prepareInstall(): boolean {
    const { deps } = this
    if (this.state.phase !== 'ready' || this.artifact === null) return false
    const { artifact } = this
    try {
      if (deps.platform === 'darwin' && artifact.stage !== undefined && artifact.target !== undefined) {
        const script = join(deps.workDir, 'swap.sh')
        writeFileSync(script, macSwapScript(), { mode: 0o755 })
        const backup = join(deps.home, 'Library', 'Application Support', 'NextCoWork-2.x-backup', 'NextCoWork.app')
        this.abandonScript = deps.spawnDetached('/bin/sh', [script, String(deps.pid), artifact.stage, artifact.target, backup], this.onSpawnError)
      } else if (deps.platform === 'win32') {
        this.log(`windows: starting powershell to run ${artifact.file} after pid ${deps.pid} exits`)
        this.abandonScript = deps.spawnDetached(
          'powershell.exe',
          ['-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden', '-Command', windowsInstallCommand(deps.pid, artifact.file, this.logPath())],
          this.onSpawnError
        )
      } else if (deps.platform === 'linux') {
        const dir = join(deps.home, 'Applications')
        mkdirSync(dir, { recursive: true })
        const appImage = join(dir, 'NextCoWork-3.AppImage')
        copyFileSync(artifact.file, appImage)
        chmodSync(appImage, 0o755)
        const script = join(deps.workDir, 'launch.sh')
        writeFileSync(script, linuxLaunchScript(), { mode: 0o755 })
        this.abandonScript = deps.spawnDetached('/bin/sh', [script, String(deps.pid), appImage], this.onSpawnError)
      } else {
        return false
      }
    } catch (error) {
      console.warn('[v3] 启动 3.0 安装脚本失败:', error)
      this.set({ phase: 'error', code: 'permission' })
      return false
    }
    this.log(`install started (${deps.platform}, pid ${deps.pid}); waiting for 2.x to quit`)
    this.set({ phase: 'installing' })
    return true
  }

  /** 退出被用户取消时调用:脚本会等到超时自己放弃,界面回到 `ready` 可以再点。 */
  installCancelled(): void {
    if (this.state.phase !== 'installing') return
    // 结束还在等 2.x 退出的脚本:否则用户稍后自己退出时它会突然把 3.0 换进来
    this.abandonScript?.()
    this.abandonScript = null
    this.log('install cancelled: the quit did not go through; script abandoned')
    this.set({ phase: 'ready' })
  }

  /** 安装脚本起不来(找不到 powershell、被安全软件拦截……):记下来,回到可重试 */
  private readonly onSpawnError = (error: Error): void => {
    this.log(`spawn failed: ${error.message}`)
    console.warn('[v3] 安装脚本没能启动:', error)
    if (this.state.phase === 'installing') this.set({ phase: 'error', code: 'permission' })
  }

  /** 安装过程的日志(用户反馈「点了没反应」时能看到走到了哪一步):<工作目录>/install.log */
  private logPath(): string {
    return join(this.deps.workDir, 'install.log')
  }

  private log(message: string): void {
    try { appendFileSync(this.logPath(), `${new Date().toISOString()} [main] ${message}\n`) } catch { /* 日志写不了不影响安装 */ }
  }

  private set(next: V3InstallState): void {
    this.state = next
    this.deps.onState(next)
  }
}
