# 08.06 · 扩展

参考 pi 的 Extension API,但用在两类扩展上:

- **一方扩展**:C# 实现,随 Agent 发布,默认启用。权限、计划模式、待办、目标、子代理、Hooks、Skills、插件宿主都是一方扩展。
- **插件扩展**:插件 JS 在 Jaunt 里运行(见 [02 A7](../02-agent.md#a7-插件与-js-执行jaunt)),通过插件宿主这个一方扩展间接接入,
  只能订阅白名单里的事件,不能注册 `before_provider_request` 这类高权限扩展点。

## 1. 注册接口

```csharp
public interface IAgentExtension
{
    string Id { get; }
    int Order { get; }                       // 同一扩展点内的执行顺序;权限扩展固定在最前
    void Register(IExtensionRegistry r);
}

public interface IExtensionRegistry
{
    void On<TEvent>(ExtensionPoint<TEvent> point, Func<TEvent, ExtensionContext, ValueTask> handler);
    void Gate<TEvent, TDecision>(GatePoint<TEvent, TDecision> point, Func<TEvent, ExtensionContext, ValueTask<TDecision>> handler);
    void Transform<T>(TransformPoint<T> point, Func<T, ExtensionContext, ValueTask<T>> handler);
    void RegisterTool(IAgentTool tool);
    void RegisterCommand(CommandDefinition command);
    void ContributeSystemSection(string name, Func<SessionInfo, string?> section);
}
```

`ExtensionContext` 提供:当前会话和 Run 信息、`SendMessage(..., deliverAs: Steer | FollowUp)`、`AppendEntry`(扩展自己的持久状态,不进上下文)、
`Interact`(仅顶层 Run)、日志。

## 2. 扩展点与组合语义

| 扩展点 | 组合方式 | 典型使用者 |
|---|---|---|
| `session_start` / `session_shutdown` | 通知 | 目标(恢复状态)、插件宿主 |
| `input` | 链式变换(可以改写或直接处理掉) | 命令展开校验、`UserPromptSubmit` Hook |
| `before_run` | 就地修改运行参数 | 计划模式(工具允许列表、写入围栏)、子代理(权限收窄) |
| `prepare_turn` | 就地修改 | 压缩、工具快照过滤、状态块(待办、计划文件、git) |
| `system_sections` | 收集 | Skills 目录、模式提示词、权限说明、插件的说明 |
| `before_tool`(闸门) | **第一个拒绝即短路**;放行需要所有人不反对 | 权限(固定第一)、`PreToolUse` Hook、插件拦截器(只能收紧) |
| `after_tool` | 链式变换,只允许追加和翻转 `IsError` | `PostToolUse` Hook、变更记录器、读取跟踪 |
| `turn_end`(闸门) | 任一要求继续则继续(按 `Order` 取第一个) | 目标、待办提醒、`Stop` Hook |
| `before_compact` | 闸门:可以取消或提供自定义摘要 | 插件(高级) |
| `after_compact` | 通知 | 压缩后附件 |
| `run_end` / `run_settled` | 通知 | 后台报告、`SubagentStop` Hook、目标暂停 |
| `before_provider_request` | 最后者胜 | **仅一方扩展**(调试、请求补丁) |
| `tool_registry` | 收集 | 插件工具、MCP 工具 |

规则:

- 扩展处理器的异常**不传播到循环**:通知类记日志忽略;闸门类按「放行」处理并记诊断(与 Hook 的失败放行一致)——
  **权限扩展例外**,它的异常按拒绝处理。
- 处理器都带 `CancellationToken`,Run 中止时一起取消。
- 插件扩展点的处理受 Jaunt 预算限制(见 [02 A7](../02-agent.md#a7-插件与-js-执行jaunt)),超时视为弃权。

## 3. 一方扩展清单

| 扩展 | 使用的扩展点 | 能否关闭 |
|---|---|---|
| `Permissions` | `before_tool`(第一位)、`system_sections` | 否 |
| `Hooks` | `input`、`before_tool`、`after_tool`、`turn_end`、`run_end` | 可(没有配置 Hook 时等于不存在) |
| `PlanMode` | `before_run`、`prepare_turn`、工具 | 可(设置里的计划工作流开关) |
| `Todos` | `prepare_turn`(状态块)、`turn_end`(提醒)、工具 | 可 |
| `Goals` | `session_start`、`turn_end`、`run_end`、工具 | 可 |
| `Subagents` | `before_run`、工具(`Task`) | 可 |
| `Skills` | `system_sections`、工具(`Skill`)、`after_compact` | 可 |
| `ChangeTracking` | `after_tool` | 否(撤销依赖它) |
| `PluginHost` | 代理插件订阅的事件 | 可(无插件时等于不存在) |
| `Mcp` | `tool_registry` | 可 |

## 4. 为什么这样拆

- 循环层保持 pi 那样小,可以单独测试:给一个假上游和几个假工具,就能验证回合、中止、引导消息、孤儿修复。
- 每个功能是一个扩展,可以单独测试、单独关闭,也可以按「设备能力」降级(例如云端或手机控制的会话不启用某些功能)。
- 插件和一方功能用同一套扩展点,插件 API 的能力边界由白名单显式定义,而不是「插件能碰到什么就是什么」。
