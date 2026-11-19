# NextCoWork 3.0(Avalonia 分支)

这是 NextCoWork **3.0** 的独立分支(`v3/avalonia`,孤儿分支,和 2.x 的 `main` 没有共同历史)。

3.0 全栈 C#(.NET 11):客户端用 **Avalonia 12 + [TKCode.Arc](https://docs.tkcode.si/llms.txt)** 重写,覆盖桌面端(macOS / Windows / Linux)和移动端(iOS / Android);
Agent 运行时也用 C# 重写,发布为 NativeAOT 原生可执行文件 `ncw-agent`(同目录带 e_sqlite3 原生库)。两者分成两个进程,只通过「设备协议」通信。
中继服务、构建发布工具同样用 C#;插件里的 JS 用 [Jaunt](https://github.com/AIDotNet/Jaunt) 执行。

> 3.0 只做 GUI:不做命令行控制端、插件 CLI,也不做独立部署的设备服务。

> 现阶段:**M2 协议与 Agent 骨架**(M0 设计、M1 界面骨架已完成)。

## 开发

需要 .NET 11 SDK(见 `global.json`)。

```bash
dotnet build NextCoWork.slnx                       # 构建(桌面端构建时会把 ncw-agent 复制到 <输出>/agent/)
dotnet test NextCoWork.slnx                        # 单测 + 集成测试 + headless UI 测试(含截图)
dotnet run --project src/Ncw.Desktop               # 启动桌面端:自动拉起本机 Agent 并连接
NCW_MOCK=1 dotnet run --project src/Ncw.Desktop    # 用模拟数据演示界面,不连接 Agent
```

| 环境变量 | 作用 |
|---|---|
| `NCW_MOCK=1` | 模拟数据模式 |
| `NCW_AGENT_DATA_ROOT` | 本机 Agent 的数据根,默认 `~/.next-cowork/agent-dev`(2.x 数据接管完成前,不碰 2.x 的 `~/.next-cowork/data`) |
| `NCW_CLIENT_DIR` | 控制端偏好目录,默认 `~/.next-cowork/client` |
| `NCW_AGENT_PATH` | 指定 ncw-agent 可执行文件 |
| `NCW_LANG` | 界面语言(`zh-CN` / `en-US`) |

| 项目 | 内容 |
|---|---|
| `src/Ncw.Core` | 领域模型与派生纯函数(客户端和 Agent 共用) |
| `src/Ncw.Protocol` | 设备协议 v1:帧、编解码(Nerdbank.MessagePack)、会话(客户端 / 服务端)、本机承载 |
| `src/Ncw.Client` | Store、Action、ViewModel、DeviceLink(连接设备并同步进 Store);模拟数据 |
| `src/Ncw.UI` | Avalonia 视图、Arc 封装、桌面外壳、中英文 |
| `src/Ncw.Desktop` | 桌面宿主:拉起并守护本机 Agent(sidecar) |
| `src/Ncw.Agent.Host` / `src/Ncw.Agent` | Agent 服务与 `ncw-agent` 可执行文件(NativeAOT) |
| `tests/*` | xunit v3;集成测试与 UI 端到端测试在真实本机 socket 上连接进程内的 Agent |

## 设计文档

| # | 文档 | 内容 |
|---|---|---|
| 00 | [总览与关键决策](docs/design/00-overview.md) | 目标、非目标、ADR 决策清单、术语 |
| 01 | [整体架构](docs/design/01-architecture.md) | 进程模型、解决方案结构、共享协议、Agent 的 C# 移植策略、状态统一管理 |
| 02 | [Agent 设计](docs/design/02-agent.md) | Agent 运行时的通用化、多端并发、移动端约束、云端 Agent、插件与 JS 执行(Jaunt)、模型目录(models.dev);Agent 交互界面 |
| 03 | [界面设计与布局](docs/design/03-ui-layout.md) | 三栏布局、项目栏与分组、响应式断点、移动端导航 |
| 04 | [更新与安装](docs/design/04-update-install.md) | 2.x → 3.0 升级链路、直接接管 2.x 数据、3.x 自更新、回滚 |
| 05 | [路线图与风险](docs/design/05-roadmap.md) | 里程碑、功能对齐矩阵、风险、待定问题结论、后端配合清单 |
| 06 | [执行环境与 SSH](docs/design/06-environments-ssh.md) | 控制端 / 设备 / 执行环境三层、SSH 实现、多设备执行 |
| 07 | [首次启动、登录与设置页](docs/design/07-onboarding-settings.md) | 首次启动流程、`nextcowork://` 登录跳转、设置页重新设计 |
| 08 | [Agent Runtime](docs/design/08-agent-runtime/README.md) | 核心运行时:模型层、Agent 循环、工具与权限、会话与上下文、编排、扩展(参考 pi + 2.x 内核) |
| 09 | [设备协议](docs/design/09-protocol.md) | 全新设计:承载与 TLS、配对与信任、帧、会话与续连、方法和订阅、中继、开放网关 |

工作约束见 [AGENTS.md](AGENTS.md)。
