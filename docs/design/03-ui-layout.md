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
│ ＋ │ 文件 · Git · 定时     │ ┌────────────────────────────────────────────────┐ │              │
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
  - **文件 / Git / 定时任务**:入口项,点开后在主工作区打开对应的标签页。
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
- **窄窗口**:检查器目前只在 ≥ 1440 的宽度停靠;1024–1440 的「抽屉」形态还没有实现(截图测试把面板单独放在 400 / 480 / 520 宽度下验证版式)。

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
- 动效:遵守 Arc 的 `MotionLevel`,在设置里提供「完整 / 减弱 / 关闭」三档。
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
  点已选中的标签回到该标签的根页;**会话页不显示底部标签栏**。
- **Arc.Mobile 封装**:`TKCode.Arc.Mobile`(锁定 `1.1.0`,`UseArcMobile()`)的 `NavigationStack` / `Page` / `Navbar` / `Tabbar` / `Sheet` / `PullToRefresh` / `ListGroup` / `ListItem` 经 `Ncw.UI/Controls/MobileControls.cs` 薄封装(`NcwNavigationStack` 等),
  业务视图只引用封装。Arc 文档注明「尚未在真机上验证」:这里验证了版式和状态,左缘右滑 / 弹层手势的真机手感待宿主项目(见 [11 §4](11-mobile-hosts.md))。
  Arc 的 `Tabbar` 没有角标,「收件箱」的待处理角标由壳自己叠在第一个标签上。
- **对话视图复用**:会话页里就是桌面的 `ConversationView`(同一个 `ConversationViewModel`、转录行、审批卡片、输入框),不复制。差异靠继承属性 `NcwDensity.IsTouch`(手机壳对每个页面设为 true):
  审批卡片 `InteractionCard` 切成触控版式(拒绝 + 长按允许),输入框 `ComposerView` 切成紧凑工具行(也按宽度 < 480 触发,桌面窄窗口同样受益)。
  窄屏的模型 / 思考 / 权限三个选择器只留图标(模型名截短),菜单照旧;软键盘由 `Page.AvoidKeyboard` 处理。
- **触控密度与宽度是两回事**:`IsTouch` 只由手机壳设置,桌面窗口再窄也不会变成「长按允许」。

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
  解析用 `PairingQr.TryParse`,过期判断用注入的时钟;**解析成功后要用户确认才配对**(显示设备 ID 前 8 位和地址,让用户和电脑上的核对);配对走 `DeviceActions.PairWithQrAsync`(唯一入口,
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
