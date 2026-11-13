# 06 · 执行环境与 SSH(多设备执行)

3.0 有三个层次的「在哪里」,设计时必须分清,界面上也要让用户一眼看清:

| 层次 | 是什么 | 例子 |
|---|---|---|
| **控制端(Client)** | 用户操作的界面,**从不执行任何工具** | 桌面端、手机端 |
| **设备(Device)** | `ncw-agent` 运行的地方,持有会话、数据库、模型调用 | 本机(桌面内置)、云端 |
| **执行环境(Environment)** | 工具真正执行、文件真正所在的地方 | 设备自己(`local`)、设备通过 SSH 连上的远程主机(`ssh`) |

```
 控制端                    设备(ncw-agent)                     执行环境
┌────────┐   设备协议    ┌──────────────────┐   ┌─ local:设备自身的文件系统和进程
│ 手机    │─────────────▶│ 本机(用户的电脑)    │───┤
│ 桌面    │              └──────────────────┘   └─ ssh:用户电脑能连到的主机(内网、跳板机后面的机器)
└────────┘              ┌──────────────────┐   ┌─ local:云端沙箱
            ───────────▶│ 云端(nextco.work) │───┤
                        └──────────────────┘   └─ ssh:云端能连到的主机(公网可达、用户授权)
```

**一个项目 = (设备, 执行环境, 根目录)**。同一台远程主机上的同一个目录,从「本机」打开和从「云端」打开是两个项目,
各有各的会话历史。界面上会提示「这个目录也在 云端 中打开过」。

## 1. 关键原则

1. **SSH 连接从设备发起,不从控制端发起**。手机控制本机 Agent 时,能连到的是本机所在网络里的主机,不是手机所在的网络。
   新建 SSH 环境的界面里写明「从 本机 发起连接」。
2. **所有工具都只通过执行环境接口访问文件和进程**。Agent 里的工具代码禁止直接用 `System.IO` 或 `Process` 碰设备本地,
   必须拿到项目的 `IExecutionEnvironment`。这样同一个工具在本机、SSH、云端行为一致。(写进 AGENTS.md)
3. **审批必须显示「在哪执行」**。例如「将在 `dev-server`(SSH,经由 本机)上执行 `rm -rf build/`」。手机上同样显示。
4. **凭据提示也是交互**。SSH 密码、私钥口令、键盘交互式两步验证、主机指纹确认,都以「交互」的形式出现,
   和审批走同一套机制。所以任何已连接的控制端(包括手机)都能回答,先到先得。
5. **不在远程主机上安装任何东西**。3.0 不部署远程 helper,只用标准的 SSH exec、SFTP、PTY 和端口转发。

## 2. 执行环境接口(C#,移植自 2.x 的 `environment/contract.ts`)

```csharp
interface IExecutionEnvironment
{
    EnvironmentId Id { get; }
    int Generation { get; }                     // 断线重连后 +1,旧 generation 发起的操作一律失败
    EnvironmentFacts Facts { get; }             // 操作系统、架构、默认 shell、home、路径风格、git 版本、可用工具(rg、git……)
    IEnvironmentFs Fs { get; }                  // stat / lstat / read / write / mkdir / rename / unlink / list / copy
    Task<IEnvProcess> OpenProcessAsync(CommandSpec spec, CancellationToken ct);   // 带进程树终止
    Task<ITerminal> OpenTerminalAsync(TerminalSpec spec, CancellationToken ct);  // PTY
    Task<Stream> OpenTcpAsync(string host, int port, CancellationToken ct);      // 端口转发(预览开发服务器)
}
```

- **路径类型化**:用 `EnvPath`(带路径风格)而不是 `string`,避免把本机路径和远程路径混用。路径守卫按环境根目录检查。
- **实现**:`LocalEnvironment`(设备本身)、`SshEnvironment`(远程主机)。以后可以加 WSL、容器等。

## 3. SSH 实现

| 方面 | 设计 |
|---|---|
| 库 | SSH.NET(纯托管)。M2 要验证它在 NativeAOT 下能不能用;不行就换库或自研协议层 |
| 连接复用 | 每个 SSH 环境一条连接,exec、SFTP、PTY、端口转发都在上面开 channel(对应 2.x 的 `session-mux.ts`) |
| `~/.ssh/config` | 自己解析设备上的配置:Host、HostName、User、Port、IdentityFile、ProxyJump、IdentitiesOnly。新建环境时可以从配置导入 |
| 跳板机 | ProxyJump 通过在上一跳开 direct-tcpip channel 串起来(对应 2.x 的 `proxy.ts`) |
| 认证 | 私钥(OpenSSH 格式,ed25519 / rsa / ecdsa)、ssh-agent(Unix 上的 `SSH_AUTH_SOCK`、Windows 上的 OpenSSH 命名管道)、密码、键盘交互式。2.x 为了喂密码给系统 OpenSSH 用的 askpass 方案,3.0 不需要了 |
| 主机指纹 | 首次连接时以交互形式让用户确认指纹;已知主机按设备保存。指纹变化时直接拒绝连接,并在界面上给出明确的处理入口 |
| 保存凭据 | 密码和口令可以选择保存,用 `NCK1` 加密存在设备上(云端用 KMS) |
| 远程操作系统 | Linux 和 macOS 用 POSIX shell;Windows 用 OpenSSH + PowerShell。引号转义、环境变量注入、进程树终止(POSIX 用 `setsid` 加进程组,Windows 用 `taskkill /T`)都按 `Facts` 选适配器 |
| 工具降级 | 搜索优先用远程的 `rg`,没有就退回 `grep` / `find`,再不行就用 SFTP 遍历。Git 直接在远程执行 `git` |
| 文件监视 | SFTP 没有变更通知,改为对打开的目录轮询,并在执行工具后主动刷新 |
| 断线 | 保活加指数退避重连。正在执行的工具返回「连接中断」错误给模型;终端显示「重新连接中」横幅。环境状态在客户端的 Store 里按 `(deviceId, environmentId)` 存,断开时标为 `unknown` |
| 端口转发 | 远程开发服务器可以转发到设备上;设备是本机时直接用浏览器打开 `localhost`。手机上不提供 |

## 4. 其他组件在哪个环境里运行

| 组件 | 运行位置 |
|---|---|
| 内置工具(文件、Shell、Git、搜索、终端) | 项目的执行环境 |
| Hooks | 项目的执行环境(和 2.x 一致) |
| MCP 服务器(stdio) | 默认在设备上运行;可以按服务器设置成「在执行环境中运行」,这时通过 SSH exec 的 stdin/stdout 通信 |
| 插件(Jaunt) | Agent 进程内;插件要碰文件或进程,只能通过宿主 API,再由宿主转给执行环境 |
| 浏览器工具 | 设备上 |
| 模型调用 | 设备上 |

## 5. 云端设备上的 SSH

- 云端沙箱访问外网走白名单。用户添加 SSH 主机时,要明确授权放行这个主机和端口。
- 云端的出口 IP 固定并公开,方便用户在自己的防火墙上放行。
- 私钥建议**在云端生成**,界面上展示公钥,让用户加到服务器的 `authorized_keys` 里。也支持上传已有私钥,用 KMS 加密保存。

## 6. 协议新增

- `environments.list / create / update / delete / test / browse`(`browse` 用来在新建项目时浏览远程目录)
- 事件:`environment.status`(connecting / ready / reconnecting / failed / unknown)
- 交互类型:`ssh.hostKey`(确认指纹)、`ssh.credential`(密码 / 口令 / 键盘交互式问答)

## 7. 界面

- **新建项目**:选择设备(本机 / 云端 / 另一台电脑)→ 选择位置(本机目录 / SSH 主机 / 克隆 Git 仓库)→ 浏览并选择目录。
- **项目栏**:SSH 项目的图标右下角有 🔗 标记;连接断开时显示灰色斜线。
- **项目面板头部**:`💻 本机 → 🔗 dev-server · main ✓`,点击可以查看连接详情、重新连接、打开终端。
- **设置 → 执行环境**(按设备分):SSH 主机列表、从 `~/.ssh/config` 导入、测试连接、已知主机、密钥管理。
- **审批卡片和工具卡片**:都带上执行环境的标识。
