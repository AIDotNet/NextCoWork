# 03 · 界面设计与布局

全新设计,不沿用 2.x 的界面。整体参考「左侧项目图标栏 + 项目面板 + 主工作区」的组织方式,
并增加**项目分组**。

## 1. 桌面三栏布局

```
┌────┬─────────────────────┬────────────────────────────────────────────────────┬──────────────┐
│ ●○●│ ◧ NextCoWork      ⇤ │ [✱ 重构登录 ×][>_ zsh ×][± 3 files ×]  +    ⋯  ◔ 51% ◫ ◨│              │
├────┼─────────────────────┼────────────────────────────────────────────────────┤  检查器(可选)│
│ 👤 │ NextCoWork      ⌄   │                                                    │  ─────────── │
│    │ ~/code/NextCoWork   │                                                    │  上下文 51%   │
│ ┌──┤ 💻 本机 · main ✓   │                                                    │  计划 / 待办   │
│ │🐙│ ┌─────────────────┐ │                                                    │  子代理 (2)   │
│ │🦊│ │🔍 在此项目中搜索 ⌘P│ │                主工作区(标签页容器)                 │  变更文件 (3) │
│ └──┤ └─────────────────┘ │                会话 / 终端 / 文件 / Diff / 浏览器     │  费用          │
│ 🟠 │ 会话             +  │                                                    │              │
│ ┌──┤  ● 重构登录    2:31 │                                                    │              │
│ │A │  ○ 修复 CI           │                                                    │              │
│ │A │ 终端和智能体      +  │                                                    │              │
│ └──┤  ✱ Claude Code      │                                                    │              │
│ ⣿  │  >_ zsh             │                                                    │              │
│ ＋ │ 定时任务             │ ┌────────────────────────────────────────────────┐ │              │
│    │                     │ │ 输入消息,@ 引用文件,/ 命令          模型 ▾  ↑    │ │              │
│ ⚙  │                     │ └────────────────────────────────────────────────┘ │              │
└────┴─────────────────────┴────────────────────────────────────────────────────┴──────────────┘
 栏 1:项目栏     栏 2:项目面板             栏 3:主工作区                                    右侧检查器
 64px 固定       240–360,可拖动、⌘B 收起    自适应                                            300–420,⌘⌥I
```

### 窗口外框与顶部一行(参考 Octopus)

窗口**没有系统标题栏**,内容铺到窗口顶端(`IsTitleBarVisible=false`、`ExtendClientAreaTitleBarHeightHint=40`)。
顶部 40px 这一行由各栏自己的头部拼成,没有单独的标题栏:

```
┌─ 左侧区域头部 ─────────────┬─ 工作区标签栏 ──────────────────────────────┬─ 检查器页签 ─────────────┐
│ ●●● (78px) ◧ NextCoWork  ⇤ │ [✱ 会话 ×][>_ zsh ×] +          ⋯  ◔ 65%  ◫ │ [概览] ⇄ 🤖 ⟲      ◨ – □ ×│
└────────────────────────────┴─────────────────────────────────────────────┴──────────────────────────┘
```

- **macOS**:红绿灯浮在左上角,左侧区域头部先留 78px 再放 Logo 和名称。
- **Windows / Linux**:最小化、最大化、关闭由应用画在右上角,和检查器开关一起常驻;此时检查器页签只显示图标。
- 这一行里没有被标签、按钮、输入框处理的按下**用来拖动窗口**,双击最大化 / 还原。
- **⌘B 收起整个左侧区域**(项目栏 + 项目面板)。收起后,红绿灯留白、名称和展开按钮并入标签栏最左边。
- 检查器收起时,标签栏右端给右上角常驻按钮让出位置。
- 平台差异只在 `Ncw.UI/Chrome/WindowChrome.cs` 一处判断。

### 栏 1 · 项目栏(Project Rail,64px)

- 从上到下:账户头像 → 项目和项目组 → 「＋ 添加项目」→ 底部设置。
- **项目图标**:自定义图片、emoji,或首字母加颜色。右下角状态点:
  - 旋转环 = 有 Run 在运行;数字角标 = 待处理交互数;灰色斜线 = 所在设备离线;月牙 = 云端 Agent 休眠中(点击即唤醒)。
  - 左上角小标记表示项目所在的设备类型:无标记 = 本机,☁ = 云端,⌁ = 其他设备。
  - 角标数据来自 `RunIndexStore`,和侧边栏、活动页读的是同一份。
- **项目组**:
  - 展开时,用一块带底色的胶囊容器把组内项目包起来(和参考图里蓝色那组一样),顶部是组图标。
  - 收起时,显示为 2×2 的迷你网格(类似 iOS 文件夹),角标是组内所有项目角标之和。
  - 支持拖拽:把项目拖到另一个项目上创建新组,拖进或拖出组,组之间排序。右键菜单可以重命名组、改颜色、解散组。
- 选中的项目有高亮描边,左侧有指示条。
- **拖放规则**(纯函数 `RailDropPlanner`,参考 Octopus):
  - 项目拖到另一个**顶层**项目上:上下 30% 边缘 = 放到它前 / 后;中间 = 两者新建一个组。
  - 项目拖到**组内**项目上:上半 = 放到它前面,下半 = 放到它后面(进入那个组)。
  - 项目拖到组上 = 进入该组末尾;拖到所有项目下面的空白处 = 移出组、放到最后。
  - 组拖到其他项上:上半 = 前,下半 = 后;组不能进组。
  - 拖动时画落点提示:前后放置是一条线,成组 / 进组是给目标描一圈。
- **右键菜单**:项目 —— 新建项目组 / 移出项目组、从项目栏移除;项目组 —— 展开 / 收起、更改颜色、解散项目组。
- 快捷键:`⌘1…⌘9` 切换到第 N 个项目,`⌃Tab` 在最近的项目之间切换。

### 栏 2 · 项目面板(Project Panel)

- **头部**:项目名(点击弹出切换和设置菜单)、路径、所在设备(本机或远程设备名)、当前分支和工作区是否干净。
- **搜索框**:搜索本项目内的会话、文件和命令,`⌘P`。
- **分区**(每个都可以折叠,顺序可调):
  - **会话**:按最近活动排序。运行中的会话有状态点和计时,计时用 `useTicker` 的 C# 等价实现。
  - **终端和智能体**:终端、外部 Agent CLI(比如 Claude Code、Codex)的会话。
  - **定时任务**:入口项,点开后在主工作区打开对应的标签页。**文件和 Git 变更不在项目面板里放入口**:它们是右侧面板的页签(文件是默认页签,Git 通过「+」添加,见下),两处各放一份只会重复。
- 左侧区域(项目栏 + 项目面板)可以拖动右边的分隔条调整宽度:304–484px,默认 352px;右侧面板 260–520px,默认 320px。
- 栏宽、栏的开关、右侧面板添加的页签是「本应用」的界面偏好,存在 `~/.next-cowork/client/ui.json`(`NCW_CLIENT_DIR` 可改),下次启动恢复。

### 栏 3 · 主工作区

- 顶部是标签栏,标签类型包括:会话、终端、文件、Diff、浏览器、设置。支持拖动排序和分屏(右上角 ◫)。
- 右上角:更多菜单 ⋯、上下文占用环(◔ 51%)、分屏、检查器开关(◨)。
- 会话页内的结构:消息流 + 底部输入框,组件见 [02 B1](02-agent.md#b1-组件清单桌面和手机共用通过密度变量适配)。

### 右侧面板(可选的第四栏)

宽屏时停靠在右侧,⌘⌥I 或右上角按钮开关。页签行就是窗口顶部这一行的右段(参考 Octopus):

| 页签 | 何时出现 | 内容 |
|---|---|---|
| **文件**(默认) | 总是存在,不能关闭 | 文件管理器:项目名 + 全部折叠 / 刷新 / 更多;搜索框(文件名 / 内容);**惰性展开**的文件树(展开时才 `files.list` 一层,排序是 Core 的 `FileEntrySort`,缺省不列忽略表里的条目,「显示被忽略的文件」打开后淡显);文件后显示 Git 状态字母,含变更的目录后面一个点;右键菜单(新建 / 重命名 / 删除进回收站 / 复制路径 / 在文件管理器中显示 / 用默认程序打开 / 另存为);点文件进入**预览与简易编辑**(见下) |
| Git | 通过「+」添加,可以关闭 | 分支(切换 / 新建)、拉取 / 推送(带领先 / 落后数)、变更分组(冲突 / 暂存区 / 未暂存 / 未跟踪,分组由 Core 的 `GitStatus` 派生)、点文件看 diff(统一 / 并排)、提交框(含「生成提交信息」)、提交历史 |
| 审查 | 通过「+」添加,可以关闭 | 当前会话里改过文件的各轮(下拉选轮)、这一轮的文件与 `+X −Y`、点文件看 before / after 的 diff、撤销 / 重做(撤销前先预检,磁盘上又被改过的文件列出来,确认后才强制撤销) |
| 终端 | 通过「+」添加,可以关闭 | 当前项目的终端会话(多标签),标题上是「设备 · 环境」;远程环境先出批准卡片(`terminals.prepare / approve`);输出用 TKCode.Arc.Terminal 的 `TerminalView` 渲染 |
| 上下文 | 通过「+」添加,可以关闭 | 上下文占用、待办 |
| 历史 | 通过「+」添加,可以关闭 | 本会话的文件改动与检查点(M4) |
| **子代理** | **当前会话有子代理时自动出现**,没有时自动消失,不抢焦点 | 子代理卡片:状态、当前工具、工具调用次数、耗时、停止按钮;点卡片看这个子代理的只读转录;页签上显示仍在运行的数量 |

- 选中的页签显示图标 + 名称,其余只显示图标。
- 页签组成规则是纯函数 `InspectorTabs.Compose`:文件 → 用户添加的(按添加顺序)→ 子代理(有才显示)。「+」菜单的顺序:Git、审查、终端、上下文、历史。
- 子代理的状态文案、计时、能否停止只来自 `SubagentView.Of`,与转录卡片、手机详情页共用。

**检查器各页签的实现约定**

- **事实在设备,镜像只有一份**:git 概览(`ClientStores.Git`)、变更集摘要(`Review`)、终端元数据(`Terminals`)是 Store 里的镜像,页签只派生;文件树、文件内容、diff、搜索结果是页签自己的纯 UI 状态(`files.*`、`git.diff`、`review.fileDiff` 的结果直接返回给调用方,不进 Store)。终端输出字节也不进 Store,经 `ITerminalSink` 的 UI 适配(`TerminalOutputRelay`)直接写进终端控件。
- **文件预览 / 编辑**:≤ 2 MiB 的 UTF-8 文本一次读完,`Arc.CodeBlock` 只读着色显示;编辑用多行输入框(没有 Arc 的代码编辑组件),保存带读到的 `Revision`,对不上(`files.conflict`)时不丢编辑内容,让用户选「重新载入」或「保留我的修改」;更大的文本按 256 KiB 一块 `files.readRange` 读,只读;二进制 / 非 UTF-8 / 过大只提示,可「另存为」;图片(≤ 16 MiB)显示。「在文件管理器中显示 / 用默认程序打开 / 另存为 / 复制路径」是客户端本机的动作(`ILocalFiles`),只对「本机设备 + 本机环境」的项目开放「显示 / 打开」。
- **终端**:本机和 SSH 项目同一条握手路径。键盘输入「没有写在途就立刻发,写在途期间的输入合成下一个命令」(`TerminalInputPump`,单次 ≤ 64 KiB,写失败丢弃攒着的输入而不是重放),大小变化只发最新的(`TerminalResizePump`)。设备断开时标签是「未知」(不判成结束、不接受输入、没有计时),重连后由连接层按游标续传(补发错过的批,续不上就整个缓冲重置显示);已退出的终端终态压过一切。
- **子代理转录**:点开才订阅子会话(子 Run 还在跑时再订阅 `run/<childRunId>`),行由和主对话同一个 `TranscriptBuilder` 从 Store 派生;只靠回执重建的旧卡片(没有子会话 id)点不开,给明确的提示而不是静默没反应。
- **输入区**:`@` 弹出项目文件候选(`files.suggest`,候选里已选的不再出现),选中后变成输入框上方的文件引用 chip,发送时作为 `InputPartKinds.File` 块(项目相对路径)和正文一起发出(排队 / 插话同样);`/` 弹出设备上的命令清单(`commands.list`,带说明),内置命令还没接入的保持占位;以 `/` 开头的草稿发送前先展开(Core 的 `CommandExpansion`,失败不静默地当普通文本发出)。
- **窄窗口**:检查器在 ≥ 1440 的宽度停靠(偏好 `IsInspectorOpen`);< 1440(1024–1440 和更窄)时右上角的检查器开关 / ⌘⌥I 打开的是**右侧抽屉**:盖在工作区上、靠右、宽度同检查器宽度,有遮罩(点遮罩关闭,和左侧抽屉共用一个遮罩),带滑入 / 滑出动效(§9)。抽屉开着与否是纯 UI 状态(`ShellViewModel.IsInspectorDrawerOpen`,不持久化,不改停靠偏好;窗口宽到能停靠时自动收掉)。

## 2. 响应式断点

| 窗口宽度 | 布局 |
|---|---|
| ≥ 1440 | 项目栏 + 项目面板 + 主工作区 + 停靠的检查器 |
| 1024–1440 | 项目栏 + 项目面板 + 主工作区;检查器以抽屉形式覆盖 |
| 720–1024(平板竖屏、窄窗口) | 只有主工作区;⌘B 或标签栏左端的按钮把左侧区域作为抽屉打开(覆盖在工作区上,点遮罩关闭) |
| < 720(手机) | 切换到移动端导航结构(见 §3) |

一套 ViewModel,两个 Shell:`DesktopShell`(栏式布局)和 `MobileShell`(导航栈)。按宽度切换,不按平台切换,
所以 iPad 横屏用的也是桌面布局。切换由容器 `AdaptiveShell`(`Ncw.UI/Views/AdaptiveShell.cs`)完成:`KindFor(width)` 是纯函数(< 720 → `Mobile`),
两个外壳共用同一个 `ShellViewModel`、按需创建并留着(切回来保住滚动位置和导航栈)。桌面主窗口 `MinWidth = 760`,不会自己落进手机宽度;
手机宿主的单视图直接用 `AdaptiveShell` 当根视图(`NcwApp` 在 `ISingleViewApplicationLifetime` 下装配,见 [11](11-mobile-hosts.md))。
768 宽的平板竖屏落在 720–1024 档:桌面外壳的「只有主工作区」布局。

## 3. 移动端导航

```
┌───────────────────────┐   ┌───────────────────────┐   ┌───────────────────────┐
│ 项目            ⊕  👤 │   │ ‹ NextCoWork      ⋯   │   │ ‹ 重构登录    ◔ 51% ⋯ │
│ 🔍 搜索项目            │   │ 💻 本机 · main ✓      │   │                       │
│                       │   │ [会话|终端|文件|Git]   │   │  助手:我先看下……       │
│ ▾ 工作             3  │   │                       │   │  ▸ 读取 3 个文件  ✓   │
│   🐙 NextCoWork  ● 2  │──▶│ ● 重构登录      2:31  │──▶│  ▸ 运行测试      …    │
│   🦊 Octopus          │   │ ○ 修复 CI             │   │ ┌───────────────────┐ │
│ ▾ 个人                 │   │ ○ 写周报              │   │ │ 需要审批:rm -rf … │ │
│   A  Alpha      ⌁离线 │   │                       │   │ │ [拒绝]  [长按允许]  │ │
│   A  Beta             │   │                       │   │ └───────────────────┘ │
│                       │   │                       │   │ ┌───────────────────┐ │
├───────────────────────┤   ├───────────────────────┤   │ │ 输入消息…   📎  ↑ │ │
│ 项目 · 活动② · 设备 · 我│   │ 项目 · 活动② · 设备 · 我│   └─┴───────────────────┴─┘
└───────────────────────┘   └───────────────────────┘
```

(图是最初稿的示意,标签名以下面为准。)

- 底部标签栏有四个页面:**收件箱**(原「活动」:跨设备的待办聚合,角标是现在能处理的待办总数)、**会话**(原「项目」:按组折叠的项目列表,和桌面的分组同一份数据;
  点项目进它的会话列表,点会话进对话)、**设备**(所有设备的在线状态、添加设备:扫码 / 输入 `IP:端口` + 配对码;局域网发现和创建云端 Agent 待做)、**设置**(导航栈,账户入口随登录页接入)。
  > 与最初稿的差异:标签从「项目 · 活动 · 设备 · 我」改为「收件箱 · 会话 · 设备 · 设置」(名字直接说明内容;「我」里的账户部分归登录 / 账户页,不占标签)。
- 首次启动时,如果没有任何设备,引导页给出两个选项:「连接我的电脑」(扫码配对)和「创建云端 Agent」。
- 会话页里不显示底部标签栏,把空间留给输入框。
- 手势:右滑返回(Arc 的 `NavigationStack` 左缘右滑,每个推入的页面也都有可见的返回按钮);在会话列表项上左滑可以停止或归档(Arc 的 `SwipeActions`,同时每行有「更多」菜单);
  下拉刷新(重拉会话列表,见 §7 的限制)。实现说明见 [§7](#7-移动端m5-共享层实现说明)。

## 4. 视觉规范

- 基于 Arc 的 token,不写死任何颜色值。默认深色,支持跟随系统和 8 个 Arc 强调色预设,也可以填自定义品牌色。
- 字体:正文用 Geist,代码和终端用 Geist Mono,中文回退到系统字体(苹方 / 微软雅黑 / Noto Sans CJK)。
- 图标:Lucide。运行时才确定的图标要在 csproj 里显式声明(否则报 ARCL002)。
- 窗口:Arc 的 `Window`,自定义标题栏。macOS 上保留红绿灯按钮,和项目栏顶部对齐。
- 动效:统一的动效系统见 [§9](#9-动效统一的动效系统)——时长 / 缓动 / 级别全部是 Arc 的,「设置 · 外观」里提供「跟随系统 / 完整 / 减弱 / 关闭」。
- 启动与加载:桌面启动时由真实的加载状态驱动的整窗加载界面、数据页与首次启动页的交接规则、窗口第一帧的底色,见 [§10](#10-启动与加载)。
- 语言:简体中文、英文(Arc 自带的控件文字另有繁体中文)。界面文字用 Arc 的语言目录生成器:`NcwTextKind` 枚举 +
  `en-US.xlf` / `zh-CN.xlf`,XAML 写 `{l:NcwText Key}`,切换语言时界面跟着变。Core、Client 不产出界面文字,
  只给结构化数据(例如子代理状态是枚举,由 UI 按语言写成「运行中 / Running」)。启动语言暂由环境变量 `NCW_LANG` 决定,
  设置页做好后改为「本应用」设置。

## 5. 数据模型(布局相关)

```csharp
record ProjectRef(string DeviceId, string WorkspaceId);           // 事实由 Agent 持有
record ProjectGroup(string Id, string Name, string? Icon, string Color, bool Collapsed);
record RailItem(string Id, RailItemKind Kind /* Project | Group */, string? GroupId, double Order);
record ProjectAppearance(ProjectRef Ref, string? Icon, string? Color, string? Alias);
```

- `RailLayout`(项目组、顺序、外观)存在 `LayoutStore`,通过账户配置同步到各个控制端(2.x 已有 config-sync)。
- 布局里引用的项目如果在设备上已经不存在,显示为「已失效」,由用户决定移除,不要静默删掉。

## 6. 会话区与输入框(M4 实现说明)

栏 2 的会话列表、栏 3 的会话内容、输入框已经接上真实数据(协议见 [09](09-protocol.md) §8.4)。要点和与上文设计的差异:

- **会话列表**是 `sessions.list` 的镜像(`WorkspaceState.Sessions`,含归档 / 收藏标记)。设备没有「会话列表」主题,所以连接在三处重拉某个项目的列表:连上后、`activeRuns` 里有 Run 开始或结束(新会话、自动命名、更新时间)、自己的重命名 / 归档 / 收藏 / 删除之后;已订阅的会话的 `session.updated` / `session.deleted` 直接合并进列表。
  过滤和排序是纯函数 `SessionListQuery`:归档默认隐藏(「显示已归档」才出现,排最后)、收藏置顶、其余按更新时间倒序,搜索按标题忽略大小写子串。
- **运行状态点**只来自 `activeRuns`(Core 的 `SessionActivityView`:运行中 / 空闲 / 未知)。设备断开时是「未知」(旋转图标),既不显示运行也不显示结束,标签页、列表行、对话区共用这一个函数。
  **差异**:`activeRuns` 里没有 Run 的开始时间,所以会话行暂不显示运行计时(上文 §1 栏 2);等广播带上 `startedAt` 再补。
- **转录**由纯函数 `TranscriptBuilder` 从 Store 派生(消息、流式增量、工具、子代理、挂起的交互、Run 状态),每一行有稳定的 key,视图模型按 key 对齐、原地更新,展开 / 折叠这类纯 UI 状态不会因为数据更新而丢。
  列表用虚拟化的 `VirtualizingStackPanel`;「跟随最新」是视图里的纯 UI 状态:贴底时新行和流式增量继续贴底,用户向上滚就停止并显示「回到最新」,回到底部恢复。
  工具卡片和审批卡片都带**执行环境标识**(设备 · 环境):实时事件带的优先,等待审批的用审批带的,落库的旧调用用项目所在环境兜底。
- **消息工具条**(桌面悬停显示,手机 `NcwDensity.IsTouch` 常驻;编辑 / 确认删除时钉住):
  - **用户消息**:时间、复制、编辑、从这里分支、删除这一轮;**AI 回复**:复制(流式中不显示)。复制是本机剪贴板的纯 UI 动作,不经设备。
  - **编辑**是行内的:「取消 / 保存 / 保存并重新运行」。保存 = `sessions.editMessage`(`truncate:false`,只改文字,后面的回复保留);保存并重新运行 = `truncate:true`(这条和之后的全部移除)再用新文字发起一次新的 Run(和发送是同一个入口,档位取输入框当前的)。截断成功而发送失败时,历史已经改了,**文字放回输入框**并提示,不让用户丢掉它。
  - **删除这一轮**要原位二次确认(`sessions.deleteTurn`,提问 + 全部回复与工具回执)。**分支** = `sessions.branch`(到这一轮为止的转录,独立的顶层会话),成功后刷新会话列表并打开它。
  - 意图在 `Ncw.Client/Actions/MessageActions`(`IMessageCommands`,一个意图一个方法);改写由设备执行,之后设备广播 `history.reset`,转录跟着 Store 重新派生。
  - **什么时候不能用**(`ConversationViewModel.CanEditHistory`):会话有 Run 在跑(设备会回 `busy` 拒绝改写,这里直接禁用并在提示里说明原因)、设备断开(状态未知,规则 7)、只读角色。失败是稳定 key(`messages.busy` / `unavailable` / `forbidden` / `gone` / `failed` / `resendFailed`),界面翻译成错误条,可以关掉。
  - **重新生成**:AI 回复的工具条上,只出现在**最近一轮的最后一段、已写完**的回复上(`MarkRegenerable`;更早的回复后面还有对话,重跑会把后面全部截掉)。它和「编辑后重新运行」是同一个入口,提问的文字和附件原样重发,旧回复被截断移除;重发失败时历史已经截断,原提问放回输入框。
  - **重跑带上原附件**:编辑后重新运行 / 重新生成要用同样的输入重发,所以从原消息还原附件(`MessageInputs.FilesOf`:图片 → 附件引用,`file_ref` → 项目文件引用)。Agent 侧为此改了一处:**为重发而截断时**(`sessions.editMessage` 的 `truncate:true`),被移除消息的会话附件**降回草稿**而不是随消息级联删除(`IMessageWriteObserver.OnMessagesRemoving`),文件和 `ncw://` 引用都保留,新消息提交时重新认领;删除一轮 / 只改文字不受影响。「引用别的消息附件」的 `:managed:` 行直接删。
  - **图片缩略图与灯箱**:用户消息里的图片显示 112px 缩略图(`UserRowViewModel.Images`),**行滚进视野才向设备取字节**(`AttachedToVisualTree` → `ImageThumbViewModel.EnsureLoadedAsync`,虚拟化列表只实例化视野附近的行);引用是 `data:` URL(本机解码)或 `ncw://` 附件(`attachments.read` 分块,单张 ≤ 32 MiB,成功的结果按设备 + 引用缓存最多 32 张,失败不缓存)。加载中转圈,失败显示「无法加载图片」并可点击重试。点缩略图进灯箱(盖住对话区,点空白 / 关闭 / Esc 关,打开期间 WebView 小组件让出空域)。解码用 `Bitmap.DecodeToWidth`(缩略图 320、灯箱 1600),不把原图整张解成大位图。字节不进 Store;`Ncw.Client` 不依赖 Avalonia,位图转换在 `ImageBytesConverter`。取不到引用时(旧快照)退化成「N 张图片」。
  - **没做**:删除单条回复(协议有 `sessions.deleteReply`,但一段回复由多条助手消息 + 工具回执组成,「删哪一段」的交互还没定)、回滚到这一轮(`review.undo`,要先定「只回代码 / 只回对话」的交互)、灯箱里的「另存为 / 复制图片」、助手回复里生成图片的显示。
- **输入框**的状态机是纯函数 `ComposerLogic`:空闲时发送;运行中有文字则排队(`queue.enqueue`)或「立即插话」(`runs.steer`),没文字则停止;已排队的消息可以逐条「插话」(`queue.promote` 开关)或移除;
  设备断开 / 只读角色 / 没有模型时不能发送。发送失败不丢草稿。模型候选来自设备算好的 `providers.models.list`(含上下文窗口、图片 / 思考能力,停用的显示为不可用),
  权限档位 `full` 只有 owner 可选,思考档位随模型声明的强度收窄。`@` 文件引用和 `/` 斜杠命令目前是占位(files.* 协议接入后出候选)。
- **交互卡片**(工具审批 / 提问 / 计划批准 / 目标提案)跟着 `interactions` 全量主题;回答带着看到的版本号。被另一台设备先处理时,卡片消失,转录底部提示「已在另一台设备上处理」(谁、什么时候);版本对不上的卡片保留并提示重新确认。
  **差异**:计划批准目前只有「批准 / 要求修改 / 驳回」,「在新会话中批准执行」随计划执行入口一起做。
- **标签栏**目前只有会话标签(打开了哪些标签是按项目记的纯 UI 状态);终端、文件、Diff 标签随各自功能加入。

## 7. 移动端(M5 共享层实现说明)

`MobileShell`、收件箱、设备页、扫码配对、推送与附件的**共享部分**已经实现,在 Avalonia.Headless 下验证(手机宽度 360 / 390 / 430 与平板 768,深浅两种主题)。
iOS / Android 宿主项目、相机、推送令牌、附件选择器是平台能力,只做了抽象和界面,**没有在真机上验证**(宿主设计见 [11](11-mobile-hosts.md))。

### 结构

- **一套 ViewModel,两个 Shell**:`ShellViewModel` 多出 `Navigation`(`MobileNavigationViewModel`)、`Inbox`、`DevicesPage`、`QrPairing`、`Push`、`Host`(`MobileHostServices`);桌面外壳不用它们。
  `AdaptiveShell` 按宽度选外壳(§2)。
- **导航栈**:`MobileNavigationViewModel` 持有当前标签和**每个标签各一个页面栈**(`ProjectRoute` / `SessionRoute` / `SettingsRoute` / `NotificationsRoute` / `QrPairRoute` / `ManualAddRoute`,
  只引用 id 的记录,是纯 UI 状态,不持久化)。`MobileShell` 把栈同步到 Arc 的 `NavigationStack`:ViewModel 里栈变长就 `Push` 按路由现建的页面,变短就 `Pop`;
  左缘右滑 / 返回按钮在 Arc 的栈上弹出后,再把深度回写给 ViewModel(`PopSilently`)。选中哪个项目 / 会话仍是 `Selection` Store 的事实,打开会话走 `SessionActions.Open`。
  点已选中的标签回到该标签的根页(Arc 的 `Tabbar` 对已选中项不再发 `SelectionChanged`,所以外壳另外听「按下前就是选中的那一项被点按 / 连点」;以前这条只在 ViewModel 里有、真点不触发,「设备」标签尤其明显);**会话页不显示底部标签栏**。
- **Arc.Mobile 封装**:`TKCode.Arc.Mobile`(锁定 `1.1.0`,`UseArcMobile()`)的 `NavigationStack` / `Page` / `Navbar` / `Tabbar` / `Sheet` / `PullToRefresh` / `ListGroup` / `ListItem` 经 `Ncw.UI/Controls/MobileControls.cs` 薄封装(`NcwNavigationStack` 等),
  业务视图只引用封装。Arc 文档注明「尚未在真机上验证」:这里验证了版式和状态,左缘右滑 / 弹层手势的真机手感待宿主项目(见 [11 §4](11-mobile-hosts.md))。
  Arc 的 `Tabbar` 没有角标,「收件箱」的待处理角标由壳自己叠在第一个标签上。
- **对话视图复用**:会话页里就是桌面的 `ConversationView`(同一个 `ConversationViewModel`、转录行、审批卡片、输入框),不复制。差异靠继承属性 `NcwDensity.IsTouch`(手机壳对每个页面设为 true):
  审批卡片 `InteractionCard` 切成触控版式(拒绝 + 长按允许),输入框 `ComposerView` 切成紧凑工具行(也按宽度 < 480 触发,桌面窄窗口同样受益)。
  窄屏的模型 / 思考 / 权限三个选择器只留图标(模型名截短),菜单照旧;软键盘由 `Page.AvoidKeyboard` 处理。
- **触控密度与宽度是两回事**:`IsTouch` 只由手机壳设置,桌面窗口再窄也不会变成「长按允许」。

### 宿主里的安全区与横屏(Android 模拟器上发现并处理,详见 [11 §6](11-mobile-hosts.md))

- `AdaptiveShell` 在有 `InsetsManager` 的宿主(手机 / 平板)里启用 `DisplayEdgeToEdgePreference` 并关闭 Avalonia 的 `AutoSafeAreaPadding`:内容画到状态栏 / 手势条后面,留白由页面自己负责。
- 手机壳:`NcwPage` 不用 Arc 的 `UsePlatformSafeArea`(它读 `SafeAreaChanged` 事件参数,那是物理像素,页面顶部多出 2.6 倍的空白),改读 `IInsetsManager.SafeAreaPadding`(dip),并在 `SafeAreaChanged` / `ScalingChanged` 时重读;只有最外层页面留安全区。
- 平板 / 手机横屏(宽度 ≥ 720)走桌面外壳:`AdaptiveShell` 给桌面外壳的 `Padding` 设成安全区;`WindowChrome.ForCurrentPlatform()` 在 Android / iOS 上不再画最小化 / 最大化 / 关闭。
- 窄屏(360dp)输入框的工具行(模型 / 思考 / 权限)会换行:权限图标掉到第二行(已知,未修)。

### 审批的「长按允许」(02 B2)

- 手势状态机是 Client 的 `HoldGesture`(纯逻辑,时间由 `TimeProvider` 注入):按下 → 按住期间每帧 `Tick` → 按满时长触发一次;松手早了 / 手指移出按钮边缘 24px(在滚动)/ 失焦 / 卡片换了版本 / 正在提交,都取消并把进度回零;
  触发之后锁定到回答失败(才 `Reset`),所以一次按压最多回答一次。
- 普通审批按住 0.8s,**危险审批(命令风险 `dangerous` / 破坏性工具)按住 1.5s**(多一点反悔时间)。拒绝是普通点按。手机上的卡片不提供「总是允许」和记住范围(规则只在桌面上建)。
- `NcwHoldButton` 只负责输入和画面:指针 / 键盘(空格、回车按住)事件转给 `InteractionRowViewModel.BeginAllowHold / MoveAllowHold / EndAllowHold / TickAllowHold`,
  进度画成填充条,填充边缘两侧的文字反色保证对比度。回答仍是同一个 `AllowOnceCommand`(带看到的版本号,先到先得,冲突提示照旧)。
- **差异**:02 B2 写的是「底部弹出 Bottom sheet」。现在审批卡片留在消息流里(收件箱点进来会滚到并高亮那张卡片),没有做成弹层;`Sheet` 已封装但还没有页面使用。理由:消息流里的上下文(前面的助手文字、工具调用)对判断是否批准有用,弹层会盖住它。

### 活动收件箱

- 聚合与排序是 Core 的纯函数 `ActivityInboxView.Build`(有单测):输入是各设备的挂起交互、活动 Run 索引、连接状态、已结束的 Run;输出按设备分组。
  组内:待办在前(危险审批 → 工具审批 / 设备确认 → 提问 → 计划 / 工作流 → 目标,同级先登记的在前),通知在后(失败在前、新的在前);组间:有「现在能处理」的待办的在前(等得最久的先)。
- `InboxViewModel` 把 Store 镜像翻成 Core 的输入(协议 DTO → `InboxInteraction`),再把结果映射成行;视图不另算。
- **设备断开 = 未知**(规则 7):断线设备的分组标「状态未知」,它上次的待办保留但标 `Unknown`、灰显、不计入角标、不判成已处理;已结束的 Run 通知是终态事实,保留。
- **先到先得**:待办从 Store 里消失而不是本机回答的(`InteractionLedger` 记本机的回答,`ConversationActions.RespondAsync` 在 Answered 时写入),设备又连着 → 留一条「已在另一台设备上处理」墓碑,直到用户划掉。
- 点一条:进入对应会话(项目 → 对话)并**定位到卡片**(`ConversationViewModel.RequestFocus("i:<交互 id>")`:行还没到就等它出现,再滚到并高亮);设备级的交互(SSH 主机指纹 / 凭据)没有会话,去设备页。
- 通知(Run 完成 / 失败)按「控制端第一次看到它结束的时间」排序(设备广播里没有结束时间);点开会话或划掉即已读;被用户中止的不通知。
- **限制**:控制端只知道自己订阅过的 Run 的结局;别的设备上开始、本机从没订阅过的 Run,结束时本机收件箱没有通知(靠推送;见下)。下拉刷新重拉会话列表(`SessionActions.RefreshAllAsync`),**不是**文档里写的「强制对账」——`activeRuns` / `interactions` 是权威广播主题,重连时自带快照,没有单独的「强制对账」调用;需要时再加(要改连接层)。

### 设备页与添加设备

- 设备页列出所有设备(本机 / 云端 / 配对过的电脑)的连接状态、角色、项目数、运行 / 待处理数(断开时是「状态未知」,不显示数字),配对过的远程设备可以移除。
- **扫码配对**(`QrPairingViewModel`,状态机:`Idle → RequestingAccess → Scanning → Parsed → Pairing → Paired / Failed`):相机权限与解码是宿主的 `IQrScanner`(没有时扫码页画占位框);
  解析用 `PairingQr.TryParse`,过期判断用注入的时钟;**解析成功后要用户确认才配对**(有效期按界面语言格式化:中文「15:30」、英文「3:30 PM」,不跟操作系统的区域走)(显示设备 ID 前 8 位和地址,让用户和电脑上的核对);配对走 `DeviceActions.PairWithQrAsync`(唯一入口,
  内容不合法 / 已过期在发网络请求前就返回)。相机被拒、系统限制、没有相机时,「粘贴二维码内容」走同一条解析和配对路径。
- **IP:端口 + 配对码**:复用设置里的 `DevicesSettingsViewModel`(同一个 `PairAsync` 意图),手机只是另一个版式。
- 局域网发现(mDNS)、「创建云端 Agent」(M6)还没有。

### 推送与附件(抽象 + 界面)

- **推送**:`IPushRegistration`(宿主:权限、APNs / FCM 令牌)→ `PushViewModel` 在设备连上后经新增的协议方法 `push.register`(member 起、command、幂等键、能力 `push`)登记到每台声明了推送能力的设备;
  设备断开再连上 / 令牌被平台刷新后重新登记。设备只存不发(`PushRegistrations`:kv `v3.push.registrations`,按控制端指纹;被吊销的控制端不是发送目标);**发送由 nextco.work 的推送中继(B4)负责**,没做。
  设置 → 通知页显示状态(不支持 / 未开启 / 被拒绝 / 登记中 / 已登记 N 台 / 失败)和「开启通知」。
- **附件**:`IAttachmentPicker`(相册 / 拍照 / 文件)→ `AttachmentTrayViewModel`(输入框的 `Attachments`):读字节(超过 32 MiB 在读之前就拒绝)→ 分块上传(`AttachmentActions.UploadAsync` 新增 `IProgress<long>` 重载,每个分块一次进度)
  → 完成 / 失败(原因分类:太大 / 空 / 设备未连接 / 无权限 / 设备拒绝 / 读取失败,可重试)/ 取消。上传完成的附件随消息一起发出(图片 → `image` 块,`DataRef` = `ncw://` 引用;其他类型 → `file` 块,`Path` = 引用);
  还有上传中的附件时发送按钮不可用(否则会静默丢掉它)。**未验证**:设备对「`file` 块的路径是 `ncw://` 引用」的读取(图片的 `ncw://` 解析是 M4 做好的)。

### 设置的导航栈

手机上设置是导航栈:入口列表(本应用:设备、通知;设备:通用、模型与供应商、技能、命令、Hooks、MCP、用量、存储;项目:项目设置)→ 推入对应页面(桌面同一批页面内容、同一批 ViewModel,不重做)。
桌面窄窗口的 56px 图标栏(`SettingsOverlay`)保留。「连接与远程」「开放网关」是本机 Agent 的开关,手机没有本机 Agent,所以不列。账户 / 插件页随各自的视图接入后再加入口。

### 测试

- `Ncw.Core.Tests.ActivityInboxTests`:聚合、分组、排序、断线语义。
- `Ncw.Client.Tests/Mobile`:`HoldGestureTests`(时间可注入)、`InboxViewModelTests`(聚合与排序、先到先得、断线、点进去定位)、`QrPairingTests`(状态机与 `DeviceActions.PairWithQrAsync`)、`AttachmentTrayTests`、`PushViewModelTests`、`MobileNavigationTests`、`DevicesAndComposerTests`(设备页、带附件发送、长按允许与版本变化)。
- `Ncw.UI.Tests.MobileScreenshotTests`:截图在 `tests/Ncw.UI.Tests/bin/Debug/net10.0/screenshots/mobile-*.png`,断言外壳按宽度切换、页面结构、长按进度、触控版式、导航栈同步。

## 8. 内联可视化(M8:WebView 评估与渲染宿主)

范围:桌面端把 `visualize_*` 工具产出的 HTML / SVG 载荷在对话里渲染出来,给小组件一个宿主桥 `window.ncw`(提交、动作、状态、数据、外链);手机端降级为静态快照。Agent 侧(消毒、静态快照、动作、`widgets.*`)已在 [08.03](08-agent-runtime/03-tools-permissions.md)。

### 8.1 WebView 方案评估(结论:可行,桌面用 Avalonia 官方 `NativeWebView`;手机降级)

**选定**:NuGet `Avalonia.Controls.WebView`(MIT,AvaloniaUI OÜ;锁定 **12.1.2**,依赖 `Avalonia >= 12.0.0`,解析到锁定的 12.1.5)。Avalonia 12.1.5 的核心包里**没有** WebView 控件;这个官方包提供 `NativeWebView`(嵌入的控件,基于 `NativeControlHost`)和 `NativeWebDialog`(独立窗口),各平台用系统自带的引擎,不带 Chromium。注意它的版本号和 Avalonia 核心包不同步(AGENTS.md §2 的「所有 Avalonia 包保持一致」指核心包),单独锁在 `Directory.Packages.props`。

| 平台 | 引擎与嵌入方式 | 本机验证 |
|---|---|---|
| macOS | WKWebView(系统 WebKit),`NativeControlHost` 嵌入 | **已验证**(见下) |
| Windows | WebView2(Evergreen 运行时,没装时 `AdapterInfo.IsInstalled = false`),`NativeControlHost` 嵌入;旧的 WebView1 作后备 | **没有机器可测**,同一份代码 |
| Linux | WebKitGTK(`libwebkit2gtk-4.1` / `4.0`)或 WPE WebKit(12.0 新增),X11 / Wayland | **没有机器可测**,同一份代码 |
| iOS | 包里**没有** iOS 目标(`lib/` 只有 `net10.0` / `net10.0-android36.0` / `net10.0-browser1.0` / `net8.0`) | 不用:手机降级(§8.3) |
| Android / 浏览器 | 有适配器(Android WebView / iframe),但手机壳按触控密度降级,不创建 WebView | 不用 |

**能力核对**(API 与 macOS 实测;「—」= 没有这个能力,靠别的办法):

| 需要 | `NativeWebView` | 怎么用 |
|---|---|---|
| 加载内联 HTML 字符串 | `NavigateToString(html, baseUri)` | 外壳页面(§8.4)整页喂进去 |
| 禁用外部导航 | `NavigationStarted` 的 `e.Cancel`、`NewWindowRequested` 的 `e.Handled` | `WidgetNavigationPolicy` 决策,**一律取消** |
| 禁外链资源(CSP) | **没有设响应头的入口**(`WebResourceRequested` 只能观察,不能取消请求) | CSP 写在外壳的 `<meta http-equiv>` 里,放在所有脚本之前 |
| JS → C# | 页面里有全局 `invokeCSharpAction(string)` → `WebMessageReceived.Body` | 桥消息(§8.4) |
| C# → JS | `InvokeScript(string)`(页面加载完成之前调用会抛) | `__ncwHost.recv(json)`,页面「就绪」之后才推 |
| 自适应高度 | —(没有内容高度属性) | 页面量 `#ncw-root` 的下沿经桥报上来,宿主钳制到 24–1400 后设给控件 |
| 透明背景 | `Background = Transparent` | 外壳 `background: transparent`;**视觉未验证**(见下) |
| 不落盘的数据存储 | `EnvironmentRequested`:Apple `NonPersistentDataStore`、WebView2 `IsInPrivateModeEnabled`、GTK `EphemeralDataManager`;`EnableDevTools = false` | `NcwWidgetSurface.OnEnvironment` |
| 可用性探测 | `AdapterInfo.GetAdapterInfo(type)`:`IsSupported` / `IsInstalled` / `UnavailableReason` | 适配器建不起来 → 降级;**5 秒内没有 `AdapterCreated`**(Headless、没有原生句柄)同样降级 |
| Headless 可测 | 适配器类型有 `Headless`,但 Headless 下**不会建出适配器**(控件只占一块空白) | 把宿主桥与载荷逻辑和 WebView 外壳分开(§8.2);外壳在 Headless 里走降级路径,真实渲染用 §8.7 的冒烟 |

**本机(macOS,WKWebView)实测**——最小程序(临时 spike,已清理)与之后的 `tools/WidgetSmoke`(真窗口 + 真外壳 + 真桥 + 恶意载荷,`NCW_WIDGET_SMOKE=1` 打开 `NcwWidgetSmokeTests`)共 26 项全过:

- 带脚本的 HTML 能加载,页面 → 宿主、宿主 → 页面**双向都通**(含中文、引号);`NavigateToString` 在 macOS 上内部用 `http://localhost:12345/` 作来源,所以导航策略**不能**把外壳地址写死(见 `WidgetNavigationPolicy`:「就绪」之前的导航都是外壳自己的加载)。
- **CSP 生效**(meta 写法):`fetch` 外网被拒;`eval` / `new Function` 被拒;内联事件处理属性(`onerror=`)不执行;没有 nonce 的内联 `<script>` 不执行;`<img src=https://…>` 被拒;`<form action=https://…>` 提交被拒;`securitypolicyviolation` 事件都收得到。
- **导航与弹窗全被拦**:`location.href = 'https://…'`、`<a href>` 点击、`target=_blank`、`window.open`、表单提交都触发 `NavigationStarted` / `NewWindowRequested` 并被取消(`window.open` 返回 `null`),页面仍是外壳。
- 主题 token(CSS 变量)、状态(`getState`)、数据(`onData`)推得到;`window.ncw` 冻结、改不掉;报高对(内容 240px + 链接 + 表单 → 282px)。
- 观察到的坑:① 窗口被遮挡 / 不在前台时 WKWebView 停掉 `requestAnimationFrame`,报高要有定时器兜底(已做);② 取消导航之后页面的定时器会停顿一两秒(用户看到的是 widget 里的动画卡一下,不影响正确性);③ 原生视图盖在 Avalonia 内容上(空域):对话框 / 设置页盖不住它,见 §8.5。

**未验证**(如实):Windows WebView2、Linux WebKitGTK / WPE 的真实行为(尤其是它们的 `NavigateToString` 内部地址形状和导航事件的时序);macOS 上 `NativeControlHost` 在 `ScrollViewer` 里滚动时的**裁剪与合成**、透明背景的**视觉效果**、键盘焦点进出 WebView——本环境不允许截屏,只能靠逻辑与 Headless 覆盖外壳。这几项在第一台真机上需要目测。

### 8.2 分层:WebView 外壳与逻辑分开

```
Agent 卡片 Details(kind: widget)           ←  Ncw.Agent.Widgets(已有:消毒后的 code、静态 snapshot、size、actions、findings、environment)
      │ tool_progress(交互卡片)/ tool_end / 落库的 tool_result
Ncw.Core        WidgetView            阶段(加载中 / 就绪 / 已过期 / 出错 / 未知)与「能不能提交 / 能不能调设备」的唯一派生函数
Ncw.Client      WidgetCardReader      Details → WidgetCard(引用相等,同一份文本只解析一次)
                TranscriptBuilder     工具卡 → WidgetRow(阶段、等待提交、按标题认领的 update 数据、环境标识)
                WidgetsMirror         Store:状态 JSON、提交进度(单调);key = 设备 + 会话 + 调用
                WidgetActions         意图:读 / 写状态、提交、跑动作(含确认流程)——Actions 里唯一一份
                WidgetBridge          页面消息的白名单校验 + 限速 + 派发到 Actions;不依赖任何 UI / WebView 类型
                WidgetShell / WidgetPush / WidgetTokens / WidgetNavigationPolicy / WidgetSnapshotText   外壳装配、推送消息、主题映射、导航策略、快照文字(纯函数)
Ncw.UI          NcwWidgetSurface      整个仓库里唯一引用 NativeWebView 的文件(加载、收消息、执行脚本、拦导航)
                NcwWidgetHost         内容区:WebView / 静态快照 / 加载中 / 出错 / 已过期 / 未知 / 崩溃重载
                WidgetCard            卡片头(标题、环境标识、状态标记、重新加载、复制源码)+ 内容区
```

客户端项目不引用 WebView 包,Agent 项目更不会(`WidgetHostTests` 核对)。逻辑层的全部单测不需要 WebView。

### 8.3 降级策略

| 情形 | 表现 |
|---|---|
| 手机壳(触控密度)、没有可用的 WebView(适配器建不起来 / 5 秒超时 / 运行时没装 / Headless) | **静态快照**:Agent 产出的无脚本、无外链的快照经 `WidgetSnapshotText` 抽成**文字**(只当文字画,不当标记),只读,标「静态预览」。没有任何可执行的东西 |
| WebView 崩溃 / 加载失败 / 12 秒没有就绪 / 桥被锁死 | 「可视化没有响应了」+ **重新加载**按钮(不自动重试:同一份卡片坏过,等用户点) |
| 设备断开 | 内容已定稿的卡片照常显示,只标「离线」(桥不能调设备);还在生成 / 等提交的卡片是「状态未知」(规则 7),不转圈、不判失败 |

**不做「在浏览器中打开」**:把载荷写成临时文件交给系统浏览器会丢掉外壳的全部保护(nonce、导航策略、桥的校验与限速),还会让页面以 `file://` 来源跑在用户的浏览器里,等于放开沙箱。需要图形的人在桌面端看;降级只给文字。SVG 快照的**图片**渲染(需要引入 SVG 渲染库)属于后续,本批没做。

### 8.4 外壳页面、桥与 `window.ncw`

外壳(`WidgetShell.Build`)是一页静态 HTML:`<meta>` CSP(每页新的 nonce)→ 基础样式表(2.x 的 `base-styles.ts` 逐字,规范里的 `class="box"` / `c-blue` 等靠它)→ 外壳样式(透明背景)→ **唯一一段带 nonce 的内联脚本**(`WidgetShell.Runtime`:手写 JS 常量,AGENTS.md §6 不引入 Node;没有 JS 引擎可以在单测里跑它,所以只单测它「自包含、消息名与 C# 常量一致、不碰危险 API」,行为由 §8.7 的冒烟验证)。widget 的内容在页面「就绪」之后经消息推进去,运行时把它塞进 `#ncw-root`,**逐个重建脚本并带上 nonce、按顺序执行**(有 `src` 的等加载完再跑下一个,Chart.js 在使用它的内联脚本之前就绪;2.x 没有保证这一点)。

| 方向 | 消息(`ncw:widget:*`,与 2.x 同名) | 内容 |
|---|---|---|
| 页面 → 宿主(`invokeCSharpAction`) | `ready` | 外壳就绪(宿主关上导航放行窗口并推送) |
| | `height` | 内容下沿(未钳制) |
| | `call` | `{id, method, args}`:`setState` / `submit` / `runAction` / `openUrl` |
| 宿主 → 页面(`__ncwHost.recv`) | `content` / `theme` / `loading` / `env` / `reply` | 内容、主题 token、加载提示、环境(状态 / 数据 / 交互 / 已提交 / 动作清单)、调用结果 |

`window.ncw`(冻结):`getState()` / `setState(v)`(↔ `widgets.getState` / `setState`)、`submit(v)`(↔ `widgets.submit`,仅 `interactive` 卡)、`runAction(id, params)`(↔ `widgets.runAction`,先弹确认框)、`openUrl(url)`、`getData()` / `onData(cb)`(↔ `visualize_update_widget`)、`actions()` / `isInteractive()` / `isSubmitted()` / `title()`。**没有 `sendPrompt()`**(同 2.x)。调用返回 Promise,失败 reject 一个带稳定英文码的 `Error`:`offline` / `unavailable` / `closed` / `not_interactive` / `already_submitted` / `declined` / `rate_limited` / `forbidden` / `invalid` / `unsupported` / `failed` / 校验码(`bad-args` / `unknown-method` / `state-too-large`……)/ 设备给的 `widgets.action.*`。

方法与上限(`WidgetLimits`,取自 2.x 的 `WIDGET_LIMITS`):单条消息 256K 字符;`setState` 16K;`submit` 32K;动作 id 64、参数 ≤ 16 个且只能是有界字符串 / 数字 / 布尔、总计 4K;URL 4K,只许 http(s)、不带用户信息、无控制字符;JSON 深度 ≤ 32;高度钳制 24–1400。

### 8.5 安全

页面里跑的是模型写的代码,**页面发出的一切都当成不可信输入**。四道防线:

1. **服务端消毒**(`Ncw.Agent.Widgets`,已有)。
2. **外壳的 CSP**(`WidgetShell.Csp`):`default-src 'none'`;`script-src 'nonce-…'` + CDN 白名单(**没有** `unsafe-inline` / `unsafe-eval`);`style-src 'unsafe-inline'` + CDN;`img-src data: blob:`;`font-src data:` + CDN;`connect-src 'none'`;`frame-src` / `object-src` / `base-uri` / `form-action` 全 `'none'`。CDN 白名单与服务端 `WidgetUrls.CdnHosts` **同一张表**(`cdn.jsdelivr.net` / `unpkg.com` / `cdnjs.cloudflare.com` / `esm.sh`,`WidgetHostTests` 核对)。**比 2.x 更严两处**:`img-src` 不放 `https:`(服务端本来就拿掉外链图片,这里不比服务端宽),`connect-src` 默认全拒(2.x 放了 CDN)。
3. **导航与弹窗全拦**(`WidgetNavigationPolicy`):放行的只有外壳自己的加载(`ExpectShellLoad` 到页面「就绪」之间,此时 widget 的代码还没被推进来)和同一页内的锚点;之后任何导航取消,http(s) 目标走「系统浏览器 + 确认框(显示真实地址)」,别的方案(`javascript:` / `file:` / `data:` / `mailto:`)直接丢弃;新窗口永远不开。
4. **桥的白名单与限速**(`WidgetBridge`):只认已知消息类型和四个方法;全局令牌桶(突发 120、60/s)+ 调用令牌桶(突发 12、6/s);累计 100 次违规就**锁死**(之后一切消息丢弃);同一时刻只有一个确认框;动作的「要跑什么」由 Agent 从转录里取,页面只能选 id 和受约束的参数;推给页面的环境里**没有**动作的工具与入参。

不暴露任何本机文件 / 进程能力:桥只有上面四个方法,没有 `download` / `openFile` / `copy` / `complete`(2.x 里这些是渲染宿主的能力,3.0 没有做,见 §8.8)。**与 2.x 的隔离模型不同**:2.x 是不透明源的 `<iframe sandbox>` 嵌在特权渲染进程里;3.0 一个 widget 就是**一个独立的 WebView 页面**(非持久数据存储、没有任何控制端状态、没有 `file://`、没有 cookie 落盘),页面里的原生桥函数(`invokeCSharpAction`)任何脚本都能调——所以桥一侧不信任何东西,而不是靠嵌套 iframe 把脚本和桥隔开。

**空域**:`NativeWebView` 是盖在窗口上的原生视图,Avalonia 画的覆盖层盖不住它。凡是盖在对话区上面的东西打开时调 `NcwWidgetAirspace.Suspend()`,所有 widget 的 WebView 随之隐藏(页面不销毁、状态保留)——桥的确认框、设置页 / 首次启动页 / 数据页、窄屏抽屉的遮罩(`DesktopShell` 监视它们的 `IsVisible`)。同时最多 8 个 WebView(虚拟化列表只实例化屏幕附近的行,这是兜底),超出的先画快照。

### 8.6 状态管理七条规则怎么落的

1. 一个事实一个真源:卡片(代码 / 快照)在会话消息里,经转录派生;小组件状态与提交进度只在 `Store<WidgetsMirror>` 一份,key 带 deviceId。
2. 视图只有纯 UI 状态(WebView 实例、页面就绪与否、这一页的失败);`WidgetRowViewModel` 只在行贴到屏幕上(`Attach`)期间订阅镜像,不订阅协议事件。
3. 阶段与「能不能提交 / 调设备」只有 Core 的 `WidgetView` 一个函数,桌面卡、手机快照卡共用。
4. 意图只在 `WidgetActions`:读 / 写状态、提交、跑动作(含确认流程)各一个方法。
5. 「等提交」由 `interactions` 主题的全量广播兜底(转录派生时看设备上挂着的 `widget_submit`)。
6. 单调:提交的终态(已接受 / 已关闭)不被覆盖;读回来的状态若期间有本地写入就丢弃;`tool_end` 的详情压过重放的旧 `tool_progress`;提交值进了卡片就是终态。
7. 设备断开:镜像里的状态标陈旧、在途的提交退回可重试,卡片标「状态未知」,不判结束、不继续转圈;重连后重读状态。

### 8.7 测试

- `Ncw.Core.Tests.WidgetViewTests`:阶段与提交 / 调设备资格。
- `Ncw.Client.Tests/Widgets`:`WidgetBridgeTests`(**恶意载荷**:垃圾 / 超大 / 伪造类型 / 白名单之外的方法 / 越界参数 / 超深 JSON / 危险 URL / 洪水 / 锁死 / 导航劫持 / 双确认框)、`WidgetShellTests`(CSP 每一条、nonce 每页不同、外壳脚本自包含、消息名一致、推送消息可嵌进脚本、主题映射——移植自 2.x 的 `widget-protocol` / `widget-runtime-artifact` / `widget-tokens` 测试)、`WidgetModelTests`(卡片读取、快照文字、镜像单调规则、转录里的阶段 / 提交窗口 / 断线 unknown / 数据认领)。
- `Ncw.Integration.Tests.WidgetLinkTests`:真实 Agent + `DeviceLink` + `WidgetActions` + `WidgetBridge`:交互卡片经桥提交、Run 拿到值、卡片变「已提交」;状态往返并进镜像;未声明 / 越界的动作被拒、非只读动作先确认。
- `Ncw.UI.Tests.WidgetScreenshotTests`:四个断点 × 深浅主题,覆盖静态快照降级、加载中、出错、已过期、断线 unknown / 离线,英文界面,手机壳;`WidgetHostTests`:CDN 表与服务端一致、WebView 只有一个文件碰、主题来自 Arc token。
- **真实 WebView 冒烟**(默认跳过,`NCW_WIDGET_SMOKE=1`):`NcwWidgetSmokeTests` 起 `tools/WidgetSmoke`,逐项断言 CSP / 导航 / 桥 / 主题 / 报高 / 恶意洪水。**只在 macOS 上通过**。

### 8.8 偏离与未完成

- **不做边写边渲染**(2.x 的 `widget-push` 节流与 `widget-sync` 的 DOM 增量同步):3.0 的卡片是 Agent 消毒后一次给出的定稿(模型写的过程中只有加载提示),没有「半截 HTML」要推,所以这两个模块**不移植**,对应的 2.x 测试也不移植。代价:模型写得久的时候只看到加载提示,不是看着图长出来。
- 不做 TSX(同 08.03);没有 `copy` / `download` / `openFile` / `toast` / `draft` / `setContext` / `complete`:桥只有 `setState` / `submit` / `runAction` / `openUrl`(外加 `getState` / `onData` 这两个读)。
- 小组件状态没有推送主题:别的控制端改了状态,这个控制端在下次读取(卡片出现、重连后)才知道。
- 手机端的快照是文字,不是图;SVG 的图片渲染未做。
- WebView 在 `ScrollViewer` 里的合成 / 裁剪、透明背景、键盘焦点、Windows / Linux 真实行为未验证(§8.1);light 主题下 Arc 的三层背景 token 全是白色,规范里 `background-secondary` 的「面板」层在浅色下只剩描边区分。
- 高度上限 1400 之后内容在 WebView 内部滚动,没有「展开 / 全屏」。

## 9. 动效(统一的动效系统)

范围:桌面和手机共用的一套动效规则,落在 `Ncw.UI/Controls/NcwMotion*.cs`、`NcwCollapsible.cs`、`NcwSection.cs`(业务视图只引用这些封装)。设计原则:**能用 Arc 的就用 Arc 的**,不平行造 token;
没有的部分只做薄封装,封装里不出现魔法数字(每个数都指回 Arc 的 token 或在下面写清来源)。设置入口见 [07 §3「外观」](07-onboarding-settings.md)。

### 9.1 Arc 提供了什么(对 `TKCode.Arc 1.1.0` 的程序集、XML 文档和文档站逐项核对的结论)

> 文档站(docs.tkcode.si)现在写的是 1.1.1-preview.1;本分支锁定 1.1.0。下面的事实都用 1.1.0 的程序集核对过(反射 + XML 文档 + 实测),文档站上有而 1.1.0 里没有的不算。

| 类别 | Arc 有的 | 说明 |
|---|---|---|
| **级别** | `ArcMotionLevel`:`Full` / `Reduced` / `Off`;继承附加属性 `ArcMotion.Level`;主题 token `MotionMode`(`ArcTheme.Apply(t => t.MotionLevel(..))`) | 控件实际级别 = `min(继承的 Level, 主题的 MotionMode)`(`ArcMotion.GetEffectiveLevel`)。`Reduced` = 只保留短淡入淡出和颜色变化,位置与尺寸直接跳到终态,装饰性循环停止;`Off` = 全部立即生效。**各级别的最终状态完全一致**(文档明确保证) |
| **时长 token** | `DurationInstant` 120 ms、`DurationFast` 160、`DurationExit` 180、`DurationStandard` 240、`DurationConsidered` 480、`DurationSpring` 580;`Stagger*`(16 / 40 / 80 / 35 ms) | 主题资源里是 `TimeSpan`,XAML 写 `{arc:ArcDesignTokenResource DurationStandard}`;代码里是 `ArcMotionPresets.Instant / Fast / Exit / Standard / Considered` |
| **缓动 token** | `EaseEnter`(进入,减速)、`EaseExit`(退出,加速)、`EaseStandard`、`EaseInOut`、`CssSpring` | XAML `{arc:ArcDesignTokenResource EaseEnter}`;代码 `ArcEasings.*` |
| **弹簧** | `ArcSpring`:`Smooth`(无回弹)、`Snappy`、`Morph`、`Responsive`、`Gentle`(1.1.0 里还有 `Quick`、`Settle`) | 实测收敛到 0.1% 的时间:Smooth 0.70 s、Snappy 0.43 s、Quick 0.44 s、Settle 0.58 s、Morph 0.70 s(肉眼 0.4 s 内就到 99%)。被打断时从当前位置和速度接着走 |
| **运动值** | `MotionValue<T>`(可打断)、`MotionSpec.Spring / Tween / Instant`、`MotionTimeline`、`LiveLoop`(挂在界面上 + 可见 + 在视口里 + 级别够才跑)、`ArcBusyPulse`(减弱级别下「忙」指示的明暗呼吸)、`ArcMotion.BeginSettle / EndSettle`(回收容器换数据时不动画) | `MotionValue` 遵守拥有者的级别:`Off` 全跳,`Reduced` 下弹簧跳、补间照走 |
| **测试钩子** | `ManualFrameClock`(只在 `Advance` 时才走帧)、`MotionScheduler.ClockOverride` | 见 §9.6 |
| **组件自带的动效** | `TreeView`(文件夹打开时空间弹簧增长 + 子行错峰进入,不整树重排)、`Accordion` / `ExpandableCard`、`ArcPopup` / 菜单 / 下拉 / `Dialog` / `Drawer`(`ModalPresence`)、`Button` 按下缩放、`SegmentedControl` 滑块、`NavigationStack`(推入:新页从右滑入、下面一页左移 30% 并变暗;**返回手势跟手**,松手按速度决定弹出还是回弹)、`Tabbar`(选中胶囊弹簧滑动)、`Sheet`、`Progress` 等 | 这些**不再另做**,只靠 `MotionMode` 一起降级 |
| **读操作系统的「减少动态效果」** | **没有** | Arc 文档明确写:库不读系统偏好,由应用自己提供设置并写到主窗口 |
| **通用的展开 / 收起容器** | **没有公开的**(`TimelineGrowHost`、`AiCollapsible`、`BlockCollapse` 都是内部类型;`Accordion` 是带固定模板的 FAQ 控件,装不下任意卡片) | 所以有 `NcwCollapsible`(下面),用 Arc 的 `MotionValue` + `Smooth` 弹簧做,效果与 Accordion 一致 |
| **任意元素的出现 / 退出、列表新项到达** | 没有 | `NcwMotion` 的 `Arrive` / `Leave` / `IsPresent` / `ShowMotion` / `ArriveOnAttach`(下面) |

**Avalonia 12.1.5 读不到系统的「减少动态效果」**(`IPlatformSettings` 只有主题和对比度),所以由桌面宿主自己探测(§9.3)。

### 9.2 `NcwMotion`:薄封装与规则

**档位**(`NcwMotionTier`,全部取自 Arc 的时长 token,测试核对数值与主题资源一致):

| 档位 | 时长 | 来源 | 用在 |
|---|---|---|---|
| `Instant` | 120 ms | `DurationInstant` | 悬停 / 按下的底色(`PlainButton`) |
| `Fast` | 160 ms | `DurationFast` | 箭头旋转;减弱级别下代替位移的短淡入 |
| `Base` | 240 ms | `DurationStandard` | 出现(进入)、状态点 / 卡片描边的颜色渐变 |
| `Slow` | 480 ms | `DurationConsidered` | 预留(目前没有用到) |
| 退出 | 180 ms | `DurationExit` | 所有退出(比进入快) |

- **缓动**:进入用 `EaseEnter`(减速),退出用 `EaseExit`(加速)(`NcwMotion.EnterEasing / ExitEasing`)。
- **展开 / 收起**:高度走 Arc 的 `Smooth` 弹簧(`NcwMotion.ExpandSpec`);弹簧的好处是被打断(快速连点)时连续。
- **幅度**:出现的上移 = **6 px**(`NcwMotion.RiseOffset`,与 Arc 的 Accordion「答案落进 6 px」同一个数);整幅面板(左侧区域、抽屉、检查器)滑动距离 = 面板自己的宽度;箭头 90° / 180°。没有缩放动画(Arc 的弹层 / 对话框自己带淡入 + 缩放)。
- **性能规则**(每条都有对应的实现或测试):
  1. **优先动 `Opacity` / `RenderTransform`**(合成层,不触发布局):出现、消失、滑入滑出、箭头旋转、忙指示、新行到达都只动这两个属性。位移只用 `RenderTransform`。
  2. **虚拟化列表(对话转录、文件树、会话列表)里不动 Width / Height**:转录里**没有**为新行做高度动画;新行只做「淡入 + 6 px 上移」(`Arrive`),而且**只给真正新追加的行**:同一会话、快照已到、一轮里 ≤ 4 行(`ConversationView`,打开会话 / 快照到了 / 重连补齐都是加载历史,不播放);滚动时被实例化的旧行不动,所以**不影响滚动,也不影响「跟随最新」**(位移不触发布局,不产生额外的 Extent 变化)。回收容器时(`ContainerClearing` / 复用)`Rest` 掉没播完的动画。文件树是 Arc `TreeView` 自己的动画(不整树重排)。
  3. **必须动高度的地方限定范围**:`NcwCollapsible` 只用在**有限数量、可见、一次只会被用户点开一两个**的容器上——工具卡片详情、思考块、待办、审批卡片的错误行、设置里的供应商 / 插件行、项目组、Git 分组、项目面板的分区、审查冲突提示。代价:动画期间每帧改变这一个控件的测量高度,布局代价 = 它自己 + 祖先链(兄弟的测量有缓存);内容按**完整高度**排布后被裁剪,所以动画里文字不会反复换行。真窗口实测见 §9.6。**不做**成逐帧改 Width 的有:左右栏(见下)、设置侧栏导航的 220 → 56 收窄(随窗口宽度走,一次到位)。
  4. **左右栏的滑入滑出不逐帧改列宽**:列宽一次到位(工作区只重排一次),栏自己用 `RenderTransform` 滑动;滑出时栏临时以「覆盖层」形态(跨列、固定宽度)留在原处,滑完才真正隐藏。整幅面板只滑不淡(半透明压在内容上文字会叠在一起);减弱级别换成短淡入。窗口缩放跨过断点引起的显隐**不播放**动画(不是用户的操作)。
  5. **流式输出的文字增量不做逐字动画**:`NcwMarkdown` 整段重排,没有任何包它的动效;只有末尾的「流式小圆点」在呼吸(`Busy`)。
  6. **没有无限循环的样式动画**:以前加载图标是一条永远在跑的样式动画(不看级别、看不见也在跑);现在是 Arc 的 `LiveLoop`,离开界面 / 隐藏 / 滚出视口自动暂停。
- **被打断、不留残余**:所有行为都用 Arc 的可打断运动值;中途反向从当前位置接着走;离开界面树时落到终态并释放订阅;元素原有的透明度 / `RenderTransform` 在动画结束时恢复(`MotionTests` 逐条核对:快速连点、动画途中内容变高 / 变矮、容器回收、离开界面树后不再请求帧)。

### 9.3 级别与设置(本应用作用域)

- **偏好**:`UiPrefs.Motion`(`UiMotionPreference`:`System` 跟随系统(默认)/ `Full` / `Reduced` / `Off`),存在 `ui.json` 的 `motion` 字段(追加的可空字段,旧文件缺失 = 跟随系统,版本号不动;手改成未知值回落成跟随系统)。设置页「本应用 · 外观」(桌面)和手机设置首页「外观」两处入口,**同一个** `AppearanceViewModel`(读写 `Prefs` Store,不缓存;显示「当前生效:完整 / 减弱 / 关闭」)。
- **解析**:纯函数 `UiMotion.Resolve(偏好, 系统是否要求减少动态效果)`(`Ncw.Client`,有单测):明确的选择压过系统;`System` 时系统要求减少 → `Reduced`,否则 `Full`;读不到系统设置 → `Full`(外观页如实写「这个平台读不到系统设置,按完整处理」)。
- **应用**:`NcwMotion.Bind(stores)`(`NcwApp` 启动时调一次)把解析结果写进 Arc 主题的 `MotionMode`;**这是整个应用唯一一处写 Arc 动效级别的地方**。此后 Arc 自带的控件(树、菜单、弹窗、导航栈、标签栏、按钮……)和 `Ncw.UI` 里的行为一起降级。改级别不会重置浅色 / 深色和强调色(有测试)。
- **操作系统的「减少动态效果」**:Avalonia 12.1.5 没有这个 API,所以由宿主注入 `NcwMotion.SystemReducesMotionProbe`:桌面 `Ncw.Desktop/SystemMotionProbe`——macOS 读 `NSWorkspace.accessibilityDisplayShouldReduceMotion`(本机实测可调用),Windows 读 `SystemParametersInfo(SPI_GETCLIENTAREAANIMATION)`(**没有 Windows 机器可测**),Linux 读不到(返回 `null`,不猜)。只在启动和偏好变化时探测一次,不轮询:运行期间改了系统设置,要等下次启动或在外观页重选才跟上。**iOS / Android 宿主还没有接探测**(宿主项目没有真机验证,见 11),它们现在按「读不到」处理,用户可以在外观页直接选。
- **降级的统一语义**(Arc 的约定,本分支所有动效遵守):`Reduced` = 位置 / 尺寸立即跳到终态,只留短淡入(展开时),收起 / 退出没有时长;装饰性循环停止,「忙」指示换成明暗呼吸(`ArcBusyPulse`);`Off` = 一切立即。

### 9.4 已落地的动效清单

**展开 / 收起**(统一用 `NcwCollapsible` + 旋转的箭头;分区用 `NcwSection`——标题行 + 展开状态,是控件自己的纯 UI 状态,不进 ViewModel / Store):

| 位置 | 控件 | 备注 |
|---|---|---|
| 对话 · 工具卡片详情(diff 行 / 输出前几行) | `NcwCollapsible` | 展开状态在 `ToolRowViewModel.IsExpanded`(纯 UI 状态,按 key 对齐,数据更新不丢) |
| 对话 · 思考块 | `NcwCollapsible` | 同上 |
| 对话 · 待办面板的列表 | `NcwCollapsible` | 标题行现在有箭头 |
| 对话 · 审批 / 交互卡片的「回答没成功」提示行 | `NcwCollapsible` | 出现 / 消失带高度 |
| 对话 · 待办 / 目标条 / 「已在另一台设备上处理」 | `ShowMotion=Rise` / `ArriveOnAttach=Rise` | 出现(淡入 + 6 px 上移);它们的高度变化不做动画(底部区域变高会让转录视口逐帧变化) |
| 检查器 · Git 变更分组(冲突 / 暂存 / 未暂存 / 未跟踪) | `NcwSection` | |
| 检查器 · 审查的冲突提示 | `NcwCollapsible` | |
| 检查器 · 文件树节点 | Arc `TreeView` 自带 | 弹簧增长 + 错峰 |
| 项目栏 · 项目组折叠 / 展开 | `NcwCollapsible`(展开的胶囊)+ 收起的 2×2 网格淡入 | |
| 项目面板 · 「会话」「终端和智能体」分区 | `NcwSection` | 设计 03 §1 写了「每个都可以折叠」,这次才真正实现 |
| 左侧区域(⌘B / 标题栏侧栏按钮) | `RenderTransform` 滑动(§9.2.4) | 窄窗口的抽屉同样滑入滑出,遮罩淡入淡出 |
| 右侧检查器(⌘⌥I) | 同上,向右滑动 | **新增**:< 1440 时是右侧抽屉(上面 §1 的「窄窗口」) |
| 设置 · 供应商行 / 插件行 | `NcwCollapsible` + 旋转箭头 | |
| 设置 · 导航收窄(220 → 56) | 不做动画 | 随窗口宽度,一次到位(见 §9.2.3) |
| 手机 · 抽屉 / 设置分区 | Arc `NavigationStack` / `Sheet` 自带 | 手机壳没有自己的抽屉,设置分组是导航栈里的页面;**没有可折叠的设置分区**,所以没有对应项 |

**出现 / 消失**:

| 位置 | 做法 |
|---|---|
| 对话 · 新追加的消息 / 工具卡 / 审批卡片 | `Arrive`:淡入 + 6 px 上移(§9.2.2 的规则) |
| 审批被处理后的「收起」 | 卡片从转录里消失(行已经不在集合里,虚拟化列表没法为已删除的行做退出动画);「回答没成功」的提示行是带高度的展开 / 收起。**没做**消失行的退出动画 |
| 设置覆盖层 / 首次启动页 / 数据页 | `IsPresent` + `PresenceKind=Fade`:淡入,退出先淡出再隐藏(整窗覆盖层不做位移,否则会露出边) |
| 账户状态条 | `IsPresent`(上移 + 淡入;退出淡出) |
| 各页签内容(检查器的文件 / Git / 审查 / 终端 / 上下文 / 历史 / 子代理、设置各分页、工作区内容、定时任务页、加载中 / 空状态) | `ShowMotion=Fade`:每次从隐藏变可见淡入(首次挂载不播) |
| 项目栏的待处理角标 | `ShowMotion=Fade` |
| 弹层 / 菜单 / 下拉 / 对话框 / toast | Arc 自带(淡入 + 缩放 / 滑入) |
| 加载 / 进度 | 加载文字与图标 `ShowMotion=Fade`;进度条是 Arc `Progress` 自带 |

**状态过渡**:

- **按钮**:`PlainButton` 悬停 / 按下底色渐变(`Instant`,关闭级别没有);Arc 的 `Button` 自带按下缩放与颜色过渡。
- **运行中指示**:`NcwMotion.Busy`——加载图标旋转(0.9 s 一圈)、流式小圆点 / 会话行 / 标签页 / 项目栏的运行点呼吸(缩放 ≤ 1.25 + 明暗);减弱级别换成 Arc 的「忙」呼吸(只有明暗),关闭级别静止;离开界面 / 隐藏 / 滚出视口自动暂停。
- **颜色**:状态点(`Border.dot`)和卡片状态描边(等待审批 / 失败)用 `Base` 档渐变。**未知(设备断开)不用颜色表达**:未知是旋转的加载图标(规则 7),和「已结束」(✓ / 灰点)是完全不同的视觉;颜色渐变只发生在 ok / warn / bad / mute 之间,所以不会出现「运行中 → 未知」时经过任何像「已结束」的中间色。
- **「长按允许」填充**:**故意是线性的**——填充就是手势的时钟(`HoldGesture` 的进度),加缓动会让条满的时刻和动作触发的时刻对不上,所以不走统一缓动;只有**回退**(松手早了 / 移出 / 取消)走统一的退出缓动,条平滑退回而不是瞬间归零(减弱 / 关闭立即归零)。
- **箭头**:展开 / 收起的箭头是同一个图标旋转(`Fast` 档),不再是两个图标互换;只有完整级别旋转。
- **模型选择器 / 下拉菜单**:Arc `DropdownMenu` / `Select` 自带。

**导航过渡**:

- 手机 `MobileShell` 导航栈的页面推入 / 弹出、跟手的左缘返回手势、底部标签的胶囊滑动:**Arc `NavigationStack` / `Tabbar` 自带**(横向滑动 + 下面一页视差变暗,手势跟手,松手按速度决定);本分支只在**切换标签**时让新标签的栈淡入(`Arrive(Fade)`)。
- 桌面端:页签切换、检查器页签切换、设置分组切换 = `ShowMotion=Fade`;设置覆盖层进出 = `IsPresent`(淡入淡出)。

### 9.5 修掉的几处小缺陷

- 手机端点已选中的标签不回根页:Arc `Tabbar` 对已选中项不再发 `SelectionChanged`,命令根本没被调用。现在外壳听「按下前就是选中的那一项被点按(连点时 Avalonia 报 `DoubleTapped`,同样算)」,调用同一个 `SelectTabCommand`(它在 ViewModel 里本来就会回根页)。测试覆盖「切换不误触」「再点一次回根页」「已在根页什么也不发生」。
- 手机扫码确认页的有效期是英文 12 小时制:转换器用的是绑定传进来的区域(操作系统的 en-US);改成跟界面语言(`ArcLanguage.Culture`)走。测试把系统区域设成 en-US、界面是中文,断言不出现 AM / PM(用旧实现验证过这条会失败)。
- 窄屏输入框权限图标换行:已随输入框重设计(§6「输入框版式」)消失,不是本次改的。
- 桌面检查器在 1024–1440 没有抽屉:补上了(§1「窄窗口」)。
- **浅色主题层级评估(只评估,没有改 token)**:实测 Arc 的 token——深色 `Background #141414` / `Surface #191919` / `SurfaceRaised #1F1F1F` / `SurfaceMuted #242424`(逐级有明度台阶);浅色 `Background`、`Surface`、`SurfaceRaised` 都是纯白,只有 `SurfaceMuted` 是 `#F8F8F8`(与白色的对比度约 1.04:1),`Border #E9E9E9`、`BorderSubtle #F3F3F3`(约 1.1:1)。影响:① 浅色下卡片 / 面板的层级几乎只能靠描边,而本应用的 `Border.card` 用的是最淡的 `BorderSubtle`;② `PlainButton` 悬停用 `SurfaceMuted`,浅色下悬停反馈很弱(过渡 120 ms 也看不出来);③ 浮起的层(抽屉、检查器覆盖层)已有 `DropShadow`,浅色下它是唯一的层级信号。建议(没做,需要设计取舍):浅色下卡片描边用 `Border` 而不是 `BorderSubtle`;悬停底色在浅色下改用强调色的低透明度叠加;给 `Border.card` 在浅色下加 1–2 px、很低不透明度的阴影——这三条都需要「按主题变体选样式」,Arc 没有现成的选择器,要么用 `RequestedThemeVariant` 的样式选择器,要么在封装层读 `ArcTheme` 的外观,所以放到有人拍板之后再做,不改 Arc token。

### 9.6 测试与验证

- **Headless 能推进动画时钟,也能抓动画中间帧**:Arc 的 `ManualFrameClock.Install()` 让动画只在 `Advance` 时走帧,所以单测可以停在「展开到一半」去断言高度 / 透明度 / 变换,并截图(`motion-*-half-*.png`),不靠 sleep。注意 Avalonia 自己的样式 `Transitions`(箭头旋转、按钮底色)走的是 Avalonia 的动画时钟,不受 `ManualFrameClock` 控制——那部分只断言「定义存在、只在完整 / 减弱级别启用」,截图里它们的中间状态不确定,所以**截图矩阵只在关闭 / 减弱级别下跑**,完整级别只截并断言 `NcwMotion` 驱动的中间帧。
- **测试默认关闭动效**:`TestApp` 启动后 `NcwMotion.Apply(UiMotionLevel.Off)`,其它测试类收尾时也恢复到 Off(以前恢复到 Full);动效测试自己切到 Full + `ManualFrameClock`,结束恢复 Off。
- 单测:`Ncw.Client.Tests/UiMotionTests`(级别解析、偏好相等 / 规范化 / JSON 往返 / 旧文件兼容 / 外观页读写 Store);`Ncw.UI.Tests/MotionTests`(档位 = Arc token、级别应用、`NcwCollapsible` 状态机、`Arrive` / `IsPresent` / `ShowMotion` / `Busy`、长按填充);`MotionScreenshotTests`(展开收起终态 × 四个断点 × 深浅 × 关闭 / 减弱,中间帧,新行到达只给新追加的行,左右栏滑动与反向,抽屉,设置覆盖层,外观页);`MotionMobileTests`(标签淡入、再点回根页、有效期格式);`Ncw.Desktop.Tests/SystemMotionProbeTests`。
- **真窗口验证**(`tools/MotionSmoke`,开发用,不进 slnx;`dotnet run` 并指定项目 `tools/MotionSmoke`):在 macOS 上开真实窗口(真实 Skia / Metal 渲染和真实帧时钟),完整级别下记录动画期间每一帧的间隔,并用 `RenderTargetBitmap` 离屏截动画中间帧(不需要系统的屏幕录制权限)。本机(60 Hz)稳态结果:展开 / 收起一张工具卡、左侧区域滑出 / 滑入,帧间隔中位数 16.7 ms、p95 ≈ 17–18 ms、超过两帧(> 33 ms)的间隔为 0;机器负载高(其它项目在构建)时偶尔会有单次 50–150 ms 的卡顿,冒烟里只记录、不判失败。第一次展开(模板创建 / JIT)预热后不再卡。**目视流畅度只能由人来判断**——这里的结论是「没有掉帧」,不是「好看」;Windows / Linux 同一份代码,没有机器可测。

### 9.7 没做的

- 审批卡片被处理后、转录里被删掉的行的退出动画(虚拟化列表里对已不在集合里的行做退出,要保留它的容器,代价和风险都大于收益)。
- 待办 / 目标条 / 提示的**高度**动画(底部区域变高会让转录视口逐帧变化;现在只有淡入 + 上移)。
- 设置侧栏导航 220 → 56 的收窄动画(随窗口宽度走)。
- 手机的「设置分区折叠」(手机设置是导航栈,没有可折叠分区)。
- iOS / Android 的系统「减少动态效果」探测(宿主没有真机验证);Linux 桌面的探测。
- 主题 / 强调色 / 字体 / 界面密度这些「外观」其余项(外观页现在只有动效)。

## 10. 启动与加载

范围:桌面启动时盖住整个窗口的加载界面,以及手机壳里同一个 ViewModel 的简化版式。**「全屏」是覆盖窗口里的全部内容(含项目栏和主工作区),不是操作系统全屏**;窗口按钮(Windows / Linux)和顶部 40px 的拖动区域始终可用(§10.7)。
**它由真实的加载状态驱动,不是固定时长的假动画**:每一个阶段的完成都对应一个真实发生过的事实(下表),进度只由这些事实算出,时间只用在两件事上——表现时序(延迟出现 / 最短停留,§10.3)和「慢了 / 卡住了」的判断(§10.2)。

| 层 | 位置 |
|---|---|
| Core(纯函数,Agent 和客户端共用) | `Ncw.Core/Domain/StartupProgress.cs`:`StartupFacts`(事实)、`StartupProgress.Derive`(阶段 / 进度 / 失败 / 慢启动 / 谁拥有整窗)、`StartupPresentation.Decide`(出现 / 停留 / 淡出) |
| Client | `ClientStores.Startup`(`StartupStore`,启动事实的**唯一**汇集点)、`StartupRecorder`(Workspace / Account Store 的订阅者,把已经发生的事锁存成里程碑)、`StartupSignals`(任意线程的写入口)、`StartupActions`(重试 / 复制诊断 / 退出 + 向 Store 报告外壳布局与可交互)、`StartupViewModel`(只读 Store) |
| UI | `StartupOverlay`(整窗覆盖层)、`NcwProgressArc`(标识外圈的弧)、`StartupConverters`;`DesktopShell` / `MobileShell` 里各放一个 |
| Desktop | `Sidecar/StartupBridge`(守护的状态机 → 启动事实)、`SidecarStartupHost`(重试 / 诊断)、`NativeWindowBackground`(原生窗口底色)、`Program`(进程创建时间 / 进入 Main / 单实例锁) |
| 开发验证 | `tools/StartupSmoke`(不进 slnx):无界面时间线、故障注入、真实 2.x 接管、真窗口 |

### 10.1 真实的加载阶段与信号

清单上用户看到 5 个阶段(桌面;手机壳只有 应用 → 连接 → 内容)。每个阶段由若干**里程碑**组合而成;里程碑是只增不减的锁存(第一次到达的时间,重放的旧事件覆盖不了它)。

| 清单阶段 | 完成条件(里程碑) | 信号来源 | 怎么得知完成 |
|---|---|---|---|
| ① 准备应用 | `InstanceReady` ∧ `WindowShown` ∧ `PlatformReady` ∧ `AccountDecided` | `Program`(单实例锁拿到的时刻)/ `MainWindow.Opened` / `NcwApp`(偏好读完、动效级别写进主题之后)/ `Account` Store 的 `Mode` 不再是 `Loading`(`AccountService.InitializeAsync` 读完 `account.json`;没有账户服务的宿主开始时直接记) | 事件 / Store 变化,不是计时 |
| ② 启动本机 Agent(桌面) | `AgentReady`(`AgentSpawned` 只进时间线) | `LocalAgentSidecar.Spawned`(进程已拉起,接上上次留下的 Agent 不触发)、守护状态 `Running`(= 本机通道 + 本机令牌握手成功,**不是**「进程起来了」或「打印了 ready」) | `StartupBridge` 订阅守护的事件 |
| ③ 连接设备 | `Connected` | `Workspace.Devices[本机].Connection` 第一次变成 `Connected`(`DeviceLink`:连接 → 握手 → 设备信息写进 Store 之后) | `StartupRecorder` 订阅 Workspace Store |
| ④ 准备数据 | `DataReady`;接管时每一步 `backing-up → migrating → committing → credentials` | `data` 主题(`DeviceLink.ApplyData` → `Workspace.Devices[..].Data`,**和数据页读的是同一份**);接管步骤序号锁存只增不减 | Recorder;`StartupViewModel` 从 `DataGateViewModel` 取当前步骤 |
| ⑤ 加载项目与会话 | `TopicsSynced` ∧ `ProjectsLoaded` ∧ `SessionsLoaded` | `DeviceLink.SyncAsync` 末尾(各主题的首帧都到了)/ `ApplyProjects` 把项目列表(哪怕是空的)写进镜像之后 / 每个项目的 `sessions.list` 都回来(失败的不拖住启动) | `StartupSignals`(任意线程取时间戳,切到 UI 线程写 Store) |
| (交接) | `ShellLaidOut` → `Interactive` | 视图:所有阶段完成后,下面的外壳拿到数据后的第一次布局完成;淡出结束 | `StartupActions.ReportShellLaidOut / ReportInteractive` |
| 只在时间线 | `ProcessStarted`、`MainEntered` | 操作系统记的进程创建时间、进入 `Main` | `Program` → `NcwApp.EarlyMilestones` |

**失败与异常**(每一项都有稳定 key,界面按 key 翻译,认不出来的 key 用通用文字):

| 情形 | 信号 | 表现 |
|---|---|---|
| 找不到 `ncw-agent` | 守护 `Failed` + `AgentNotFound` | 错误面板 `agent-not-found`(重试:立刻再找,不用等 5 秒的间隔) |
| 进程拉不起来(权限 / 被安全软件拦截) | 守护 `Restarting` 或 `Failed` + `LaunchFailed` | 错误面板 `launch-failed`,**在第一次失败就显示**(守护要等 8 次连续失败才放弃,中间一百多秒;这类问题不先处理不会好) |
| 数据目录被另一个本应用无法通信的 Agent 占着 | 守护 `Restarting` / `Failed` + `AlreadyRunningElsewhere`(退出码 75) | 错误面板 `already-running-elsewhere`;对方退出后守护拿到锁,面板自己消失 |
| Agent 反复崩溃 | 一次都没就绪过,却已经连续退出 ≥ 2 次(`Restarting`),或守护 `Failed` + `CrashLoop` | 错误面板 `crash-loop` |
| 数据目录里有加密的数据,却找不到它的凭据主密钥(`credential.key`) | Agent 拒绝启动:退出码 65 + 一行 `ncw-agent startup-failed agent.masterKey.missing credential.key`(02 A6.1);守护**不退避、不重启**,直接 `Failed` + `MasterKeyMissing` | 错误面板 `master-key-missing`,**第一次拒绝就显示**,动作:重试 / 查看诊断 / 复制诊断 / 退出。说明用人话写原因(为了不让已有的凭据变得无法解密,没有自动创建新密钥),并如实告诉用户怎么办:把 `credential.key` 放回数据目录后点「重试」;文件确实丢了的话,已保存的凭据无法恢复、需要重新输入——**这一步目前没有自动流程**,文案照实写,不假装有。诊断里有数据目录路径、文件名和原因 key,**没有任何密钥内容**(事实里本来就只有 key 和字段名) |
| `credential.key` 在,但读不了 / 已损坏 / 不是这份数据的密钥 | 同上,key 是 `agent.masterKey.unreadable` → `MasterKeyUnreadable` | 错误面板 `master-key-unreadable`,动作和上一行相同;说明里强调「没有改动它、也没有创建新的,已保存的凭据保持原样」。手机壳没有本机 Agent,用不到这两个原因(Core 对非桌面形态不产生失败,验证过不崩) |
| 版本不一致 | `ClientOutdated`(Agent 比桌面新 / 拒绝了过旧的客户端)、`AgentOutdated`(包里的 Agent 比桌面旧) | 错误面板,**没有「重试」**(重试不会好) |
| Agent 在运行但连不上 | 连接层退避三次后 `Offline` | 错误面板 `connection-failed` |
| 「连接」或「内容」阶段卡住 | 阶段开始(上一阶段完成的时刻)起 ≥ 30 秒 | 错误面板 `timeout`;阶段完成后自己消失。Agent 阶段由守护自己的就绪超时和退避管,数据接管本来就可能很久,都不套用 |
| 2.x 还在运行(被挡住)/ 接管失败 / 接管完成要点「继续」 | `data` 主题 `blocked` / `failed` / `ready` + 摘要 | **不是启动失败**:加载层整个让位给数据页(§10.7) |
| 启动期间 Agent 崩溃重启 | 守护 `Restarting`(没到 2 次) | 不退回「正在启动」:已完成的阶段保持完成,当前一行改成「正在重新连接 / Agent 正在重新启动」 |
| 运行中断线 / Agent 崩溃(进入过可交互之后) | — | **不再出现全屏加载**,只用已有的断线提示(规则 7) |
| 账户 / 首次启动页 | `AccountMode.Undecided` | 不挡加载层(Onboarding 本来就盖在外壳上):加载层淡出后露出的就是首次启动页(§10.7) |

失败是**派生的,不是锁存的**:守护恢复、阶段完成,错误面板自己消失;「重试」只是让守护 / 连接层立刻再来一次,没有「重试之后的状态」另存。

### 10.2 纯函数 `StartupProgress.Derive(facts, now, timings) → StartupView`

输入是已经发生的事实(各里程碑的时间戳、数据接管步骤序号、此刻的守护状态 / 连接状态 / 数据状态),时间由调用方注入。输出:

- `Phase`:`Loading`(在等)/ `Failed`(错误面板)/ `NeedsUser`(数据页接管)/ `Settling`(阶段都完成了,等外壳布局)/ `Revealing`(可以淡出)/ `Done`;`Owner`:谁拥有整窗(启动层 / 数据页 / 无)。
- `Steps`(每阶段 `Pending / Active / Done`)、`Current`、`Detail`(当前一行下面的说明,枚举)、`DataStep`。
- **确定进度**:`Fraction = (已完成阶段数 + 当前阶段内真实步骤) / 阶段数`,只由锁存算出。**只有数据接管有真实步骤**(`DataStepCount = 4`:备份、升级数据库、提交、转换凭据;最后一步只有 2.x 库里有旧凭据时才出现,所以总数固定为 4,进度条在没有凭据步骤时走到 3/4 再因为「完成」一次到位);其他阶段只有阶段数,`IsDeterminateInStage = false`,标识上转一小段「正在进行」的弧。协议里的 `DataStatus` 只有步骤、没有字节数 / 条数,所以**没有假百分比**(要显示字节级进度需要扩展协议,没做)。
- `IsSlow` + `SlowHint`(从进程创建算起超过阈值,按当前阶段给提示种类;失败 / 数据页接管时不说「慢」);`Failure`(原因 key、能否重试、诊断细节;「查看诊断」「复制诊断信息」「退出」总是可用);`IsReconnecting`;`NextTimeAt`(不依赖新事实、仅因时间流逝输出会变的下一个时刻:慢启动、阶段超时——调用方在那一刻再算一次,没有轮询)。

**单调(规则 6)**:阶段完成是锁存;重放的旧事件、接管失败后重试从头来、Agent 重启、设备断开重连,都不能让进度倒退(`StartupProgressTests`:一串抖动里 `Fraction` 非递减)。**区分「首次加载」和「运行中断线重连」(规则 7)**:一旦 `Interactive` 到达,`Derive` 永远返回 `Done`(`Owner = None`,没有失败、没有慢),运行中的断线只用已有的断线条;启动期间的断线只让当前一行显示「正在重新连接」,已完成的阶段不退回。

### 10.3 时序规则 `StartupPresentation.Decide`(纯函数,时钟注入)

1. **第一帧就是底色**:覆盖层的底色(`Background` token)从窗口的第一帧起就在;**内容**(标识、清单)等 `ShowDelay` 才出现。更快就绪时内容根本不出现,底色直接淡出——没有一闪而过的加载界面。**延迟和最短停留都从「窗口真正显示出来」(`WindowShown`)算起**,不是从 ViewModel 创建算起:窗口显示之前 UI 线程要花几秒创建整个外壳(真窗口实测,§10.6),那段时间用户什么也看不到,若也算进去,用户第一次看到加载界面时「最短停留」早已用完,几百毫秒就淡出(这是第一次真窗口冒烟发现并修掉的缺陷)。
2. **失败立刻显示**:错误面板不等延迟。
3. **最短停留**:内容一旦出现,至少停留 `MinDwell` 才淡出;内容没出现过则没有停留要求。
4. **让位**:数据需要用户处理时,整个加载层(含底色)让位给数据页;回来时内容不用再等延迟。
5. **淡出只开始一次**:开始之后状态不再变化,重放的旧事件拉不回来;淡出结束由视图报告,之后是 `Done`。
6. **交接不跳**:所有阶段完成后,视图等外壳在**拿到数据后**的第一次布局完成(布局完成事件 + 一轮低优先级调度;不用 `RequestAnimationFrame`,Headless 和被遮挡的窗口里它不可靠),才报告 `ShellLaidOut`,淡出才会开始。所以淡出时下面的外壳已经完整渲染,淡出过程中没有任何布局变化(真窗口实测见 §10.6)。

`StartupViewModel` 自己没有定时器:它只给出 `NextWake`(延迟出现、最短停留结束、慢启动、阶段超时中最近的一个),视图用一个一次性的 UI 定时器到点调 `Tick`(和对话区的计时一样,计时在视图里)。

### 10.4 视觉与动效

背景全部是 Arc token(`Background`、`Border`、`Foreground`、`TextSecondary`、`TextMuted`、`Danger`),深浅两套主题都对,没有任何颜色值;文字全部走 `NcwTextKind`(43 条,中英文齐全),不依赖具体字体宽度(清单和说明都会换行)。

- **标识**:中间是 NextCoWork 的品牌图标(2.x 的应用图标,`src/Ncw.UI/Assets/nextcowork-mark.png`,256 px,圆角方块自带透明边距,方块本身约 55 px;作为 `AvaloniaResource` 随程序集发布);失败时换成 `Danger` 色的 `TriangleAlert`。外壳左上角目前仍是 Lucide `Sparkles` 的占位图形,和这里不一致,换标识时一起换。外圈是 `NcwProgressArc`:弧长 = `Fraction`(已经走完了多少个阶段 / 接管步骤),走 Arc 的 `Smooth` 弹簧(无回弹,被打断时从当前位置接着走);减弱 / 关闭级别 Arc 让弹簧直接跳到终点。**还在等的整个期间**(`IsIndeterminate` = `Phase == Loading`)再叠一小段短弧,用 `NcwMotion.Busy=Spin` 旋转:弧长只表示「走完了几步」,一步本身要多久是不知道的(真实的 1.6 GB 库上接管的「备份」一步实测 37 秒,这期间弧长一动不动),所以短弧一直转,证明还在干活而不是进度条停住了(减弱级别是 Arc 的「忙」明暗呼吸,关闭级别不画——静止的一段弧会被误读成进度)。失败时外圈换成 `Danger` 色、中间换成 `TriangleAlert`。
- **清单**:每个阶段一行。已完成的打勾(勾的容器用 `NcwMotion.IsPresent` 淡入,遵守动效级别)并淡色;当前阶段最亮、加粗、前面是转圈图标;后续阶段暗淡。当前阶段下面是一行说明(接管时用和数据页同一套步骤文字)。**行的透明度变化是立刻的**,故意没有用 Avalonia 样式的 `Transitions`:淡出结束时覆盖层会被摘出界面树,而样式过渡在控件离开界面树时仍被时钟驱动(Headless 下复现过 `NullReferenceException`)。
- **数据接管**:说明下面有四段小条,点亮的段数 = 真实的步骤序号(锁存,只增不减)。
- **慢启动**:一行温和的提示(`NcwMotion.IsPresent` 上移 + 淡入),文字按当前阶段区分(Agent / 数据 / 内容 / 通用),不是错误。
- **失败**:明确的错误面板——标题 + 原因说明 + 按钮「重试」(能重试才有)、「查看诊断」(展开 `NcwCollapsible`,里面是 `StartupProgress.Diagnostics` 的文本 + 守护日志尾部)、「复制诊断信息」、「退出」。不无限转圈。
- **版式**:宽(≥ 720)画整张清单;窄窗口 / 手机壳(< 720)只留标识、当前阶段的名字和说明(清单收起)。内容整体可滚动(矮窗口)。

#### 入场编排

内容出现的那一刻(`ShowDelay` 过了,或失败要立即显示)按一份固定脚本进入(`StartupOverlay.BuildEntrance`,测试里读得到 `LastEntrance`);时长、缓动、错峰全部是 Arc 的 token,没有魔法数字。t = 内容出现的时刻:

| t | 谁 | 怎么进 |
|---|---|---|
| 0 | 标识 | `Grow`(从 `GrowFrom` = 0.84 缩放到 1 + 淡入,`Slow` 480 ms) |
| 0 | 外圈轨道 | `NcwProgressArc.PlayEntrance`:从十二点钟方向顺时针被画出来(480 ms),已有的进度弧一起画 |
| `StaggerLine` 80 | 标题 | `Rise`(`Base` 240 ms) |
| 2 × `StaggerLine` + i × `StaggerItem` | 清单第 i 行 | `Rise`,一行比一行晚 35 ms,往下错峰进来 |
| 清单之后 | 当前说明 | `Fade`(`Fast` 160 ms) |
| 480 | 短弧 | 轨道画完之后淡入,开始转 |

- 内容根先变可见并布局、再在同一个 UI 线程回合里编排入场,所以不会有一帧「已经完整可见」的内容;每个部分在延迟期间就停在起点(透明)。
- 减弱级别:没有延迟、缩放、轨道绘制,所有部分一个 `Fast` 档的短淡入;关闭级别立即就位。三档的最终状态一致。
- 只播放一次:让位给数据页再回来不重播(`LastEntrance` 是同一份)。失败时只有标识和错误面板(标识之后一个 `StaggerLine`)。
- `NcwMotion.Arrive` 为此新增 `delay` / `tier` 参数和 `NcwArrival.Grow`;完成后精确落回静止值(不透明度、变换都清掉,没有残留)。
- 真实定时器偶尔比预定时刻早几毫秒触发,`NextWake` 不变时 `Arm` 不会重新武装、层会卡住;视图的 `RunWake` 因此在每次 `Tick` 之后重新武装(`StartupOverlayTests` 有专门用例,变异检查确认过)。
- 真窗口里量过入场:约 400 ms 轮询期间帧间隔中位约 16.7 ms,5 次运行全部通过(`tools/StartupSmoke window`,`window.entrance`)。

### 10.5 无障碍与减少动效

- **播报**:「当前在做什么」那一行(`StartupOverlay` 的 `DetailText`,说明文字)是 `AutomationProperties.LiveSetting = Polite`,它只在阶段 / 子步骤变化时才变,不是每一帧;慢启动提示 `Polite`;失败标题 `Assertive`;整个覆盖层有名字(「正在启动 NextCoWork」),清单有名字(「启动进度」)。
- **动效级别**(用 `NcwMotion` 的 `IsPresent` / `Busy` / Arc 弹簧,不另造开关;「外观」设置里已有的四档):**完整**——内容淡入、弧长弹簧、短弧旋转、勾淡入、整层淡出;**减弱**——保留短淡入(`Fast` 档)、弧长直接到位、短弧换成 Arc 的明暗呼吸、淡出没有时长(立即);**关闭**——全部静止、立即,**仍显示真实的阶段文字、清单和进度弧**。三档的最终状态一致(`StartupOverlayTests` 三档逐项核对)。

### 10.6 第一帧、窗口底色与真窗口验证

窗口出现的第一帧必须是加载界面的底色,绝不能先白屏再变深色。Avalonia 里分两层:
1. **Avalonia 自己画的内容**:`MainWindow` 的 `Background` 显式写成 `Background` token(和覆盖层底色是同一个 token),覆盖层的底色从 `IsPresented` 的初值起就在(`NcwMotion.IsPresent` 对尚未挂上界面树的控件直接就位,没有淡入)。
2. **原生窗口在 Avalonia 画出第一帧之前显示的底**:macOS 的 NSWindow 默认随系统外观(浅色系统是浅灰 / 白)。`NativeWindowBackground` 在窗口显示之前把 `NSWindow.backgroundColor` 设成 Arc 的 `Background` token 解析出来的 RGB(`NcwApp` 创建窗口后、显示前调用;RGB 由 token 现解析,代码里没有颜色值),并可读回核对。**Windows / Linux 没有做,也没有机器可验证**。

**真窗口实测**(`tools/StartupSmoke window`;macOS arm64,真实 Skia / Metal 渲染,Release 构建 + NativeAOT 的 `ncw-agent`)。**机器负载很高**(10 核,系统负载 16–22,别的会话在构建),数字只作数量级参考,不是干净机器上的基准:

- **原生底色**:`NSWindow.backgroundColor` 读回 `#141414`,和 Arc 的 `Background` token(深色)一致 ✓。不设置时系统默认是 `windowBackgroundColor`(随系统外观的动态色,转不成固定 RGB,读不出来),所以浅色系统上第一帧会是浅色——这正是要修的。
- **第一帧**:窗口打开时对窗口做离屏渲染,中心和底部像素都是 `#141414`、内容还没出现 ✓。这验证的是 Avalonia 自己画的那一层;原生窗口「最早的一帧」在这台机器上没法截屏,只能靠上面的原生底色读回做逻辑验证。
- **进程到窗口的时间线**(Release,4 次,中位数 / 范围,ms,从进程创建算起):`FrameworkReady`(Avalonia + Arc 初始化完)870(860–997)→ `ShellCreated`(窗口和外壳创建完)2228(2207–2418)→ `WindowShown` 2512(2475–2710)→ `AgentReady` 2757(2410–3510)→ `Interactive` 4648(3850–5450)。
  **窗口显示之前要 2.5 秒**(框架 0.9 + 创建窗口与外壳 1.4 + 显示与首次布局 0.3),这段时间用户什么也看不到,加载层帮不上;窗口显示之后加载层可见 1.3–2.8 秒(中位 2.1),这段才是真实的在等 Agent / 数据 / 内容。刚构建完的第一次冷启动更慢(框架初始化 3.8 秒,窗口 6.5 秒才出现)。
- **淡出**:约 180 ms(`NcwMotion` 的退出时长),11–15 帧,帧间隔中位 16.5–17.4 ms,p95 45–76 ms,1–2 个间隔超过 33 ms(淡出开始的头几帧正好和外壳最后的布局 / JIT 撞在一起)。外壳几个关键区域(工作区、左侧区域、主网格)的位置和尺寸在淡出开始时和释放之后**完全一致**,没有布局跳动 ✓。加载期间的帧:中位 16.6–16.7 ms,p95 21–47 ms(冷启动那次 max 400 ms,是 JIT)。
- **释放**:覆盖层和 `NcwProgressArc` 都不在界面树里,ViewModel 的 `OwnsScreen` / `NextWake` 清空 ✓;外壳稳定之后 2 秒内空闲 CPU 0.1–0.7%(释放后立刻的 2 秒是外壳自己在收尾:会话、Git 状态等,4–26%,不是加载层)。
- **「提前启动 sidecar」评估后没有采用**:想让 Agent 的拉起和握手(冷启动约 0.9–1.1 秒)和框架初始化、窗口创建并行——在 `Main` 里单实例之后就启动守护,界面起来后用守护记下的拉起 / 就绪时刻回放里程碑。4 组交叉的 A/B(Release,负载 16–22):`Interactive` 中位 4967 ms(提前)对 4648 ms(现状);提前启动反而让 `FrameworkReady` 变慢(1408 对 870 ms,和 AOT Agent 进程抢 CPU),`AgentReady` 只早了约 100 ms。**没有收益**,还多出回放和幂等启动的代码,已还原。在更空闲的机器上结论可能不同;没有证据之前不做。
- **没有做「先显示一个只有底色的轻量窗口、延后创建外壳」**:创建窗口与外壳的 1.4 秒确实在第一帧之前、是最大的单项,但要改 `MainWindow` 内容的延迟装配(焦点、快捷键、测试里大量直接创建 `DesktopShell` 的地方),这次没动,记入 §10.13。

### 10.7 层级与交接

同一时刻只有一个全屏层。顺序:**启动加载 →(数据被挡住 / 失败 / 接管完成等「继续」)数据页 →(首次启动)首次启动页 → 外壳**。

- `DesktopShell` 里的叠放(后写的在上):`AccountStatusBar` < `OnboardingOverlay` < `SettingsOverlay` < `DataGateOverlay` < **`StartupOverlay`** < 右上角常驻(检查器开关 + Windows / Linux 窗口按钮)。窗口按钮在加载层之上,顶部 40px 里没被控件处理的按下不被拦截,`MainWindow` 照旧用它拖动窗口 / 双击最大化;加载层盖着时检查器开关隐藏(它会悬在空白上)。
- 职责分开:启动加载是「在等」,数据页是「需要用户处理」。加载层里数据接管的步骤和数据页读**同一个事实**(`Workspace` Store 里本机设备的 `Data`,经 `DataGateViewModel`),不各读各的;`DataGateViewModel.IsPresented`(数据页的覆盖层绑它)= 有内容要显示 **且** 加载层没有拥有整窗。接管中(`preparing` / `taking-over`)由加载层显示步骤,数据页不同时出现;被挡住 / 失败 / 接管完成等「继续」时 `StartupView.Owner = DataGate`,加载层让位(整层含底色),数据页接管;用户点「重试」回到接管中,加载层接回来(内容不再等延迟)。进入可交互之后加载层不再存在,数据页的行为和以前一样(`IsPresented == IsVisible`)。
- 首次启动:`AccountMode.Undecided` 时 `OnboardingOverlay` 早就开着(盖在外壳上),加载层在它之上;加载层淡出后露出的就是首次启动页,不会先闪一下外壳。
- 手机壳:`MobileShell` 里同一个控件、同一个 `StartupViewModel`(`AdaptiveShell` 可能同时持有两个外壳,各有一个覆盖层绑同一个 ViewModel;第一个淡出完毕的报告可交互,另一个随 ViewModel 释放而不再有任何动作)。

### 10.8 手机壳

阶段 应用 → 连接 → 内容(`StartupProfile.Remote`):`Connection` 取所有已配对设备里最好的一个(任一已连接 → 已连接;否则任一在连接 → 连接中);**设备连不上不是启动失败**——所有设备离线 / 休眠 / 被吊销就算「连接」阶段「已落定」,「内容」阶段跳过,外壳照常进入(设备页显示离线);没有配对设备时没有什么要等的。版式是简化版(标识 + 当前阶段名 + 说明,没有整张清单)。Headless 验证(390 宽、深浅主题);iOS / Android 宿主不在范围内,**没有真机验证**。已知限制:手机宿主若在外壳创建之后才异步把已配对设备装进 Store,「没有设备」会让启动层在设备出现之前就放行(外壳里设备页随后显示连接中)。

### 10.9 资源与性能

加载层本身很轻:一个整窗 `Border` + 一个居中的小控件树(两个弧、一个图标、五行清单);不在自己里面做任何重活;动画只动 `Opacity` / `RenderTransform` 和一段圆弧的弧长(`NcwProgressArc` 只在 `Progress` 变化时重画,弧长是 Arc 的弹簧)。覆盖层在没有跟踪启动(测试、模拟数据)时隐藏,零成本。
**释放**:淡出结束(减弱 / 关闭级别立即)→ 视图报告 `Interactive` → `StartupViewModel` 退订所有 Store / ViewModel 事件、`StartupRecorder` 自退订 → 视图停掉一次性定时器、退订 ViewModel、把自己从界面树里摘掉(标识的弧、短弧的旋转循环、淡入淡出的运动值随之离开界面树,Arc 的 `LiveLoop` / 运动值在离开界面树时自行释放)。`StartupOverlayTests` 逐项核对(摘掉、定时器不在等、没有 `NcwProgressArc` 留在树里、之后 Store 怎么变都不再有通知),真窗口里还量了释放之后的空闲 CPU(§10.6)。

### 10.10 测试与验证

| 层次 | 内容 | 位置 / 命令 |
|---|---|---|
| Core 纯函数 | 阶段、单调(重放 / 抖动 / 重试从头)、慢启动阈值与提示、每种失败原因及可用动作、失败自己消失、数据页让位、手机形态、最短停留 / 延迟出现 / 失败立即显示 / 淡出只一次、首次加载与断线重连的区分;变异检查(把锁存改成覆盖,3 条失败) | `Ncw.Core.Tests/StartupProgressTests`(73 条,含主密钥拒绝的两个原因) |
| Client | ViewModel + Store + Recorder + 真实的 DataGate:延迟出现、最短停留、淡出触发、释放后零通知、失败面板与重试、复制诊断、退出、数据页让位与交回、接管步骤与数据页同源、手机形态、`DeviceLink.RetryNow`;变异检查 | `Ncw.Client.Tests/StartupViewModelTests`(33 条) |
| Desktop | 守护状态 → 启动事实的映射(每个问题 key 都有标题 + 说明文字)、拉起 / 握手两个里程碑、接上不触发拉起、找不到 Agent 时重试立即生效、重试不留杂散信号、诊断含日志尾部;变异检查 | `Ncw.Desktop.Tests/StartupBridgeTests`(9 条,含主密钥原因的映射);守护的 65 / 78 不重启、原因映射、重试恢复在 `SidecarSupervisorTests`,真实进程在 `RealAgentMasterKeyTests` |
| Headless 界面 | 四个断点 × 深浅:第一帧 / 启动 Agent / 连接 / 数据迁移(带确定进度)/ 加载首批数据 / 慢启动 / 失败(Agent 不存在、版本不匹配、主密钥缺失 / 无法使用、展开诊断)/ 数据页让位;手机壳;动画中间帧(ManualFrameClock:标识弧长、勾淡入、淡出透明度);释放;窗口外框与拖动区域;三档动效终态一致;中英文 | `Ncw.UI.Tests/StartupOverlayTests`(71 条),截图在 `bin/Debug/net10.0/screenshots/startup-*.png` |
| 真实启动(无界面) | 真实 `ncw-agent`(NativeAOT)+ 真实守护 + 真实 DeviceLink + 真实 Store:时间线(冷 / 热 / 接上)、故障注入、真实 2.x 库副本接管 | `dotnet run --project tools/StartupSmoke -- timeline|faults|takeover --agent <ncw-agent>`(结果见 §10.11) |
| 真窗口(macOS) | 原生窗口底色读回、第一帧像素、帧间隔、淡出布局稳定、释放后空闲 CPU | `dotnet run --project tools/StartupSmoke -- window --agent <ncw-agent>`(结果见 §10.6) |

### 10.11 实测时间线与阈值校准

全部是真实的 `ncw-agent`(NativeAOT 发布产物)+ 真实守护 + 真实 `DeviceLink` + 真实 Store + `StartupViewModel`,无界面(`tools/StartupSmoke timeline`;Release 构建的 harness;macOS arm64;**机器负载 15–17,10 核**,别的会话在跑,数字是数量级参考)。ms,从启动流程开始算,每格是 **中位数(最小–最大)**,每种场景 10 次:

| 场景 | AgentSpawned | AgentReady | Connected | DataReady | TopicsSynced | ProjectsLoaded | SessionsLoaded | 全部阶段完成 | 淡出开始 |
|---|---|---|---|---|---|---|---|---|---|
| **冷**:全新数据根(Agent 要建库、迁移 schema),没有 Agent 在运行 | 55(40–707) | 1130(903–2347) | 1154(917–2503) | 1163(925–2850) | 1302(1040–3436) | 1312(1040–3500) | 1312(1040–3501) | **1316**(1044–3568) | 1344(1045–3573) |
| **热**:已有数据根 + 3 个项目,没有 Agent 在运行(最常见的一次启动) | 50(36–76) | 704(489–736) | 724(506–755) | 736(518–767) | 873(637–908) | 874(642–909) | 912(684–956) | **921**(685–959) | 948(852–1313) |
| **接上**:上次「关窗后继续运行」留下的 Agent,不拉起进程 | — | 2(1–13) | 2(1–13) | 2(2–13) | 3(2–15) | 3(2–15) | 3(2–15) | **6**(6–18) | 6(6–18) |

读数说明:① 「淡出开始」= max(全部阶段完成, 内容出现时刻 + 最短停留),这里的内容出现时刻是 `ShowDelay` 到点后 UI 线程第一次空下来的时刻(启动期间 UI 线程在装配后端,有时会晚一些);② 「冷」里第一次(707 / 2347 / 3568)是刚发布的 AOT 产物第一次运行(文件缓存是冷的),之后 9 次稳定在 0.9–1.5 秒;更早一次在系统负载 72 时测过冷启动 8.1 秒;③ 阶段之间:Agent 就绪 → 连接 ≈ 15–55 ms(最慢一次 156 ms),连接 → 内容就绪(全部阶段完成)≈ 0.13–0.4 秒(冷启动第一次 1.1 秒)。

**阈值取值与依据**(`StartupTimings`,都是数据,可调):

| 阈值 | 取值 | 依据 |
|---|---|---|
| `ShowDelay`(内容延迟出现) | 250 ms | 「接上」的 10 次全部阶段完成 ≤ 18 ms,「热 / 冷」的 20 次最快 685 ms:任何 25–600 ms 之间的值都能把「根本不该出现」(接上)和「一定要出现」(要拉起进程)分开;取 250 ms(≈ 人对延迟的感知门槛,也是 Arc `DurationStandard` 的量级)。接上上次留下的 Agent 时内容根本不出现,底色直接淡出 |
| `MinDwell`(最短停留) | 600 ms | 热启动最快的一次全部完成在 685 ms,内容 250 ms 出现,所以最快的几次(10 次里 4 次 < 850 ms)会被多留 100–165 ms(淡出开始在 850 ms),其余不受影响;600 ms 够让勾的淡入(240 ms)和阶段文字被读到,不会一闪而过 |
| `SlowAfter`(慢启动提示) | 8 秒(从进程创建算起) | 热启动 p95 0.96 秒、冷启动中位 1.3 秒,8 秒是它们的 6–8 倍,正常启动不会误报;更早一次在系统负载 72 时冷启动 8.1 秒,说明机器极忙时会碰到;真实的 2.x 接管 43 秒,提示「升级数据可能需要几分钟」在 8 秒时出现正合适 |
| `StageTimeout`(连接 / 内容阶段超时) | 30 秒 | 「连接」阶段实测 15–55 ms(最慢一次 156 ms),「内容」阶段 0.13–0.4 秒(最慢一次 1.1 秒),30 秒是最慢一次的 27 倍以上;Agent 阶段(实测 0.5–2.3 秒)由守护自己的 30 秒就绪超时和退避管,数据接管(1.6 GB 库上 38 秒)不套用 |

**真实 2.x 库接管**(`takeover --legacy-db`,1.6 GB 的 2.x 库的**只读副本**,原库没有碰;23 个项目、631 个会话,凭据主密钥是随机造的,旧凭据解不开会计入「需要重新输入」,这次没有旧凭据行):加载层显示的是 Agent 的真实步骤 备份(`backing-up`)→ 升级(`migrating`)→ 提交(`committing`),进度值 0.200 → 0.600 → 0.650 → 0.700 → 0.800,单调不降;`备份并校验` 这一步用了 **37 秒**(系统负载 20–30;`docs/parity.md` 在空闲机器上量过约 16 秒),升级 10 ms,提交 410 ms;期间加载层一直拥有整窗,数据页没有同时出现 ✓。**这 37 秒里弧长一动不动**(弧长只数走完了几步),所以短弧在整个等待期间一直转,证明还在干活——这是看了这份数据之后才改的(以前只在没有真实步骤的阶段转)。数据被挡住(2.x 还在运行,`SingletonLock` 指向活进程)时:1.8–2.0 秒内加载层整个让位给数据页(数据页在场、加载层不在场,同一时刻只有一个全屏层),放开锁后点数据页的「重试」→ 加载层接回来显示接管步骤 → 摘要页(「继续」)→ 继续后加载层接回来完成内容阶段 ✓。

**主密钥拒绝之后的回归复测**(同一份只读克隆,NativeAOT Agent 的新构建):没有 `credential.key` 时 0.95 秒出 `master-key-missing` 面板(没有退避);补一把随机密钥、点重试后接管照常完成——24.5 秒(机器负载 20–40;另一次完整运行是 36.2 秒)、23 个项目、631 个会话,进度 0.200 → 0.600 → 0.700 → 0.800 单调不降,步骤仍是 备份 → 升级 → 提交,加载层一直拥有整窗 ✓(接管路径的主密钥来源没有变)。

**故障注入**(`faults`;真实的 NativeAOT `ncw-agent` 或能被它替身的可执行文件;每一项都落到明确的面板 / 不转圈,且「重试」真的恢复):

| 故障 | 怎么注入 | 失败面板出现 | 面板 key | 重试 |
|---|---|---|---|---|
| 找不到 Agent | `NCW_AGENT_PATH` 指向不存在的文件 | 640 ms | `agent-not-found` | 指向真文件后重试:立刻再找(以前要等满 5 秒),4.4 秒后全部阶段完成(含 Agent 冷启动) |
| 起不来 | 存在但没有执行权限的文件 | **8 ms** | `launch-failed` | 恢复 1.7 秒后全部阶段完成。守护要连续失败 8 次(一百多秒)才放弃,所以这一条是「Restarting 也立即显示」才做到的 |
| 反复崩溃 | 立刻 `exit 3` 的脚本 | 4.1 秒(连续退出第 2 次;守护自己要 8 次才放弃) | `crash-loop` | 恢复 2.0 秒 |
| 版本不匹配(Agent 比桌面新) | 把桌面版本注入成 `0.0.1` | 1.7 秒 | `client-outdated`,**没有「重试」** | —(重试不会好) |
| 版本不匹配(包里的 Agent 比桌面旧) | 注入 `99.0.0` | 2.1 秒 | `agent-outdated`,**没有「重试」** | — |
| 数据根被别的 Agent 占着 | 先起一个用别的令牌的 Agent | 90 ms(握手被拒 → 守护 `Failed / AlreadyRunningElsewhere`) | `already-running-elsewhere` | 对方退出后点重试,约 1.9 秒后全部阶段完成 |
| 所有阶段完成、还没淡出时 Agent 被杀 | 杀进程 | 没有面板:已完成的阶段不退回(5 → 5,进度 1.00 → 1.00),照常淡出,Agent 被拉起、设备重连 | — | — |
| 进入可交互之后 Agent 被杀 | 杀进程 | **加载层没有回来**(设备断开,2.4 秒后重连;只用已有的断线条) | — | — |
| 数据根里有加密凭据的库、却没有 `credential.key`(夹具:库里一行真实的 NCK1 密文) | 只放库,不放密钥 | **0.9 秒**(真实 NativeAOT Agent 退出码 65 + 一行结构化输出;守护 `Starting → Failed`,没有 `Restarting`,`RestartCount` 0;原来是 4–5 秒的崩溃面板,守护自己要 45 秒才放弃) | `master-key-missing`,有「重试」 | 把有效的 `credential.key` 放回去、点重试:恢复(夹具库是 2.x 形状,恢复后照常进入接管并停在摘要页),密钥文件还是放回去的那一把(没有被重新生成) |
| `credential.key` 已损坏(长度不对,内容是哨兵) | 同上,放一个 17 字节的文件 | 0.6–0.8 秒 | `master-key-unreadable`,有「重试」 | 换上有效的密钥后重试:恢复;损坏的文件在失败期间原样保留,诊断里没有它的内容 |
| Agent 起来就崩溃(未处理异常) | 指向一个以 134 退出的假可执行文件(临时目录里的 shell 脚本)——**不再靠「缺主密钥」注入**:真实的 Agent 现在对那种情形是结构化拒绝(上两行) | 4.2 秒(连续退出第 2 次,有退避) | `crash-loop` | 指回真 Agent 重试,恢复约 2.2 秒 |
| 真实 2.x 库(只读克隆)、没有 `credential.key` | 只放库,不拷密钥 | 0.95 秒 | `master-key-missing`,没有退避 | 补一把随机密钥、点重试,继续走接管(见上) |

### 10.12 与 2.x 的对应与差异

对应(`docs/parity.md`「启动与加载」登记):2.x 的启动闸门决策 `decideMigrationGate`(`waiting` / `app` / `gate` / `startup-failed`)和它的时序测试(`migration-release.test.ts`)、骨架屏 `AppSkeleton`、15 秒启动超时诊断页、`WindowControls` 在闸门期间仍可用,在这里分别对应 `StartupProgress.Derive`、加载层、阶段超时与错误面板、窗口按钮排在加载层之上。
差异:① 2.x 是**骨架屏**(轮廓与外壳重合,静止,不做动画;理由:纯色像程序坏了),3.0 是**品牌标识 + 真实阶段清单**,第一帧只有底色、内容等 `ShowDelay` 才出现——因为 3.0 的启动链更长(要拉起 sidecar 并握手),用户需要知道在等什么,而且更快就绪时不该闪一下;② 2.x 的「15 秒」是整体超时,3.0 按阶段:Agent 阶段看守护自己的就绪超时与退避,连接 / 内容阶段 30 秒,接管不设上限;③ 2.x 的诊断页有「打开数据目录」,3.0 没有(诊断页的入口还没做),只有诊断文本(展开 / 复制)。

### 10.13 没做的与已知限制

- 数据接管的字节级 / 条数级进度:协议 `DataStatus` 只有步骤,没有分子分母,需要扩展协议(升协议版本或加能力开关,同步黄金向量),这次没有动协议。
- Windows / Linux 的原生窗口底色、真机验证;iOS / Android 宿主里的加载界面(Headless 验证了共享部分)。
- 「先显示一个只有底色的轻量窗口、延后创建外壳」:创建窗口与外壳的约 1.4 秒在第一帧之前,是窗口显示前最大的一项(§10.6);要改 `MainWindow` 内容的延迟装配,没有做。
- 「提前启动 sidecar」评估后没有采用(§10.6 的 A/B)。
- ~~Agent 侧缺陷:数据根里有 2.x 的库却没有 `credential.key` 时 Agent 抛未处理异常退出(134),用户只看到「崩溃循环」~~——已修:Agent 拒绝启动并给出结构化原因(退出码 65),守护不重启,面板写明原因和怎么办(§10.1 失败表、02 A6.1、04 §3)。仍然**没有**的:密钥确实丢了时重新输入凭据的自动流程(界面如实写明),以及「打开数据目录」入口(诊断里有数据目录路径)。
- 加载层之后的 Onboarding 首屏也是整窗层,两者之间是「先淡出加载层再露出首次启动页」,没有做两层之间的合并动画。
