import type { Readable, Writable } from 'node:stream'
import type { Socket } from 'node:net'
import type { EnvironmentFacts } from '../../shared/domain/environment'
import type { KernelFs, WorkspaceHost } from '../kernel/host'

export interface EnvironmentStat {
  size: number
  mtimeMs: number
  mode: number
  isDir: boolean
  isFile: boolean
  isSymbolicLink: boolean
}

export interface EnvironmentFs extends KernelFs {
  stat(path: string): Promise<EnvironmentStat>
  lstat(path: string): Promise<EnvironmentStat>
  readBytes(path: string, maxBytes?: number): Promise<Buffer>
  writeBytes(path: string, bytes: Uint8Array, options?: { exclusive?: boolean; mode?: number }): Promise<void>
  copyFile(source: string, destination: string, maxBytes: number, mode?: number): Promise<number>
  mkdir(path: string): Promise<void>
  rename(source: string, destination: string, replace?: boolean): Promise<void>
  unlink(path: string): Promise<void>
  rmdir(path: string): Promise<void>
}

export interface EnvironmentProcess {
  stdin: Writable
  stdout: Readable
  stderr: Readable
  exited: Promise<{ code: number | null; signal?: string | null }>
  kill(): void
}

export interface TerminalDriver {
  write(data: string): void
  resize(cols: number, rows: number): void
  kill(): void
  onData(listener: (data: string) => void): { dispose(): void }
  onExit(listener: (event: { exitCode: number }) => void): { dispose(): void }
}

export interface WorkspaceEnvironment extends WorkspaceHost {
  generation: number
  terminalShell?: string
  fs: EnvironmentFs
  facts: EnvironmentFacts
  assertReady(): void
  /**
   * `detached`：让子进程成为新进程组的组长，于是 `kill()` 能覆盖它派生的一整棵树。
   * ★ 只有钩子传 true —— 它跑的是用户手写的任意命令（`npm run xxx` 会再派生一堆
   *   子进程），超时只杀那一个 shell 会留下一地僵尸。其余调用点行为一字不变。
   *   远程实现忽略这个字段：kill 走 SSH channel，没有本地进程组这回事。
   */
  openProcess(command: string, args: readonly string[], options: { cwd: string; env?: Record<string, string>; detached?: boolean; windowsVerbatimArguments?: boolean }): Promise<EnvironmentProcess>
  /**
   * `env` 是**额外注入**的环境变量(`tabs.openTerminal` 的启动 spec 带来的,
   * 见 `main/terminal-host.ts` 的 launchSpecs),合在实现自己的默认 env 之后
   * —— 插件值赢过默认值。远程实现可以忽略它:一期插件终端仅本地
   * (远程拒绝在 `ipc/plugins.ts` 的 launchTerminal 里做,这里忽略只是兜底)。
   */
  openTerminal(options: { cwd: string; cols: number; rows: number; env?: Record<string, string> }): Promise<TerminalDriver>
  openTcp?(hostname: string, port: number): Promise<Socket>
}

export interface EnvironmentConnection extends Omit<WorkspaceEnvironment, 'rootPath'> {
  close(): Promise<void>
}

export interface EnvironmentLease {
  environment: WorkspaceEnvironment
  release(): void
}