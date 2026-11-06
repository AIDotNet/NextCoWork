import { spawn } from 'node:child_process'
import { constants, promises as fs, type Stats } from 'node:fs'
import { homedir, hostname, userInfo } from 'node:os'
import { createConnection } from 'node:net'
import type { KernelHost } from '../kernel/host'
import { killTree, mergeChildEnv, terminalShellArgs } from '../kernel/node-spawn'
import type { EnvironmentFs, EnvironmentStat, WorkspaceEnvironment } from './contract'
import { EnvironmentError, missingPath } from './errors'
import { createWorkspacePaths } from './paths'

const attributes = (stat: Stats): EnvironmentStat => ({ size: stat.size, mtimeMs: stat.mtimeMs, mode: stat.mode,
  isDir: stat.isDirectory(), isFile: stat.isFile(), isSymbolicLink: stat.isSymbolicLink() })

export function localEnvironment(host: KernelHost, rootPath: string): WorkspaceEnvironment {
  const platform = { ...host.platform }
  const terminalShell = platform.shell
  const filesystem: EnvironmentFs = {
    ...host.fs,
    stat: async (path) => attributes(await fs.stat(path)),
    lstat: async (path) => attributes(await fs.lstat(path)),
    readBytes: async (path, limit = 32 * 1024 * 1024) => {
      const handle = await fs.open(path, 'r')
      try {
        const stat = await handle.stat()
        if (!stat.isFile() || stat.size > limit) throw new EnvironmentError('unsupported')
        const bytes = Buffer.alloc(Math.min(stat.size + 1, limit + 1))
        const { bytesRead } = await handle.read(bytes, 0, bytes.length, 0)
        if (bytesRead > limit) throw new EnvironmentError('unsupported')
        return bytes.subarray(0, bytesRead)
      } finally { await handle.close() }
    },
    writeBytes: async (path, bytes, options = {}) => {
      const handle = await fs.open(path, options.exclusive ? 'wx' : 'w', options.mode ?? 0o600)
      try { await handle.writeFile(bytes); await handle.sync() } finally { await handle.close() }
    },
    mkdir: async (path) => { await fs.mkdir(path, { mode: 0o700 }) },
    copyFile: async (source, destination, maxBytes) => {
      const stat = await fs.stat(source)
      if (!stat.isFile() || stat.size > maxBytes) throw new EnvironmentError('unsupported')
      await fs.copyFile(source, destination, constants.COPYFILE_EXCL)
      return stat.size
    },
    rename: async (source, destination, replace = false) => {
      if (!replace) {
        try { await fs.lstat(destination); throw new EnvironmentError('conflict') } catch (error) { if (!missingPath(error)) throw error }
      }
      await fs.rename(source, destination)
    },
    unlink: (path) => fs.unlink(path),
    rmdir: (path) => fs.rmdir(path)
  }
  return {
    key: 'local', rootPath, generation: 0, remote: false, description: 'Local', fs: filesystem, terminalShell,
    path: createWorkspacePaths(filesystem, platform.os), platform,
    facts: { ...platform, home: homedir(), hostname: hostname(), username: userInfo().username },
    assertReady: () => {},
    spawn: (command, options) => host.spawn(command, { ...options, shell: platform.shell }),
    openTcp: async (hostname, port) => createConnection({ host: hostname, port }),
    openProcess: async (command, args, options) => {
      /*
        需求:后台 shell 和本地钩子与前台 Bash 是同一批「Agent 起的本机命令」,
        代理跟随必须给同一个答案(`npm run dev` 在后台装依赖、钩子里 curl 回调都是
        真出网的)。这条路不经过 `host.spawn`,得在这里自己并一次 —— 合并次序
        (继承 ← 注入不覆盖 ← 显式 env)与 `nodeSpawn` 共用 `mergeChildEnv`,
        「注入不越过用户自己的配置」这条不变式只有一份实现。
      */
      const extra = (await host.childEnv?.()) ?? {}
      const env = mergeChildEnv(process.env, extra, options.env)
      /*
        调用方传 `detached` 表达的需求是**「kill 要带走整棵树」**,两件事因此分开:

        ★ Windows 上**不给 spawn 传 `detached`**:libuv 会把它翻成 `DETACHED_PROCESS`,
          而 Windows 一旦看到这个标志就忽略 `CREATE_NO_WINDOW`(即 `windowsHide`),
          于是每跑一个钩子、每起一条后台 Bash 都会弹出一个独立的控制台窗口。
          对齐 `kernel/node-spawn.ts:169` 的 `detached: !isWindows`。
        ★ 杀树用的是 `tree` 而不是 `detached`:Windows 那条路靠 `taskkill /T`,
          本来就不依赖进程组。混用一个变量的话,Windows 上就退化成「只杀那一个
          shell」——`npm run dev` 的 node 活下来继续占着端口,而 KillShell 报告已停。
      */
      const tree = options.detached === true
      const detached = tree && process.platform !== 'win32'
      const child = spawn(command, [...args], { cwd: options.cwd, env, stdio: 'pipe', windowsHide: true,
        windowsVerbatimArguments: options.windowsVerbatimArguments, shell: false, detached })
      child.stdin.on('error', () => {})
      return { stdin: child.stdin, stdout: child.stdout, stderr: child.stderr,
        exited: new Promise((resolve) => {
          child.once('close', (code, signal) => resolve({ code, signal }))
          child.once('error', () => resolve({ code: null }))
        }),
        /*
          ★ `detached` 时杀整棵树。`sh -c "npm run x"` 只杀 sh 的话，node 会活下来
          继续占着端口 —— 而调用方看到的是「已经 kill 过了」。理由与实现同
          `kernel/node-spawn.ts` 的 `killTree`，那里是同一个问题的另一条路径。
        */
        kill: () => {
          if (tree && child.pid !== undefined) killTree(child.pid, 'SIGTERM')
          else child.kill()
        } }
    },
    openTerminal: async (options) => {
      const pty = await import('node-pty')
      /*
        `options.env` 是插件终端的额外注入(`tabs.openTerminal` 的启动 spec),
        放在默认值之后 —— 插件值赢过默认值;没传时与原行为逐字节一致。
      */
      return pty.spawn(terminalShell, terminalShellArgs(terminalShell), {
        ...options,
        name: 'xterm-256color',
        env: { ...process.env, TERM: 'xterm-256color', ...(options.env ?? {}) } as Record<string, string>
      })
    }
  }
}