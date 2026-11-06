# ClaudeCode / Codex 插件:+ 菜单一键启动 + 已安装页连接配置

## 目标

1. 两个新插件 `acme.claude-code`、`acme.codex`,各在标签 `+` 菜单贡献一个启动项(**Claude Code** / **Codex**),使用官方 logo 图标(插件自带图标文件)。
2. 点击菜单 → 在**当前工作区根目录**打开一个终端 Tab,自动带上用户配置的 baseURL / 模型 / API Key(**环境变量注入**,不落盘),并启动对应 CLI(`claude` / `codex`)。
3. 已安装插件详情页提供配置表单(走既有 `contributes.configuration` 贡献点,宿主零改动)。
4. 一期仅本地工作区;SSH 工作区点菜单给出提示。

## 已确认的决策

| 决策 | 结论 |
|---|---|
| logo 进法 | **插件自带图标文件**(新贡献字段 `iconFile`,经 IPC 转 data URL 渲染;渲染层 CSP `img-src` 已允许 `data:`,无需改 CSP) |
| 配置字段 | 每插件 3 项:`baseUrl` / `model` / `apiKey`(string,均可空) |
| 注入方式 | **spawn 时环境变量注入**(不写 ~/.claude、~/.codex) |
| 远程范围 | **一期仅本地工作区**,SSH 降级 + toast 提示 |

## 现状事实(已查证)

- `+` 菜单 = 内置项 + 插件贡献项合成(`shell/tab-menu.ts` → `mergeMenuItems`),挂载点 `tabBar/new` **已 live**;插件项 clamp 在同组内置项之后,单插件最多 3 项。
- 菜单点击插件项 → `Dock.tsx:192` / `InnerTabBar` → `usePluginsStore.runCommand(pluginId, commandId)` → IPC `plugins:runCommand` → `manager.runCommand`(manager.ts:859,转发 `command.run` 事件,**目前不携带任何上下文参数**)。
- 菜单图标是闭集 `MENU_ICON_NAMES`(shared/plugin/contribution.ts)↔ `MENU_ICON`(shell/icons.tsx),头注释写明「插件给名字不给 SVG」的防钓鱼理由 —— 本次按用户决策**有意松动**,松动理由必须写回注释。
- 插件 API 无任何开终端能力(`nextcowork.d.ts` tabs 命名空间只有 openCustomEditor / openWebApp / openBrowser);终端是主进程 `TerminalHost`(node-pty),`terminal:create` 请求无 env/command 字段。
- 终端 Tab 的 `ref.terminalId` 由渲染层生成(tabs.ts:99 `ulid()`),`TerminalCreateRequest.id` 命中已有活会话时**复用**(terminal-host.ts:113-126)—— 这是「主进程先备好会话、渲染层 Tab 来认领」的既有通道。
- `environment.openTerminal({ cwd, cols, rows })`(environment/contract.ts:56);local 实现里 pty spawn 已带 `env: { ...process.env, TERM }`(local.ts:102)—— env 注入是参数级改动。
- 配置:`contributes.configuration` **live**(详情页渲染表单,`plugins:getConfiguration`/`setConfiguration` IPC 既有);插件侧 `ncw.configuration.get()` 读合并值,零权限。
- `tabs.openWebApp` 的既有范式:manager 校验 → `deps.openTab` → `windows.emitToAll('plugins:openTab')` → 渲染层 `placePluginTab`(纯函数)决定落格。
- 渲染层 CSP:`img-src … data: …`(renderer/index.html:27)✓;插件视图 iframe 才受 `ncw-plugin://` 限制。

## 架构总览

```
用户点 + 菜单「Claude Code」
  → runCommand(pluginId, commandId, { workspaceId })        [宿主:参数转发,新]
  → 插件 handler: configuration.get() 读 baseUrl/model/apiKey
  → ncw.tabs.openTerminal({ workspaceId, command, args, env, title })  [宿主:新 RPC]
      主进程校验:启用/权限 process/command ∈ allowedCommands/env 上限/本地工作区
      → TerminalHost.setLaunchSpec(terminalId, { env, argv })  [一次性,TTL 60s]
      → deps.openTab 广播 plugins:openTab { kind:'terminal', terminalId, … }
  → 渲染层 placePluginTab → 开 terminal Tab(ref.terminalId = 主进程给的 id)
  → TerminalView → terminal:prepare/create(id 命中 spec)
      spawn 时合并 env(local.ts)→ spawn 后写入启动行 `claude\n` / `codex -m X\n`
```

env 只存在于主进程 spec 与 pty 进程环境,**不经渲染层、不进终端回滚缓冲、不落盘**。

---

## 宿主改动

### A. 插件自带菜单图标(`iconFile`)

1. `src/shared/plugin/manifest.ts` — `PluginCommandContribution` 增 `iconFile?: string`(包内相对路径,`.svg`/`.png`,≤32KB);解析时校验路径形状(不许 `..`、绝对路径)。
2. `src/main/plugin/installer.ts` — 安装核验清单加 `iconFile` 引用存在性(同 `icon` 的既有做法)。
3. `src/main/ipc/plugins.ts`(manager deps 或 catalog 投影处)— 读包内文件转 `data:` URL,随菜单项数据下发(和 `publishLocaleBundles` 同一个「装载时准备好」的取向);读不到 → 不带 iconUrl,走名字回落。
4. `src/shared/plugin/contribution.ts` — `TabMenuItem` 增 `iconUrl?: string`(data URL,仅投影层填);`MENU_ICON_NAMES` 闭集**不动**。
5. `src/renderer/src/stores/plugins.ts` — `menuItems('tabBar/new')` 投影时带上 `iconUrl`(titleKey 同源的来源)。
6. `src/renderer/src/shell/InnerTabBar.tsx`(两处 MenuItem)+ `Dock.tsx` 侧边栏如共用 — `item.iconUrl !== undefined` 时渲染 `<img src draggable={false} className="size-[14px]">`,否则走 `MENU_ICON[item.icon]`。
7. `src/renderer/src/shell/icons.tsx` — 头注释补一段:**为什么现在允许插件带图**(图标取自用户已安装插件自己的包、经主进程读文件转 data URL、只出现在带插件署名的菜单条目上;闭集对「名字 → 宿主审过的字形」的保证不变)。

### B. 命令上下文转发(插件得知道「哪个工作区」)

1. `src/renderer/src/stores/plugins.ts:269` — `runCommand(pluginId, commandId, args?: { workspaceId: string })`。
2. `src/shared/ipc/contract.ts` — `plugins:runCommand` req 增可选 `args`。
3. `src/main/ipc/plugins.ts:588` + `src/main/plugin/manager.ts:859,880` — `runCommand` 透传,`command.run` payload 变 `{ commandId, args? }`。
4. `src/renderer/src/shell/Dock.tsx:192` — 调用点带 `{ workspaceId: workspace.id }`(InnerTabBar 的 onOpen 链路从 Dock/WorkspaceView 传下来,实现时确认最短穿线)。
5. `packages/plugin-api/nextcowork.d.ts` — `registerCommand` handler 参数注释补「args 为宿主菜单透传」。

### C. 新 RPC `tabs.openTerminal`

1. `src/shared/plugin/protocol.ts` — PluginMethodMap 增
   `'tabs.openTerminal': { params: { workspaceId: string; command: string; args?: string[]; env?: Record<string,string>; title?: string }; result: { opened: boolean } }`;
   permission map:`'tabs.openTerminal': 'process'`(复用既有能力,不新增权限名)。
2. `src/main/plugin/manager.ts`(case 分发处,同 `tabs.openWebApp` :1367 附近)— 校验:启用/激活;`command` 的 argv[0] 命中该插件 `allowedCommands`(复用 process.exec 的 stem 匹配器,`capabilities.ts`);`args`/`env` 键值均为 string,env ≤16 项、单值 ≤4KB、键名 `^[A-Za-z_][A-Za-z0-9_]*$`;**cwd 不接受插件指定**(恒为工作区根,由 TerminalHost 按既有 `resolveWithin(rootPath)` 解析)。全部通过 → `deps.launchTerminal(pluginId, spec)`,返回 `{ opened: true }`;任一失败返回 `{ opened: false }`(插件侧抛错)。
3. `src/main/ipc/plugins.ts` — 实现 `launchTerminal`:取 `getEnvironments().acquire(workspaceId)`,`environment.remote === true` → 释放并返回 `{ opened: false, reason: 'remote' }`;否则 `terminalId = randomUUID()`,`terminalHost.setLaunchSpec(terminalId, { workspaceId, env, argv: [command, ...args] })`,然后走既有 `openTab` 广播(target 见下)。
4. `src/shared/plugin/ui-request.ts` — `PluginTabTarget` 增 `{ kind: 'terminal'; terminalId: string; workspaceId: string; open: 'tab' }`。
5. `src/renderer/src/shell/plugin-tab-target.ts` — `placePluginTab` 处理 `kind: 'terminal'` → `{ kind: 'terminal', pane: 'main', init: { title, terminalId, workspaceId } }`。
6. `src/renderer/src/stores/tabs.ts:98-99` — `init.terminalId !== undefined` 时 `ref.terminalId` 用它,不再 `ulid()`(★ 注释:主进程备好的会话按 id 认领,换 id = 起一个裸 shell,注入配置静默丢失)。
7. 远程提示:tabs store 或 placePluginTab 调用侧对 `reason: 'remote'` 不感知(主进程直接不开 Tab);提示由**插件**根据 `opened: false` 抛错 → 宿主 `runCommand` 失败路径补一个 toast(渲染层 stores/plugins.ts catch 处,新增宿主 i18n key `plugins.commandFailed`,按 §6 落到既有 plugins 域文案旁;两个插件自己不再各写一份提示)。

### D. 终端 env 注入(主进程)

1. `src/main/environment/contract.ts:56` — `openTerminal(options: { cwd; cols; rows; env?: Record<string,string> })`。
2. `src/main/environment/local.ts:100-102` — `env: { ...process.env, TERM, ...options.env }`(插件值赢)。
3. `src/main/environment/ssh/provider.ts:81` — 增参不使用(忽略 `env`),文件头注明「一期插件终端仅本地」。
4. `src/main/terminal-host.ts` —
   - `launchSpecs = Map<terminalId, { env, argv, workspaceId, expiresAt }>`;`setLaunchSpec()` 写入,TTL 60s(同 intents 的清理模式,`unref` 定时器);
   - `createOne`(local 分支)spawn 前取 `const spec = this.launchSpecs.get(req.id)`,有则并入 `openTerminal` 的 env;spawn 后 `child.write(shellLine(spec.argv) + '\n')` 并立即 `delete`(★ 一次性:复用会话重连时不得重放启动行);`shellLine` 做单引号转义(`'` → `'\''`);
   - **spec 只在 `!environment.remote` 分支生效**;关闭/超时统一清理。
   - 现有审批/归属逻辑零改动(spec 不改变 sender 语义,会话归属仍是发起 create 的渲染层)。

---

## 两个插件

```
examples/acme.claude-code/          examples/acme.codex/
├── package.json                    (同构)
├── build.mjs / tsconfig.json / .gitignore   (照抄 acme.think)
├── l10n/zh-CN.json, en-US.json
├── assets/
│   ├── icon.png                    (插件自身图标,调 routin.ai 生成或官方 logo 转制)
│   └── menu-claude.svg / menu-codex.svg   (官方品牌 logo;实现时从官方品牌页/官方仓库取)
└── src/extension.ts
```

### manifest 要点(以 claude-code 为例;codex 同构换名)

```jsonc
{
  "publisher": "acme", "name": "claude-code",
  "displayName": "Claude Code", "description": "在 + 菜单一键启动 Claude Code 终端…(详细中文描述)",
  "version": "0.1.0", "engines": { "nextcowork": ">=0.3.1" },
  "main": "./dist/extension.js", "l10n": "./l10n", "icon": "assets/icon.png",
  "activationEvents": ["onCommand:acme.claude-code.launch"],
  "permissions": ["process"],
  "allowedCommands": ["claude"],
  "contributes": {
    "commands": [{ "command": "acme.claude-code.launch", "title": "%cmd.launch%", "iconFile": "assets/menu-claude.svg" }],
    "menus": { "tabBar/new": [{ "command": "acme.claude-code.launch", "group": "tools@30" }] },
    "configuration": {
      "title": "%config.title%",
      "properties": {
        "baseUrl": { "type": "string", "title": "%config.baseUrl%", "default": "" },
        "model":   { "type": "string", "title": "%config.model%",   "default": "" },
        "apiKey":  { "type": "string", "title": "%config.apiKey%",  "default": "" }
      }
    }
  }
}
```

- `group: "tools@30"` → 排在「新建终端 / 网页浏览」之后(同组 clamp 规则,正好是用户截图里期望的位置);两条插件各自一项,互不触发 3 项上限。
- 菜单标题 l10n:Claude Code / Codex 是品牌名,两份 locale 同文。

### extension.ts 行为(每插件 ~60 行)

```ts
registerCommand('acme.claude-code.launch', async (args) => {
  const workspaceId = (args as { workspaceId?: string } | undefined)?.workspaceId
  if (workspaceId === undefined) throw new Error('no workspace')      // 理论不可达,菜单必带
  const cfg = await ncw.configuration.get()                           // 零权限
  const env: Record<string, string> = {}
  if (cfg.baseUrl !== '') env.ANTHROPIC_BASE_URL = cfg.baseUrl
  if (cfg.apiKey !== '')  env.ANTHROPIC_AUTH_TOKEN = cfg.apiKey       // claude
  if (cfg.model !== '')   env.ANTHROPIC_MODEL = cfg.model
  const result = await ncw.tabs.openTerminal({
    workspaceId, command: 'claude', ...(Object.keys(env).length > 0 ? { env } : {}),
    title: 'Claude Code'
  })
  if (result.opened === false) throw new Error(result.reason === 'remote' ? 'remote-unsupported' : 'failed')
})
```

codex 差异:`allowedCommands: ["codex"]`;env 用 `OPENAI_BASE_URL` / `OPENAI_API_KEY`;模型走启动行参数 `codex -m <model>`(codex 不读模型环境变量),即 `args: cfg.model !== '' ? ['-m', cfg.model] : undefined`。
`nextcowork.d.ts` 补 `tabs.openTerminal` 类型声明。

### 文案

- 插件 l10n:`cmd.launch`(Claude Code / Codex)、`config.title/baseUrl/model/apiKey`。
- 宿主 i18n:`plugins.commandFailed`(「插件命令执行失败」)、`plugins.terminal.remote`(「SSH 远程工作区暂不支持在此启动,请使用本地工作区」)— 落在既有 plugins 相关文案同一文件;zh/en 同步补。

## 端到端验收

1. 本地工作区 + 已配置 baseUrl/model/key:点 `+` → Claude Code → 新终端 Tab 自动进入工作区根目录并进入 `claude` 会话;终端内 `/status`(或 env)可见 ANTHROPIC_BASE_URL 已生效;回滚缓冲里**看不到** apiKey。
2. 未配置:直接裸启动 CLI(可选项缺失就是缺失,不造默认值)。
3. SSH 工作区:点菜单 → toast「远程暂不支持」,不开 Tab。
4. `codex -m` 启动行、env 注入同理验证。
5. 卸载/禁用插件后 `+` 菜单项消失(既有 catalog 驱动,预期免费)。
6. 主进程侧校验:非 allowedCommands 命令、env 超限、远程工作区 → `opened: false`。

## 测试(跟随仓库既有位置与写法,`src/**/*.test.ts`)

- `shared/plugin` contract 测试:TabMenuItem `iconUrl` 透传;PluginTabTarget terminal 分支的 `placePluginTab`(含 title 传递)。
- `src/main/__tests__/terminal-host.test.ts` 补:spec 命中时 env 并入 + 启动行写入 + 一次性消费(复用 create 不重放)+ 远程不消费;spec TTL 清理。
- `environment` 测试:local openTerminal 合并 env(插件值覆盖默认值)。
- manifest 测试:`iconFile` 路径校验;installer 引用存在性。
- manager/权限测试:`tabs.openTerminal` 的 allowedCommands stem、env 上限、远程拒绝。
- 跑法:`npm run typecheck:node` + `npm run typecheck:web` + 目标 vitest 文件 + `npx eslint <改动文件>`。typecheck:node 现存他人在建文件的报错(document-engine 等)与本次无关,以「不新增涉及本次文件的报错」为准线。

## 明确不做(本次)

- 不写 ~/.claude / ~/.codex 配置文件;不做「写入全局配置」按钮。
- 不支持 SSH/远程工作区的插件终端(二期:ssh invocation 行内 env + 审批流贯通)。
- 不做多个工作区根的选择(恒为 workspaceId 对应的根;多根工作区一期不涉及)。
- 不动 `MENU_ICON_NAMES` 闭集本身(名字回落机制原样保留,iconFile 是并行的第二条路)。

## 风险与注释义务

- icons.tsx 头注释记录了「不给 SVG」的防钓鱼理由 —— 本次改动**必须**按 §10.2 改写该段并保留原理由(「原先闭集是因为 X;现在 X 由 Y(图标只来自用户已安装的插件包 + 主进程读文件 + 只出现在插件署名条目)缓解,所以放宽为 Z」),不直接删。
- terminal-host.ts / tabs.ts / mergeMenuItems 均有 ★ 注释,改动处同步更新注释并说明症状。
- env 注入让 key 进入 pty 子进程环境:不落盘、不进回滚缓冲,但**对在该终端里手动执行 `env` 的用户可见** —— 与用户自己 export 无异,属预期;插件描述里写明。
