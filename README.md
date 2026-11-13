# NextCoWork 3.0(Avalonia 分支)

这是 NextCoWork **3.0** 的独立分支(`v3/avalonia`,孤儿分支,和 2.x 的 `main` 没有共同历史)。

3.0 用 **Avalonia 12 + [TKCode.Arc](https://docs.tkcode.si/llms.txt)** 重写客户端,覆盖桌面端(macOS / Windows / Linux)和移动端(iOS / Android)。
Agent 运行时和客户端分开,两者之间只通过「设备协议」通信。

> 现阶段:**设计计划评审(M0)**。下面的文档没有评审通过之前,不写业务代码。

## 设计文档

| # | 文档 | 内容 |
|---|---|---|
| 00 | [总览与关键决策](docs/plan/00-overview.md) | 目标、非目标、ADR 决策清单、术语 |
| 01 | [整体架构](docs/plan/01-architecture.md) | 进程模型、解决方案结构、协议 SDK、状态统一管理(C# 版) |
| 02 | [Agent 设计](docs/plan/02-agent.md) | Agent 运行时的通用化、多端并发、移动端约束;Agent 交互界面 |
| 03 | [界面设计与布局](docs/plan/03-ui-layout.md) | 三栏布局、项目栏与分组、响应式断点、移动端导航 |
| 04 | [更新与安装](docs/plan/04-update-install.md) | 2.x → 3.0 升级链路、3.x 自更新、数据接管、回滚 |
| 05 | [路线图与风险](docs/plan/05-roadmap.md) | 里程碑、功能对齐矩阵、风险、待定问题 |

工作约束见 [AGENTS.md](AGENTS.md)。
