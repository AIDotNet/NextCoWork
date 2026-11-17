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
interface IExecutionEnvironment : IAsyncDisposable
{
    EnvironmentId Id { get; }                   // 设备 + 环境;审批卡片和工具卡片都带上它
    EnvironmentKind Kind { get; }               // Local / Ssh
    string DisplayName { get; }
    int Generation { get; }                     // 每次(重)连成功 +1;旧 generation 发起的进程、流、监视器一律以「连接中断」结束
    EnvironmentState State { get; }             // Idle / Connecting / Ready / Reconnecting / Failed / Closed
    event EventHandler<EnvironmentStatusChangedEventArgs>? StatusChanged;   // 对应 environment.status 事件
    EnvironmentFacts Facts { get; }             // 操作系统、架构、默认 shell、home、路径风格、git 版本、可用工具(rg、git……)
    PathGuard Paths { get; }                    // 路径规则与路径守卫(按环境根目录检查,先 realpath 最近的已存在祖先)
    IEnvironmentFs Fs { get; }                  // stat / lstat / realpath / list / read / write / mkdir / rename / delete / copy / watch
    ValueTask EnsureReadyAsync(CancellationToken ct);                              // SSH 惰性连接;Failed 后再调用 = 用户主动重连
    Task<EnvironmentFacts> ProbeToolsAsync(CancellationToken ct);                  // 探测 git / rg / grep / find
    Task<IEnvProcess> OpenProcessAsync(CommandSpec spec, CancellationToken ct);    // 带进程树终止;流式输出;stdin 默认关闭
    Task<ITerminal> OpenTerminalAsync(TerminalSpec spec, CancellationToken ct);    // PTY(本机由 Ncw.Agent.Terminal 注入;SSH 用 ShellStream)
    Task<Stream> OpenTcpAsync(string host, int port, CancellationToken ct);        // 从环境内部发起 TCP(direct-tcpip)
    Task<IPortForward> OpenLocalForwardAsync(string host, int port, CancellationToken ct);  // 环境里的端口 → 设备回环端口(预览开发服务器)
}
```

- **路径类型化**:用 `EnvPath`(带路径风格,构造时规范化)而不是 `string`,避免把本机路径和远程路径混用。风格不一致的路径直接报 `InvalidPath`。
  Windows 风格的规则(盘符、UNC、拒绝盘符相对路径和设备路径、大小写不敏感)在任何宿主上都按 Windows 处理,所以 macOS 上也能正确处理远程 Windows 主机的路径。
- **路径守卫 `PathGuard`**:移植自 2.x 的 `createWorkspacePaths`。解析输入时先对最近的已存在祖先取 realpath、再拼回不存在的尾部,
  所以指向根外的符号链接不能用来写新文件;只有 `NotFound` / `NotDirectory` 才当作「路径不存在」,连接断开等错误原样抛出。
- **进程 `IEnvProcess`**:原始字节流(stdout / stderr)、可选 stdin、`Exited`、`KillAsync`(整棵进程树:先礼貌请求退出,宽限期后强杀)。
  一次性执行用 `ProcessRunner.RunAsync`:流式回调、超时、取消(**取消和超时不抛异常**,返回已读到的输出)、输出截断策略(`IOutputPolicy`:头部 / 尾部 / 头尾 / 不截断,
  默认保留尾部 512 KiB)。超出预算后仍持续读管道,进程退出后只给管道一个宽限期(后台子进程攥着管道时调用不会挂住)。
- **文件系统 `IEnvironmentFs`** 的错误码是结构化的(`NotFound`、`AlreadyExists`、`IsDirectory`、`TooLarge`、`PathEscape`……);`WriteStreamAsync`(临时文件 + 改名,超限当场清理)、
  `CopyFileAsync`、读上限等便捷方法本机和 SSH 共用同一份实现。目录监视本机用系统通知,SSH 轮询,并可 `Refresh()` 主动刷新。
- **环境注册表 `EnvironmentRegistry`**:一台设备上的环境列表和默认环境(`local` 永远存在);每个环境一个复用的运行时实例;不依赖协议层、存储层和界面,持久化由宿主通过 `Snapshot` / `Load` 完成。
- **实现**:`LocalEnvironment`(设备本身,**全项目唯一允许直接用 `System.IO` / `Process` 碰设备本地的执行环境**)、`SshEnvironment`(远程主机)。以后可以加 WSL、容器等。
  契约测试(文件系统、进程)对本机和 SSH 跑同一份用例,保证同一个工具在两种环境里行为一致。

## 3. SSH 实现

| 方面 | 设计 |
|---|---|
| 库 | SSH.NET 2026.0.0(纯托管)。M3 已验证:NativeAOT 发布零裁剪警告,密钥认证、exec、ShellStream、SFTP、host key 回调都可用。它依赖 BouncyCastle ≥ 2.7.0,中央包版本已同步升级 |
| 连接复用 | 每个 SSH 环境**一条控制连接 + 一条 SFTP 连接**,在环境存活期间复用:exec、PTY、端口转发、跳板机隧道都在控制连接上开 channel(对应 2.x 的 `session-mux.ts`)。SSH.NET 没有公开在同一条 SSH 会话上再开 SFTP 通道的办法,所以 SFTP 用同一个环境的第二条连接;凭据在内存里缓存,不会二次提示 |
| `~/.ssh/config` | 自己解析设备上的配置:Host、HostName、User、Port、IdentityFile、ProxyJump、IdentitiesOnly。新建环境时可以从配置导入 |
| 跳板机 | ProxyJump 通过在上一跳开 direct-tcpip channel(`ForwardedPortLocal`,绑定回环端口)串起来;**每一跳都按它的真实主机名校验指纹**;跳板机名字也按 ssh_config 解析(别名、它自己的 ProxyJump 链,检测环路)。到第一跳之前可以经过 HTTP CONNECT / SOCKS4 / SOCKS5 代理(对应 2.x 的 `proxy.ts`,握手由 SSH.NET 完成) |
| 认证 | 私钥(OpenSSH 格式,ed25519 / rsa / ecdsa;有口令时经凭据回调询问并按环境缓存)、密码、键盘交互式、ssh-agent(Unix 上的 `SSH_AUTH_SOCK`、Windows 上的 OpenSSH 命名管道,见 §10.7)。`Auto` 模式不会一上来就问密码:私钥被拒绝后先发一个 `none` 请求确认服务器接受密码,再问(最多 3 次,和 OpenSSH 的 `NumberOfPasswordPrompts` 一致)。2.x 为了喂密码给系统 OpenSSH 用的 askpass 方案,3.0 不需要了 |
| 主机指纹 | 首次连接时以交互形式让用户确认指纹;已知主机按设备保存(按 `host` 或 `[host]:port` 命名,与 OpenSSH known_hosts 同形;按算法钉扎)。**指纹先于一切凭据**:这台主机(这一跳)还没有钉扎时,先只取指纹并中止握手(此时没有发出任何凭据,**也没有问过解私钥的口令**),用户确认并钉扎后再问口令、正式认证——所以用户在核对「这台主机是不是我要连的那台」之前不会被要求输入任何东西,等用户回答也不会撞上握手超时。指纹变化时直接拒绝连接(不弹确认,不覆盖旧指纹),把「出示的指纹 vs 钉扎的指纹」放进环境状态(界面据此展示不一致),处理入口是「忘记这台主机」(§10.4) |
| 保存凭据 | 密码和口令可以选择保存,用 `NCK1` 加密存在设备上(云端用 KMS)。ref 见 §10.3;**明文只在 Agent 里,永远不进任何 DTO / 事件 / 错误 / 日志** |
| 远程操作系统 | Linux 和 macOS 用 POSIX shell;Windows 用 OpenSSH + PowerShell。引号转义、环境变量注入、进程树终止都按 `Facts` 选适配器:POSIX 上 sshd 为每个 exec 请求新建会话和进程组,包装脚本一开始把自己的 pid(即进程组 id)写到 stderr 开头的 `NCW-PID:<n>` 行(被剥掉,调用方看不到),终止时在控制连接的另一条 channel 上 `kill -s TERM -- -pid`,宽限期后 `KILL`;Windows 用 `taskkill /T /F`。环境变量不放命令行(会出现在远端 `ps` 里),作为 stdin 第一行传给包装脚本。本机进程树终止见 §8 |
| 工具降级 | 搜索优先用远程的 `rg`,没有就退回 `grep` / `find`,再不行就用 SFTP 遍历。Git 直接在远程执行 `git` |
| 文件监视 | SFTP 没有变更通知,改为对打开的目录轮询,并在执行工具后主动刷新 |
| 断线 | 保活加指数退避重连(`ReconnectPolicy`:初始 1 秒、上限 30 秒、±20% 抖动;认证失败、指纹变化/被拒绝、配置错误、用户取消这类重试无意义的错误直接进入 `Failed`)。每次重连成功 `Generation` +1。正在执行的进程以 `ConnectionLost` 结束(退出码未知,不报成正常退出),写类文件操作在请求可能已经发出时报 `ResultUnknown`;重连期间新发起的操作最多等 `ReadyWait`(默认 15 秒)再报 `Disconnected`。终端显示「重新连接中」横幅。环境状态在客户端的 Store 里按 `(deviceId, environmentId)` 存,断开时标为 `unknown` |
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

- `environments.list / status / create / update / remove / setDefault / connect / disconnect / test / browse / importSshConfig / knownHosts / forgetHostKey` 和主题 `environments`(全量状态 + 单调 `Rev`:每个环境的种类、状态 `idle / connecting / ready / reconnecting / failed`、最近错误 key、延迟、是否默认)。方法表、角色、错误码见 [09 §8.6](09-protocol.md),实现见 §10。
- 交互类型:`ssh_host_key`(首次出现的主机指纹确认)、`ssh_credential`(密码 / 口令 / 键盘交互式问答),走 `interactions` 主题和 `interactions.respond`(09 §8.3),与审批同一套机制。
- 项目与环境的绑定:`projects.add` 带 `EnvironmentId`,`projects.list` / `ProjectDto` 带 `EnvironmentId`。
- 早先草案里的 `environment.status` 事件被 `environments` 主题的全量状态取代(规则 5:「运行中」类状态一律全量权威广播 + 单调版本号,首帧和增量同一个类型)。

## 7. 界面

- **新建项目**:选择设备(本机 / 云端 / 另一台电脑)→ 选择位置(本机目录 / SSH 主机 / 克隆 Git 仓库)→ 浏览并选择目录。
- **侧边栏**:SSH 项目的名字后面写环境名;连接断开时项目行右侧是断网图标(03 §1)。
- (原「项目面板头部」随 2026-11 的侧边栏重设计去掉;分支在检查器的 Git 页签,打开终端在项目菜单的「新建终端」。)
- **设置 → 执行环境**(按设备分):SSH 主机列表、从 `~/.ssh/config` 导入、测试连接、已知主机、密钥管理。
- **审批卡片和工具卡片**:都带上执行环境的标识。

## 8. 实现备注(M3)

`src/Ncw.Agent.Environments`,命名空间 `Ncw.Agent.Environments`(SSH 部分 `Ncw.Agent.Environments.Ssh`)。偏离或补充上文设计的地方:

- **SFTP 单独一条连接**:见 §3「连接复用」。
- **本机进程树终止基于「后代快照」而非进程组**:.NET 的 `Process` 不能让子进程自成进程组(2.x 用 `detached` + `kill(-pid)`)。POSIX 上先对整棵树的快照(Linux 读 `/proc`,macOS 用 `ps`)发 SIGTERM,宽限期内没退干净的再 SIGKILL;
  先拍快照再发信号,根进程先死也不会漏掉它的子孙。代价:自己 `setsid` / 双重 fork 脱离的守护进程杀不到(进程组方式同样杀不到 `setsid` 的)。Windows 用 `Process.Kill(entireProcessTree: true)`。
- **SSH 的「退出」以收到 exit-status 为准,不等 channel 关闭**:后台子进程攥着 stdout 时 channel 会一直开着,调用方不能被它挂住(和本机一致)。
- **SSH.NET 的输入流在「从没写过数据」时释放不会发出 EOF**:关闭 stdin 靠包装脚本把 stdin 重定向到 `/dev/null`(环境变量头读完之后)。
- **符号链接**:SSH.NET 的 SFTP 删除/改名会先对路径做 realpath,对符号链接就变成操作它的目标。所以链接的删除、改名,以及 POSIX 主机上的递归删除,经 exec 的 `rm` / `mv` 完成(不跟随链接)。`LStat` 靠「规范路径是否等于规范父目录加名字」识别链接,悬空链接从父目录列表里找。
- **手填目标的跳板机沿用目标的私钥**(`ssh -J` 不会,但手填表单里没有地方给每一跳单独指定私钥);ssh_config 里的别名仍然各用各的 `IdentityFile`。
- **`ProxyCommand` 不支持**(不执行任意命令作为传输),遇到时报 `Unsupported`;ssh_config 的 `Match` 只支持 `all` / `host` / `originalhost` / `user`,`exec` 等整块忽略并在结果里给出警告。
- **本机 PTY 不在本项目里**:`LocalEnvironment.OpenTerminalAsync` 需要宿主注入 `TerminalFactory`(`Ncw.Agent.Terminal` 的 `LocalTerminals.CreateFactory()`,见 §9);没注入时报 `Unsupported`。SSH 终端用 `ShellStream`,只在 POSIX 主机上提供。`SshTerminal.KillAsync` 会自己把「已终止」报到 `Exited`(§9 的终端管理器依赖它)。
- **未实现 / 未验证**:Windows 远程主机的真机验证(命令构造有单测,但没有 Windows sshd 可测)、Windows 本机的进程树终止与 `cmd` / PowerShell 方言的真机验证。

## 9. 终端(M3,`Ncw.Agent.Terminal`)

`src/Ncw.Agent.Terminal`,命名空间 `Ncw.Agent.Terminal`。两层:**本机 PTY**(`LocalEnvironment` 的 `TerminalFactory` 注入物)和**终端管理器 `TerminalManager`**(设备上的终端会话表,本机和 SSH 环境共用)。
只依赖 `Ncw.Agent.Environments`(`ITerminal`、`TerminalSpec`、`EnvPath`、`IExecutionEnvironment`);不引用协议层、存储层、Host、Avalonia。本项目和执行环境实现一样,是 AGENTS.md §5「直接碰设备本地进程」的例外
(PTY 本来就是 `LocalEnvironment` 的一部分,只是拆出来);工具、Hooks、插件宿主仍然只能经 `IExecutionEnvironment.OpenTerminalAsync` 拿终端。

### 9.1 PTY 方案评估(结论:自己实现)

路线图风险表要求「先评估现成的库」。评估方式:查 NuGet 包的 TFM、依赖、是否带原生二进制;对 `Ghostflyby.Pty`、`Porta.Pty` 做了 osx-arm64 的 NativeAOT 发布加跑一个 shell 的实测,其余只检查了包内容。

| 库 | 状态 | 结论 |
|---|---|---|
| Pty.Net(`0.1.16-pre`,2021 年后无更新) | netstandard2.0 / net46;Windows 走 **winpty**(不是 ConPTY),带 winpty 的二进制 | 排除:停更,Windows 方案过时 |
| Porta.Pty `2.2.4`(Pty.Net 的分支,32 万下载) | net10.0,MIT;POSIX 部分是**预编译的原生垫片** `libporta_pty`(linux / osx 的 x64 与 arm64,**没有 musl**);Windows 依赖 `Microsoft.Windows.Console.ConPTY` 包里的 `conpty.dll` + `OpenConsole.exe`。NativeAOT osx-arm64 发布零警告、能跑 | 排除:不可审计的原生二进制进了供应链(AGENTS.md §6「整个仓库只用 C#」的精神),且缺 musl(云端沙箱可能是 Alpine) |
| MiniPty `1.7.0` | net10.0,MIT,标榜 NativeAOT 友好;同样带 `libminipty_unix` 和 `minipty_spawn_helper` 原生二进制,Windows 需要另配 ConPTY 库 | 排除:同上 |
| Ghostflyby.Pty `1.0.1`(纯 C# P/Invoke,Apache-2.0) | net10.0;NativeAOT osx-arm64 发布零警告、能跑;但 POSIX 路径是**在托管代码里 `fork`**(包里有 `ForkLock` / `ForkHangException` / `ForkNoGcBudget` 之类的防护),约 4 千下载、单一维护者 | 排除:fork 之后子进程里只剩调用线程,.NET 运行时(GC 挂起、托管线程池)的状态是按多线程设计的,偶发死锁是原理性风险;终端是高频、长期运行的功能,不能把它押在这上面 |
| Terminal.Pty / PtySharp / Quick.PtyNet | 夹带 `conpty.dll` / `OpenConsole.exe` 二进制 / 只有 net8 且没有 Windows / 基于 winpty | 不适用 |

**自己实现**,理由:(1) 保持全 C#、零原生二进制;(2) 需要对进程树终止、退出码、反压、UTF-8 边界、cwd 与环境变量有完全的控制;(3) 实际要写的东西不多(POSIX、ConPTY 各数百行),而且 `posix_spawn` 让「托管 fork」这个最大的风险根本不存在。

### 9.2 本机 PTY

`LocalTerminals.CreateFactory(LocalPtyOptions?)` 返回 `LocalEnvironmentOptions.TerminalFactory` 的委托。`LocalPtyOptions.ChildEnvironment` 应和 `LocalEnvironmentOptions.ChildEnvironment` 传同一个委托(跟随系统代理等)。
启动参数的解析(`PtyLaunch`,所有平台共用):环境变量合并次序 继承 ← 注入(不覆盖用户自己已有的)← `TERM=xterm-256color` ← 调用方显式给的(2.x `local.ts` 的次序);POSIX 没有任何 locale 变量时补 `LANG`(macOS `en_US.UTF-8`、Linux `C.UTF-8`,
桌面启动的应用常常没有 locale,不补的话 bash 把多字节字符当问号);认得 `-l` 的 shell 以登录 shell 启动(2.x `terminalShellArgs`:桌面启动的应用继承的 PATH 只有系统那几条);PowerShell 加 `-NoLogo`。

**POSIX(Linux / macOS)**
- 创建:`posix_openpt`(`O_CLOEXEC`)→ `grantpt` / `unlockpt` → 父进程自己打开从设备、`TIOCSWINSZ` 设好初始大小(macOS 对「还没有人打开从设备」的主设备做 `TIOCSWINSZ` 会 `ENOTTY`),再 `posix_spawn` 启动 shell,spawn 返回后立刻关掉父进程的从设备(否则子进程退出时读端收不到 EOF)。
  属性:`SETSID`(新会话)、`SETSIGDEF`(把运行时设成忽略的 `SIGPIPE` 等重置成默认,被忽略的信号会跨 exec 保留)、`SETSIGMASK`(清空)、macOS 另加 `CLOEXEC_DEFAULT`;文件动作**在子进程里 setsid 之后**打开从设备并 dup 到 0/1/2、`chdir`
  ——「会话首进程打开一个还没有控制终端的 tty」就是它的控制终端,所以 Ctrl-C、作业控制、`SIGWINCH` 都按真终端工作(实测平台:macOS arm64、Linux aarch64 的 glibc(Ubuntu 24.04)与 musl(Alpine)容器;x86-64 没有实测,只是常量和 ABI 按规范取值)。托管代码一行都不会在子进程里跑。老 glibc(< 2.29)没有 `addchdir_np`,退到 `sh -c 'cd -- "$0" && exec "$@"'`。
- 读:专用线程 `poll`(50ms 超时只用来检查停止标志)+ `read`,输出进一条 64 块 × 16 KiB 的有界通道:消费者慢时读线程停下,内核 PTY 缓冲写满,子进程自然被反压(托管内存不会无限涨)。`PtyStream` 是双向流,读 = 终端输出,写 = 键盘输入(写到子进程不读的终端会阻塞,所以放在线程池;子进程被杀后以 `EIO` 返回)。
- 退出:专用线程 `waitpid`;退出码来自内核,被信号杀死报 `128 + 信号` 并带 `Signal`。`ITerminal.Exited` 在「进程已退出 **且** 输出已读完」时完成;后台进程还攥着从设备时没有 EOF,所以进程退出后输出安静 100ms 或最多 2 秒就收尾;消费者一直不读输出时,进程退出后最多再等 5 秒也会完成(通道里已缓存的仍可读到 EOF)。
- 终止:**后代快照**(Linux 读 `/proc`,macOS 用 `ps`)+ **进程组**:先对快照里的每个进程和 `-pgid` 发 `SIGHUP` + `SIGTERM`(交互式 shell 忽略 `SIGTERM` 但响应 `SIGHUP`),宽限期(默认 2 秒)后 `SIGKILL`。shell 自己是进程组组长,前台任务一定在组里;
  作业控制开出来的其它进程组靠快照。和 `ProcessTree`(§8)一样,自己 `setsid` 脱离的守护进程杀不到。
- **ioctl 是变参函数**:Apple Silicon 的 ABI 把变参放在栈上而 P/Invoke 按寄存器传,所以 macOS arm64 上 `TIOCSWINSZ` 用「前面补 6 个占位参数」的声明;其它平台按普通声明。
- P/Invoke 全部 `LibraryImport` 源生成,不用反射。NativeAOT 发布零警告:osx-arm64 和 linux-arm64(Ubuntu 24.04 容器里发布)都实测过,发布出来的二进制真的跑通了「开 PTY → 管理器 → 写命令 → 读输出 → 退出码」(linux 是在没有 `libc6-dev` 的裸 Ubuntu 24.04 里跑的,`libc` 的解析没问题;需要系统装了 libicu,因为本项目不启用 `InvariantGlobalization`)。win-x64 只做了 ILLink 裁剪分析(零警告),没有做 NativeAOT 发布(不支持跨系统)。验证用的是临时的最小程序,没有改 `Ncw.Agent.csproj`。

**Windows(ConPTY)——没有在 Windows 真机上验证**
- `CreatePseudoConsole`(Windows 10 1809+;更老的系统 `LocalTerminals.IsSupported` 为假、开终端报 `Unsupported`)+ 两条匿名管道;子进程用 `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` 挂上去。flags 用 0(不用 `INHERIT_CURSOR`:它会让伪控制台启动时向终端查光标位置,没人回答就挂住)。
- **进程树**:子进程创建时就经 `PROC_THREAD_ATTRIBUTE_JOB_LIST` 放进一个 `KILL_ON_JOB_CLOSE` 的作业对象,终止 = `TerminateJobObject`,整棵树一次杀掉,宿主崩溃时也不留孤儿。
- 退出:shell 退出后伪控制台不会自己关管道,要 `ClosePseudoConsole` 才有输出 EOF;等 100ms 让最后的输出写进管道再关,读线程继续排空(旧版 Windows 上「关闭时没人读输出管道」会死锁)。
- 命令行引号(`CommandLineToArgvW` 规则)和环境块(按名字不区分大小写排序、双 NUL 结尾)是纯字符串逻辑,在所有平台上有单测;其余只做过编译检查,`WindowsConPtyTests` 里的真机用例只在 Windows 上执行。**第一次在 Windows CI 上跑,预计需要微调。**

### 9.3 终端管理器 `TerminalManager`

移植自 2.x 的 `agent/server/terminals.ts`(会话表、合批、环形缓冲、游标、限额)与 `main/terminal-host.ts`(创建、`prepare / approve`、一次性启动规格、取消),合成一个逻辑层。**本机和 SSH 走同一个接口**,只依赖 `IExecutionEnvironment.OpenTerminalAsync`。

- **会话**:id、作用域(项目 id)、**环境标识(设备 + 环境 + 名字 + 种类)**、标题(目录名)、cwd、shell、大小、状态 `Running → Exited`(**终态单调**:退出之后迟到的输出和第二个退出事件一律丢弃;同一个 id 重新开是新的一段,序号和缓冲接着走,订阅者续订)、退出码 / 信号 / 退出原因(`Exited` / `Killed` / `ConnectionLost`)。
- **输出事件流**:字节而不是字符串(09 的 `terminal/<id>` 主题本来就是二进制;多字节字符可能被切在两批之间,由显示端的流式解码器处理)。合批 16ms / 64 KiB,每批一个递增 `Seq`;**退出事件也占一个序号**,所以订阅者看到的序号严格连续,有缺口就是丢了。
- **环形缓冲**(`getBuffer`):最近 256 KiB,精确到字节,从中间裁时对齐到 UTF-8 字符边界(不从一个多字节字符的中间开始),`Truncated` 标记。
- **游标续传 `Attach(id, sink, sinceSeq?)`**:取快照和挂订阅在同一把锁里,之后的事件一条不漏也不重复。`sinceSeq` 之后的批都还完整地在日志里 → 只补发错过的事件(`Resumed`),否则给整个缓冲(控制端重置显示)。
- **限额**:每作用域 8、总共 32(正在创建的也算);已退出的保留 10 分钟供回看,之后回收,还在跑的永不回收。
- **输入**:一次最多 64 KiB(空输入 `InvalidArgument`、超限 `TooLarge`);写之前检查环境连接代(`EnsureGeneration`)和环境状态,断线后旧终端的句柄一律失败,不会「复活」到新连接上。
- **创建的取消**:创建中途被取消(关页签、控制端离开、设备停止)时,迟到才开出来的 PTY 会被立刻释放(= 终止),不留孤儿。
- **一次性启动规格**(插件终端 `tabs.openTerminal`):`SetLaunchSpec(终端 id, 环境变量 + argv)`,创建**本机**终端时环境变量注入 PTY,启动行写进 shell(按 shell 方言加引号;CLI 退出后终端仍是活 shell);用后即删,TTL 60 秒。远程环境**绝不消费**它(环境变量注入不进 PTY,而且里面可能有密钥)。

**远程(SSH)环境的 `prepare / approve`**(移植自 2.x):SSH 环境里开终端等于在别人的机器上拿到一个 shell,所以**每个终端**要先逐个批准——`PrepareAsync` 返回带**执行环境标识**(设备 + 环境)、shell、目录的意图(此时什么都没启动),发起它的控制端 `Approve` 得到一次性凭据,凭它 `CreateAsync`。
凭据绑定控制端、终端 id、作用域、环境实例、连接代、目录,60 秒过期;意图最多 64 个;别的控制端回答别人的意图得到「已过期」(不泄漏它是否存在)。远程终端只有创建它的控制端能看、写、杀、列出,控制端离开(`ReleaseController`)时它名下的意图、进行中的创建、远程终端全部作废。本机终端属于设备,不受控制端离开影响。
**为什么不走 `InteractionGate`**:它是 Run 作用域的(要 `RunId`、随 Run 结束清理),而这个握手是用户在界面上点开一个终端的意图,不是模型的一次工具调用;所以本层自己管,不依赖 `Ncw.Agent.Extensions`。控制端界面仍然是「审批卡片」,带环境标识。

权限与租约不在本层:`WriteAsync / ResizeAsync / KillAsync` 由 Host 在租约闸(02 A2)之后调用;控制端标识只用来判断远程终端的归属。

### 9.4 Host 接线(M4 已完成)

实现:`Ncw.Agent.Host/Terminals`(`TerminalHandlers`、`TerminalTopicSource`、`TerminalMapping`、`TerminalControllers`)和 `WorkspaceToolsRuntime.Terminals`,协议见 [09 §8.4 工作区工具](09-protocol.md) 与 `Ncw.Protocol/Methods/TerminalMethods.cs`。下面 1–5 点**都已做**;租约闸(`write / resize / kill` 在租约闸之后)**未做**:member 即可写,多个控制端同时写同一个终端不互斥。控制端标识用 `CallContext.Principal`(本机 `local`,远程为对端公钥指纹),一个控制端的最后一条连接结束才 `ReleaseController`;本机 PTY 不可用时设备不声明 `terminals` 能力。

1. `new LocalEnvironment(deviceId, new LocalEnvironmentOptions { TerminalFactory = LocalTerminals.CreateFactory(new LocalPtyOptions { ChildEnvironment = … }) })`,与 `ChildEnvironment` 共用同一个委托。
2. 设备上一个 `TerminalManager` 单例;`TerminalCreateRequest` 的环境和根目录由 Host 按项目登记解析(项目 → 设备 + 环境 + 根目录)。
3. 订阅 `terminal/<id>` 主题:`Attach` 的快照 = 主题快照(二进制),`TerminalOutput.Seq` = 主题 `seq`(游标),`resync` 时控制端不带游标重订阅;发送缓冲满按 09 §8.2 的规则对 `terminal/*` 发 `resync`(终端输出没有「可丢的增量」,所有批都是结构性的)。
4. 控制端断开时调 `ReleaseController`;项目删除调 `CloseScopeAsync`;设备停止调 `ShutdownAsync`。
5. 能力协商:`LocalTerminals.IsSupported` 为假(Windows 10 1809 以前)时不声明 `terminals` 能力;云端手机端的「只读查看 + Ctrl-C + 发送一行」(02 B2)是控制端的降级,不影响本层。

建议的协议方法(`terminals.*`,能力名 `terminals`;种类 / 角色在 `Ncw.Protocol` 的方法表里声明):

| 方法 | 种类 | 角色 | 对应 |
|---|---|---|---|
| `terminals.list { projectId }` | query | viewer | `List` |
| `terminals.prepare { projectId, id?, cwd?, cols, rows }` | command | member | `PrepareAsync`(结果 `ready { terminal }` 或 `approval { intent }`) |
| `terminals.approve { intentId, approved }` | command | member | `Approve`(结果:凭据或空) |
| `terminals.create { projectId, id?, cwd?, cols, rows, approval? }` | command | member | `CreateAsync`(本机直接创建;远程要凭据) |
| `terminals.getBuffer { id }` | query | viewer | `GetBuffer`(二进制数据 + `truncated` + `seq`) |
| `terminals.write { id, data, token }` | command | member(租约) | `WriteAsync`(幂等键:同一个键重发不重复写) |
| `terminals.resize { id, cols, rows, token }` | command | member(租约) | `ResizeAsync` |
| `terminals.kill { id, token }` | command | member(租约) | `KillAsync` |
| `terminals.close { id }` | command | member | `CloseAsync`(页签关闭) |
| 主题 `terminal/<id>` | topic | viewer | `Attach`(首帧 = 快照或补发,事件 `terminals.data` / `terminals.exit`,游标 = `Seq`) |

`write` 是高频输入:协议层可以用 09 §8.1 的 `stream` 种类(信用值流控)承载,管理器接口不变。错误映射:`NotFound` → `not_found`,`Conflict` / `ApprovalRequired` / `ApprovalExpired` → `conflict`(`details` 区分),`ResourceLimit` → `busy`,
`TooLarge` → `too_large`,`InvalidArgument` → `invalid_request`,`Cancelled` → `unavailable`;`EnvironmentException` 按环境层的映射(`Disconnected` → `unavailable`,`Unsupported` → `unsupported`)。09 §8.4 早先草案里的 `terminals.open`(stream)已由上表取代(09 已更新)。

### 9.5 实现备注与未做

- **未做**:Windows 真机验证(见 §9.2);SSH 远程 Windows 主机的终端(同 §3,`SshEnvironment` 对 Windows 主机报 `Unsupported`);自己 `setsid` 脱离的守护进程不会被终止;终端输出里的转义序列不做任何解析(显示端 `TKCode.Arc.Terminal` 的事);会话不持久化(设备重启后终端本来就不在了)。
- **与 2.x 的差异**:见 `docs/parity.md` 的终端各行。要点:输出是字节不是字符串;`terminals.attach` 拆成 `GetBuffer`(无订阅)和 `Attach`(带游标续传);`kill` 保留会话为已退出,页签关闭用 `CloseAsync`;`prepare / approve` 的「窗口」换成「控制端」;远程终端的归属检查按控制端标识。

## 10. SSH 环境管理(M4,Agent 侧)

`src/Ncw.Agent.Host/Environments/`(`EnvironmentService` 及其分部、`EnvironmentsHandlers`、`ProjectEnvironmentGate`、`SshInteractions`、`EnvironmentProfiles`、`ConnectionSecrets`、`HostKeyStores`)。协议表在 `Ncw.Protocol/Methods/EnvironmentsMethods.cs`;
客户端 `Ncw.Client` 的 `EnvironmentsMirror` + `EnvironmentActions`(规则 1 / 4)。M3 的 `Ncw.Agent.Environments` 只加了 `EnvironmentRegistry.TryGetInstance / DisconnectAsync`、`IHostKeyCatalog`、ssh-agent(§10.7)和「指纹先于口令」(§3)。

### 10.1 持久化(沿用 2.x 的 schema,不加迁移)

| 什么 | 存在哪 | 说明 |
|---|---|---|
| 环境配置 | 2.x 的 `connection_profiles` 表(`id` / `json`) | JSON 形状就是 2.x 的 `SshConnectionProfile`(`target.kind = config / manual`、`authMethod?`、`platform = auto / linux / darwin / win32`、`enabled`、`revision` ……),接管 2.x 数据后原样可用;老行没有 `authMethod` = `auto`。3.0 新增的 `proxy`(HTTP / SOCKS 代理,不含密码)只增键,更新时未知键原样保留。**逐字段构造**:凭据天然进不了这一行 |
| 默认环境 | kv `v3.environments.default` | 初始 `local`;删掉默认环境后回到 `local` |
| 已知主机 | kv `v3.ssh.knownHosts` | `{ 主机名: [ { algorithm, fingerprint } ] }`。读到坏 JSON **失败即关闭**(连接失败),写入也拒绝覆盖读不懂的内容——不当作「没钉扎过」,否则用户会在已被篡改的库上重新确认指纹。用 kv 而不是新表:不需要迁移,也不和别的批次抢迁移编号 |
| 项目绑定 | 工作区 JSON 的 `environment = { kind: 'connection', connectionId }`(2.x 原样) | `ProjectDto.EnvironmentId` = `connectionId`;SSH 项目的 `rootPath` 是远程主机上的规范路径(`projects.add` 时经环境 `realpath` + `stat` 核实是目录) |

`local` 永远存在、不落库、不能删。环境 id 是设备铸的 GUID(2.x 的 `randomUUID`),执行环境标识 = (设备 id, 环境 id)。

### 10.2 角色

读取 `list / status / browse` + `environments` 主题 = **member**(清单里有内网主机名和用户名,属于设备配置,viewer 整个领域都不给,同 MCP / 插件);`connect / disconnect` = member(连接本来就是惰性的,member 通过项目就能触发;`disconnect` 对在用的环境返回 `conflict`);
配置的增改删、`setDefault`、`test`、`importSshConfig`、`knownHosts`、`forgetHostKey` = **owner**(它们决定设备会去连谁、信任谁,或在设备上读文件)。回答连接提问走 `interactions.respond`(member)。
**viewer 不会让设备去连内网主机,也不会替用户在别的控制端上弹确认框**:路由前置闸门(§10.5)对 viewer 的请求用 `ConnectMode.None`,环境没连着就是可重试的 `unavailable`(`environments.notConnected`)。

### 10.3 凭据(只写不读)

- **写**:`environments.create / update / test` 的**请求**里有密码、私钥口令、代理密码、私钥文件路径(null = 不动,空串 = 清除,同 2.x 的 `password` 字段);响应、主题、事件、错误、日志里只有 `HasSavedPassword / HasIdentityFile / HasPassword / HasConfigFile`。请求 / 响应记录的 `ToString()` 都被覆盖成 `redacted`,断言输出、异常消息、日志带不出去。先存凭据再写配置:存不了(主密钥读不出来)整个请求失败、什么都不改。
- **存**:NCK1(`credentials` 表)。ref:`connection:<id>:password`(2.x 原样)、`…:password:<主机名>`(跳板机)、`…:passphrase:<私钥路径的 SHA-256 前 16 位>`、`…:proxyPassword`。删除环境时 `RemoveByPrefix("connection:<id>:")` 一并清掉。
- **用**:`SshCallbacks`(`ISshCredentialProvider` 的 Host 实现)先看设备上保存的(用户勾选过「记住」),没有、或上一次被服务器拒绝了(`PreviousRejected`;被拒绝的已保存凭据当场作废),才发起 `SshCredential` 交互。键盘交互式不保存。
- **私钥内容**(「上传已有私钥」)**未做**:云端设备没有用户的 `~/.ssh`,那一批(M6,§5)需要它;现在只支持设备上的私钥文件路径。

### 10.4 交互:`SshHostKey` 与 `SshCredential`

`Ncw.Agent.Extensions.Interactions` 里的两种新 `InteractionRequest`(03 §4),走同一个 `InteractionGate`:先到先得(`conflict` 带谁、什么时候)、`expectedVersion`、断线 / 取消清理、**必带执行环境标识**(设备 + 环境 + 名字 + 种类)。
和 Run 无关,所以每次提问开一个**设备级作用域**(`RunId = env:<环境 id>:<ulid>`,问完即关;子代理不能提问的规则不受影响);`Registry.Post` 对不存在的 Run 是空操作,所以不进任何 Run 的事件流。

- `SshHostKey { Env, HostName, Algorithm, Fingerprint }` ↔ `SshHostKeyResponse { Trust }`。**只有首次出现才走这里**;和钉扎不一致时连接以 `HostKeyChanged` 失败,**不弹确认、不覆盖旧指纹**,详情(`HostKeyMismatchDto`:主机名、算法、出示的指纹、钉扎的指纹)在环境状态的 `Error.HostKey`;处理入口是 `environments.forgetHostKey`(owner,忘记钉扎,之后再连重新确认)。03 §4 早先草案里 `SshHostKey.Changed` 这个字段因此**不存在**。
- `SshCredential { Env, PromptKind(password / keyPassphrase / keyboardInteractive), HostName, UserName, KeyName?, Instruction?, Prompts[], PreviousRejected, CanRemember, HasSaved }` ↔ `SshCredentialResponse { Answers?, Remember }`(`Answers = null` = 取消 → 连接以 `cancelled` 失败)。
  `KeyName` 只有私钥**文件名**,不含目录。回答校验(数量、非空、≤ 8192、无 NUL;草稿不能「记住」)失败是 `invalid_request` 且不消耗这次交互。**回答里有秘密**:`InteractionRequest.RedactsResponse` 为真,`interaction_resolved` 事件不带回答(只有谁、什么时候),
  `Mapping.ToDto(SshCredentialResponse)` 直接抛(宁可抛也不把口令映射出去);回答只经 `Respond` 交给等待的连接。
- **没人答怎么办**:没有超时(同审批)。连接请求被取消(`cancel` 帧、控制端断开)时 `ct` 传进 `RequestAsync`,交互以中止收尾,环境回到 `idle`(不是 `failed`)。**非交互模式**(`EnvironmentService.NonInteractiveScope`,定时任务、MCP 启动这类没人在场的调用):首次指纹一律拒绝、凭据一律取消,已钉扎 + 已保存凭据能连上就连。
- 顺序:指纹 → (口令 / 密码)→ 认证(§3「主机指纹」)。多跳时每一跳各问各的(`HostName` 区分)。

### 10.5 项目 → 环境:惰性连接、前置闸门、租约

- **`IProjectEnvironments` 的真实实现就是 `EnvironmentService`**(本机 + SSH;`AgentServices.Environments` 仍可注入假的,只换「项目 → 环境」的解析)。同步的 `Resolve` 只在环境已就绪(Ready / Reconnecting)时返回,否则是可重试的 `unavailable`(`environments.notConnected` / 失败的错误 key)——**绝不悄悄把 SSH 项目当成本机目录用**;新增的 `ResolveReadyAsync` 惰性发起连接。
- **只有 Idle 才惰性连接,Failed 不会自动重连**:被拒绝的指纹 / 失败的认证要用户主动 `environments.connect`,否则文件树每刷新一次就弹一次确认。Connecting / Reconnecting 等进行中的那次出结果。
- **路由前置闸门**(`MethodRouter.BeforeInvoke` + `ProjectEnvironmentGate`):参数里带 `WorkspaceId / ProjectId` 的方法(`files.*`、`git.*`、`terminals.prepare / create`、`skills.list / install* / uninstall`、`commands.*`、`hooks.*`、`subagents.list / diagnostics`、`plugins.runCommand`)先确保项目的环境已连接,
  请求在这里等用户回答(取消即取消)。项目 id 从 MessagePack 参数里按键名取(一个自带的小扫描器,有深度和越界保护),测试守着「被闸门管的方法一定带项目 id」。按会话 id 进来的方法(`runs.start / executePlan`、`queue.enqueue`、`context.compact / preview`、`goal.set`)、`review.undo / redo / precheckUndo`(按 Run id)、`projects.add`、定时任务、MCP 启动在各自的处理器里 `await`。
  方法实现里仍只拿同步的 `EnvironmentOf`:不各自处理连接。所有走 `IExecutionEnvironment` 的地方因此在 SSH 项目上自然可用——Read / Edit / Bash 等工具、`files.*`、`git.*`、`terminals.*`(逐个批准 + 环境标识)、Hooks、Skills、MCP 的 stdio(「在执行环境中运行」)、子代理——均有端到端测试(`SshEnvironmentsTests`)。
- **租约与空闲回收**(2.x `EnvironmentManager` 的 `acquire / retain / release` 与「空闲 60 秒回收」):Run 活着期间持有租约(`ConversationRunExtension.OnCreated → Hold`,`OnSettled` 放);终端、后台 Shell 不是租约持有者,用**忙碌探针**(`AddBusyProbe`:项目的活终端、`BackgroundShells` 里在跑的)。
  没有租约、没有忙碌、也没被访问过(`Touch`)超过 `EnvironmentHostOptions.IdleTimeout`(默认 60 秒)就 `EnvironmentRegistry.DisconnectAsync`:丢弃运行时实例(连接和内存里的凭据缓存随之清掉),配置、已保存的凭据、钉扎都保留,下次使用惰性重连。`disconnect` 对在用的环境是 `conflict`(`environments.busy`)。
- **断线重连**仍由 `SshEnvironment` + `ReconnectPolicy` 驱动(M3);Host 只把状态变化推到 `environments` 主题。重连期间新发起的操作最多等 `ReadyWait`。

### 10.6 状态:`environments` 主题

全量 `EnvironmentsTopicState { Rev, DefaultId, Environments[] }`,首帧、`environments.changed` 事件、`environments.list` 是同一个类型(规则 5);`Rev` 在发布锁里递增、推送在同一把锁里(推送序 = 版本序);首帧「先读 Rev 再读内容」,内容只会比 Rev 新。
**状态只由各环境实例回答**(`registry.TryGetInstance(id)?.State ?? Idle`),Host 不另存「连没连上」。`EnvironmentDto` 的 `Revision` 是配置修订号(改配置才变,`update` 的乐观锁),`LatencyMs` 是连接成功后测的一次 SFTP stat 往返。
断线时控制端把这台设备上的环境状态标为**未知**(`EnvironmentsReducer.MarkUnknown`,规则 7),不判成断开或失败。tool_start 里的环境标识只有 (设备, 环境 id),显示名和种类控制端按镜像取(`EnvironmentsMirror.Find`)。

### 10.7 ssh-agent(已实现)

**评估**:SSH.NET 的 `PrivateKeyAuthenticationMethod` 只接受 `IPrivateKeySource`,但它对私钥只做两件事:报公钥 blob(`HostAlgorithm.Data`)和对认证数据签名(`HostAlgorithm.Sign`)。`HostAlgorithm` 是公开的抽象类,所以**不需要自定义 `AuthenticationMethod`**:
`AgentHostAlgorithm : HostAlgorithm` 把 `Sign` 转给 ssh-agent(同步回调里在线程池线程上阻塞等 agent 的回答,握手线程本来就在等这一步),`AgentKeySource : IPrivateKeySource` 把一把 agent 身份包成它。私钥永远不进本进程。

- **协议客户端** `SshAgentClient`(draft-miller-ssh-agent):`REQUEST_IDENTITIES(11)` / `SIGN_REQUEST(13)`,**每次请求新开一条连接**(一问一答,不持有长连接:agent 重启、`SSH_AUTH_SOCK` 变了都不会留下失效句柄)。承载:Unix 域套接字(`SSH_AUTH_SOCK`)/ Windows 命名管道 `\\.\pipe\openssh-ssh-agent`。消息体上限 256 KiB、身份数上限 1024、超时(签名可能在等用户在 agent 上确认)。RSA 身份提供 `rsa-sha2-512 / rsa-sha2-256 / ssh-rsa` 三个算法(flags 4 / 2 / 0),证书类身份(`*-cert-v01@openssh.com`)不提供(SSH.NET 的认证不支持)。
- **认证次序**(同 OpenSSH):agent 的身份在前、私钥文件在后;配套的 `.pub` 能对上 agent 里某把身份的私钥文件**不再加载**(不为文件里的私钥口令打扰用户);`IdentitiesOnly` 且指定了私钥文件时不用 agent。agent 不可达 / 拒绝列表 = 当作没有 agent,不阻止其它认证方式。
- **接入**:`SshEnvironmentOptions.Agent`;Host 的 `EnvironmentHostOptions.DiscoverSshAgent`(默认开)在本机设备上用 `SshAgentClient.Discover()` 找系统 agent,云端设备没有用户的 agent(找不到就是没有)。
- **验证**:`SshAgentTests`(真实 `ssh-agent -D -a` + `ssh-add`):列身份与 `.pub` 一致;私钥文件删掉后仅靠 agent 连上真实 sshd(ed25519、RSA 2048 走 sha2);agent 持有口令私钥时不问口令;agent 不可达回退私钥文件;agent 里没有对的钥匙认证失败;畸形 / 超长 / 截断回复、失败回复、静默 agent 超时。Windows 命名管道**没有在 Windows 上验证**(`OpenSSH Authentication Agent` 服务的真机)。
- 没做:ssh_config 的 `IdentityAgent`(只识别 `none`,不使用自定义路径)、`AddKeysToAgent`、`ForwardAgent`(转发 agent 到远端)、agent 的锁定 / 约束扩展。

### 10.8 与设计的偏离

- 03 §4 的 `SshHostKey(Env, Fingerprint, Changed)`:**没有 `Changed`**(指纹变化不是交互,见 §10.4);多了 `HostName / Algorithm`(多跳时区分哪一跳)。`SshCredential(Env, Prompt)` 展开成 `PromptKind / HostName / UserName / KeyName / Instruction / Prompts / PreviousRejected / CanRemember / HasSaved`。
- 本文 §6 早先写的 `environments.list / create / update / delete / test / browse` 与 `environment.status` 事件:`delete` 叫 `remove`,新增 `status / setDefault / connect / disconnect / importSshConfig / knownHosts / forgetHostKey`,事件换成主题的全量状态。
- 「指纹先于认证」的实现由「先握手只取指纹」扩成「先于一切凭据」:未钉扎的主机在问口令之前先确认指纹(M3 的实现是先问口令、再取指纹)。
- `environments.connect` 等到这次尝试**结束**才返回(失败是结果不是错误),而不是立刻返回再靠主题推进度:连接过程中的交互靠 `interactions` 主题,请求的取消就是「放弃这次连接」。
- 凭据不上线的口径比 2.x 严:私钥文件**路径**也只写不读(DTO 只有 `HasIdentityFile`;交互里只有文件名),因为路径泄漏用户名和目录布局。
- 本批修的协议缺陷:`DeviceClient.CallAsync` 的取消帧在「`WaitAsync` 续体内联执行先释放登记」时从不发出,设备上等用户回答的处理器收不到取消(只有连接关闭才会);改成在 `catch (OperationCanceledException)` 里显式发。测试 `SessionTests.A_cancel_frame_reaches_the_running_handler_promptly…`。

### 10.9 未完成 / 未验证

- **Windows**:远程 Windows 主机(`RemoteCommands` 的 Windows 适配器有单测,没有 Windows sshd 可测;终端未做,06 §8)、`environments.browse` 列盘符(PowerShell `DriveInfo`,2.x 同款,没在真机上跑)、ssh-agent 命名管道。
- **真实主机**:所有端到端测试用的是本机上的用户态 sshd(OpenSSH 10.x,POSIX);没有在真实内网主机、密码认证(PAM)、键盘交互式两步验证(用户态 sshd 以非 root 跑不了 PAM / 密码)的服务器上验证——这几条路径的 `SshCredential` 提问只有单测(`SshInteractionTests` 的校验与脱敏、`SshAuthPlan` 的纯函数测试、`SshCallbacks` 的「已保存 / 被拒绝作废 / 非交互」逻辑经口令私钥的真 sshd 测试间接覆盖),密码与键盘交互式没有真服务器可测。
- 云端设备的 SSH 白名单、私钥在云端生成 / 上传(§5)、`ProxyCommand`、`known_hosts` 文件的导入(只有设备自己的钉扎库)。
- 租约闸之外的:`terminals.write` 的租约闸(02 A2)仍没有;空闲回收不跟踪 `files.*` 的长读写(一次操作超过空闲时间且期间没有别的访问时可能被回收,操作以 `ConnectionLost` / `Disconnected` 结束)。
