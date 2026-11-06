import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest'
import { localEnvironment } from '../../environment/local'
import { isAbortError } from '../abort'
import { nodeHost } from '../host'
import { agentShell, nodeSpawn, quoteShellArg, shellCommandArgs, shellDialect, terminalShellArgs } from '../node-spawn'

vi.mock('node-pty', () => ({ spawn: vi.fn() }))

/**
 * `SpawnFn` 的真进程测试。
 *
 * ★ 这里几乎每一条测的都不是「结果对不对」,而是**进程有没有真的死透**、
 * **命令会不会挂住**。这两类问题在打桩的测试里一律是绿的,而它们正是
 * bash 工具唯一会让用户痛的地方(「我点了停止,但端口还占着」)。
 */

// 进程生命周期测试固定 sh，不受运行测试的账户默认 shell 影响。
const spawn = nodeSpawn(() => process.platform === 'win32' ? 'cmd.exe' : '/bin/sh')

let root = ''

beforeAll(() => {
  root = mkdtempSync(join(tmpdir(), 'nextcowork-spawn-'))
})

afterAll(() => {
  rmSync(root, { recursive: true, force: true })
})

function opts(over: Partial<{ cwd: string; signal: AbortSignal; timeoutMs: number }> = {}): {
  cwd: string
  signal: AbortSignal
  timeoutMs?: number
} {
  return { cwd: root, signal: new AbortController().signal, ...over }
}

/** 轮询到进程真的不见了。★ 不用固定 sleep —— 那在 CI 上必然 flaky。 */
async function waitGone(pid: number, budgetMs = 3000): Promise<boolean> {
  const deadline = Date.now() + budgetMs
  while (Date.now() < deadline) {
    try {
      process.kill(pid, 0)
    } catch {
      return true
    }
    await new Promise((r) => setTimeout(r, 50))
  }
  return false
}

/** 轮询到子进程把 pid 写进文件。同上,不固定 sleep。 */
async function waitPidFile(path: string, budgetMs = 3000): Promise<number> {
  const deadline = Date.now() + budgetMs
  while (Date.now() < deadline) {
    if (existsSync(path)) {
      const raw = readFileSync(path, 'utf8').trim()
      if (raw !== '') return Number(raw)
    }
    await new Promise((r) => setTimeout(r, 25))
  }
  throw new Error(`子进程没在 ${String(budgetMs)}ms 内写出 pid`)
}

describe.skipIf(process.platform === 'win32')('spawn 基本语义', () => {
  it('拿到 stdout 与退出码 0', async () => {
    const r = await spawn('echo hi', opts())
    expect(r.code).toBe(0)
    expect(r.stdout.trim()).toBe('hi')
  })

  it('非零退出码原样回传,不当成异常', async () => {
    const r = await spawn('echo oops >&2; exit 3', opts())
    expect(r.code).toBe(3)
    expect(r.stderr).toContain('oops')
  })

  it('在给定的 cwd 里跑', async () => {
    const r = await spawn('pwd', opts())
    // macOS 的 /var → /private/var,所以比后缀不比全等
    expect(r.stdout.trim().endsWith(root.replace(/^\/private/, ''))).toBe(true)
  })

  it('★ 命令起不来时回 127 而不是抛 —— 这是工具错误,模型能自己改', async () => {
    const r = await spawn('echo hi', opts({ cwd: join(root, 'does-not-exist') }))
    expect(r.code).toBe(127)
    expect(r.stderr.length).toBeGreaterThan(0)
  })

  it('★ stdin 是关的 —— 等输入的命令必须立刻返回,不是挂在那里', async () => {
    const r = await spawn('cat', opts({ timeoutMs: 4000 }))
    expect(r.code).toBe(0)
    expect(r.stdout).toBe('')
  })
})

describe.skipIf(process.platform === 'win32')('spawn 的 env 清洗', () => {
  it('★ ELECTRON_RUN_AS_NODE 不继承 —— 继承会让子进程里的 node 其实是 Electron', async () => {
    const before = process.env.ELECTRON_RUN_AS_NODE
    process.env.ELECTRON_RUN_AS_NODE = '1'
    try {
      const r = await spawn('echo "[${ELECTRON_RUN_AS_NODE}]"', opts())
      expect(r.stdout.trim()).toBe('[]')
    } finally {
      if (before === undefined) delete process.env.ELECTRON_RUN_AS_NODE
      else process.env.ELECTRON_RUN_AS_NODE = before
    }
  })

  it('NODE_OPTIONS 不继承 —— 否则子进程会去抢同一个 --inspect 端口', async () => {
    const before = process.env.NODE_OPTIONS
    process.env.NODE_OPTIONS = '--inspect'
    try {
      const r = await spawn('echo "[${NODE_OPTIONS}]"', opts())
      expect(r.stdout.trim()).toBe('[]')
    } finally {
      if (before === undefined) delete process.env.NODE_OPTIONS
      else process.env.NODE_OPTIONS = before
    }
  })

  it('其余环境变量照常继承', async () => {
    const before = process.env.NEXTCOWORK_SPAWN_PROBE
    process.env.NEXTCOWORK_SPAWN_PROBE = 'kept'
    try {
      const r = await spawn('echo "${NEXTCOWORK_SPAWN_PROBE}"', opts())
      expect(r.stdout.trim()).toBe('kept')
    } finally {
      if (before === undefined) delete process.env.NEXTCOWORK_SPAWN_PROBE
      else process.env.NEXTCOWORK_SPAWN_PROBE = before
    }
  })

  /**
   * 需求:Agent 的 shell 命令默认跟随应用/系统代理 —— `nodeSpawn` 的 `extraEnv`
   * 是那撮代理变量的入口(electron 侧由 `shellProxyEnv` 提供),这里钉两件事:
   * 变量真的进了子进程;同步版和 Promise 版 provider 都被接受。
   */
  describe('extraEnv 注入(代理跟随的入口)', () => {
    it('注入的变量被子进程看到,同步与 Promise 两种 provider 都行', async () => {
      const sync = nodeSpawn(() => '/bin/sh', () => ({ NCW_SPAWN_EXTRA: 'sync' }))
      const r1 = await sync('echo "${NCW_SPAWN_EXTRA}"', opts())
      expect(r1.stdout.trim()).toBe('sync')

      const async = nodeSpawn(() => '/bin/sh', async () => ({ NCW_SPAWN_EXTRA: 'async' }))
      const r2 = await async('echo "${NCW_SPAWN_EXTRA}"', opts())
      expect(r2.stdout.trim()).toBe('async')
    })

    /** ★ 注入不越过用户自己的环境:provider 里带了同名键也不该盖掉 process.env */
    it('注入的键与父进程撞名时不覆盖父进程', async () => {
      const before = process.env.NCW_SPAWN_PARENT
      process.env.NCW_SPAWN_PARENT = 'parent'
      try {
        const s = nodeSpawn(() => '/bin/sh', () => ({ NCW_SPAWN_PARENT: 'injected' }))
        const r = await s('echo "${NCW_SPAWN_PARENT}"', opts())
        expect(r.stdout.trim()).toBe('parent')
      } finally {
        if (before === undefined) delete process.env.NCW_SPAWN_PARENT
        else process.env.NCW_SPAWN_PARENT = before
      }
    })

    /**
     * ★ 代理变量是异步问来的,await 期间 run 可能已被中断 —— 这条钉的是
     * 「不会先把命令跑起来再被杀」:aborted 的 signal 必须连进程都不起。
     */
    it('await 期间 signal 已响时不启动进程,直接按中断抛出', async () => {
      const ac = new AbortController()
      const deferred = new Promise<Record<string, string>>((resolve) => {
        setTimeout(() => {
          ac.abort()
          resolve({})
        }, 20)
      })
      const s = nodeSpawn(() => '/bin/sh', () => deferred)
      await expect(s('echo hi', opts({ signal: ac.signal }))).rejects.toSatisfy(isAbortError)
    })
  })
})

describe.skipIf(process.platform === 'win32')('spawn 超时', () => {
  it('★ 超时回 124(对齐 timeout(1)),并在 stderr 里说人话', async () => {
    const r = await spawn('sleep 30', opts({ timeoutMs: 200 }))
    expect(r.code).toBe(124)
    expect(r.stderr).toContain('超时')
  })

  it('超时时也把整个进程组带走,不留下孤儿', async () => {
    const pidFile = join(root, 'timeout.pid')
    await spawn(`sleep 30 & echo $! > ${pidFile}; wait`, opts({ timeoutMs: 300 }))
    const pid = await waitPidFile(pidFile)
    await expect(waitGone(pid)).resolves.toBe(true)
  })
})

describe.skipIf(process.platform === 'win32')('spawn 转后台', () => {
  it('★ detach 后立刻返回已有输出,进程原样交出且超时不再生效', async () => {
    const detach = new AbortController()
    const p = spawn('echo early; sleep 0.6; echo late', { ...opts({ timeoutMs: 300 }), detach: detach.signal })
    await new Promise((r) => setTimeout(r, 150))
    detach.abort()
    const r = await p
    expect(r.detached).toBeDefined()
    expect(r.stdout).toContain('early')
    // 交出去的管道继续出数据;超时 300ms 已经卸掉,进程能活到自己退出
    let tail = ''
    r.detached!.stdout.on('data', (chunk: Buffer) => { tail += chunk.toString('utf8') })
    // 交出来的流是 pause 过的:接手方负责 resume(见 node-spawn 的 onDetach)
    r.detached!.stdout.resume()
    r.detached!.stderr.resume()
    const exit = await r.detached!.exited
    expect(exit.code).toBe(0)
    expect(tail).toContain('late')
  })

  it('交出去的进程 kill 时带走整棵树', async () => {
    const pidFile = join(root, 'detach.pid')
    const detach = new AbortController()
    const p = spawn(`sleep 30 & echo $! > ${pidFile}; wait`, { ...opts(), detach: detach.signal })
    const pid = await waitPidFile(pidFile)
    detach.abort()
    const r = await p
    r.detached!.kill()
    await expect(waitGone(pid)).resolves.toBe(true)
  })

  it('已经跑完的命令不会被 detach 改写成「转走了」', async () => {
    const detach = new AbortController()
    const r = await spawn('echo done', { ...opts(), detach: detach.signal })
    detach.abort()
    expect(r.detached).toBeUndefined()
    expect(r.code).toBe(0)
  })
})

describe.skipIf(process.platform === 'win32')('spawn 中断', () => {
  it('★ 中断**抛出** AbortError,不返回一个普通结果', async () => {
    const ac = new AbortController()
    const p = spawn('sleep 30', opts({ signal: ac.signal }))
    // 等进程真的起来了再中断,否则测的是 spawn 之前那条早退分支
    await new Promise((r) => setTimeout(r, 100))
    ac.abort()
    await expect(p).rejects.toSatisfy(isAbortError)
  })

  it('signal 进来时已经 aborted:立刻抛,连进程都不起', async () => {
    const ac = new AbortController()
    ac.abort()
    await expect(spawn('echo hi', opts({ signal: ac.signal }))).rejects.toSatisfy(isAbortError)
  })

  it('★ 杀的是**整个进程组** —— 孙子进程也必须死', async () => {
    /*
      这条对应 §八 的 3 号风险:只杀那一个 shell 的话,`sh -c "npm run dev"` 里的
      node 还活着、端口还占着,用户看到的是「停止了但项目跑不起来」。
      所以断言的不是 spawn 的返回值,而是**孙子进程的 pid 真的 ESRCH 了**。
    */
    const pidFile = join(root, 'group.pid')
    const ac = new AbortController()
    const p = spawn(`sleep 30 & echo $! > ${pidFile}; wait`, opts({ signal: ac.signal }))
    const pid = await waitPidFile(pidFile)
    // 先确认它真的活着,否则这条测试可能因为进程压根没起来而白绿
    expect(() => process.kill(pid, 0)).not.toThrow()

    ac.abort()
    await expect(p).rejects.toSatisfy(isAbortError)
    await expect(waitGone(pid)).resolves.toBe(true)
  })
})

describe.skipIf(process.platform === 'win32')('spawn 的输出预算', () => {
  it('★ 输出撑爆预算后仍然抽干管道 —— 不抽干的话子进程会阻塞在 write 上永不退出', async () => {
    /*
      这是**行为测试**:断言的核心是「它返回了」。不继续读管道的实现会在这里挂死,
      并被 timeoutMs 兜成 124 —— 所以 code 必须是 0。
    */
    const r = await spawn("head -c 2000000 /dev/zero | tr '\\0' 'a'", opts({ timeoutMs: 20_000 }))
    expect(r.code).toBe(0)
    // 封了顶(最多溢出一个 chunk),而不是把 2MB 全塞进来
    expect(r.stdout.length).toBeGreaterThanOrEqual(512 * 1024)
    expect(r.stdout.length).toBeLessThan(2_000_000)
  }, 30_000)
})

/**
 * ★ 提示词里那句 `Shell: …` 和 bash 工具真正跑命令用的那个 shell,必须是同一个。
 *
 * 这条看起来像在测一个 getter,它测的其实是**别让提示词说谎**:
 * 事实型提示词一旦是假的,比不写更糟 —— 模型不会怀疑它,只会照着
 * 那个不存在的 shell 写语法,然后拿到一条毫无头绪的语法错误。
 */
describe('terminalShellArgs', () => {
  it('POSIX 上以登录 shell 起交互终端,才能读到 ~/.zprofile 里的 PATH(#178)', () => {
    expect(terminalShellArgs('/bin/zsh', 'darwin')).toEqual(['-l'])
    expect(terminalShellArgs('/usr/local/bin/bash', 'linux')).toEqual(['-l'])
    expect(terminalShellArgs('/opt/homebrew/bin/fish', 'darwin')).toEqual(['-l'])
  })

  it('Windows 与不认识的 shell 不加参数', () => {
    expect(terminalShellArgs('powershell.exe', 'win32')).toEqual([])
    expect(terminalShellArgs('/usr/bin/nu', 'linux')).toEqual([])
  })
})

describe('agentShell', () => {
  it('★ nodeHost().platform.shell 和 spawn 真用的 shell 是同一个', () => {
    expect(nodeHost().platform.shell).toBe(agentShell())
  })

  it('给得出一个非空的 shell —— 环境没有 SHELL 时也要有兜底', () => {
    expect(agentShell().length).toBeGreaterThan(0)
  })

  it.each(['darwin', 'linux'])('%s 自动选择优先使用 SHELL，再读账户登录 shell', (platform) => {
    const loginShell = vi.fn(() => '/usr/local/bin/fish')
    expect(agentShell('system', { platform, env: { SHELL: '/bin/zsh' }, loginShell })).toBe('/bin/zsh')
    expect(loginShell).not.toHaveBeenCalled()
    expect(agentShell('system', { platform, env: { SHELL: ' ' }, loginShell })).toBe('/usr/local/bin/fish')
    expect(loginShell).toHaveBeenCalledOnce()
  })

  it.each([['darwin', '/bin/zsh'], ['linux', '/bin/sh']])('%s 账户信息不可用时仍有系统兜底', (platform, fallback) => {
    expect(agentShell('system', { platform, env: {}, loginShell: () => null })).toBe(fallback)
    expect(agentShell('system', { platform, env: {}, loginShell: () => { throw new Error('unavailable') } })).toBe(fallback)
  })

  it('Windows 自动选择尊重 ComSpec，不被 POSIX 的 SHELL 干扰', () => {
    const env = { ComSpec: String.raw`C:\Windows\System32\cmd.exe`, SHELL: '/bin/bash' }
    expect(agentShell('system', { platform: 'win32', env })).toBe(env.ComSpec)
    expect(agentShell('system', { platform: 'win32', env: { COMSPEC: 'custom-cmd.exe' } })).toBe('custom-cmd.exe')
    expect(agentShell('system', { platform: 'win32', env: { ComSpec: ' ' } })).toBe('cmd.exe')
  })

  it.each([
    ['win32', 'cmd', 'cmd.exe'], ['win32', 'powershell', 'powershell.exe'], ['win32', 'pwsh', 'pwsh.exe'],
    ['darwin', 'zsh', 'zsh'], ['darwin', 'bash', 'bash'], ['linux', 'fish', 'fish'], ['linux', 'sh', 'sh'],
    ['linux', 'pwsh', 'pwsh']
  ] as const)('%s 显式选择 %s 不再跟随系统', (platform, preference, executable) => {
    expect(agentShell(preference, { platform, env: { SHELL: '/bin/other', ComSpec: 'other.exe' } })).toBe(executable)
  })

  it('别的平台导入的选项回落到当前系统', () => {
    expect(agentShell('zsh', { platform: 'win32', env: {} })).toBe('cmd.exe')
    expect(agentShell('powershell', { platform: 'darwin', env: { SHELL: '/bin/zsh' } })).toBe('/bin/zsh')
    expect(agentShell('cmd', { platform: 'linux', env: { SHELL: '/bin/bash' } })).toBe('/bin/bash')
  })
})

describe('shell 参数与引号', () => {
  it.each([
    [String.raw`C:\Windows\System32\CMD.EXE`, 'cmd'],
    [String.raw`C:\Program Files\PowerShell\7\pwsh.exe`, 'powershell'],
    ['powershell.exe', 'powershell'], ['/opt/powershell/pwsh', 'powershell'],
    ['/usr/local/bin/fish', 'fish'], ['/bin/zsh', 'posix'], ['/bin/bash', 'posix'], ['/bin/sh', 'posix']
  ])('按可执行文件 %s 判断语法，不按测试机的系统判断', (shell, dialect) => {
    expect(shellDialect(shell)).toBe(dialect)
  })

  it('CMD 使用 /d /s /c，POSIX 与 fish 使用 -c', () => {
    const command = 'echo "a b"'
    expect(shellCommandArgs('cmd.exe', command)).toEqual(['/d', '/s', '/c', `"${command}"`])
    for (const shell of ['/bin/bash', '/bin/zsh', '/bin/sh', '/usr/local/bin/fish']) {
      expect(shellCommandArgs(shell, command)).toEqual(['-c', command])
    }
  })

  it.each(['powershell.exe', 'pwsh.exe', '/usr/local/bin/pwsh'])('%s 使用非交互编码命令并保留退出码', (shell) => {
    const command = 'Write-Output "中文 `"引号`""\nexit 7'
    const args = shellCommandArgs(shell, command)
    expect(args.slice(0, -1)).toEqual(['-NoLogo', '-NoProfile', '-NonInteractive', '-OutputFormat', 'Text', '-EncodedCommand'])
    const script = Buffer.from(args.at(-1)!, 'base64').toString('utf16le')
    expect(script).toContain(`\n${command}\n`)
    expect(script).toContain('[System.Text.UTF8Encoding]::new($false)')
    expect(script).toContain('[Console]::SetOut($writer)')
    expect(script).toContain('[Console]::SetError($writer)')
    expect(script).toContain('[Console]::SetIn(')
    expect(script).toContain('$writer.AutoFlush = $true')
    expect(script).not.toContain('catch {}')
    expect(script).toContain('if (-not $?) { if ($LASTEXITCODE) { exit $LASTEXITCODE }; exit 1 }')
  })

  it('PowerShell 的参数保持字面量，包括单引号、弯引号与命令替换', () => {
    expect(quoteShellArg('pwsh', "O'Brien")).toBe("'O''Brien'")
    expect(quoteShellArg('pwsh', 'a\u2019b')).toBe("'a\u2019\u2019b'")
    const literal = '$env:USER $(Write-Output injected) ` " ;\nnext'
    expect(quoteShellArg('powershell.exe', literal)).toBe(`'${literal}'`)
    expect(quoteShellArg('pwsh', '')).toBe("''")
  })

  it('CMD 包住空格和空参数，拒绝无法安全引号化的字符', () => {
    expect(quoteShellArg('cmd.exe', 'a b')).toBe('"a b"')
    expect(quoteShellArg('cmd.exe', '')).toBe('""')
    expect(quoteShellArg('cmd.exe', 'C:\\Program Files\\')).toBe('"C:\\Program Files\\\\"')
    expect(quoteShellArg('cmd.exe', 'C:\\work\\')).toBe('C:\\work\\')
    expect(quoteShellArg('cmd.exe', 'one(two)')).toBe('"one(two)"')
    for (const arg of ['a&b', '%PATH%', '!value!', '"', 'a\nb', 'a\rb']) {
      expect(() => quoteShellArg('cmd.exe', arg)).toThrow('cannot be quoted')
    }
  })

  it('fish 单引号内的反斜杠与单引号均转义', () => {
    expect(quoteShellArg('/usr/local/bin/fish', "a\\b'c")).toBe("'a\\\\b\\'c'")
  })
})

describe('本地 Shell 配置生效范围', () => {
  it.skipIf(process.platform === 'win32' || !existsSync('/bin/bash'))('新任务使用新选择，已有环境的提示词与执行器保持同一 shell', async () => {
    let selected = '/bin/sh'
    const host = nodeHost({}, () => selected)
    const before = localEnvironment(host, root)
    selected = '/bin/bash'
    const after = localEnvironment(host, root)
    expect(host.platform.shell).toBe('/bin/bash')
    expect(before.platform.shell).toBe('/bin/sh')
    expect(before.facts.shell).toBe('/bin/sh')
    expect(after.platform.shell).toBe('/bin/bash')
    expect((await before.spawn('printf "%s" "$0"', opts())).stdout).toBe('/bin/sh')
    expect((await after.spawn('printf "%s" "$0"', opts())).stdout).toBe('/bin/bash')
    expect((await host.spawn('printf "%s" "$0"', opts())).stdout).toBe('/bin/bash')
  })

  it('终端实际启动的 shell 与环境记录一致，已有终端不被设置变更重启', async () => {
    const { spawn: terminalSpawn } = await import('node-pty')
    vi.mocked(terminalSpawn).mockClear()
    let selected = 'sh'
    const host = nodeHost({}, () => selected)
    const before = localEnvironment(host, root)
    await before.openTerminal({ cwd: root, cols: 80, rows: 24 })
    selected = 'bash'
    const after = localEnvironment(host, root)
    expect(terminalSpawn).toHaveBeenCalledTimes(1)
    await after.openTerminal({ cwd: root, cols: 80, rows: 24 })
    expect(before.terminalShell).toBe('sh')
    expect(after.terminalShell).toBe('bash')
    const login = process.platform === 'win32' ? [] : ['-l']
    expect(terminalSpawn).toHaveBeenNthCalledWith(1, 'sh', login, expect.objectContaining({ cwd: root }))
    expect(terminalSpawn).toHaveBeenNthCalledWith(2, 'bash', login, expect.objectContaining({ cwd: root }))
    expect(terminalSpawn).toHaveBeenCalledTimes(2)
  })

  it('手动选择的 shell 不存在时明确失败，不偷偷切到另一种语法', async () => {
    const run = nodeSpawn(() => join(root, 'missing-shell'))
    const result = await run('echo hello', opts())
    expect(result.code).toBe(127)
    expect(result.stderr).toContain('missing-shell')
  })
})

for (const shell of ['/bin/sh', '/bin/bash', '/bin/zsh']) {
  it.skipIf(process.platform === 'win32' || !existsSync(shell))(`${shell} 实际保留空格、引号、反斜杠与多行参数`, async () => {
    const literal = "中文 a b'c\\d\n$HOME $(printf injected) `printf injected`"
    const result = await nodeSpawn(() => shell)(`printf '%s' ${quoteShellArg(shell, literal)}`, opts())
    expect(result).toEqual({ code: 0, stdout: literal, stderr: '' })
  })
}

describe.skipIf(process.platform !== 'win32')('Windows Shell 真进程', () => {
  it('CMD 能执行命令并返回退出码', async () => {
    const result = await nodeSpawn(() => 'cmd.exe')('echo hello & exit /b 3', opts())
    expect(result.code).toBe(3)
    expect(result.stdout.trim()).toBe('hello')
  })

  it('CMD 原生参数保留空格路径的末尾反斜杠与相邻参数', async () => {
    const args = ['C:\\Program Files\\', 'next', '', 'one(two)']
    const command = [process.execPath, '-e', 'process.stdout.write(JSON.stringify(process.argv.slice(1)))', ...args]
      .map((arg) => quoteShellArg('cmd.exe', arg)).join(' ')
    const result = await nodeSpawn(() => 'cmd.exe')(command, opts())
    expect(result.code).toBe(0)
    expect(JSON.parse(result.stdout)).toEqual(args)
  })

  it('Windows PowerShell 返回 UTF-8 文本与显式退出码', async () => {
    const result = await nodeSpawn(() => 'powershell.exe')('Write-Output "中文"; exit 3', opts())
    expect(result).toEqual({ code: 3, stdout: '中文\r\n', stderr: '' })
  })

  it('PowerShell 保留原生命令退出码，也不会把 cmdlet 错误当成功', async () => {
    const run = nodeSpawn(() => 'powershell.exe')
    expect((await run('cmd.exe /d /c exit 7', opts())).code).toBe(7)
    expect((await run('Write-Error "failure"', opts())).code).toBe(1)
  })
})
