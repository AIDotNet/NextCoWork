/**
 * `SpawnFn` 的纯 Node 实现 —— `bash` 工具脚下的那块地板。
 *
 * 文件不长,但里面三件事都是「不这么写就会在生产里咬人」的那种:
 * 进程组、抽干管道、env 清洗。其余部分是薄的。
 *
 * env 清洗之外,这里还负责把宿主注入的代理变量(`nodeSpawn` 的 `extraEnv`,
 * 需求见那行注释)并进子进程环境 —— 「该走哪台代理」的答案在 electron 侧,
 * kernel 只负责送进去。
 */
import { spawn } from 'node:child_process'
import { constants, userInfo } from 'node:os'
import { shellPreferencesForPlatform, type ShellPreference } from '../../shared/domain/settings'
import { abortError } from './abort'
import type { SpawnFn, SpawnResult } from './host'

/** 单次调用 stdout / stderr 各自的字符上限。超出后仍然读,只是不再往缓冲里塞。 */
const MAX_STREAM_CHARS = 512 * 1024
/** 先 SIGTERM,给这么久收尾,再 SIGKILL。 */
const KILL_GRACE_MS = 2000
/** 对齐 `timeout(1)`:超时退出码 124。模型见过这个约定,不用我们解释。 */
const TIMEOUT_CODE = 124
/** shell 本身没起来(不存在 / cwd 不存在)。对齐 shell 的 "command not found"。 */
const SPAWN_FAILED_CODE = 127

const isWindows = process.platform === 'win32'

/**
 * 子进程环境的合并次序:继承(`process.env`)← 注入(**不覆盖已有键**)← 调用方显式 env,
 * 最后摘掉两个会毒害子进程的变量:
 *
 * - `ELECTRON_RUN_AS_NODE`:父进程是 Electron 时它可能是 1。继承下去,子进程里的
 *   `node` / `npx` 其实是 Electron 二进制在冒充 node,行为诡异且极难归因。
 * - `NODE_OPTIONS`:开发时常带 `--inspect`,子进程继承会抢同一个调试端口然后启动失败。
 *
 * ★ 「注入不越过继承」这条不变式放在**合并点**而不是 provider 里:注入的是
 *   「默认跟随代理」那一撮变量,用户自己 export 过的配置永远赢;靠 provider
 *   自觉的话,换一个 provider 这条就没了。`nodeSpawn`(前台命令)和
 *   `environment/local.ts` 的 `openProcess`(后台命令/钩子)共用这一份实现。
 */
export function mergeChildEnv(
  parent: NodeJS.ProcessEnv,
  injected: Record<string, string>,
  explicit: NodeJS.ProcessEnv = {}
): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = { ...parent }
  for (const [name, value] of Object.entries(injected)) {
    if (env[name] === undefined) env[name] = value
  }
  Object.assign(env, explicit)
  delete env.ELECTRON_RUN_AS_NODE
  delete env.NODE_OPTIONS
  return env
}

/**
 * ★ 杀**整个进程组**,不是杀那一个 shell。
 *
 * `sh -c "npm run dev"` 被中断时只杀 sh,结果是 `node` 还活着、端口还占着 ——
 * 用户看到的是「我点了停止,但项目跑不起来了」,而且找不到是谁占的端口。
 * POSIX 下 `detached: true` 让子进程成为新进程组的组长(pgid === pid),
 * 于是 `kill(-pid)` 覆盖它派生的一整棵树。
 *
 * Windows 没有进程组的等价物,只能靠 `taskkill /T`。⚠️ v1 的已知缺口:
 * 这条分支没有自动化测试覆盖。
 */
/**
 * 杀掉整棵进程树。
 *
 * ★ 导出是给 `environment/local.ts` 的 `openProcess` 用的（钩子会跑
 *   `npm run xxx` 这类会派生子进程的命令）。**一份实现** —— 平台差异
 *   （POSIX 进程组 vs Windows `taskkill /T`）只该有一个答案。
 */
export function killTree(pid: number, signal: NodeJS.Signals): void {
  try {
    if (isWindows) {
      // ★ `windowsHide` 不能省:主进程是 GUI 程序、自己没有控制台,taskkill 是
      //   控制台程序,少了它每次停止/超时都会闪一个黑框(Win11 上还可能被
      //   「默认终端应用」接管成一个真的终端窗口)。
      spawn('taskkill', ['/pid', String(pid), '/T', '/F'], { stdio: 'ignore', windowsHide: true })
    } else {
      process.kill(-pid, signal)
    }
  } catch {
    // 已经死了 / 组不存在(ESRCH)。杀一个死人不是错误。
  }
}

/** 信号名 → 编号,给 shell 那套 `128 + n` 的退出码约定用。认不出就当 0。 */
function signalNumber(sig: NodeJS.Signals): number {
  const table = constants.signals as unknown as Record<string, number | undefined>
  return table[sig] ?? 0
}

/**
 * Agent 的 bash 工具真正会用的那个 shell。
 *
 * ★ 导出它,是因为**提示词里要把这个名字告诉模型**(见 `context-assembler.ts`
 * 的 `# Environment`)。写死一份在提示词那边的话,两处迟早会分叉 ——
 * 而分叉的结果是我们信誓旦旦告诉模型「你在 zsh 里」,它据此写了一条
 * zsh 才有的语法,然后在 /bin/sh 里失败。事实型提示词一旦是假的,
 * 比不写更糟:模型不会怀疑它。
 */
export function agentShell(
  preference: ShellPreference = 'system',
  options: { platform?: string; env?: NodeJS.ProcessEnv; loginShell?: () => string | null } = {}
): string {
  const platform = options.platform ?? process.platform
  const env = options.env ?? process.env
  // 跨系统导入的选择不能拿到本机直接执行；回到本机的自动选择。
  const selected = shellPreferencesForPlatform(platform).includes(preference) ? preference : 'system'
  if (selected !== 'system') {
    return platform === 'win32' ? `${selected}.exe` : selected
  }
  if (platform === 'win32') return env.ComSpec?.trim() || env.COMSPEC?.trim() || 'cmd.exe'
  if (env.SHELL?.trim()) return env.SHELL.trim()
  try {
    // Finder 启动时可能没有 SHELL，仍优先使用账户配置的登录 shell。
    const login = (options.loginShell ?? (() => userInfo().shell))()?.trim()
    if (login) return login
  } catch {
    // 账户信息不可读时仍须有可用的系统兜底。
  }
  return platform === 'darwin' ? '/bin/zsh' : '/bin/sh'
}

export function shellDialect(shell: string): 'cmd' | 'powershell' | 'fish' | 'posix' {
  const name = shell.split(/[\\/]/).pop()?.toLowerCase().replace(/\.exe$/, '')
  if (name === 'cmd') return 'cmd'
  if (name === 'powershell' || name === 'pwsh' || name === 'pwsh-preview') return 'powershell'
  return name === 'fish' ? 'fish' : 'posix'
}

/**
 * 交互终端起 shell 的参数。
 *
 * 需求(#178):终端里认不出 brew / nvm 装的命令。Homebrew 等把 PATH 写在
 * `~/.zprofile` / `~/.bash_profile` 里,只有**登录 shell** 才会读;从 Finder
 * 启动的应用继承的 PATH 又只有系统那几条。macOS「终端」与 VS Code 都以登录
 * shell 起,这里对齐。只给认得 `-l` 的 shell 加,不认识的 shell 维持原样。
 */
export function terminalShellArgs(shell: string, platform: string = process.platform): string[] {
  if (platform === 'win32') return []
  const name = shell.split('/').pop()?.toLowerCase()
  return name !== undefined && ['sh', 'bash', 'zsh', 'dash', 'ksh', 'mksh', 'fish'].includes(name) ? ['-l'] : []
}

/** 插件的 argv 必须按实际 shell 引号化，不能把 PowerShell 参数当 cmd 字符串拼接。 */
export function quoteShellArg(shell: string, arg: string): string {
  switch (shellDialect(shell)) {
    case 'cmd':
      if (/["^&|<>%!\r\n]/.test(arg)) throw new Error('argument contains characters that cannot be quoted on Windows')
      // 结束引号前的反斜杠需翻倍，否则原生程序会把结束引号吞进 argv。
      return arg === '' || /[\s()]/.test(arg) ? `"${arg.replace(/\\+$/, (slashes) => slashes + slashes)}"` : arg
    case 'powershell':
      // PowerShell 也把弯单引号识别为引号；必须同样转义，不能让数据结束字面量。
      return `'${arg.replace(/['\u2018-\u201b]/g, (quote) => quote + quote)}'`
    case 'fish':
      return `'${arg.replaceAll('\\', '\\\\').replaceAll("'", "\\'")}'`
    default:
      return `'${arg.replaceAll("'", String.raw`'\''`)}'`
  }
}

/** Agent 命令与本地钩子共用参数规则，不能仅凭操作系统假定 shell 语法。 */
export function shellCommandArgs(shell: string, command: string): string[] {
  switch (shellDialect(shell)) {
    case 'cmd':
      // 与 Node 的 shell: true 一致：/s /c 去掉最外层引号，内部原样保留。
      return ['/d', '/s', '/c', `"${command}"`]
    case 'powershell': {
      // 编码传入避免 Windows 的二次引号解析；输出按 UTF-8 解码，原生命令退出码保留。
      const script = '$OutputEncoding = [System.Text.UTF8Encoding]::new($false)\n'
        // Windows PowerShell 无控制台时不能设代码页，改为直接配置重定向的文本流。
        + 'try { [Console]::OutputEncoding = $OutputEncoding } catch { & {\n'
        + '$writer = [System.IO.StreamWriter]::new([Console]::OpenStandardOutput(), $OutputEncoding); $writer.AutoFlush = $true; [Console]::SetOut($writer)\n'
        + '$writer = [System.IO.StreamWriter]::new([Console]::OpenStandardError(), $OutputEncoding); $writer.AutoFlush = $true; [Console]::SetError($writer)\n'
        + '} }\n'
        + 'try { [Console]::InputEncoding = $OutputEncoding } catch { [Console]::SetIn([System.IO.StreamReader]::new([Console]::OpenStandardInput(), $OutputEncoding)) }\n'
        + command + '\nif (-not $?) { if ($LASTEXITCODE) { exit $LASTEXITCODE }; exit 1 }'
      return ['-NoLogo', '-NoProfile', '-NonInteractive', '-OutputFormat', 'Text',
        '-EncodedCommand', Buffer.from(script, 'utf16le').toString('base64')]
    }
    default:
      return ['-c', command]
  }
}

export function nodeSpawn(
  resolveShell: () => string = agentShell,
  /*
    需求:Agent 的 shell 命令默认跟随应用/系统代理。「走哪台代理」要问 Chromium 的
    session(系统代理、PAC 只有它知道),而 kernel 不能 import electron —— 所以由宿主侧
    (`net/proxy.ts` 的 `shellProxyEnv`)把答案算成一撮环境变量,以参数形态从这里注入。
    收 Promise 也收现成 Record(`KernelHost.childEnv` 两种都允许),下面统一 await。
  */
  extraEnv?: () => Record<string, string> | Promise<Record<string, string>>
): SpawnFn {
  return (cmd, opts) => {
    // 已经中断的 run 不必再去问一遍代理 —— 原先那个同步 reject 的行为保持不变
    if (opts.signal.aborted) return Promise.reject(abortError())
    const base = extraEnv === undefined
      ? Promise.resolve({} as Record<string, string>)
      : Promise.resolve(extraEnv())
    return base.then((extra) =>
      new Promise<SpawnResult>((resolve, reject) => {
        /*
          ★ await 代理变量期间 signal 可能已经响过 —— executor 顶部这次判断不是
            多余的重复:少了它,已中断的 run 会先把命令真的跑起来再被杀,
            而 spawn 自己的那个 abort 分支只在起了之后才管用。
        */
        if (opts.signal.aborted) {
          reject(abortError())
          return
        }

        const file = opts.shell ?? resolveShell()
        const child = spawn(file, shellCommandArgs(file, cmd), {
          cwd: opts.cwd,
          env: mergeChildEnv(process.env, extra),
          windowsHide: true,
          windowsVerbatimArguments: isWindows && shellDialect(file) === 'cmd',
          // POSIX:自成进程组,让 killTree 能一次带走整棵树
          detached: !isWindows,
          /*
            ★ stdin 关掉。留着的话,一个等输入的命令(不带 -m 的 git commit、npm login)
            会永远挂在那里 —— 表现成「工具卡住了」,而没有任何人能给它敲字。
          */
          stdio: ['ignore', 'pipe', 'pipe']
        })

        let stdout = ''
        let stderr = ''
        let killedBy: 'timeout' | 'abort' | null = null
        let detached = false
        let graceTimer: NodeJS.Timeout | undefined
        let timeoutTimer: NodeJS.Timeout | undefined

        /*
          ★ 超出预算后**仍然消费数据**,只是不再 append。
          直觉上应该 pause() 或者干脆不读 —— 但那样管道会写满,子进程阻塞在 write 上
          永不退出。症状是「命令挂住了」,而 stdout 里已经有正确的前 512KB,
          看起来完全不像一个背压问题。
        */
        const onStdout = (chunk: Buffer): void => {
          const text = chunk.toString('utf8')
          if (stdout.length < MAX_STREAM_CHARS) stdout += text
          // ★ 逐个 try:一个订阅者抛异常不能把命令本身带下去。
          try { opts.onOutput?.({ stream: 'stdout', text }) } catch { /* 订阅者的问题,不是命令的 */ }
        }
        const onStderr = (chunk: Buffer): void => {
          const text = chunk.toString('utf8')
          if (stderr.length < MAX_STREAM_CHARS) stderr += text
          try { opts.onOutput?.({ stream: 'stderr', text }) } catch { /* 同上 */ }
        }
        child.stdout.on('data', onStdout)
        child.stderr.on('data', onStderr)

        const { pid } = child

        function terminate(why: 'timeout' | 'abort'): void {
          killedBy = why
          if (pid === undefined) return
          killTree(pid, 'SIGTERM')
          graceTimer = setTimeout(() => killTree(pid, 'SIGKILL'), KILL_GRACE_MS)
          // 宽限计时器不该拖住 Node 退出 —— 进程通常在它到点之前就走了
          graceTimer.unref()
        }

        const onAbort = (): void => {
          terminate('abort')
        }
        opts.signal.addEventListener('abort', onAbort, { once: true })

        if (opts.timeoutMs !== undefined && opts.timeoutMs > 0) {
          timeoutTimer = setTimeout(() => {
            terminate('timeout')
          }, opts.timeoutMs)
        }

        /*
          转去后台:把进程连同两条管道原样交出去,这个 Promise 就此了结。

          ★ 已经在被杀(超时 / 中断)或已经收尾的不转 —— 转出去的会是一个
          正在死的进程,接手方会把「被我们杀掉」误报成「自己退出了」。
          ★ 我们自己的 data 监听要摘掉:留着的话两份缓冲各攒一遍,而这边那份
          已经没有人会读了。接手方在同一个同步块里挂上它自己的。
        */
        const onDetach = (): void => {
          if (killedBy !== null || detached || child.exitCode !== null || child.signalCode !== null) return
          detached = true
          cleanup()
          child.stdout.off('data', onStdout)
          child.stderr.off('data', onStderr)
          /*
            ★ 摘掉最后一个 data 监听**不会**让流停下来:它仍在 flowing 模式,
            接手方挂上监听之前到的那几块会直接丢掉。显式 pause,由接手方 resume。
          */
          child.stdout.pause()
          child.stderr.pause()
          resolve({
            code: 0,
            stdout,
            stderr,
            detached: {
              stdout: child.stdout,
              stderr: child.stderr,
              exited: new Promise((done) => {
                child.once('close', (code, signalName) => done({ code, signal: signalName }))
                child.once('error', () => done({ code: null }))
              }),
              kill: () => {
                if (pid !== undefined) killTree(pid, 'SIGTERM')
                else child.kill()
              }
            }
          })
        }
        if (opts.detach?.aborted === true) queueMicrotask(onDetach)
        else opts.detach?.addEventListener('abort', onDetach, { once: true })

        function cleanup(): void {
          opts.signal.removeEventListener('abort', onAbort)
          opts.detach?.removeEventListener('abort', onDetach)
          if (graceTimer !== undefined) clearTimeout(graceTimer)
          if (timeoutTimer !== undefined) clearTimeout(timeoutTimer)
        }

        child.on('error', (err: Error) => {
          if (detached) return
          cleanup()
          // 命令根本没起来。这是工具错误,不是中断 —— 让模型看见原因,它能自己改。
          resolve({ code: SPAWN_FAILED_CODE, stdout, stderr: `${stderr}${err.message}` })
        })

        child.on('close', (code, signalName) => {
          if (detached) return
          cleanup()

          /*
            ★ 中断**抛出**,不返回一个 code = -1 的结果。

            `defineTool` 的契约是中断原样抛出、不伪装成工具失败(见 define.ts 文件头)。
            这里返回普通结果的话,每个调用方都得自己记得再查一次 signal ——
            而漏查的那一个,会把「用户点了停止」表现成「命令失败了,换个方式再试一次」。
          */
          if (killedBy === 'abort') {
            reject(abortError())
            return
          }

          if (killedBy === 'timeout') {
            resolve({
              code: TIMEOUT_CODE,
              stdout,
              stderr: `${stderr}\n[命令超时:超过 ${String(opts.timeoutMs)}ms 未结束,已终止整个进程组]`
            })
            return
          }

          if (code !== null) {
            resolve({ code, stdout, stderr })
            return
          }

          // 被外部信号杀掉。用 shell 的 128+signum 约定,而不是编一个 0 出来。
          resolve({
            code: 128 + (signalName === null ? 0 : signalNumber(signalName)),
            stdout,
            stderr: `${stderr}\n[命令被信号 ${signalName ?? '未知'} 终止]`
          })
        })
      })
    )
  }
}
