# NextCoWork 全仓代码质量与架构改进实施计划

## 1. 计划目标

本计划不是一次性重写，而是把当前仓库从「架构方向正确、质量门禁长期带红」推进到以下状态：

1. 默认质量命令代表真实结果：`typecheck`、`test`、`lint`、`build` 不再依赖已知红项通过。
2. 渲染层、preload、主进程、内核、共享层的依赖方向由代码和静态检查共同保证。
3. IPC、插件、持久化、Agent 流式状态的现有不变式不被重构破坏。
4. 大文件只在领域功能继续变化时渐进拆分，不进行无收益的全仓格式化或大规模重命名。
5. 发布包、跨平台启动、数据迁移、插件权限和原生 helper 都有可重复的验证路径。

### 本计划的范围

包含：

- 质量门禁、测试组织、类型和 lint 约束
- Electron 打包与 CI 验证
- IPC service/contract/handler 分层
- runtime、DB repository、renderer store、i18n、plugin manager 的渐进拆分
- SQLite 同步调用的性能评估和后续迁移准备
- 插件、webview、preload、原生 helper 的安全回归测试

不包含：

- 本计划不实现新的 Office/PDF 产品功能
- 不改变现有用户可见功能的产品语义
- 不引入新的状态管理库、路由框架、事件总线或格式化工具
- 不在没有性能数据前把整个 SQLite 迁移到 utility process
- 不删除现有测试来制造绿色基线

文档引擎当前只作为质量门禁和架构状态处理：未接线的测试必须被明确标记为 pending 并进入独立验证任务，不能继续混在默认测试中造成永久红色。

---

## 2. 已核实的基线

### 2.1 仓库规模

静态统计结果：

| 区域 | 文件数 | TypeScript 行数 | 说明 |
|---|---:|---:|---|
| `src/main` | 526 | 138,192 | Agent 内核、IPC、数据库、插件、环境和 Electron 生命周期 |
| `src/renderer/src` | 518 | 95,094 | React 界面、store、services、设置和 shell |
| `src/shared` | 201 | 44,856 | 共享类型、领域模型、纯函数和 IPC 契约 |
| `src/preload` | 2 | 116 | 主窗口安全桥 |

当前最大热点文件：

- `src/main/db/repo.ts`：3545 行
- `src/main/runtime.ts`：3038 行
- `src/main/imports/service.ts`：2638 行
- `src/renderer/src/i18n/index.tsx`：4255 行
- `src/renderer/src/views/chat/Composer.tsx`：2495 行
- `src/main/plugin/manager.ts`：2148 行
- `src/main/ipc/storage.ts`：2207 行
- `src/shared/ipc/contract.ts`：1852 行
- `src/renderer/src/stores/session.ts`：1725 行

### 2.2 工具基线

当前实际运行结果：

```text
npm run typecheck   通过
npm test            失败：416 个测试文件，6435 项通过，12 项失败，5 个失败文件
npm run lint        通过：0 error，4 个 warning
```

失败分布：

- `src/main/environment/__tests__/session-mux.test.ts`：依赖本机 `python` 命令，3 项失败。
- `src/main/document-engine/__tests__/native-host.test.ts`：缺少 `helperEnv` 约定和 `DocumentSessionManager.render`，5 项失败。
- `src/main/document-engine/__tests__/provider-registry.test.ts`：引用不存在的 `provider-registry`。
- `src/main/plugin/__tests__/document-rpc.test.ts`：引用不存在的 `document-rpc`。
- `src/main/plugin/__tests__/document-scope.test.ts`：`documents.*` RPC 未接入，4 项失败。

`tsconfig.node.json:20-25` 当前把四个文档引擎测试从类型检查中排除，因此 typecheck 通过不能证明这些测试对应的代码完整。

最终 TypeScript 配置中的 `noImplicitAny` 为 `false`。来源是基础配置 `@electron-toolkit/tsconfig` 显式关闭了它，而 `tsconfig.node.json` 与 `tsconfig.web.json` 没有覆盖。

### 2.3 当前工作区状态

本次审查时工作区有 33 个未提交条目，其中 15 个已修改文件、18 个未跟踪文件、没有 staged 改动。未提交改动集中在：

- 会话分支/复制
- 从会话提炼 Skill
- session、IPC contract、runtime、i18n 等热点文件
- 文档引擎相关测试和已有 pending 实现

`recovered/` 目录未被 `.gitignore` 忽略。任何实现开始前都必须先确认它是临时恢复产物还是产品输入，不能使用 `git add -A` 处理当前工作区。

### 2.4 已确认的架构优点

1. `src/main/kernel/host.ts:1-14` 定义了内核端口，`src/main/kernel/**` 不直接依赖 Electron。
2. `src/main/runtime.ts:1-14` 明确保持无 Electron import，使 Agent 链路可在普通 Node 中测试。
3. `src/shared/ipc/contract.ts:135-169` 使用 `IpcResult` 和事件序号；`src/preload/index.ts:32-62` 运行时校验频道并返回退订函数。
4. `src/main/ipc/index.ts:245-271` 使用完备的 `HandlerMap`，`safeHandle` 位于 `src/main/ipc/index.ts:804-823`，异常不会直接穿过 IPC。
5. 主窗口和 webview 的安全基线位于 `src/main/index.ts:260-375`；插件权限上界位于 `src/shared/plugin/permission.ts:104-162`。
6. 流式状态按会话存储，单一 Agent 事件泵位于 `src/renderer/src/stores/session.ts:1639-1665`。
7. 注释普遍记录不变式、失败症状和需求原因，重构时必须保留这些理由。

---

## 3. 目标架构与必须保留的不变式

### 3.1 目标依赖图

```text
renderer components / views / shell / settings
                    │
                    ▼
renderer services（唯一的 renderer IPC 域封装）
                    │
                    ▼
preload typed bridge（运行时白名单与安全边界）
                    │
                    ▼
main IPC domain handlers（只做边界适配和错误封装）
                    │
        ┌───────────┼───────────┐
        ▼           ▼           ▼
   runtime      state/store   plugin host
        │           │           │
        ▼           ▼           ▼
   kernel ports   db repos    capability gates
        │           │           │
        └───────────┴───────────┘
                    ▼
             shared pure types/functions
```

### 3.2 不变式

以下不变式在所有阶段都不得破坏：

1. renderer 不 import `src/main`、`electron` 或 Node 内置模块。
2. kernel 不 import Electron；外部能力经 `KernelHost`、窄接口或注入回调进入。
3. IPC 频道必须先登记在 shared contract，再进入 main handler 和 renderer service。
4. preload 只暴露 payload，不把 `IpcRendererEvent` 传给 renderer。
5. `on()` 返回的退订函数必须进入 effect cleanup，或由明确的全局生命周期持有。
6. SQLite、设置和会话持久化的事实源仍在主进程；renderer 只保存可重建的投影。
7. 流式文本继续放在 per-session store，不迁入全局 store。
8. 插件运行时权限上限仍由清单决定，插件拿不到主进程句柄、stream、fd 或任意路径。
9. 错误在边界处归一化；不在 renderer、service、IPC handler 三层重复翻译同一错误。
10. 所有迁移都必须兼容已有用户数据库、旧 session JSON、配置作用域和插件状态。

---

## 4. 阶段 0：恢复可信基线和工作区卫生

目标：先让仓库的质量结果可信，再开始结构重构。

### 4.1 固定当前工作区快照

涉及：

- 当前所有已修改文件
- `git status --porcelain=v1`
- `git diff --stat`
- `git diff --cached --stat`
- `.plan/`
- `recovered/`

步骤：

1. 由维护者确认当前 Skillify、session clone 和文档引擎改动分别属于哪个功能任务。
2. 不把本计划与已有功能改动混在一个提交中。
3. 对每条功能线单独记录文件清单和验证状态。
4. 清理或移出 `recovered/`；如果它属于临时数据，加入 `.gitignore`，但不忽略产品源码目录。
5. 检查 `.plan/` 中重复或过期计划，不在代码质量提交中顺手删除仍被使用的计划。

边界：

- 本阶段不改业务代码。
- 不使用 `git add -A`。
- 不因为发现并发修改而覆盖整文件。

验收：

```text
git status --short 中没有未确认归属的恢复文件
没有任何质量提交包含 unrelated 的功能改动
```

### 4.2 处理文档引擎 pending 测试

本计划选择：**文档引擎产品功能不纳入本次架构治理的默认发布范围，但测试规格保留并显式单独运行。**

涉及：

- `vitest.config.ts`
- `src/main/document-engine/__tests__/provider-registry.test.ts`
- `src/main/plugin/__tests__/document-rpc.test.ts`
- `src/main/plugin/__tests__/document-scope.test.ts`
- `src/main/document-engine/__tests__/native-host.test.ts`
- `src/main/document-engine/manager.ts`
- `src/main/plugin/unsupported.ts`
- `package.json`

步骤：

1. 将未接线的文档引擎测试移到明确的 pending 测试目录，或通过独立 include 配置加载。
2. 新增 `npm run test:pending`，让这些测试仍可被执行和统计。
3. 在默认 `npm test` 中不再加载不存在源文件的 pending 测试。
4. 在 pending 测试报告中明确显示失败数量，不能静默 `skip`。
5. 删除 `tsconfig.node.json` 对已经不再默认参与类型检查的测试的隐式排除；pending 测试要么进入独立 typecheck，要么在目录规则中明确说明原因。
6. 在 `unsupported.ts` 保留“文档引擎已校验但宿主尚未接线”的诊断。
7. 当产品决定正式接入时，再单独建立文档引擎实现计划，补齐：
   - `provider-registry.ts`
   - `document-rpc.ts`
   - `DocumentSessionManager.render`
   - 原生 helper 环境白名单
   - plugin manager 和 IPC 接线

验收：

```text
npm test              只包含当前发布范围，退出码为 0
npm run test:pending  明确显示文档引擎 pending 结果
```

### 4.3 消除 Python 测试环境依赖

涉及：

- `src/main/environment/__tests__/session-mux.test.ts`
- `src/main/environment/ssh/session-mux.ts`

步骤：

1. 用 `process.execPath` 启动仓库内 Node fixture，复现 mux 的 stdin/stdout 分帧协议。
2. fixture 必须覆盖：
   - banner 握手
   - 多次 exec
   - 长生命周期 stdin/stdout
   - TCP 转发
   - banner 前噪声和异常退出
3. Python 生成的远端启动脚本继续由生产代码保留，不把测试 fixture 当成生产 helper。
4. 如果必须保留 Python 兼容测试，将其放入平台/环境 smoke suite，并提供明确的 Python 路径配置，不作为默认单元测试前置条件。

验收：

```text
在没有 python 命令的环境中，session-mux.test.ts 仍全部通过
测试不产生未处理的 child_process 异常
```

### 4.4 阶段 0 完成条件

- 默认 `npm test` 退出码为 0。
- pending 测试显式可执行、可统计。
- `recovered/` 归属明确且不会被误提交。
- 当前功能改动与质量治理改动分开。

---

## 5. 阶段 1：TypeScript、lint、构建和发布门禁

目标：使类型系统和发布包真正反映代码质量。

### 5.1 打开 `noImplicitAny`

涉及：

- `tsconfig.node.json`
- `tsconfig.web.json`
- 必要的 `src/main/**`、`src/renderer/src/**`、`src/shared/**` 文件

步骤：

1. 在 node/web 两份项目配置中显式设置：

```json
"noImplicitAny": true
```

2. 分层运行：

```text
npm run typecheck:node
npm run typecheck:web
```

3. 先修复边界文件、services、shared 纯函数和 IPC handler 的隐式 any。
4. 不用 `any` 作为快速修复；优先使用 `unknown`、类型守卫、联合类型或 shared domain 类型。
5. 对 plugin API 的公共声明单独处理，不能把发布给插件作者的类型问题用 `skipLibCheck` 掩盖。

验收：

```text
最终 tsc --showConfig 显示 noImplicitAny=true
npm run typecheck 通过
渲染层新增 explicit any 和 implicit any 均被阻断
```

### 5.2 引入分阶段类型感知 lint

涉及：

- `eslint.config.mjs`
- `src/main/**`
- `src/renderer/src/services/**`
- 后续扩展到 `src/renderer/src/stores/**`

步骤：

1. 为 main 和 renderer services 配置 `projectService` 或等价的类型项目解析。
2. 首批启用：
   - `no-floating-promises`
   - `no-misused-promises`
   - `await-thenable`
   - `no-unnecessary-condition`
3. 修复现有 warning 后，把 `no-explicit-any` 和 `no-unused-vars` 从 warning 升为 error。
4. 不对生成目录、插件模板、资源 bundle 误启用类型 lint。
5. 对需要保留的 `unknown`、回调和动态插件 payload 写类型守卫，不使用 `eslint-disable`。

验收：

```text
npm run lint 通过且 0 error / 0 warning
关键 service 的浮空 Promise 能被 lint 捕获
```

### 5.3 收紧 electron-builder 包内容

涉及：

- `electron-builder.yml:6-20`
- `scripts/`
- `examples/`
- `packages/`
- `.plan/`
- `recovered/`
- `resources/`
- 新增 package artifact test

步骤：

1. 先执行一次 `dist:dir`，列出 `app.asar` 实际内容。
2. 优先使用正向 allowlist，只纳入：
   - Electron 构建产物 `out/**`
   - 生产依赖
   - `package.json`
   - 必需的 `resources/**`
3. 如果保留排除式配置，显式排除：
   - `examples/**`
   - `packages/**`
   - `scripts/**`
   - `.plan/**`
   - `recovered/**`
   - `AGENTS.md`
   - 开发文档和测试源码
4. 保留 `extraResources` 和插件 runtime 的实际生产依赖，不能误删 `resources/skills`、`resources/plugin-runtime` 或 native helper 必需文件。
5. 新增测试检查 asar 不含开发目录。
6. 在 macOS、Windows、Linux 各至少执行一次目录版启动检查。

验收：

```text
npm run dist:dir 通过
asar 中没有 examples、.plan、recovered、scripts、测试源码
安装包仍能加载 renderer、插件 runtime、内置 skills 和 node-pty
```

### 5.4 扩大 CI 验证范围

涉及：

- `.github/workflows/ci.yml`
- `package.json`
- `vitest.config.ts`
- `scripts/e2e-*.mjs`

目标流水线：

#### Pull Request

```text
npm ci --no-audit --no-fund
npm run typecheck
npm run lint
npm test
npm run build
```

#### Nightly

```text
npm run e2e
npm run e2e:theme
npm run e2e:import
npm run shot
```

#### Release

```text
npm run dist:dir
npm run test:release
asar 内容检查
跨平台启动 smoke test
```

步骤：

1. 保持 Ubuntu 作为快速质量门禁。
2. 增加 macOS 和 Windows 的构建/启动 smoke job。
3. 将耗时较长的 Electron probe 放入 nightly，避免 PR 过慢，但不能完全无人运行。
4. CI 输出测试失败分类，区分默认测试、pending 测试、Electron smoke 和 release uploader。

---

## 6. 阶段 2：IPC 边界治理

目标：保留现有强类型 IPC 优势，同时消除频道字符串外泄和中央注册表冲突。

### 6.1 补齐 renderer domain services

涉及：

- `src/renderer/src/services/*.ts`
- `src/renderer/src/stores/plugins.ts`
- `src/renderer/src/stores/documents.ts`
- `src/renderer/src/stores/mcp.ts`
- `src/renderer/src/stores/models.ts`
- `src/renderer/src/stores/skills.ts`
- `src/renderer/src/stores/window.ts`
- `src/renderer/src/App.tsx`
- `src/renderer/src/shell/AppShell.tsx`
- `src/renderer/src/settings/pages/import/ImportPage.tsx`
- `src/renderer/src/settings/pages/model/ProviderAccounts.tsx`
- `src/renderer/src/settings/pages/model/ProviderPanel.tsx`
- `src/renderer/src/shell/PluginCardFrame.tsx`
- `src/renderer/src/views/chat/CardRenderer.tsx`
- `src/renderer/src/views/extensions/plugins/PluginConfiguration.tsx`

步骤：

1. 每个领域 service 同时提供 invoke/send/event wrapper。
2. 例如 plugin 域集中提供：
   - `listPlugins`
   - `setPluginEnabled`
   - `onPluginChanged`
   - `onPluginInstallProgress`
   - `cardAction`
   - `confirmClose`
3. App 层使用命名订阅函数，不再直接调用通用 `on('...')`。
4. `window.nextcowork.getPathForFile` 这类非频道平台能力也通过明确的 platform/app service 暴露，减少组件直接触碰 bridge。
5. 迁移时保持函数返回值、错误类型和 cleanup 行为不变。
6. 每次迁移一个领域，运行对应 renderer tests，不进行全文件格式化。

### 6.2 建立架构静态检查

新增一个可在 Node 中运行的架构测试或 ESLint 规则，检查：

- renderer `components/views/stores/shell/settings` 中不能出现应用 IPC 频道字符串。
- `window.nextcowork.invoke/send/on` 只能出现在 `services/ipc.ts` 或平台 service。
- renderer 不得 import `electron`、`node:*`、`src/main`。
- shared 不得 import renderer/main runtime。
- kernel 不得 import Electron。

验收：

```text
架构检查加入 npm test 或 npm run lint
新增违规时 CI 直接失败
```

### 6.3 按领域拆分 IPC contract

涉及：

- `src/shared/ipc/contract.ts`
- 新增 `src/shared/ipc/contracts/` 目录
- `src/main/ipc/index.ts`
- `src/main/ipc/*.ts`
- `src/renderer/src/services/*.ts`

目标目录：

```text
shared/ipc/contracts/app.ts
shared/ipc/contracts/agent.ts
shared/ipc/contracts/session.ts
shared/ipc/contracts/workspace.ts
shared/ipc/contracts/provider.ts
shared/ipc/contracts/plugin.ts
shared/ipc/contracts/storage.ts
shared/ipc/contracts/import.ts
shared/ipc/contracts/theme.ts
shared/ipc/contracts/scheduled.ts
shared/ipc/contract.ts          // 组合、导出、白名单、派生类型
```

步骤：

1. 将请求/响应/event 类型按领域迁移，不改变频道名称。
2. `contract.ts` 通过类型组合重新导出 `IpcInvokeMap`、`IpcSendMap`、`IpcEventMap`。
3. 各 main IPC 模块导出领域 handler map。
4. `main/ipc/index.ts` 只负责组合和注册，不再承载所有业务 handler。
5. `safeHandle`、`safeListen`、migration startup gate 保持一个实现。
6. 增加 contract/handler 完备性测试，覆盖：
   - contract 有而 handler 无
   - handler 有而 contract 无
   - startup migration channel 重复注册
   - event/send/invoke 白名单原型链绕过

验收：

```text
新增频道只需修改所属领域 contract/service/handler
全量 typecheck 仍能捕获漏 handler
运行时白名单和 seq/attach 行为不变
```

### 6.4 插件内部 RPC 做边界校验

涉及：

- `src/main/plugin/host-window.ts`
- `src/shared/plugin/protocol.ts`
- `src/main/plugin/manager.ts`
- `src/main/plugin/rpc.ts`
- `src/main/plugin/capabilities.ts`

步骤：

1. 对 `raw as PluginRequest`、`raw as PluginInvocation` 等边界增加运行时 narrow/schema 校验。
2. 校验 id、method、params、response code 和 invocation kind。
3. 无效报文返回结构化 `invalid_argument` 或 `internal_error`，不能让异常穿出插件 host IPC。
4. 保留 sender id → plugin id 的身份绑定，不信任报文中的 pluginId。
5. 增加畸形 payload、重复 response、未知 invocation、超时和 render-process-gone 测试。

---

## 7. 阶段 3：渐进拆分主进程和渲染层热点

目标：降低变更冲突和单模块认知负担，不改变外部 API。

### 7.1 runtime 引入 RuntimeContext

涉及：

- `src/main/runtime.ts`
- 新增 `src/main/runtime/context.ts`
- 新增 `src/main/runtime/lifecycle.ts`
- 新增 `src/main/runtime/pricing.ts`
- 新增 `src/main/runtime/plugin-bridges.ts`
- 新增 `src/main/runtime/environment-runtime.ts`

步骤：

1. 把 `host/router/tools/mcp/environments/sessionTitles/agentDrafts/commitMessages` 聚合到 `RuntimeContext`。
2. 把 install callback 归入 context 的显式 bridge 集合。
3. 用 `createRuntimeContext()`、`startRuntime()`、`stopRuntime()` 表达生命周期。
4. 现有 `getHost()`、`getTools()`、`initRuntime()` 等函数暂时保留为 facade。
5. 将 pricing 纯逻辑优先抽离，因为它依赖较少、风险最低。
6. 将测试 reset 从“清理一堆模块级变量”改成“销毁一个 context”，但在迁移期保留旧 reset 兼容。

必须保留：

- runtime 不得反向 import `ipc` 或 Electron。
- 所有广播继续通过注入 sink 进入 IPC。
- shutdown 顺序必须可测试：runs → environments → MCP → titles → host resources。

验收：

- Agent 无头测试无需 Electron。
- `resetRuntimeForTest` 不再新增全局变量清理项。
- 并发测试之间没有 timer、run、MCP、plugin tool 泄漏。

### 7.2 DB repository 按实体拆分

涉及：

- `src/main/db/repo.ts`
- `src/main/state/store.ts`
- 新增 `src/main/db/repos/`
- `src/main/db/index.ts`
- `src/main/db/schema.ts`

拆分顺序：

1. `session-repo.ts`
2. `workspace-repo.ts`
3. `provider-repo.ts`
4. `usage-repo.ts`
5. `scheduled-repo.ts`
6. `config-sync-repo.ts`
7. `storage-repo.ts`

步骤：

1. 保持 `store` 对外方法签名不变。
2. 事务仍从 `db/index.ts` 提供统一入口。
3. 迁移版本和 schema 不在本次拆分中重排。
4. 每个 repository 只处理本领域 SQL 和 row mapping。
5. 所有跨实体写入显式调用共享 `tx`，不能因为拆文件丢失事务边界。
6. 所有返回对象继续保持拷贝/不可意外修改语义，尤其是 workspace/settings。

验收：

- 既有 DB roundtrip、migration、provider、session tests 全通过。
- 不产生新的 handler 直接 SQL。
- repository 可在未来被 utility process adapter 替换。

### 7.3 renderer session store 渐进抽纯逻辑

涉及：

- `src/renderer/src/stores/session.ts`
- `src/shared/agent/transcript.ts`
- `src/renderer/src/views/chat/`
- `src/renderer/src/stores/__tests__/`

抽离顺序：

1. history hydration 和 restore
2. queue 操作和 queued input persistence
3. event envelope/seq gap/replay
4. run lifecycle 和 active run index
5. goal injection/interject
6. UI-only message projection

约束：

- 保留 `sessionStore(sessionId)` 作为 renderer 唯一入口。
- 抽出的模块必须是纯函数或窄 service，不创建第二份 session state。
- 所有高频事件仍通过单一 Agent event pump。
- `requestAnimationFrame` cleanup 仍由 `startAgentEventPump()` 持有。

需要新增/修复的测试：

- attach 与 live event 重叠时不重复 usage
- late snapshot 不覆盖更新事件
- store 销毁后 active run 仍在全局索引
- queue 在 reload 后与主进程重新对账
- child run 事件只投递到对应 parent card/session
- mock service 必须包含 `interjectRun` 等完整导出，避免测试产生误导性 stderr

### 7.4 i18n 分域

涉及：

- `src/renderer/src/i18n/index.tsx`
- `src/renderer/src/i18n/*.ts`
- `src/renderer/src/i18n/index.test.ts`
- `src/renderer/src/i18n/plugin-messages.ts`

步骤：

1. 新增领域只允许进入独立文件，不再增大 `index.tsx`。
2. 按现有 `agent.ts`、`git.ts`、`usage.ts` 模式导出 `xxxZh`/`xxxEn`。
3. `index.tsx` 逐步只保留 catalog 组合、locale 状态和公共类型。
4. 保留两语言 key 完整性测试。
5. 插件文案继续限制在 `plugin.<pluginId>.` 前缀。
6. 非组件代码继续使用 `translate()`，组件使用 `useI18n().t()`。

验收：

```text
新增 i18n 域不需要触碰大段旧 catalog
zh-CN/en-US key 集合完全一致
插件无法覆盖内置 key
```

### 7.5 plugin manager 拆策略与机制

涉及：

- `src/main/plugin/manager.ts`
- `src/main/plugin/host-window.ts`
- `src/main/plugin/rpc.ts`
- `src/main/plugin/capabilities.ts`
- `src/main/plugin/diagnostics.ts`
- 新增 `src/main/plugin/lifecycle.ts`
- 新增 `src/main/plugin/contributions.ts`
- 新增 `src/main/plugin/permission-state.ts`
- 新增 `src/main/plugin/streaming.ts`

步骤：

1. `manager.ts` 保留公共 facade 和 plugin record 索引。
2. 生命周期状态转移抽到 `lifecycle.ts`。
3. command/menu/editor/webapp/tool/skill contribution 抽到 `contributions.ts`。
4. 权限升级、授权和撤销抽到 `permission-state.ts`。
5. exec stream batching、abort、timeout、truncation 抽到 `streaming.ts`。
6. Electron BrowserWindow 机制继续留在 `host-window.ts`，策略模块不得依赖 Electron。
7. 每次拆分后运行插件 host、permission、capability、market、custom editor 测试。

---

## 8. 阶段 4：数据库性能、跨平台和安全回归

### 8.1 SQLite 性能评估

涉及：

- `src/main/db/index.ts`
- `src/main/db/repos/**`
- `src/main/ipc/storage.ts`
- `src/main/imports/service.ts`
- `src/main/runtime.ts`

先测量以下指标：

- 冷启动迁移耗时
- bootstrap 查询耗时
- 会话列表和历史加载耗时
- 每条 `message_commit` 的写入耗时
- FTS 搜索 p95
- 导入/备份/清理耗时
- VACUUM 阻塞时间

阈值建议：

- 普通 IPC 查询 p95 < 50ms
- bootstrap p95 < 150ms
- 单次 renderer 可感知阻塞不超过 100ms
- 长历史加载必须分页或异步化

只有超过阈值的路径才进入 utility process 迁移。迁移时：

1. 先定义 repository command/result 协议。
2. 保持事务和错误 code 语义一致。
3. 批量提交消息，不能逐条产生大量跨进程消息。
4. 数据库连接只允许 utility process 持有。
5. 应用退出时要有 flush/close handshake。
6. utility process 崩溃时提供可恢复错误，而不是静默丢状态。

### 8.2 Electron 安全回归

新增或增强测试：

- preload 只暴露预期 API
- contextIsolation 关闭时拒绝启动
- 未登记 invoke/send/event 频道被拒绝
- `__proto__`、`constructor` 等频道名不能绕过白名单
- webview 不继承 preload
- webview 只能加载允许协议
- 远程页面不能跳转到任意 file URL
- 插件消息根据 sender id 绑定 plugin id
- 插件不能越过 manifest permission ceiling
- workspace path、realpath、symlink、prefix collision 全部拒绝
- native helper 只能接收白名单环境变量
- helper 超时、崩溃、重复 binary frame 都能收敛到确定状态

### 8.3 跨平台 smoke matrix

至少验证：

| 场景 | macOS | Windows | Linux |
|---|---:|---:|---:|
| 主窗口启动与关闭 | 必须 | 必须 | 必须 |
| 自绘标题栏/窗口控制 | 必须 | 必须 | 必须 |
| SQLite 首次迁移 | 必须 | 必须 | 必须 |
| SSH askpass | 必须 | 必须 | 必须 |
| terminal/node-pty | 必须 | 必须 | 必须 |
| 插件宿主窗口 | 必须 | 必须 | 必须 |
| webview 导航安全 | 必须 | 必须 | 必须 |
| 打包目录版启动 | 必须 | 必须 | 必须 |
| 数据升级和回滚 | 至少 nightly | 至少 nightly | 至少 nightly |

---

## 9. 测试策略

### 9.1 单元测试

优先测试纯逻辑：

- shared domain normalizer、clone、queue、session、permission
- kernel context、permission gate、upstream codec、abort、scheduler
- IPC error normalization、contract completeness
- plugin permission/capability/path guard
- renderer session event reconciliation、dock layout、i18n catalog

测试必须：

- 使用 `src/**/*.test.ts`，不引入 `.test.tsx`
- DOM 测试自行创建 JSDOM，补齐必要全局
- 每个模块级 registry/store 在 `afterEach` 清理
- 不依赖真实 Electron，除非是专门的 Electron smoke suite
- 不依赖本机偶然存在的 `python`、shell 或用户目录

### 9.2 集成测试

覆盖：

- main IPC handler → store/repository
- Agent run → message commit → session reload
- plugin host → manager → capability
- session branch/clone → attachment rehome
- import/backup/restore/migration
- native helper protocol

### 9.3 发布测试

新增：

- build 产物存在性
- preload 单文件产物
- widget shell 产物名称和协议路径一致
- app.asar 内容白名单
- resources/plugin-runtime 和 bundled skills 存在
- `node-pty` unpack 结果正确
- clean profile 首次启动
- old profile upgrade
- interrupted migration recovery

### 9.4 覆盖率

第一阶段只增加报告，不立即设置全仓高阈值：

1. shared/kernel/plugin permission/IPC contract 先设最低阈值。
2. 每个阶段只允许新增代码覆盖率下降不超过 2%。
3. renderer 巨型组件先不设行覆盖率目标，优先测抽出的纯逻辑和边界。
4. nightly 记录覆盖率趋势，避免测试数量增加但关键路径没有覆盖。

---

## 10. 数据迁移与兼容要求

所有结构改动必须遵循：

1. 不改已有 session、workspace、provider、plugin state 的 JSON 语义，除非提供兼容读取。
2. SQLite migration 只增不改，版本号递增。
3. 新字段读取必须允许旧记录缺失。
4. 配置作用域切换时，profile-scoped KV 不能泄漏到其他账户。
5. session clone/branch 的 `ncw://` 图片必须只重写属于源会话的引用。
6. 外部文件、插件配置、附件和 Chromium profile 的删除/迁移必须有回滚路径。
7. 导入和恢复失败时不能留下半份 workspace、孤立 attachment 或 pending run。
8. DB repository 拆分不改变事务边界和返回对象复制语义。
9. utility process 迁移前后，错误 code、超时、取消和退出顺序必须一致。

迁移验证最少包括：

- 空数据库首次启动
- v2.4 数据升级到当前版本
- 有多个 profile 的账户切换
- 有大量 session/attachment 的数据库
- 中断迁移后再次启动
- 旧插件 state 和权限升级
- 旧 renderer reload 时存在 active run

---

## 11. 交付顺序和提交切片

建议按独立、可回滚的提交拆分：

```text
chore(test): 恢复默认测试门禁
chore(test): 移出并显式标记文档引擎 pending 测试
chore(test): 消除 session-mux 的 Python 环境依赖
chore(build): 收紧安装包内容边界
chore(types): 打开 noImplicitAny
chore(lint): 增加关键路径类型感知检查
ci: 增加 build 与跨平台 smoke gate
refactor(ipc): 收拢 renderer domain IPC services
refactor(ipc): 按领域拆分 contract 和 handler map
refactor(runtime): 引入 RuntimeContext
refactor(db): 按实体拆分 repository
refactor(renderer): 拆分 session store 纯逻辑
refactor(i18n): 继续拆分 catalog
refactor(plugin): 拆分 manager 生命周期与 capability 策略
perf(db): 根据指标迁移慢查询
```

每个提交必须满足：

- 不覆盖其他 agent 的未提交改动。
- 只修改必要路径。
- 不整文件重新格式化。
- 先跑相关测试，再跑全量质量命令。
- 若触碰 `★` 注释，必须同步保留原有事故原因和验证依据。

---

## 12. 完成定义

### 阶段 0 完成

- 默认测试绿色。
- pending 测试显式可运行。
- 没有未确认的恢复文件或临时产物。
- 当前功能改动和质量改动已经分开。

### 阶段 1 完成

```text
npm run typecheck 通过
npm test 通过
npm run lint 通过且 0 warning
npm run build 通过
npm run dist:dir 通过
```

并且：

- `noImplicitAny=true`
- app.asar 不含开发文件、测试文件和恢复文件
- 构建产物可启动

### 阶段 2 完成

- renderer 业务代码不再直接写 IPC 频道字符串。
- IPC contract、service、main handler 三者可由测试验证完整性。
- 所有 event subscription 有明确 cleanup。
- plugin raw payload 有运行时边界校验。

### 阶段 3 完成

- runtime、repo、session store、i18n、plugin manager 的新增功能不再继续堆入原热点文件。
- 每个新模块有明确 owner、输入/输出和测试边界。
- reset/lifecycle 不依赖越来越长的全局变量清理列表。

### 阶段 4 完成

- SQLite 是否迁移由指标决定，而不是凭直觉。
- macOS、Windows、Linux 至少有启动、打包、迁移和关键安全 smoke。
- 插件权限、preload、webview、路径安全、native helper 都有回归测试。
- release CI 不仅验证 TypeScript，还验证真实安装包内容和启动行为。

最终目标不是让所有文件都“小而漂亮”，而是让以后新增一个领域时，能够明确回答：

1. 状态的唯一真源在哪里？
2. 代码属于哪个层？
3. IPC、错误、权限和生命周期由谁负责？
4. 不启动 Electron 如何测试核心逻辑？
5. 发布前哪条自动化检查会阻止回归？
