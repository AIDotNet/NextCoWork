# 08 · Agent Runtime(核心)设计

本目录是 3.0 C# Agent 运行时的核心设计。它综合了两份参考:

- **2.x 内核**(`main` 分支的 `src/main/kernel`、`services/agent.ts`、`runtime.ts`):已在生产上验证过的行为和不变量,
  注释里的 ★ 条目记录了真实踩过的坑。**行为以它为准**。
- **pi**(Mario Zechner 的 pi-mono,现已迁到 `earendil-works/pi`):小内核、清晰的分层和事件模型、可扩展。**结构以它为参考**。

| 文档 | 内容 |
|---|---|
| [00 总览](00-overview.md) | 设计原则、分层、从 pi 和 2.x 各取什么、关键取舍(R1–R20) |
| [01 模型层](01-llm.md) | `Ncw.Agent.Llm`:统一消息模型、流式事件、三种线协议、跨模型切换、路由与重试、认证 |
| [02 Agent 循环](02-loop.md) | `AgentLoop`:回合循环、事件流、工具执行、引导与排队消息、中止、扩展点 |
| [03 工具、权限与交互](03-tools-permissions.md) | 工具协议、内置工具、权限决策链、交互(审批 / 提问)、Hooks |
| [04 会话与上下文](04-session-context.md) | 会话存储、上下文组装、提示词缓存、压缩、Skills、命令 |
| [05 编排](05-orchestration.md) | Run 生命周期、RunRegistry、事件日志与对账、子代理、计划模式、待办、目标、定时任务 |
| [06 扩展](06-extensions.md) | 扩展点与组合语义;内置功能如何以「一方扩展」实现;插件(Jaunt)如何接入 |

阅读顺序:00 → 02 → 01 → 03 → 04 → 05 → 06。
