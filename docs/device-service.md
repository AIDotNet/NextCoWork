# 设备服务(独立部署)

NextCoWork 的 Agent 运行、业务库、工作区文件、Git、终端都跑在这个服务里,桌面只是一块屏和一个输入口。
把它装在一台常开的机器上(家里的服务器、NAS、开发机),**关掉桌面任务不会停**。

要求 **Node.js 24 或更新**:服务用 `node:sqlite`,低版本 Node 连 import 都过不去。
没有 Electron、没有自动更新通道 —— 升级就是重新装一次再重启服务。

## 安装

```bash
npm i -g @aidotnet/nextcowork-device-service    # 装完得到命令 ncw-device-service
ncw-device-service diagnose                      # 先看这台机器能跑什么
```

`diagnose` 报的是**真实探测**的结果,不是读版本号推断:`node`、`sqlite`(真开一个内存库)、
`pty`(真拉起一个 shell 读它的输出)、`git`、`data-dir`(真写一次)。浏览器与 Office 组件尚未随服务提供,
如实报 `absent`,不假装已安装。`required` 那几项任一失败,退出码为 1。

npm 不通的机器可以直接装 Release 里的 tgz(与 npm 上是同一份产物):

```bash
npm i -g ./aidotnet-nextcowork-device-service-<版本>.tgz
```

包里只有 `bin.mjs`、sourcemap、本 README 和一份清单;运行时要用的 11 个依赖(含原生模块 node-pty)
由 `npm install` 在目标机上装。

## 启动

```bash
ncw-device-service --data-dir ~/ncw-device \
  --host 0.0.0.0 --port 7801 --pairing-port 7800 --advertise nas.lan --invite
```

| 参数 | 说明 |
| --- | --- |
| `--data-dir <dir>` | 数据根,**必填**。指向桌面旧数据根即「原地接管」,见下文 |
| `--host <addr>` | 监听地址,默认 `127.0.0.1`。**没给 `--host` 就只有本机能连** |
| `--port <n>` | 设备端点端口,默认 `0` —— 每次启动随机分配 |
| `--pairing-port <n>` | 配对端点端口,默认 `0`(同上) |
| `--advertise <host>` | 邀请里公布的主机名/IP(可重复),同时进服务端证书的 SAN |
| `--invite` | 启动后签发一个 5 分钟的配对邀请并打印 |

**常驻服务一定要把两个端口写死。** 默认的 `0` 是每次启动重新分配,重启之后已经配对的桌面就找不到它了。
同理:你打算用域名或外部 IP 连它,那个名字就得出现在 `--advertise` 里,证书 SAN 对不上时连接会被拒。

数据根里会看到:`nextcowork.db`(业务库,连同 `-wal` / `-shm`)、`device-identity.json`(设备身份)、
`pki/`(服务端证书与信任根)、`tmp/`、`service.lock`,以及第一次删除文件时才出现的 `trash/`
(没有系统废纸篓时的兜底,仍可恢复)。同一个数据根同一时刻只有一个写入者,靠 `service.lock` 保证 ——
第二个进程会被直接拒绝,而不是两个进程各写一半。

信号:`SIGINT` / `SIGTERM` 立刻优雅停止;`SIGUSR2` 是**排空**(拒绝新配对与新任务,等在跑的任务自然结束),
给升级用,最长等 `NCW_DRAIN_MAX_WAIT_MS`(默认 1 小时),超时不强杀,继续跑并报出来。

## 把桌面接上去

1. **在设备上拿邀请。** `ncw-device-service … --invite` 会打印一段 `ncwpair1.` 开头的文本。它 5 分钟内有效、
   只能用一次,只带「去哪连 + 这台设备的信任根公钥指纹 + 一次性 token」,不含任何私钥。
   但它仍然是一条敏感信息:当面念、或走你信得过的渠道,别丢在群里。
2. **在桌面上配对。** **设置 → 设备 → 添加设备**,粘贴,先点 **检查邀请**。这一步不联网,只从邀请里读出
   设备指纹供你核对;一致再点**指纹一致,配对**。不在同一网络时,桌面侧可以配一个自部署中转
   (只转发密文,读不到内容,也顶替不了配对),直连仍然是优先的。
3. **切过去。** 侧边栏的**设备**切换器选这台设备。这一版转发:设置,工作区列表/改名/关闭,文件浏览与读写,
   会话列表与创建,技能/命令/子代理/模式/钩子/MCP 列表,Git 查看与暂存/提交/分支,以及纯文本任务的发起、
   事件、停止、审批与终端。其余需要设备的能力会被**明确拒绝**,不会悄悄退回本机执行 —— 设备离线时同理。
   重启桌面后回到「这台电脑」。
4. **撤销。** 「移除连接」只删这台电脑上保存的连接与密钥,**不会**在设备上撤销配对;要撤销请在设备上操作。

## 开机常驻(不用登录)

`service` 只生成和写入文件,**从不 `sudo`、不启动、不启用** —— 命令打印出来交给你执行。

```bash
# 先看要写哪些文件、之后要跑哪些命令。什么都不写。
ncw-device-service service plan --user ncw --data-dir /var/lib/ncw \
  --host 0.0.0.0 --port 7801 --pairing-port 7800 --advertise nas.lan --platform linux

# 确认无误再写文件(系统目录需要管理员权限,没有会报错并让你拿 plan 的输出去装)
ncw-device-service service apply --user ncw --data-dir /var/lib/ncw \
  --host 0.0.0.0 --port 7801 --pairing-port 7800 --advertise nas.lan --platform linux
```

默认服务名 `ncw-agent`(`--name` 可改),`--bin` / `--node` 默认就是当前这个命令自己的路径。
之后要执行的大致是:

```bash
# Linux  → /etc/systemd/system/ncw-agent.service
systemctl daemon-reload && systemctl enable ncw-agent && systemctl start ncw-agent
# macOS  → /Library/LaunchDaemons/com.nextcowork.ncw-agent.plist(输出写到 <数据根>/service.log)
launchctl bootstrap system /Library/LaunchDaemons/com.nextcowork.ncw-agent.plist
```

两件必须知道的事:

- `--user` 必填且**必须是普通用户**:root / SYSTEM / Administrator 一律拒绝。服务只需要读写自己的数据根,
  不该拿到整台机器的权限。
- Linux 单元里是 `ProtectHome=read-only` + `ReadWritePaths=<数据根>`。Agent 要改的项目如果在家目录下,
  得把那些目录加进 `ReadWritePaths`,否则工作区对它是只读的 —— `plan` 的 note 里也写了这一点。

卸载计划同样只打印:`ncw-device-service service uninstall-plan …`(给出 `systemctl disable --now …` 与删除命令)。
Windows 走 WinSW 配置,`winsw.exe` 需要操作者自己装;这条路径**未在 Windows 上验证**。

## 从桌面数据根原地接管

已经用桌面版跑起来的数据根,想原地交给服务(不复制、不上传):

```bash
ncw-device-service takeover begin  --data-dir <桌面数据根>   # 查旧进程 → 一致性备份 → 逐表核对行数
ncw-device-service takeover commit --data-dir <桌面数据根>   # 取同根锁、建服务身份、追加 ownership 标记
ncw-device-service takeover status --data-dir <桌面数据根>   # 当前阶段与备份位置
```

旧进程还在跑就直接拒绝。`begin` 可重入(崩溃后能从记录的阶段继续);**提交前** `rollback` 什么都没丢,
**提交之后**必须显式 `rollback --acknowledge-data-loss`,它会先把接管之后写入的库另存一份。

## 排障

- **终端起不来,`diagnose` 里 `pty` 是 fail。** npm ≥ 12 默认不执行依赖的 install script,从 npm 装出来的
  node-pty 的 `spawn-helper` 没有执行位,`pty.spawn()` 只会报一句 `posix_spawnp failed` —— 看不出要改什么。
  服务启动时(以及 `diagnose`)会自己把这个执行位补上;补不上时(例如全局安装是 root 装的)启动日志里会多一行
  `warning: terminals are unavailable: …`,`diagnose` 的详情里也带着要执行的那条 `chmod`。
- **`diagnose` 第一项就是红的**:Node 太老,见开头。
- **第二个进程起不来**:同一个数据根已经有服务在跑(`service.lock`);要并行跑就用不同的数据根。
- **重启后桌面连不上**:检查端口是不是 `0`。
- **升级**:`npm i -g @aidotnet/nextcowork-device-service@latest` 之后重启服务;systemd 的 `ExecReload`
  就是 `SIGUSR2`,会先排空再停。

## 自己打包

```bash
npm run build:agent                 # src/agent/server/bin.ts → out/agent/bin.mjs
npm run package:agent -- --verify   # → out/aidotnet-nextcowork-device-service-<版本>.tgz,并在仓库之外真装真启动一次
```

产物自检不过就非零退出:不得引用 `electron`、必须保留 `node:sqlite`。包里的 README 就是本文档。
发版时 `release.yml` 会把同一个 tgz 传到 GitHub Release 并发到 npm(`secrets.NPM_TOKEN`)。
