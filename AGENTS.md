# AGENTS.md

本文件是给在本仓库工作的 Agent(以及人)的**硬性约束**。标注「必须」的条目是合并的前置条件:违反即视为缺陷,不论功能是否看上去能用。

## 状态统一管理(必须)

### 背景

子代理的状态曾经同时存在于四个地方:主进程 `RunRegistry`、父会话转录里的 `transcript.subagents`、
子会话自己 store 里的 `transcript.status` / `activeRunId`,以及组件内部的 `useState`。
转录卡片、右上角活动胶囊、只读子会话各读一份、各判一遍,于是出现了「卡片和胶囊说运行中,
点开子会话说已完成」「卡片的停止能收口、胶囊的停止永远转圈」「胶囊计时一直在走」这类不同步——
每一处单看都"没错",合在一起就是错的。后台 shell(胶囊里各自订阅一份)和待处理交互
(面板自己监听事件、侧边栏读权威计数)也是同一类问题。

### 规则

1. **一个事实只有一个真源。** 运行时状态的真源在主进程;渲染层对每个事实**只保留一份镜像**,
   放在 `src/renderer/src/stores/` 里的某一个 store 中。新增状态前先找它现在住在哪里。
   | 事实 | 真源(主进程) | 渲染层唯一镜像 |
   | --- | --- | --- |
   | 哪些顶层 run 在跑、各有几条待处理交互 | `RunRegistry` → `activeRunIndex()` | `stores/session.ts` 的 `useRunIndex` |
   | 哪些子 run 在跑 | `RunRegistry` → `activeSubagentIndex()` | `stores/session.ts` 的 `childRunIndex`(由 `syncActiveSubagents` 对账) |
   | 某个子代理的状态与遥测 | 子 run 事件 + 库里的 Task 回执 | **父会话**转录的 `transcript.subagents[callId]` |
   | 后台 shell | `agent-shells.ts` 注册表 | `stores/background-shells.ts` |
   | 会话转录 / 排队消息 / 目标 | `RunRegistry` 事件 + SQLite | `stores/session.ts` 的各会话 store |

2. **组件不持有业务状态的拷贝。** 组件里的 `useState` 只能放纯 UI 状态(展开/收起、当前页签、输入草稿)。
   禁止在组件里 `useState` + 自己订阅一条 IPC 来维护运行时数据;需要就去对应的 store 读,
   没有就先在 store 里加。不要把某个状态对象的**快照**沿 props 递下去再"优先使用",
   只递 id(例如 `callId`),由消费方自己去唯一的表里查。

3. **派生只有一个函数。** 同一个事实在多处展示时(状态文案、耗时、能不能停、能不能点开……),
   派生逻辑必须是 `src/shared/` 下的一个纯函数,所有展示点调用它,不许各自再判一遍。
   子代理用 `shared/domain/subagent-view.ts` 的 `subagentViewOf`;计时用 `lib/useTicker.ts`。

4. **动作只有一份。** 同一个用户意图(停止子代理、停止命令……)在多个入口出现时,
   必须调用同一个函数(例如 `stores/session.ts` 的 `stopSubagent`),不许一个入口多做一步收口、另一个入口少做。

5. **必须能从丢消息中自我收敛。** 事件流是按订阅定向推的,窗口藏着、store 未建时都会丢。
   任何"运行中"类状态都必须有一条**全量**的权威广播兜底(`agent:activeRuns` 的 `runs` + `subagents`),
   渲染层收到后对账:多出来的补上,少掉的在宽限期后向主进程核实并收口。
   bootstrap 首帧与后续广播必须共用同一个生成函数和同一个类型。

6. **状态单调。** run 只会从 `running` 走向终态。合并多个来源(实时遥测、库里回执、attach 重放)时,
   终态压过 `running`(见 `shared/agent/transcript.ts` 的 `mergeSubagentRecord`);
   重放的旧事件不得把终态改回运行中。

7. **派生视图跟随真源,不自行判定。** 例如只读子会话的运行态跟随父会话那张卡片
   (`stores/session.ts` 的 `syncChildPanel`),而不是用自己 store 里读库得到的 `'done'`。

### 合并前自检

- [ ] 新增/修改的运行时状态是否只在一个 store 里?组件里是否新增了业务 `useState` 或 IPC 订阅?
- [ ] 多处展示的同一状态是否都调用同一个派生函数?
- [ ] 漏掉任意一条结束事件时,界面是否会被权威广播收口?有没有对应的单测?
- [ ] 合并多个来源时是否保持了状态单调?
- [ ] 相关单测:`src/shared/__tests__/subagent-view.test.ts`、`src/shared/__tests__/subagent-monotonic.test.ts`、
      `src/renderer/src/stores/__tests__/subagent-sync.test.ts`、`src/main/ipc/__tests__/active-run-broadcast.test.ts`。

## 常用命令

- 类型检查:`npm run typecheck`
- 单测:`npm test`(或 `npx vitest run <path>`)
- Lint:`npm run lint`
