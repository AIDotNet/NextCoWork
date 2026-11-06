import { createHash, randomBytes, randomUUID, timingSafeEqual } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { chmod, mkdtemp, rm, writeFile } from 'node:fs/promises'
import { createConnection, createServer, type Socket } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import type { SshAuthRequest, SshAuthResponse, SshConnectionProfile } from '../../../shared/domain/environment'
import { connectionSecretRef } from '../../../shared/domain/environment'
import type { KernelHost } from '../../kernel/host'
import { EnvironmentError } from '../errors'
import { shellQuote } from './command'

interface AuthPending {
  senderId: number
  request: SshAuthRequest
  ref: string
  /** 这批问答属于哪一次连接尝试。Broker 关掉这次尝试时,按它整批撤掉。 */
  scope: PendingScope
  answer(response: { value?: string; cancelled?: boolean }): void
}

/**
 * 一次连接尝试里挂在界面上的全部问答。
 *
 * ★ 需求(取消正确性):这条连接一旦结束(用户断开、失败、abort),界面上**不能再留着**
 * 它的确认框 —— 点下去只会打到一条已经死掉的链路。`close()` 撤掉自己这批,`cancelWindow()`
 * 撤掉某个窗口的全部批次。
 */
export class PendingScope {
  private readonly ids = new Set<string>()

  /** 登记一条刚挂上去的问答。不登记 = `drain()` 撤不到它。 */
  track(id: string): void {
    this.ids.add(id)
  }

  /** 某条问答已被回答/已从 pending 表里摘掉,不再属于这批。 */
  withdraw(id: string): void {
    this.ids.delete(id)
  }

  /** 从 pending 表里整批摘掉,返回摘下来的那些,交给调用方逐个答"取消"。 */
  drain(pending: Map<string, AuthPending>): AuthPending[] {
    const taken: AuthPending[] = []
    for (const id of [...this.ids]) {
      const entry = pending.get(id)
      if (entry) { pending.delete(id); taken.push(entry) }
    }
    this.ids.clear()
    return taken
  }
}

const AUTH_TIMEOUT_MS = 5 * 60_000
const MAX_AUTH_BYTES = 32 * 1024

/** `ssh -G` 解析出来的、这次连接真正会连到哪儿。字段名对应 `ssh -G` 的输出键。 */
export interface ResolvedSshTarget {
  user?: string
  hostname?: string
  proxyJump?: string
}

/**
 * 存下的密码能不能**不弹窗**直接回给 ssh。
 *
 * 拆成纯函数是因为它是这条链路上唯一有分支的判定,而且判错的两个方向代价都很实:
 * 判松了把密码发给不该发的主机,判紧了用户存了密码却照样天天弹窗。
 *
 * 判定顺序:
 *
 * 1. 只管 `password`。私钥口令按提示哈希分开存(一条链路上多个 hop 的 key 口令本来就不同),
 *    host key 确认和交互式 MFA 永远要人来点。
 * 2. 一次连接尝试只自动答**一次**。存的密码要是过期了,ssh 默认会问三次
 *    (`NumberOfPasswordPrompts`),闷头答三遍同一个错密码,用户看到的是"卡住了"——
 *    而不是"密码不对"。第二次就回落弹窗,并告诉用户已保存的那个被拒了。
 * 3. `ssh -G` 的结果还没到就不答(fail closed)。没有它就无从判断这个提示来自哪台主机。
 * 4. 没配 ProxyJump → 整条链路只有一跳,提示必然来自目标机,直接答。
 * 5. 配了 ProxyJump → 提示文本必须指向目标机。否则**第一个**密码提示来自跳板机,
 *    无条件自动答等于把目标机的密码交给跳板机。
 *
 * ★ 第 5 条不是安全边界。`password` 方法的提示由本地 ssh 生成、可信,但
 * `keyboard-interactive`(PAM)的提示文本**由服务器控制** —— 一台恶意跳板机可以伪造一段
 * 含目标主机名的提示骗过匹配。它挡的是"把密码误发给一台用户并未打算认证的主机",
 * 挡不住一台用户已经选择信任并路由经过的主机。
 */
export function shouldAutoAnswer(input: {
  kind: SshAuthRequest['kind']
  prompt: string
  resolved?: ResolvedSshTarget
  alreadyUsed: boolean
}): boolean {
  if (input.kind !== 'password' || input.alreadyUsed) return false
  const resolved = input.resolved
  if (!resolved) return false
  if (!resolved.proxyJump || resolved.proxyJump === 'none') return true
  const hostname = resolved.hostname
  if (!hostname) return false
  const prompt = input.prompt.toLowerCase()
  const user = resolved.user
  return prompt.includes(hostname.toLowerCase()) || (user !== undefined && prompt.includes(`${user}@${hostname}`.toLowerCase()))
}

/**
 * 凭据存哪个槽位,由**提示的类型**决定。
 *
 * ★ 密码统一存 `connection:<id>:password` —— 和连接表单里那个密码框是**同一份**。
 * 用户在弹窗里勾「记住」,之后就能在表单里看到"已保存"、也能在那里清掉;反过来
 * 表单里填的密码,ssh 问的时候直接拿来答。两套并存的话,表单清空了而弹窗存的那份还在,
 * 界面上既看不到也删不掉。
 *
 * 私钥口令和 challenge 仍按提示哈希分开存:一条链路上多个 hop 的 key 各有各的口令,
 * 挤进一个槽位就会互相覆盖。
 */
function credentialRef(profile: SshConnectionProfile, kind: SshAuthRequest['kind'], prompt: string): string {
  if (kind === 'password') return connectionSecretRef(profile.id, 'password')
  return `connection:${profile.id}:prompt:${createHash('sha256').update(prompt).digest('hex')}`
}

export class SshAuthBroker {
  private readonly pending = new Map<string, AuthPending>()

  constructor(private readonly secrets: KernelHost['secrets'], private readonly notify: (senderId: number, request: SshAuthRequest) => void) {}

  async respond(senderId: number, response: SshAuthResponse): Promise<void> {
    const pending = this.pending.get(response.id)
    if (!pending || pending.senderId !== senderId) throw new EnvironmentError('approval-expired')
    if (response.value !== undefined && (typeof response.value !== 'string' || response.value.length > 8192 || response.value.includes('\0'))) {
      throw new EnvironmentError('invalid-profile')
    }
    this.pending.delete(response.id)
    pending.scope.withdraw(response.id)
    try {
      if (response.cancelled) { pending.answer({ cancelled: true }); return }
      const value = response.useSaved && pending.request.hasSaved ? await this.secrets.get(pending.ref) : response.value
      if (value == null || (pending.request.kind === 'host-key' && value !== 'yes' && value !== 'no')) {
        pending.answer({ cancelled: true }); return
      }
      if (response.remember && pending.request.canRemember) await this.secrets.set(pending.ref, value)
      pending.answer({ value })
    } catch (error) { pending.answer({ cancelled: true }); throw error }
  }

  cancelWindow(senderId: number): void {
    const scopes = new Set<PendingScope>()
    for (const pending of this.pending.values()) if (pending.senderId === senderId) scopes.add(pending.scope)
    for (const scope of scopes) for (const pending of scope.drain(this.pending)) pending.answer({ cancelled: true })
  }

  /**
   * 给内置 SSH 客户端用的一次询问。不经过系统 ssh。
   *
   * 需求:Windows 上那条 ssh2 连接自己问密码,而不是再起一个 ssh.exe。
   * 存过的密码直接返回,不弹窗;没有,或调用方声明上一次被拒了,才弹现有的询问框。
   * 弹出来的框和系统 ssh 走 SSH_ASKPASS 时是同一个,勾「记住」写的也是同一个槽位。
   */
  ask(senderId: number, profile: SshConnectionProfile, prompt: string, options: { rejected?: boolean; scope?: PendingScope } = {}): Promise<string> {
    const scope = options.scope ?? new PendingScope()
    const ref = credentialRef(profile, 'password', prompt)
    const canRemember = profile.authMethod !== 'ask' && this.secrets.available()
    return (async () => {
      const stored = canRemember && options.rejected !== true ? await this.secrets.get(ref) : null
      if (stored !== null && options.rejected !== true) return stored
      const id = randomUUID()
      const request: SshAuthRequest = {
        id, connectionId: profile.id, connectionName: profile.name, prompt, kind: 'password',
        canRemember, hasSaved: stored !== null, ...(options.rejected === true ? { savedRejected: true } : {})
      }
      return await new Promise<string>((resolve, reject) => {
        this.pending.set(id, { senderId, request, ref, scope, answer: (answer) => {
          scope.withdraw(id)
          if (answer.cancelled || answer.value === undefined) reject(new EnvironmentError('cancelled'))
          else resolve(answer.value)
        } })
        scope.track(id)
        this.notify(senderId, request)
      })
    })()
  }

  /**
   * 内置客户端的主机密钥确认。**只有明确的「是」返回 `true`。**
   *
   * ★ 复用与系统 ssh 同一个确认框、同一个 `host-key` 语义:界面只回 `yes`/`no`,
   * `respond()` 里那条 `value !== 'yes' && value !== 'no'` 的强校验继续挡着任意串。
   *
   * 槽位(`connection:hostkey:<hash>`)由 `BundledSshClient` 自己读写,这里只负责问。
   * `prompt` 里带的是**真实指纹**,不是连接表单里存的那个密码 —— 确认框只会显示它,
   * 也不提供「使用已保存的凭据」:主机确认读不出、更不该复用一个存下的密码。
   */
  askHostKey(senderId: number, profile: SshConnectionProfile, prompt: string, options: { scope?: PendingScope } = {}): Promise<boolean> {
    const scope = options.scope ?? new PendingScope()
    const id = randomUUID()
    const request: SshAuthRequest = { id, connectionId: profile.id, connectionName: profile.name, prompt, kind: 'host-key', canRemember: false, hasSaved: false }
    return new Promise<boolean>((resolve) => {
      this.pending.set(id, { senderId, request, ref: '', scope, answer: (answer) => {
        scope.withdraw(id)
        resolve(answer.cancelled !== true && answer.value === 'yes')
      } })
      scope.track(id)
      this.notify(senderId, request)
    })
  }

  async open(profile: SshConnectionProfile, senderId: number, invocation: { executable: string; appPath?: string }): Promise<{ env: NodeJS.ProcessEnv; close(): Promise<void>; resolve(values: Map<string, string>): void; ask(prompt: string, rejected: boolean): Promise<string>; askHostKey(prompt: string): Promise<boolean>; secrets: KernelHost['secrets'] }> {
    const directory = await mkdtemp(join(tmpdir(), 'ncw-auth-'))
    await chmod(directory, 0o700)
    const endpoint = process.platform === 'win32' ? `\\\\.\\pipe\\ncw-auth-${randomUUID()}` : join(directory, 'socket')
    const token = randomBytes(32).toString('hex')
    const sockets = new Set<Socket>()
    /**
     * 这次连接尝试的自动回答状态。
     *
     * ★ 生命周期正好是**一次连接尝试**:`EnvironmentManager.connect()` 每个 generation
     * 调一次 `open()`,`connectSshEnvironment` 的 close 里配套调 `close()`。所以
     * `usedStoredPassword` 天然是"这次尝试用过没有",不需要另外的复位逻辑。
     */
    const session: { resolved?: ResolvedSshTarget; usedStoredPassword: boolean; scope: PendingScope } = { usedStoredPassword: false, scope: new PendingScope() }
    const server = createServer((socket) => {
      sockets.add(socket)
      socket.setTimeout(AUTH_TIMEOUT_MS, () => socket.destroy())
      let buffer = ''
      let received = false
      let requestId: string | undefined
      socket.on('error', () => {})
      socket.once('close', () => { sockets.delete(socket); if (requestId) this.pending.delete(requestId) })
      socket.on('data', (bytes: Buffer) => {
        if (received) return
        buffer += bytes.toString('utf8')
        if (Buffer.byteLength(buffer) > MAX_AUTH_BYTES) { socket.destroy(); return }
        if (!buffer.includes('\n')) return
        received = true
        let payload: { token?: unknown; prompt?: unknown; hint?: unknown }
        try { payload = JSON.parse(buffer.slice(0, buffer.indexOf('\n'))) as typeof payload } catch { socket.destroy(); return }
        if (typeof payload.token !== 'string' || payload.token.length !== token.length
          || !timingSafeEqual(Buffer.from(payload.token), Buffer.from(token))
          || typeof payload.prompt !== 'string' || payload.prompt.length > 8192) { socket.destroy(); return }
        const prompt = payload.prompt
        /**
         * ★ 不能只认 `SSH_ASKPASS_PROMPT=confirm`。
         *
         * 实测 OpenSSH 10.3:host key 确认走 askpass 时**并不设**这个变量,于是提示掉进
         * `challenge` 分支。后果不是报错而是两层降级:UI 把指纹确认显示成普通输入框;
         * `respond()` 里那条 yes/no 强校验失效,于是用户输入的任意串被回给 ssh,ssh 再问、
         * 再回 —— 隔离 sshd 上实测重问 80+ 次直到超时。所以按**提示内容**识别。
         * OpenSSH 不本地化这些字符串,匹配英文原文是安全的。
         */
        const confirmsHostKey = payload.hint === 'confirm'
          || /\(yes\/no(?:\/\[fingerprint\])?\)\?\s*$/i.test(prompt)
          || /^please type ['"]?yes['"]?, ['"]?no['"]?/i.test(prompt)
          || /^the authenticity of host /i.test(prompt)
        const kind: SshAuthRequest['kind'] = confirmsHostKey ? 'host-key'
          : /^Enter passphrase for key /i.test(prompt) ? 'passphrase' : /\bpassword:\s*$/i.test(prompt) ? 'password' : 'challenge'
        const allowsSavedPassword = profile.authMethod === undefined || profile.authMethod === 'auto' || profile.authMethod === 'password'
        const canRemember = profile.authMethod !== 'ask' && (kind === 'passphrase' || (kind === 'password' && allowsSavedPassword)) && this.secrets.available()
        const ref = credentialRef(profile, kind, prompt)
        requestId = randomUUID()
        const id = requestId
        void (async () => {
          const stored = canRemember ? await this.secrets.get(ref) : null
          if (socket.destroyed) return
          /**
           * ★ 命中就**直接回给 ssh**,不进 `pending`、不 `notify` —— 这正是"表单里填一次密码
           * 之后不再弹窗"的全部实现。自动回答的提示不需要可取消:它同步就结束了,
           * `cancelWindow()` 没有东西可清。
           */
          if (stored !== null && shouldAutoAnswer({ kind, prompt, resolved: session.resolved, alreadyUsed: session.usedStoredPassword })) {
            session.usedStoredPassword = true
            requestId = undefined
            socket.end(`${JSON.stringify({ value: stored })}\n`)
            return
          }
          const request: SshAuthRequest = { id, connectionId: profile.id, connectionName: profile.name, prompt, kind,
            canRemember, hasSaved: stored !== null,
            // 存了密码、这次尝试也用过了,却又问一次 —— 只能是服务器把它拒了
            ...(kind === 'password' && session.usedStoredPassword ? { savedRejected: true } : {}) }
          this.pending.set(id, { senderId, request, ref, scope: session.scope, answer: (answer) => {
            this.pending.delete(id)
            socket.end(`${JSON.stringify(answer)}\n`)
          } })
          session.scope.track(id)
          this.notify(senderId, request)
        })().catch(() => socket.destroy())
      })
    })
    server.maxConnections = 8
    try {
      await new Promise<void>((resolve, reject) => { server.once('error', reject); server.listen(endpoint, () => resolve()) })
      if (process.platform !== 'win32') await chmod(endpoint, 0o600)
      let executable = invocation.executable
      if (process.platform === 'win32') {
        /**
         * 需求:Windows 上(开发态和打包版都要)用存下的密码连得上去。
         *
         * ssh 每问一次密码,就把 SSH_ASKPASS 指向的东西**再起一个进程**,而在 Windows 上
         * 那个进程只能是 exe:OpenSSH 走 `CreateProcessW(NULL, cmdline, …)`
         * (`contrib/win32/win32compat/w32fd.c` 的 `spawn_child_internal`),`.cmd` 与 `#!`
         * 脚本都起不来 —— 下面 Unix 那层 `sh` 包装在这里没有对等物。
         *
         * 所以 helper 是**同一个 exe 的 node 形态**(`ELECTRON_RUN_AS_NODE=1`,见 env),
         * 入口就是这个脚本。必须绕开 Chromium 的两条理由都很实:
         *
         * 1. ★ stdout 上除了密码**一个字节都不能有**。OpenSSH 只认第一行
         *    (`readpass.c`:`buf[strcspn(buf, "\r\n")] = '\0'`),Chromium 启动时先写的
         *    那个换行会把密码整个吃掉 —— 表现为「密码明明是对的却认证失败」,
         *    而且没有任何一条错误提到 askpass。
         * 2. ★ 不能和主窗口抢同一个 Chromium profile(两边都是 `~/.next-cowork`)。
         *    第二个实例卡在 profile 上,表现为连接一直转圈直到超时。
         *
         * ★ SSH_ASKPASS **可以带参数**:它的值被原样拼进命令行(`build_commandline_string`
         * 对已经加了引号的串照抄),ssh 只在末尾追加提示文本。于是还能多兜两层:
         * - `--user-data-dir=`:万一哪天 `runAsNode` fuse 被关掉、exe 退回 GUI 形态
         *   (由 `entry.ts` 的 askpass 分支接住),它至少不会卡在第 2 条上;
         * - 末尾的 `--`:终止开关解析。提示文本在 keyboard-interactive 下**由远端 sshd
         *   完全控制**,绝不能落到 Chromium 的开关位置上(同 Unix 分支的理由)。
         *
         * 路径直接加引号即可:Windows 文件名里不可能有 `"`。
         */
        const client = join(directory, 'askpass.js')
        await writeFile(client, windowsAskpassClient(), { mode: 0o600 })
        executable = `"${invocation.executable}" "${client}" --user-data-dir="${join(directory, 'profile')}" --`
      } else {
        // ★ OpenSSH 以 execlp(askpass, askpass, msg) 调用,msg 在 keyboard-interactive 下**由远端 sshd
        //   完全控制**;而打包形态的 executable 就是 Electron 本体,Chromium 会抢在我们的 JS 之前把它
        //   当开关解析(`--remote-debugging-port=` 之类)。始终包一层 sh 并用 `--` 终止开关解析。
        executable = join(directory, 'askpass')
        const target = invocation.appPath
          ? `${shellQuote(invocation.executable)} ${shellQuote(invocation.appPath)}`
          : shellQuote(invocation.executable)
        await writeFile(executable, `#!/bin/sh\nexec ${target} -- "$@"\n`, { mode: 0o700 })
      }
      /**
       * ★ token 走 0600 文件而**不进环境变量**。
       *
       * ssh 把整个环境交给 ProxyCommand / Match exec / KnownHostsCommand / LocalCommand
       * 跑的 `/bin/sh -c`,而用户 ssh_config 里一条 `SendEnv *` 会把它直接送到远端服务器。
       * 实测 `-o 'SendEnv=-*'` **清不掉**配置里的 `SendEnv *`(SendEnv 是累加列表,`-` 只
       * 从当前已累积的列表里移除,而命令行先于配置文件解析),OpenSSH 也没有 `SendEnv none`。
       * 所以只能换通道:env 里留的是路径,远端就算拿到这两个路径也读不到本机文件;
       * helper 与我们同机同用户,读文件零成本。
       */
      const secret = join(directory, 'token')
      await writeFile(secret, token, { mode: 0o600 })
      return {
        env: { SSH_ASKPASS: executable, SSH_ASKPASS_REQUIRE: 'force', DISPLAY: process.env.DISPLAY ?? ':0',
          NCW_SSH_ASKPASS: '1', NCW_SSH_AUTH_ENDPOINT: endpoint, NCW_SSH_AUTH_SECRET: secret,
          /*
            ★ 只能放进 **ssh 自己的环境**:Windows 没有 `sh` 可以包一层,helper 的环境
            就是 ssh 的环境(OpenSSH 用 `CreateProcessW(…, lpEnvironment = NULL, …)`
            起 askpass,子进程原样继承)。代价是 ProxyCommand 之类的兄弟进程也会看到它 ——
            对非 Electron 程序无意义,所以 `sshProcessEnvironment` 只对 overrides 放开这一个名字。
          */
          ...(process.platform === 'win32' ? { ELECTRON_RUN_AS_NODE: '1' } : {}) },
        /**
         * 传输层解析完 `ssh -G` 之后把结果交过来。
         *
         * ★ 时序是有保证的:`OpenSshTransport.connect()` 先跑 `ssh -G`(只解析配置,
         * 从不认证),再跑那个会认证的主连接。所以任何密码提示都发生在这次调用之后。
         * 万一哪天顺序变了,`shouldAutoAnswer` 在 `resolved` 缺失时判否 —— fail closed,
         * 退化成弹窗,不会拿一个来路不明的提示去对密码。
         */
        resolve: (values) => {
          session.resolved = { user: values.get('user'), hostname: values.get('hostname'), proxyJump: values.get('proxyjump') }
        },
        ask: (prompt: string, rejected: boolean) => this.ask(senderId, profile, prompt, { rejected, scope: session.scope }),
        /**
         * 内置客户端(Windows 上那条 ssh2 连接)的主机密钥确认。和上面的密码询问共用这一批
         * `session.scope`,所以 `close()` 一并把两者从界面上撤掉 —— 断开之后按下的「批准」
         * 不会去启动一条已经结束的连接。
         */
        askHostKey: (prompt: string) => this.askHostKey(senderId, profile, prompt, { scope: session.scope }),
        secrets: this.secrets,
        close: async () => {
          /**
           * ★ 先撤问答,再关套接字/服务器。
           *
           * 断开链路的顺序是 `client.close()` → `authentication.close()`,而 `close()` 是
           * 最后一步;到这一步还没有人答的确认框(用户在慢慢看指纹,或窗口已经关了)必须
           * 立刻判否,而不是等那 5 分钟超时。
           */
          for (const pending of session.scope.drain(this.pending)) pending.answer({ cancelled: true })
          for (const socket of sockets) socket.destroy()
          await new Promise<void>((resolve) => server.close(() => resolve()))
          /*
            ★ 删不掉也要往下走。这是整条断开链路的**最后一步**(`ssh/provider.ts` 的
            `close()`),从这里抛出去的错会变成「断不开连接」。而 Windows 上真会删不掉:
            GUI 兜底形态留下的 `profile/` 是 Chromium 的 profile,它握着句柄时
            `force` 也救不了。代价只是临时目录里留下一份垃圾,由系统清理。
          */
          await rm(directory, { recursive: true, force: true }).catch(() => {})
        }
      }
    } catch (error) { server.close(); await rm(directory, { recursive: true, force: true }); throw error }
  }
}

/**
 * Windows 上 ssh 真正执行的那个 helper 的源码(每次连接尝试生成一份到临时目录)。
 *
 * 需求:它要在**不带 Chromium 的 node 形态**里跑完整条问答 —— 理由写在 `open()` 里那段
 * `win32` 注释。那个形态下拿不到应用的任何模块(入口就是这一个文件),所以它是下面
 * `requestAskpass` 的第二份实现,说的是同一套线上协议。
 *
 * ★ 两份必须一起改。`askpass.test.ts` 按 ssh 的调法真的把这个脚本跑起来对答案,
 * 改坏了那条用例会挂 —— 而线上坏掉的表现只是「密码不对」,不会有任何报错指向这里。
 */
export function windowsAskpassClient(): string {
  return `'use strict'
/* 由 src/main/environment/ssh/askpass.ts 生成,不是版本库里的文件。
   约定:提示是 argv 的最后一个;stdout 的第一行就是答案,别的一个字节都不许写。 */
const { createConnection } = require('node:net')
const { readFileSync } = require('node:fs')

const endpoint = process.env.NCW_SSH_AUTH_ENDPOINT
const secretPath = process.env.NCW_SSH_AUTH_SECRET
const prompt = process.argv[process.argv.length - 1] || ''
/* 失败一律用非 0 退出码,绝不往 stdout 写空行:OpenSSH 把非 0 当成「没问到」并放弃,
   而一行空字符串会被当成**一个空密码**送给服务器(readpass.c 只取第一行)。 */
const fail = () => { process.exit(1) }
if (!endpoint || !secretPath || prompt.length > 8192) fail()
let token = ''
try { token = readFileSync(secretPath, 'utf8') } catch (error) { fail() }
if (!token) fail()
let buffer = ''
let answered = false
const socket = createConnection(endpoint)
socket.setTimeout(${AUTH_TIMEOUT_MS}, fail)
socket.on('error', fail)
socket.on('close', () => { if (!answered) fail() })
socket.on('connect', () => {
  socket.write(JSON.stringify({ token: token, prompt: prompt, hint: process.env.SSH_ASKPASS_PROMPT }) + '\\n')
})
socket.on('data', (bytes) => {
  buffer += bytes.toString('utf8')
  if (Buffer.byteLength(buffer) > ${MAX_AUTH_BYTES}) fail()
  const end = buffer.indexOf('\\n')
  if (end < 0) return
  let reply = null
  try { reply = JSON.parse(buffer.slice(0, end)) } catch (error) { fail() }
  if (!reply || reply.cancelled || typeof reply.value !== 'string') fail()
  answered = true
  socket.destroy()
  /* ★ 写完再退。stdout 到管道是异步写,直接 process.exit 会把还在队列里的密码丢掉 ——
     表现为 ssh 收到空密码,而这边看起来一切正常。 */
  process.stdout.write(reply.value + '\\n', () => { process.exit(0) })
})
`
}

export function requestAskpass(env: NodeJS.ProcessEnv, prompt: string): Promise<string> {
  const endpoint = env.NCW_SSH_AUTH_ENDPOINT
  const secret = env.NCW_SSH_AUTH_SECRET
  if (!endpoint || !secret || prompt.length > 8192) return Promise.reject(new EnvironmentError('authentication'))
  let token: string
  try { token = readFileSync(secret, 'utf8') } catch { return Promise.reject(new EnvironmentError('authentication')) }
  if (!token) return Promise.reject(new EnvironmentError('authentication'))
  return new Promise((resolve, reject) => {
    const socket = createConnection(endpoint)
    let buffer = ''
    let completed = false
    socket.setTimeout(AUTH_TIMEOUT_MS, () => socket.destroy(new EnvironmentError('timeout')))
    socket.once('connect', () => socket.write(`${JSON.stringify({ token, prompt, hint: env.SSH_ASKPASS_PROMPT })}\n`))
    socket.once('error', reject)
    socket.once('close', () => { if (!completed) reject(new EnvironmentError('authentication')) })
    socket.on('data', (bytes: Buffer) => {
      buffer += bytes.toString('utf8')
      if (Buffer.byteLength(buffer) > MAX_AUTH_BYTES) { socket.destroy(); return }
      if (!buffer.includes('\n')) return
      try {
        const reply = JSON.parse(buffer.slice(0, buffer.indexOf('\n'))) as { value?: unknown; cancelled?: boolean }
        if (reply.cancelled || typeof reply.value !== 'string') throw new EnvironmentError('cancelled')
        completed = true
        resolve(reply.value)
      } catch (error) { reject(error) } finally { socket.destroy() }
    })
  })
}
