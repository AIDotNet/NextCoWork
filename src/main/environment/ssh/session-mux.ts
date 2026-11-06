/**
 * Windows 上那条唯一的 SSH 会话里的应用层多路复用。
 *
 * 需求:一次连接只认证一次。macOS / Linux 以及 Git for Windows 自带的 ssh 靠
 * ControlMaster,后续命令搭在同一条已认证的连接上,远端什么都不用装。
 * 系统自带的 Windows OpenSSH 没有 Unix socket,`ControlMaster` 一开就是
 * `getsockname failed: Not a socket` —— 这台机器上每一份 ssh 都这样时,才退到这里。
 * 不满足会怎样:表现为连一台机器要弹三次密码框,对话框里每输入一次又弹一次。
 *
 * 做法是那一次 ssh 在远端起一个只用标准库的 Python 进程,之后所有 exec / SFTP /
 * TCP 转发都是它的一条通道。远端没有 Python 时 `remoteMuxCommand` 打出 `NCW-MUX-MISSING`,
 * 调用方退回「每条命令一次 ssh」。
 *
 * 故意不做:终端。终端要的是 PTY,这条字节流给不了,终端面板仍各自起 ssh。
 */
import { createConnection, createServer, type Socket } from 'node:net'
import { PassThrough, type Readable, type Writable } from 'node:stream'
import type { EnvironmentProcess } from '../contract'
import { EnvironmentError } from '../errors'
import { shellQuote } from './command'

const BANNER = Buffer.from('\0NCW-MUX-1\n')
const MAX_FRAME = 8 * 1024 * 1024

const OPEN_EXEC = 1
const OPEN_SFTP = 2
const STDIN = 3
const STDIN_EOF = 4
const CLOSE = 5
const OPEN_TCP = 6

const STDOUT = 1
const STDERR = 2
const EXIT = 3
const ERROR = 4

/**
 * 远端实际执行的脚本。只依赖 Python 标准库,并且必须是纯 ASCII:
 * `remoteMuxCommand` 把它 base64 之后塞进 `python -c`。
 *
 * ★ stdout 在 banner 之前一个字节都不能有。调用方按 banner 判断「这条会话活了」,
 * 前面多一个警告就会被当成会话没起来,退回每条命令一次 ssh —— 表现为修了跟没修一样。
 * 所以启动参数带 `-S`(不跑 site)和 `-u`(不缓冲)。
 */
const MUX_SCRIPT = String.raw`
import os, sys, struct, subprocess, threading, signal, socket, shutil
MAGIC = b'\0NCW-MUX-1\n'
OPEN_EXEC, OPEN_SFTP, STDIN, STDIN_EOF, CLOSE, OPEN_TCP = 1, 2, 3, 4, 5, 6
STDOUT, STDERR, EXIT, ERROR = 1, 2, 3, 4
MAX = 8 * 1024 * 1024
out = sys.stdout.buffer
inp = sys.stdin.buffer
write_lock = threading.Lock()
chans = {}

def send(sid, kind, payload=b''):
    body = struct.pack('>IB', sid, kind) + payload
    if len(body) > MAX:
        return
    frame = struct.pack('>I', len(body)) + body
    with write_lock:
        out.write(frame)
        out.flush()

def read_exact(n):
    buf = b''
    while len(buf) < n:
        chunk = inp.read(n - len(buf))
        if not chunk:
            return None
        buf += chunk
    return buf

def find_sftp():
    for path in ('/usr/lib/openssh/sftp-server', '/usr/libexec/openssh/sftp-server', '/usr/libexec/sftp-server', '/usr/lib/ssh/sftp-server'):
        if os.path.isfile(path) and os.access(path, os.X_OK):
            return path
    return shutil.which('sftp-server')

def spawn(argv=None, shell_cmd=None):
    kwargs = {'stdin': subprocess.PIPE, 'stdout': subprocess.PIPE, 'stderr': subprocess.PIPE}
    if os.name != 'nt':
        kwargs['start_new_session'] = True
    if shell_cmd is not None:
        return subprocess.Popen(shell_cmd, shell=True, **kwargs)
    return subprocess.Popen(argv, **kwargs)

def pump_pipe(sid, pipe, kind):
    try:
        while True:
            data = pipe.read(65536)
            if not data:
                break
            send(sid, kind, data)
    except Exception:
        pass
    finally:
        try:
            pipe.close()
        except Exception:
            pass

def wait_proc(sid, proc, pumps):
    code = proc.wait()
    for thread in pumps:
        thread.join()
    send(sid, EXIT, struct.pack('>i', -1 if code is None else int(code)))
    chans.pop(sid, None)

def track(sid, proc):
    chans[sid] = {'proc': proc, 'sock': None}
    pumps = [
        threading.Thread(target=pump_pipe, args=(sid, proc.stdout, STDOUT), daemon=True),
        threading.Thread(target=pump_pipe, args=(sid, proc.stderr, STDERR), daemon=True)
    ]
    for thread in pumps:
        thread.start()
    threading.Thread(target=wait_proc, args=(sid, proc, pumps), daemon=True).start()

def kill(ch):
    proc = ch.get('proc')
    if proc is not None and proc.poll() is None:
        if os.name != 'nt':
            try:
                os.killpg(proc.pid, signal.SIGKILL)
                return
            except Exception:
                pass
        try:
            proc.kill()
        except Exception:
            pass
    sock = ch.get('sock')
    if sock is not None:
        try:
            sock.close()
        except Exception:
            pass

def write_in(ch, payload):
    sock = ch.get('sock')
    if sock is not None:
        try:
            sock.sendall(payload)
        except Exception:
            pass
        return
    proc = ch.get('proc')
    if proc is not None and proc.stdin is not None:
        try:
            proc.stdin.write(payload)
            proc.stdin.flush()
        except Exception:
            pass

def eof_in(ch):
    sock = ch.get('sock')
    if sock is not None:
        try:
            sock.shutdown(socket.SHUT_WR)
        except Exception:
            pass
        return
    proc = ch.get('proc')
    if proc is not None and proc.stdin is not None:
        try:
            proc.stdin.close()
        except Exception:
            pass

def pump_sock(sid, sock):
    try:
        while True:
            data = sock.recv(65536)
            if not data:
                break
            send(sid, STDOUT, data)
    except Exception:
        pass
    finally:
        send(sid, EXIT, struct.pack('>i', 0))
        chans.pop(sid, None)
        try:
            sock.close()
        except Exception:
            pass

def handle(sid, kind, payload):
    if kind == OPEN_EXEC:
        try:
            proc = spawn(shell_cmd=payload.decode('utf-8', 'replace'))
        except Exception as exc:
            send(sid, ERROR, str(exc).encode('utf-8', 'replace'))
            return
        track(sid, proc)
        return
    if kind == OPEN_SFTP:
        path = find_sftp()
        if not path:
            send(sid, ERROR, b'sftp-server not found')
            return
        try:
            proc = spawn(argv=[path])
        except Exception as exc:
            send(sid, ERROR, str(exc).encode('utf-8', 'replace'))
            return
        track(sid, proc)
        return
    if kind == OPEN_TCP:
        try:
            text = payload.decode('utf-8')
            host, sep, port_s = text.partition('\0')
            if sep == '' or host == '':
                raise ValueError('bad address')
            sock = socket.create_connection((host, int(port_s)), timeout=15)
        except Exception as exc:
            send(sid, ERROR, str(exc).encode('utf-8', 'replace'))
            return
        chans[sid] = {'proc': None, 'sock': sock}
        threading.Thread(target=pump_sock, args=(sid, sock), daemon=True).start()
        return
    ch = chans.get(sid)
    if ch is None:
        return
    if kind == STDIN:
        write_in(ch, payload)
    elif kind == STDIN_EOF:
        eof_in(ch)
    elif kind == CLOSE:
        kill(ch)

def main():
    out.write(MAGIC)
    out.flush()
    while True:
        header = read_exact(4)
        if header is None:
            break
        (length,) = struct.unpack('>I', header)
        if length < 5 or length > MAX:
            break
        body = read_exact(length)
        if body is None:
            break
        sid, kind = struct.unpack('>IB', body[:5])
        try:
            handle(sid, kind, body[5:])
        except Exception as exc:
            send(sid, ERROR, str(exc).encode('utf-8', 'replace'))

try:
    main()
except Exception:
    sys.exit(1)
`

/** 测试与 `remoteMuxCommand` 共用同一段启动代码,避免测的和远端跑的不是同一份。 */
export function muxPythonLaunchCode(): string {
  const payload = Buffer.from(MUX_SCRIPT, 'utf8').toString('base64')
  return `import base64,sys; exec(base64.b64decode("${payload}").decode())`
}

/**
 * 交给 ssh 的远端命令。远端 shell 是 POSIX sh(OpenSSH 对 Linux 主机的默认)。
 *
 * ★ 命令里不能有单引号:整段 `python -c` 的参数用单引号包住,base64 本身不含单引号,
 * 所以这里不再做额外转义。改脚本时如果引入了单引号,远端 shell 会把命令截断,
 * 表现为 Windows 上连接直接失败、而 macOS 一切正常。
 */
export function remoteMuxCommand(): string {
  const inline = shellQuote(muxPythonLaunchCode())
  return [
    `if command -v python3 >/dev/null 2>&1; then exec python3 -S -u -c ${inline}; fi`,
    `if command -v python >/dev/null 2>&1; then exec python -S -u -c ${inline}; fi`,
    'echo NCW-MUX-MISSING >&2',
    'exit 127'
  ].join('\n')
}

export const MUX_MISSING = 'NCW-MUX-MISSING'

interface MuxChannel {
  onStdout(bytes: Buffer): void
  onStderr(bytes: Buffer): void
  onExit(code: number): void
  onError(message: string): void
}

export interface MuxByteStream {
  stdin: Writable
  stdout: Readable
  stderr: Readable
}

/**
 * 一条已经认证过的字节流上的多路复用客户端。
 *
 * `onDead` 只在 banner 已经出现之后、会话又断了的时候调用。banner 之前断掉是
 * 「没建起来」(远端没有 Python,或者 ssh 自己认证失败),由 `ready` 拒绝来表达,
 * 不能当成一次掉线 —— 否则调用方还没决定退不退回旧路径,连接就被拆了。
 */
export class SessionMux {
  readonly ready: Promise<void>
  private readonly channels = new Map<number, MuxChannel>()
  private buffer = Buffer.alloc(0)
  private nextId = 1
  private bannerSeen = false
  private dead = false
  private resolveReady!: () => void
  private rejectReady!: (error: Error) => void

  constructor(private readonly input: Writable, output: Readable, private readonly onDead: () => void) {
    this.ready = new Promise<void>((resolve, reject) => {
      this.resolveReady = resolve
      this.rejectReady = reject
    })
    output.on('data', (chunk: Buffer) => this.ingest(chunk))
    output.on('error', (error: Error) => this.die(error))
    output.on('end', () => this.die(new Error('mux ended')))
  }

  get failed(): boolean { return this.dead }

  private ingest(chunk: Buffer): void {
    this.buffer = Buffer.concat([this.buffer, chunk])
    if (!this.bannerSeen) {
      const index = this.buffer.indexOf(BANNER)
      if (index < 0) {
        // 正常的 banner 只有 11 字节。远端在它前面吐了一大段,就不是我们的进程。
        if (this.buffer.length > 4096) this.die(new Error('mux banner missing'))
        return
      }
      this.buffer = this.buffer.subarray(index + BANNER.length)
      this.bannerSeen = true
      this.resolveReady()
    }
    while (this.buffer.length >= 4) {
      const length = this.buffer.readUInt32BE(0)
      if (length < 5 || length > MAX_FRAME) { this.die(new Error('mux frame')); return }
      if (this.buffer.length < 4 + length) return
      const body = this.buffer.subarray(4, 4 + length)
      this.buffer = this.buffer.subarray(4 + length)
      this.dispatch(body.readUInt32BE(0), body.readUInt8(4), body.subarray(5))
    }
  }

  private dispatch(id: number, kind: number, payload: Buffer): void {
    const channel = this.channels.get(id)
    if (!channel) return
    if (kind === STDOUT) channel.onStdout(payload)
    else if (kind === STDERR) channel.onStderr(payload)
    else if (kind === ERROR) { this.channels.delete(id); channel.onError(payload.toString('utf8')) }
    else if (kind === EXIT) {
      this.channels.delete(id)
      channel.onExit(payload.length >= 4 ? payload.readInt32BE(0) : -1)
    }
  }

  private die(error: Error): void {
    if (this.dead) return
    this.dead = true
    if (!this.bannerSeen) this.rejectReady(error)
    else this.onDead()
    for (const channel of this.channels.values()) channel.onError(error.message)
    this.channels.clear()
    this.input.destroy()
  }

  private send(id: number, kind: number, payload: Buffer = Buffer.alloc(0)): void {
    if (this.dead) return
    const body = Buffer.alloc(5 + payload.length)
    body.writeUInt32BE(id, 0)
    body.writeUInt8(kind, 4)
    payload.copy(body, 5)
    const frame = Buffer.alloc(4 + body.length)
    frame.writeUInt32BE(body.length, 0)
    body.copy(frame, 4)
    this.input.write(frame)
  }

  private open(kind: number, payload: Buffer): { id: number; stdout: PassThrough; stderr: PassThrough; exited: Promise<number> } {
    if (this.dead) throw new EnvironmentError('disconnected')
    const id = this.nextId++
    const stdout = new PassThrough()
    const stderr = new PassThrough()
    let settle!: (code: number) => void
    const exited = new Promise<number>((resolve) => { settle = resolve })
    let done = false
    const finish = (code: number, error?: string): void => {
      if (done) return
      done = true
      this.channels.delete(id)
      if (error !== undefined && error !== '') stderr.write(error)
      stdout.end()
      stderr.end()
      settle(code)
    }
    this.channels.set(id, {
      onStdout: (bytes) => { stdout.write(bytes) },
      onStderr: (bytes) => { stderr.write(bytes) },
      onExit: (code) => finish(code),
      onError: (message) => finish(127, message)
    })
    this.send(id, kind, payload)
    return { id, stdout, stderr, exited }
  }

  exec(command: string, signal: AbortSignal, timeoutMs: number): Promise<{ code: number; stdout: string; stderr: string }> {
    signal.throwIfAborted()
    const opened = this.open(OPEN_EXEC, Buffer.from(command, 'utf8'))
    const stdout: Buffer[] = []
    const stderr: Buffer[] = []
    let length = 0
    return new Promise((resolve, reject) => {
      let settled = false
      const finish = (error?: Error): void => {
        if (settled) return
        settled = true
        clearTimeout(timer)
        signal.removeEventListener('abort', abort)
        if (error) { this.send(opened.id, CLOSE); reject(error); return }
        void opened.exited.then((code) => {
          resolve({ code, stdout: Buffer.concat(stdout).toString('utf8'), stderr: Buffer.concat(stderr).toString('utf8') })
        })
      }
      const abort = (): void => finish(new EnvironmentError('cancelled'))
      const timer = setTimeout(() => finish(new EnvironmentError('timeout')), timeoutMs)
      signal.addEventListener('abort', abort, { once: true })
      const take = (target: Buffer[], bytes: Buffer): void => {
        length += bytes.byteLength
        if (length > MAX_FRAME) finish(new EnvironmentError('unsupported', 'SSH output limit exceeded'))
        else target.push(bytes)
      }
      opened.stdout.on('data', (bytes: Buffer) => take(stdout, bytes))
      opened.stderr.on('data', (bytes: Buffer) => take(stderr, bytes))
      opened.stdout.on('end', () => finish())
      opened.stdout.on('error', (error: Error) => finish(error))
    })
  }

  openProcess(command: string, input?: string): EnvironmentProcess {
    const opened = this.open(OPEN_EXEC, Buffer.from(command, 'utf8'))
    const stdin = new PassThrough()
    if (input !== undefined) this.send(opened.id, STDIN, Buffer.from(input, 'utf8'))
    stdin.on('data', (chunk: Buffer) => this.send(opened.id, STDIN, chunk))
    stdin.on('end', () => this.send(opened.id, STDIN_EOF))
    return {
      stdin, stdout: opened.stdout, stderr: opened.stderr,
      exited: opened.exited.then((code) => ({ code, signal: null })),
      kill: () => this.send(opened.id, CLOSE)
    }
  }

  /** 远端 `sftp-server`。找不到时通道以 127 退出,stderr 里是原因,会话本身不断。 */
  openSubsystem(): MuxByteStream {
    const opened = this.open(OPEN_SFTP, Buffer.alloc(0))
    const stdin = new PassThrough()
    stdin.on('data', (chunk: Buffer) => this.send(opened.id, STDIN, chunk))
    stdin.on('end', () => this.send(opened.id, STDIN_EOF))
    stdin.on('close', () => this.send(opened.id, CLOSE))
    return { stdin, stdout: opened.stdout, stderr: opened.stderr }
  }

  /**
   * 返回的是本机 loopback 上的一只 socket,而不是多路复用通道本身。
   *
   * 需求:调用方(undici)要一只真正的 `net.Socket` —— 它会看 `socket.connecting`,
   * 也会把它交给 `tls.connect({ socket })`。直接把通道伪装成 socket,TLS 握不上。
   */
  async openTcp(hostname: string, port: number): Promise<Socket> {
    if (this.dead) throw new EnvironmentError('disconnected')
    const server = createServer()
    await new Promise<void>((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve) })
    const address = server.address()
    if (address === null || typeof address === 'string') { server.close(); throw new EnvironmentError('connection-failed') }
    const client = createConnection({ host: '127.0.0.1', port: address.port })
    return new Promise<Socket>((resolve, reject) => {
      const timer = setTimeout(() => { server.close(); client.destroy(); reject(new EnvironmentError('timeout')) }, 15_000)
      client.once('error', (error) => { clearTimeout(timer); server.close(); reject(error) })
      server.on('connection', (socket) => {
        if (socket.remotePort !== client.localPort) { socket.destroy(); return }
        clearTimeout(timer)
        server.close()
        let opened: ReturnType<SessionMux['open']>
        try { opened = this.open(OPEN_TCP, Buffer.from(`${hostname}\0${String(port)}`, 'utf8')) }
        catch (error) { socket.destroy(); client.destroy(); reject(error instanceof Error ? error : new Error(String(error))); return }
        socket.on('data', (chunk: Buffer) => this.send(opened.id, STDIN, chunk))
        socket.on('end', () => this.send(opened.id, STDIN_EOF))
        socket.on('close', () => this.send(opened.id, CLOSE))
        socket.on('error', () => this.send(opened.id, CLOSE))
        opened.stdout.on('data', (chunk: Buffer) => { socket.write(chunk) })
        opened.stdout.on('end', () => socket.end())
        opened.stderr.on('data', () => {})
        void opened.exited.then(() => socket.destroy())
        resolve(client)
      })
    })
  }

  close(): void {
    if (this.dead) return
    this.dead = true
    for (const channel of this.channels.values()) channel.onExit(-1)
    this.channels.clear()
    this.input.end()
  }
}
