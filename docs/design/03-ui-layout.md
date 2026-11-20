# 03 · 界面设计与布局

全新设计,不沿用 2.x 的界面。最初参考「左侧项目图标栏 + 项目面板 + 主工作区」的组织方式,2026-11 起左侧合成一栏「项目树」侧边栏(§1),
并增加**项目分组**。

## 1. 桌面布局:左侧边栏 + 主工作区 + 检查器

```
┌──────────────────────────────┬────────────────────────────────────────────────────┬──────────────┐
│ ●●● ◧ NextCoWork           ⇤ │ [✱ 重构登录 ×][>_ zsh ×][± 3 files ×]  +    ⋯  ◫ ◨│              │
├──────────────────────────────┼────────────────────────────────────────────────────┤  检查器(可选)│
│ ✎ 新会话               ⌘N   │                                                    │  ─────────── │
│ ⌕ 搜索                 ⌘K   │                                                    │  文件         │
│ ▤ 收件箱                ③   │                                                    │  Git / 审查   │
│ ◷ 定时任务                   │                                                    │  终端         │
│ 项目                      +  │                                                    │  子代理 (2)   │
│ ▾ 🐙 NextCoWork              │                主工作区(标签页容器)                 │              │
│     ● 设计 Runtime   1 待处理 │                会话 / 终端 / 文件 / Diff / 浏览器     │              │
│     ○ 三栏外壳       2 小时前 │                                                    │              │
│     显示更多 (2)              │                                                    │              │
│ ▾ 工作                        │                                                    │              │
│   ▸ Oc Octopus            ②  │ ┌────────────────────────────────────────────────┐ │              │
│   ▸ Ag Agent · devbox        │ │ 输入消息,@ 引用文件,/ 命令          模型 ▾  ↑    │ │              │
├──────────────────────────────┤ └────────────────────────────────────────────────┘ │              │
│ (T) token · 已登录         ⚙ │                                                    │              │
└──────────────────────────────┴────────────────────────────────────────────────────┴──────────────┘
 左侧边栏 240–420,默认 280         主工作区                                            右侧检查器
 ⌘B 完全收起                       自适应                                              300–420,⌘⌥I
```

> **2026-11 重设计**:原来的「64px 项目栏 + 项目面板」两栏合成一栏**左侧边栏**(选型见 [mockups/sidebar-options.html](mockups/sidebar-options.html) 的方案 B,用户选定;
> 收起时**完全收起**,不留图标栏)。原因:跨项目的「哪里在等我、哪里在跑」只能靠图标角标看;最常用的「新会话」藏在分区标题后的 14px「+」;
> 项目面板头部的名字 + 路径 + 设备占掉约 110px;项目名的 ⌄ 和「终端和智能体」的 + 都没有接命令。

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
- **⌘B 完全收起左侧边栏**(不留图标栏)。收起后,红绿灯留白、名称和展开按钮并入标签栏最左边。
- 检查器收起时,标签栏右端给右上角常驻按钮让出位置。
- 平台差异只在 `Ncw.UI/Chrome/WindowChrome.cs` 一处判断。

### 左侧边栏(Sidebar)

从上到下:**头部**(品牌、收起按钮,见上)→ **全局入口** → **项目树** → **底部账户**。ViewModel 是 `SidebarViewModel`(只读 Store,意图转给已有的 Actions);
手机的「会话」标签仍用 `RailViewModel` / `ProjectPanelViewModel`,两边共用会话行的 ViewModel 和意图入口(`SessionRowIntents`)。

- **全局入口**(一行一个,图标 + 文字 + 右侧快捷键或数字):
  - **新会话 ⌘N**:在当前项目里新建(和 ⌘N 是同一个入口);没有项目时不可用。
  - **搜索 ⌘K**:打开命令面板(会话、项目、文件、命令、设置都在里面)。原来项目面板里只筛选会话标题的「在此项目中搜索」框去掉,⌘P 也改成打开命令面板。
  - **收件箱**:右侧是所有设备上「现在能处理」的待办数(`InboxViewModel.Current` 里 `IsActionable` 的条目,和 Dock 角标、⌘J 同一份,Core 的 `ActivityInboxView` 派生)。
    点开在这一行下面就地列出这些待办(图标 · 「工具审批 / 提问 / …」· 项目 · 会话),点一条 = `ShellViewModel.OpenAttention`(和通知点击、⌘J 同一个入口);没有待办时写「没有需要你处理的事项」。展开与否是纯 UI 状态。
  - **定时任务**:打开定时任务页(原来项目面板「自动化」分区里的唯一一项)。
- **项目树**(标题「项目」,右侧「+」= 新建项目窗口,和原来项目栏的「+」同一个意图 `ProjectActions.NewProjectAsync`):
  - 顺序和分组就是 `RailLayout`(和手机项目列表、⌘1…⌘9 同一份);项目组是一行可折叠的标题(折叠状态仍是 `ProjectGroup.Collapsed`,`LayoutActions.ToggleGroup`),折叠时标题右侧是组内待处理数之和(`ProjectActivityView.Sum`)。
  - **项目行**:展开箭头 · 小号项目图标(20px,外观规则同下文「项目图标」)· 名字 · 设备名(只在设备维度要露出来、且不是本机时写,规则见 §11.7)· 右侧状态:离线 = 断网图标;否则收起时显示待处理数(提醒色)或运行中的呼吸点(`ProjectActivityView`)。
    悬停时右侧换成「+」(在这个项目里新建会话)和「⋯」(项目菜单,和右键菜单相同)。
  - **点项目行**:不是当前项目 → 选中并展开;是当前项目 → 展开 / 收起。点箭头只展开 / 收起。用别的方式选中项目(⌘1…⌘9、⌃Tab、命令面板、通知)时自动展开它。展开集合是纯 UI 状态(不持久化),启动时只展开当前项目。
  - **展开后的会话**:不显示已归档的;排序 = 有待处理的 → 运行中的 → 收藏的 → 其余按最近活动(纯函数 `SidebarSessions.Arrange`,单测见 `Ncw.Client.Tests`);最多显示 4 条,多的收进「显示更多 (n)」,
    当前打开的会话即使排在后面也一定显示。会话行 = 状态点(运行中 / 未知)· 标题 · 收藏星 · 右侧(有待处理时是提醒色的「n 待处理」,否则是相对时间)。
    每个会话的待处理数来自 Core 的 `SessionActivityView.PendingOf`(`activeRuns` 广播里这个会话的 Run 的 `pendingInteractions` 之和;设备断开时为 0,和运行状态一样按规则 7 不当现状)。
    右键菜单不变:重命名 / 收藏 / 归档 / 删除(删除二次确认)。项目里一个会话都没有时写「还没有会话」。
  - **项目菜单**(⋯ 和右键):新建会话、新建终端(选中项目 → 打开检查器的「终端」页签 → 新建,和页签里的「+」同一个命令)、显示 / 隐藏已归档的会话(按项目的纯 UI 状态)、
    在文件管理器中显示 / 复制路径(只有「本机设备 + 本机环境」的项目有「显示」)、新建项目组 / 移出项目组、更改图标…、从侧边栏移除。「从侧边栏移除」先确认,再在项目所在的设备上注销(`projects.remove`,文件不动;这个项目的会话以后不再显示,重新添加同一个文件夹会是新项目),然后清掉项目栏布局、自定义图标和选中(`ProjectActions.RemoveAsync`)——只改本地布局的话,设备下次推送项目列表时会把它追加回来。设备没连上时不能移除;设备上已经没有的失效项目直接从布局里拿掉。
    项目行前的图标只在用户设过时显示(图片,或自己填的文字 / emoji,`RailProjectViewModel.HasIcon`);没设过不画图标,不再有默认的首字母色块(手机项目列表同样)。路径、分支不再常驻在侧边栏里:路径在菜单里,分支和工作区状态在检查器的 Git 页签。
  - **选中状态**:当前打开的会话行高亮;当前项目的名字加粗。
- **拖放**:规则仍是纯函数 `RailDropPlanner`(不改),视图只把指针换算成 `RailHit`——项目行(只算行本身,不含展开出来的会话)按行高的比例给出 `RailHit.Project`,项目组标题给出 `RailHit.Group`,项目树下方的空白是 `RailHit.Empty`。
  落点提示同原来:前后放置画一条横线,成组 / 进组给目标行描一圈。拖动数据仍是应用自定义格式的字符串 id(macOS 上不能用进程内格式)。
- **底部账户**:头像(规则同原来的项目栏头像:照片 / 首字母 / 未登录的人形图标、可用 / 过期角标)+ 名字 + 一行状态(邮箱或团队;未登录写「尚未登录」)+ 设置按钮。点头像或名字打开**账户卡片**(内容不变,浮在这一行上方)。
- **没有项目时**:项目树的位置显示原来项目面板的空状态(图标、说明、「添加文件夹」)。
- **收起**:⌘B 或头部按钮**完全收起**侧边栏(宽度 0,不留图标栏),工作区标签栏最左边出现名称和展开按钮(见上)。窄窗口(< 1024)仍然是抽屉。
- **宽度**:拖右边的分隔条,240–420px,默认 280px;右侧面板 260–520px,默认 320px。
  栏宽、栏的开关、右侧面板添加的页签是「本应用」的界面偏好,存在 `~/.next-cowork/client/ui.json`(`NCW_CLIENT_DIR` 可改),下次启动恢复;旧版本存下的宽度超出新范围时按新范围夹紧。
- **去掉的东西**:项目面板头部(名字、路径、设备、分支)、项目内搜索框、「终端和智能体」分区(它的列表一直只有 Mock 数据;真实终端在检查器的「终端」页签,入口是项目菜单的「新建终端」)、64px 项目栏本身。

#### 项目图标

侧边栏、手机项目列表、命令面板共用这一套外观(`RailProjectViewModel`):

- 自定义图片,或文字 / emoji 加颜色;没设过不画图标。项目名后面的状态见上;设备类型(云端 / SSH / 另一台电脑)在图标角上的小标记在 20px 下看不清,侧边栏改为写在名字后面。角标数据来自 `RunIndexStore`,和侧边栏、活动页读的是同一份。
- **「新建项目」窗口**(项目树的「+」、没有项目时的「添加文件夹」、首次引导走同一个意图 `ProjectActions.NewProjectAsync`):
  - **设备**:下拉框只列已连接、角色不是 viewer 的设备(`projects.add` 要 member 起),本机在前;默认是当前选中项目所在的设备,没有就本机。窗口开着时设备掉线,自动换到还能用的设备。
  - **文件夹路径**:本机设备旁边有「浏览…」(系统文件夹选择框只能选这台电脑上的文件夹);其他设备 / 云端手填那台设备上的绝对路径(协议里还没有浏览设备本机目录的方法,`environments.browse` 只给 SSH 环境用)。**名称**可选,缺省用文件夹名。
  - 提交走 `ProjectActions.AddAsync(deviceId, path, name)`(`projects.add`,带幂等键);成功后选中新项目、关窗口。失败**不关窗口**,按结局在窗口里给出原因(没填路径 / 设备没连接 / 文件夹不存在 / 没有权限 / 其他),设备给的英文诊断不直接给用户看。
  - SSH 环境里的项目、克隆仓库(`projects.add` 已支持 `environmentId` / `cloneUrl`)暂不在这个窗口里,以后作为「来源」加入。
- **「更改图标…」= 弹出「项目图标」窗口**:三种设置——无(默认,不画图标,预览是空占位)/ 文字或 emoji(最多 2 个字符,按文本元素截断,emoji 组合不会被截半)/ 图片;文字可选底色(同一组 8 色)。「保存」「恢复默认」经 `LayoutActions.SetAppearance` 写进 `LayoutStore`;选「无」不留外观记录(以前只存了底色的旧记录打开时当成「无」,保存即清掉)。
  - **图片压缩(尽量少占内存)**:先只读文件头(最多 512 KB)拿格式、尺寸、EXIF 方向,超预算直接拒绝、不解码——文件 ≤ 20 MB;PNG / GIF / BMP 要按原尺寸解码,≤ 2500 万像素;JPEG / WebP 的解码器能直接按缩小后的尺寸解码(JPEG 最多 1/8),≤ 2 亿像素。然后按「短边 = 96」解码、居中裁成 96×96、按 EXIF 方向摆正,立刻释放解码出的大图;没有透明像素存 JPEG(质量 85,通常几 KB),有透明像素才存 PNG。解码在线程池上做。
  - **显示**:每个图片图标常驻 96×96×4 ≈ 36 KB;转换器按字符串缓存(弱引用),侧边栏、手机项目列表、窗口预览共用一份位图;图片做成画刷铺在圆角方块里(跟着圆角裁切),下面垫中性底色,透明图标不会透出项目色。布局里超过 256 K 字符的图片当坏数据,不解码。
- 快捷键:`⌘1…⌘9` 切换到第 N 个项目(侧边栏里的顺序),`⌃Tab` 在最近的项目之间切换(全部快捷键见 §11.1,可在设置里改)。

### 栏 3 · 主工作区

- 顶部是标签栏,标签类型包括:会话、终端、文件、Diff、浏览器、设置。支持拖动排序。分屏暂不做(没有实现前不放入口)。
- 右上角:更多菜单 ⋯、检查器开关(◨)。更多菜单:当前会话的重命名 / 收藏 / 归档(和会话行右键菜单同一组意图,最终都到 `SessionActions`)、
  新建会话 / 命令面板 / 设置(走 `ShellViewModel.Execute`,和快捷键、命令面板同一个入口,行尾显示当前生效的快捷键)、最后是标红的删除会话;没有打开的会话时只有中间三项。上下文占用环(◔ 51%)在输入框的工具行里(§6「输入框版式」)。
- 会话页内的结构:消息流 + 底部输入框,组件见 [02 B1](02-agent.md#b1-组件清单桌面和手机共用通过密度变量适配)。

### 右侧面板(可选的第四栏)

宽屏时停靠在右侧,⌘⌥I 或右上角按钮开关。页签行就是窗口顶部这一行的右段(参考 Octopus):

| 页签 | 何时出现 | 内容 |
|---|---|---|
| **文件**(默认) | 总是存在,不能关闭 | 文件管理器:项目名 + 全部折叠 / 刷新 / 更多;搜索框(文件名 / 内容);**惰性展开**的文件树(展开时才 `files.list` 一层,排序是 Core 的 `FileEntrySort`,缺省不列忽略表里的条目,「显示被忽略的文件」打开后淡显);文件后显示 Git 状态字母,含变更的目录后面一个点;右键菜单(新建 / 重命名 / 删除进回收站 / 复制路径 / 在文件管理器中显示 / 用默认程序打开 / 另存为);点文件在**主工作区打开它的文件标签页**(预览与简易编辑在标签页里,见下) |
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
- **文件预览 / 编辑(文件标签页)**:点文件树 / 搜索结果里的文件,在主工作区标签栏新开一个文件标签(已打开就切过去),盖在会话上;标签上有未保存的修改时显示一个点,关闭前先确认。文件页签只负责「点了哪个」(`IFileTabHost`,由 `WorkspaceViewModel` 实现),重命名 / 删除时关掉对应标签。哪个文件标签在前台是按项目记的纯 UI 状态,选中了别的会话就让位给会话。预览自己实现:Markdown 默认渲染、可切源码(编辑时一定是源码),状态栏显示语言 / 行数 / 大小,「更多」菜单有复制路径 / 显示 / 用默认程序打开 / 另存为。内容规则:≤ 2 MiB 的 UTF-8 文本一次读完,`Arc.CodeBlock` 只读着色显示;编辑用多行输入框(没有 Arc 的代码编辑组件),保存带读到的 `Revision`,对不上(`files.conflict`)时不丢编辑内容,让用户选「重新载入」或「保留我的修改」;更大的文本按 256 KiB 一块 `files.readRange` 读,只读;二进制 / 非 UTF-8 / 过大只提示,可「另存为」;图片(≤ 16 MiB)显示。「在文件管理器中显示 / 用默认程序打开 / 另存为 / 复制路径」是客户端本机的动作(`ILocalFiles`),只对「本机设备 + 本机环境」的项目开放「显示 / 打开」。
- **终端**:本机和 SSH 项目同一条握手路径。键盘输入「没有写在途就立刻发,写在途期间的输入合成下一个命令」(`TerminalInputPump`,单次 ≤ 64 KiB,写失败丢弃攒着的输入而不是重放),大小变化只发最新的(`TerminalResizePump`)。设备断开时标签是「未知」(不判成结束、不接受输入、没有计时),重连后由连接层按游标续传(补发错过的批,续不上就整个缓冲重置显示);已退出的终端终态压过一切。
- **子代理转录**:点开才订阅子会话(子 Run 还在跑时再订阅 `run/<childRunId>`),行由和主对话同一个 `TranscriptBuilder` 从 Store 派生;只靠回执重建的旧卡片(没有子会话 id)点不开,给明确的提示而不是静默没反应。
- **输入区**:`@` 弹出项目文件候选(`files.suggest`,候选里已选的不再出现),选中后变成输入框上方的文件引用 chip,发送时作为 `InputPartKinds.File` 块(项目相对路径)和正文一起发出(排队 / 插话同样);`/` 弹出内置的本地动作 + 设备上的命令清单(`commands.list`,带说明;键盘上下移动高亮,Tab 补全,Enter 补全——已经打全了命令名时 Enter 直接执行);以 `/` 开头的草稿发送前先展开(Core 的 `CommandExpansion`,失败不静默地当普通文本发出)。
  **内置本地动作**(`ComposerSlash`,2.x `Composer.tsx` 的 `localActions`):它们改的是会话状态或客户端选择,提示词展开表达不了,所以在提交时被拦下、**从不**作为文本发给模型(运行中也不进队列);和内置同名的磁盘命令不列出(执行不到)。
  `/goal <完成条件>` → `goal.set`(`GoalActions`;条件规则同 Core 的 `GoalConditions`,清除词 `/goal clear|stop|off…` → `goal.clear`,只敲 `/goal` 给用法、草稿留成 `/goal `);
  `/compact [侧重点]` → `context.compact`(`ConversationActions.CompactAsync`,只能在空闲时;进行中输入框显示「正在压缩」并禁止提交;边界经 `history.reset` 回到转录;`nothingToCompact` 只是告知);
  `/model [名称]` → 精确匹配显示名 / 别名 / `供应商/别名`,再唯一子串,有歧义不猜;切换走和模型菜单同一个意图(`PickModel` → `sessions.setModel`);不带名字打开模型菜单;
  `/clear` → 在当前项目开一个新会话(⌘N 同一个意图,旧会话原样保留)。结果提示是结构化的 `SlashNotice`(界面翻译);被拒绝时草稿还给用户。
- **窄窗口**:检查器在 ≥ 1440 的宽度停靠(偏好 `IsInspectorOpen`);< 1440(1024–1440 和更窄)时右上角的检查器开关 / ⌘⌥I 打开的是**右侧抽屉**:盖在工作区上、靠右、宽度同检查器宽度,有遮罩(点遮罩关闭,和左侧抽屉共用一个遮罩),带滑入 / 滑出动效(§9)。抽屉开着与否是纯 UI 状态(`ShellViewModel.IsInspectorDrawerOpen`,不持久化,不改停靠偏好;窗口宽到能停靠时自动收掉)。

## 2. 响应式断点

| 窗口宽度 | 布局 |
|---|---|
| ≥ 1440 | 左侧边栏 + 主工作区 + 停靠的检查器 |
| 1024–1440 | 左侧边栏 + 主工作区;检查器以抽屉形式覆盖 |
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
- 窗口:Arc 的 `Window`,自定义标题栏。macOS 上保留红绿灯按钮,和左侧边栏头部对齐。
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
- **实现现状(2026-11)**:`ProjectAppearance(Glyph, Color, Image)`——`Glyph` 是自定义文字(null 且没有图片 = 不画图标),`Image` 是压缩好的 96×96 图片的 base64(见上文「更改图标…」);别名(`Alias`)还没做。账户配置同步在后端到位前是关闭的,所以项目外观**先在本地持久化**:`LayoutPersistence` 把 `LayoutState.Appearance` 存进客户端目录的 `layout.json`(先写临时文件再改名,只在外观变了时合并写回;读到缺 id / 颜色的条目丢弃,过长的图片当作没有)。侧边栏里项目的顺序和分组还没有本地持久化(重启后由设备的项目列表重建),是已知缺口。
- 布局里引用的项目如果在设备上已经不存在,显示为「已失效」,由用户决定移除,不要静默删掉。

## 6. 会话区与输入框(M4 实现说明)

侧边栏的会话列表、主工作区的会话内容、输入框已经接上真实数据(协议见 [09](09-protocol.md) §8.4)。要点和与上文设计的差异:

- **会话列表**是 `sessions.list` 的镜像(`WorkspaceState.Sessions`,含归档 / 收藏标记)。设备没有「会话列表」主题,所以连接在三处重拉某个项目的列表:连上后、`activeRuns` 里有 Run 开始或结束(新会话、自动命名、更新时间)、自己的重命名 / 归档 / 收藏 / 删除之后;已订阅的会话的 `session.updated` / `session.deleted` 直接合并进列表。
  过滤和排序是纯函数 `SessionListQuery`:归档默认隐藏(「显示已归档」才出现,排最后)、收藏置顶、其余按更新时间倒序,搜索按标题忽略大小写子串。
- **运行状态点**只来自 `activeRuns`(Core 的 `SessionActivityView`:运行中 / 空闲 / 未知)。设备断开时是「未知」(旋转图标),既不显示运行也不显示结束,标签页、列表行、对话区共用这一个函数。
  **差异**:`activeRuns` 里没有 Run 的开始时间,所以会话行暂不显示运行计时(上文 §1「左侧边栏」);等广播带上 `startedAt` 再补。
- **转录**由纯函数 `TranscriptBuilder` 从 Store 派生(消息、流式增量、工具、子代理、挂起的交互、Run 状态),每一行有稳定的 key,视图模型按 key 对齐、原地更新,展开 / 折叠这类纯 UI 状态不会因为数据更新而丢。
  渲染链路:Store 变化 → `ConversationViewModel.Refresh` → 输入指纹没变就到此为止 → `TranscriptBuilder.Build`(整份重新派生)→ `Reconcile`(按 key 原地更新 / 移动 / 插入 / 删除行视图模型)→ `ItemsControl` 按行类型选 `DataTemplate`,
  面板 `NcwTranscriptPanel` 只为视口附近的行实例化控件(容器回收复用)。
  - **派生的开销**:① `TranscriptBuilder.Inputs`——这份转录读到的全部输入(会话镜像、它的各个 Run、子代理表、这台设备的交互、兜底环境、断线标记)按引用比较,没变就不派生也不对齐
    (别的会话在流式、会话列表 / 设备 / 模型候选变化都会触发刷新,和这份转录无关);子代理计时的每秒刷新强制派生(时间不在指纹里)。
    ② 解析结果按不可变字符串实例缓存:消息内容 JSON(原有)、工具入参的分类与目标、摘要(另按输出字符串;命令在跑时输出会变)、可视化入参里的标题与加载提示——此前流式时每个增量都把整个会话里每一次工具调用的入参 JSON 重新解析、命令输出重新切行。
    ③ `Reconcile` 先删掉已经不在的行再对齐:中间少一行只是一次删除通知,不再让后面每一行逐个「移动」一格(n 次移动通知、每次 O(n) 查找)。
    仍是「整份重新派生」(O(消息数) 的遍历与记录分配,不再有解析);按消息增量派生要把工具结果 / 审批 / 子代理这些跨消息的依赖拆开,收益不值得这份复杂度,没做。
  - **虚拟化面板 `NcwTranscriptPanel`**(替代 Avalonia 的 `VirtualizingStackPanel`):Avalonia 12.1.5 的面板把每个没实例化的行都估成「当前实例化的那几行的平均高度」,量过的高度也不记——
    视口里是一串 26px 的工具行时,上面几百 px 的回答也按 26px 算,总高度短得离谱:滚动条滑块过大、拖动乱跳、往上翻时内容「长出来」把位置顶走。新面板:
    记住每一行量过的高度(按行视图模型的引用 + 宽度);没量过的按内容估(`TranscriptHeights`:Markdown 按字数 × 字宽 / 宽度估行数、代码块按行数、工具行一行、用户气泡按 560 宽……),
    再按行类型用「真实 / 估计」的累计比值校准(0.5–2.5),估不出的用同类行的平均高度;每一行的位置都来自同一份前缀和,视口上方的行量出真实高度后位置的变化由 `ScrollContentPresenter` 的滚动锚定补偿;
    只实例化视口上下各 0.75 屏;容器按「回收键 + 行类型」进池;带键盘焦点的容器(行内编辑)滚出去也不回收。宽度变化时估计值重算。
  - **流式 Markdown**(`NcwStreamingMarkdown`):助手回复流式中只把新增的一截推给 Arc `Markdown` 的流(`Append`,StreamMark 增量解析尾部),落库时文字仍是延续就 `Complete` 原地收尾、不重载;
    文字整个换了(容器回收给另一行、编辑后)才设 `Text`。此前每个增量都把整段重新赋给 `Text`,整篇重新解析。打字机节奏只在从头开始流(已有文字 ≤ 160 字)时打开,中途接上的不把已有整段再「打」一遍。
  工具卡片和审批卡片都带**执行环境标识**(设备 · 环境):实时事件带的优先,等待审批的用审批带的,落库的旧调用用项目所在环境兜底。
  `tool_start` 的环境只带 id(设备 + 环境,名字和类型为空,Agent 内核的 `EnvironmentTag` 只有 id):`TranscriptBuilder.ResolveEnv` 补齐——是项目所在的环境就用项目环境,别的环境用 id 当名字、类型保持未知(照样画)。
  此前空名字 / 空类型直接进了行:本机的行也画出了标识,文字是「本机 · 」(悬空的点)。文字与「要不要画在行上」只有一份规则 `EnvBadge`(工具行与可视化卡片共用)。
- **思考块**:只有透传载体、没有文字的思考块(redacted / 加密推理条目,有的上游每一步都发一个)**不进转录**——此前每一个都画成一行「思考了 0 秒」、展开是空的。
  标题三选一:流式中「思考中…」、有耗时「思考了 N 秒」(耗时由 Agent 记,见 [08.01](08-agent-runtime/01-llm.md))、量不出耗时(旧消息)只写「思考」。
  **展开后的正文**(`NcwThinkingView`,2026-11):思考可能有几万字而且是流式长出来的,原来一个 `SelectableTextBlock` 装全文,每个增量整段重新排版(长会话里展开着流 16 KB 时每帧 40 多毫秒,随长度线性变慢)。现在:
  - **限高** 240(`NcwThinkingView.MaxBodyHeight`),超出的在框里滚动;转录的高度估算同样封顶(`TranscriptHeights`,只量前 4000 字);
  - **只建看得见的**:正文按换行切成段落块(超过 1000 字的段落在附近的空白 / 标点处再切,切点多一个换行),放进虚拟化列表;
  - **增量**:新文字是旧文字的延续时只重切最后一块(替换最后一项、追加新项),前面的块不动;折叠着(看不见、不测量)时不切块,展开时一次补上;
  - **跟随**:流式中停在最新内容;用户往上翻(滚动位置往上变)就不再跟,翻回底部恢复;已结束的思考展开时从头显示。
    跟随看滚动位置本身的变化而不是 `ScrollChanged`(后者在布局后合并发出,用户往上翻和同一轮里的跟随会合成一个事件);虚拟化列表修正范围时也重新贴底。
  - 每段是单独的可选中文字,选中不能跨段(思考是辅助信息,可以接受)。
  测试:`Ncw.UI.Tests/ThinkingViewTests.cs`。
- **跟随最新**(`TranscriptFollow`,纯状态机,单测直接驱动;`ConversationView` 只接线):**只认用户意图,不从偏移变化里猜**。
  虚拟化列表按估算高度排布,行被实例化后总高度和偏移都会自己漂(往上、往下,甚至漂到底),WebView 卡片报高、展开动画也一样;旧实现把「偏移往上变了」当成用户往上翻,于是会无故停止跟随,或在用户翻看时把视图拽回底部。现在:
  - 停止跟随:用户往上滚(滚轮 / 触控板向上且还没到顶、PageUp / ↑ / Home、拖滚动条往上)、展开一行、定位到某一行。
  - 恢复跟随:用户**自己**滚到底(在一次滚动操作之后 350ms 窗口内到达底部,触控板惯性每个事件都续期;拖滚动条松手时在底部)、点「回到最新」、换会话(同一个视图模型里换会话也算)。程序和布局造成的「到底」不算。
  - 跟随中:滚动容器每一轮布局之后不在底部就贴到底(`LayoutUpdated`,布局结束、渲染之前完成;旧实现投递到下一轮再滚,会先画出错位的一帧再跳)。贴底会让虚拟化列表实例化底部的行、修正估算高度,所以一次连续布局里最多追 12 轮,追不上就下一帧再来。
  - 「回到最新」按钮:不跟随且内容超过一屏时显示;显示期间转录的滚动容器声明底部 inset,WebView 小组件让出这一条(§8.5)。
- **消息工具条**(桌面悬停显示,手机 `NcwDensity.IsTouch` 常驻;编辑 / 确认删除时钉住):
  - **用户消息**:时间、复制、编辑、从这里分支、删除这一轮;**AI 回复**:复制、重新生成(只在最近一轮的最后一段上)、从这里分支、删除回复(流式中整条工具条不显示)。复制是本机剪贴板的纯 UI 动作,不经设备。
  - **AI 回复的分支和删除按「一轮」算**(`MessageTurns`,纯函数):一轮 = 一条可见提问 + 它引出的全部助手消息和工具回执,直到下一条可见提问(只有工具回执的「用户」消息、压缩边界、后台汇报都不算提问)。一次回复在 Agent 循环里常常是好几条助手消息(文字、工具调用、再文字),所以**删除回复 = 删这一轮 AI 的全部回复、保留提问**(`sessions.deleteReply`,`fromId..toId` 取这一轮第一条到最后一条助手消息,设备端再吃掉紧随其后的工具回执),同样原位二次确认;**从这里分支 = 带到这一轮结束为止**(`sessions.branch` 按「引出这一轮的提问」切),不会在一轮中间切断。点到的是不属于任何一轮的回复(之前没有可见提问)时没有分支按钮。
  - **编辑**是行内的:「取消 / 保存 / 保存并重新运行」。保存 = `sessions.editMessage`(`truncate:false`,只改文字,后面的回复保留);保存并重新运行 = `truncate:true`(这条和之后的全部移除)再用新文字发起一次新的 Run(和发送是同一个入口,档位取输入框当前的)。截断成功而发送失败时,历史已经改了,**文字放回输入框**并提示,不让用户丢掉它。
  - **删除这一轮**要原位二次确认(`sessions.deleteTurn`,提问 + 全部回复与工具回执)。**分支** = `sessions.branch`(到这一轮为止的转录,独立的顶层会话),成功后刷新会话列表并打开它。
  - 意图在 `Ncw.Client/Actions/MessageActions`(`IMessageCommands`,一个意图一个方法);改写由设备执行,之后设备广播 `history.reset`,转录跟着 Store 重新派生。
  - **什么时候不能用**(`ConversationViewModel.CanEditHistory`):会话有 Run 在跑(设备会回 `busy` 拒绝改写,这里直接禁用并在提示里说明原因)、设备断开(状态未知,规则 7)、只读角色。失败是稳定 key(`messages.busy` / `unavailable` / `forbidden` / `gone` / `failed` / `resendFailed`),界面翻译成错误条,可以关掉。
  - **重新生成**:AI 回复的工具条上,只出现在**最近一轮的最后一段、已写完**的回复上(`MarkRegenerable`;更早的回复后面还有对话,重跑会把后面全部截掉)。它和「编辑后重新运行」是同一个入口,提问的文字和附件原样重发,旧回复被截断移除;重发失败时历史已经截断,原提问放回输入框。
  - **重跑带上原附件**:编辑后重新运行 / 重新生成要用同样的输入重发,所以从原消息还原附件(`MessageInputs.FilesOf`:图片 → 附件引用,`file_ref` → 项目文件引用)。Agent 侧为此改了一处:**为重发而截断时**(`sessions.editMessage` 的 `truncate:true`),被移除消息的会话附件**降回草稿**而不是随消息级联删除(`IMessageWriteObserver.OnMessagesRemoving`),文件和 `ncw://` 引用都保留,新消息提交时重新认领;删除一轮 / 只改文字不受影响。「引用别的消息附件」的 `:managed:` 行直接删。
  - **图片缩略图与灯箱**:用户消息里的图片显示 112px 缩略图(`UserRowViewModel.Images`),**行滚进视野才向设备取字节**(`AttachedToVisualTree` → `ImageThumbViewModel.EnsureLoadedAsync`,虚拟化列表只实例化视野附近的行);引用是 `data:` URL(本机解码)或 `ncw://` 附件(`attachments.read` 分块,单张 ≤ 32 MiB,成功的结果按设备 + 引用缓存最多 32 张,失败不缓存)。加载中转圈,失败显示「无法加载图片」并可点击重试。点缩略图进灯箱(盖住对话区,点空白 / 关闭 / Esc 关,打开期间 WebView 小组件让出空域)。解码用 `Bitmap.DecodeToWidth`(缩略图 320、灯箱 1600),不把原图整张解成大位图。字节不进 Store;`Ncw.Client` 不依赖 Avalonia,位图转换在 `ImageBytesConverter`。取不到引用时(旧快照)退化成「N 张图片」。
  - **没做**:回滚到这一轮(`review.undo`,要先定「只回代码 / 只回对话」的交互)、灯箱里的「另存为 / 复制图片」、助手回复里生成图片的显示。
- **输入框**的状态机是纯函数 `ComposerLogic`:空闲时发送;运行中有文字则排队(`queue.enqueue`)或「立即插话」(`runs.steer`),没文字则停止;已排队的消息可以逐条「插话」(`queue.promote` 开关)或移除;
  设备断开 / 只读角色 / 没有模型时不能发送。发送失败不丢草稿。模型候选来自设备算好的 `providers.models.list`(含上下文窗口、图片 / 思考能力,停用的显示为不可用),
  权限档位 `full` 只有 owner 可选,思考档位随模型声明的强度收窄。`@` 文件引用出 `files.suggest` 的候选;`/` 斜杠命令见上文「输入区」(内置本地动作 + 设备命令清单)。
  - **模型的记忆**(2026-11 修复「选了模型不持久化、重开总是第一个」):真源是会话上的 `Model` / `ModelProviderId`(设备)。
    选模型 = `ConversationActions.SetModelAsync` 一个意图:`sessions.setModel` 记在会话上;owner 还顺带 `settings.project.update` 写成这个项目新会话的默认模型
    (2.x 的「药丸写回工作区默认值」;不是 owner 或写失败只记在会话上,不打断用户)。新建会话没指定模型时,设备按**项目默认 → 设备默认**整对取(和定时任务同一条规则),都没配留空。
    输入框显示哪个:会话上记住的变了(换会话、快照到、写回推回来、别的控制端改了)就跟它走;没变就保留刚才的选择(写回在路上 / 失败);上一个会话的选择不带到下一个会话;
    都没有才兜底第一个可用的。**兜底结果不写成项目默认值**(2.x 同样),但会话上还没记模型时发出的第一条会把真正用的那个记在会话上。
  - **权限档位的记忆与跟随**(2026-11 修复「右下角权限选择不持久化、不跟随会话与项目」):真源是会话上的 `PermissionMode`(`sessions.json` 里的 `permissionMode`,缺省 `ask`,不动 schema)。
    选权限档位 = `ConversationActions.SetPermissionModeAsync` 一个意图:`sessions.setPermissionMode` 记在会话上并同步重打排队里未消费消息的权限标签;
    若当前有正在跑的顶层 Run 且是 owner,顺带调 `runs.setPermissionMode` 切实时 Run 的档位;owner 还顺带 `settings.project.update` 写成这个项目新会话的默认权限档位。
    新建会话没指定权限档位时,设备按**项目默认 → 设备默认**继承(member 新建会话时若继承到 `full` 则夹紧为 `ask`)。输入框切换会话或收到推送时按会话记住的 `PermissionMode` 同步并按角色夹紧。
- **输入框版式**(2026-11 重设计,方案 A「精修单卡」,原型 `docs/design/mockups/composer-options.html`):
  - 一张卡片:书写区是 Arc 的 `TokenTextEditor`(封装 `NcwTokenEditor`,它不画自己的框),只留卡片的描边;焦点在卡片内任何位置时整张卡片描边加深(`:focus-within`)。
  - **文件引用和命令是草稿里的 token**:选中 `@` 候选,草稿末尾的 `@查询` 换成 `@文件名 `(和已引用的文件重名时写项目相对路径),开头的已知 `/命令 ` 也是 token;
    方向键跳过整个 token,一次退格删掉整个 token。哪些片段是 token 只有一条规则(`ComposerLogic.Mentions`),编辑器的解析和「这个文件还在不在引用里」都用它:
    草稿里没有了 `@名字`,引用就跟着去掉(不再有输入框上方的文件 chip)。发出去的正文里保留 `@名字`,文件照旧作为文件块一起发出。
  - 不用 Arc 的 `AiComposer`(完整的提示框 block):它的发送键在生成时只能停止,没有本应用的排队 / 插话;没有权限档位;附件走它自己的上传;`@` 候选是静态列表,不能边打边向设备查。只用它底下的 `TokenTextEditor`。
  - 工具行左侧:附件 | 分隔线 | 模型 | 思考;右侧:权限、上下文圆环、插话、主按钮(圆形的发送 / 排队 / 停止)。左侧用一行网格,不换行,模型名过长截断。
  - 模型:触发器是供应商首字母方块 + 模型名;菜单按供应商分组(组标签),每行右侧是安静的说明「上下文窗口 · 视觉 · 思考」,不可用的灰显并注明。
    没有搜索框,靠菜单自带的首字母跳转(Arc `DropdownMenu` 没有搜索头;候选多到需要搜索时再换成自建弹层)。
  - 思考:脑图标 + 六格刻度条(关 = 0 格、最大 = 6 格,「自动」用强调色画 3 格)+ 档位短名;模型不支持思考时整个按钮灰掉。
  - 权限:放到右侧并按档位着色——询问是普通文字、自动是成功色、完全访问是警告色,图标分别是 ShieldQuestion / ShieldCheck / ShieldAlert;菜单行右侧是档位说明。
  - 上下文占用从标签栏(桌面)和导航栏(手机)**挪到输入框**里,只在这一处显示:圆环 + 百分比,数据仍是 `ConversationViewModel.ContextPercent`(最近一次 `context_usage`)。
    点圆环弹出 Arc 的上下文窗口卡片(`TKCode.Arc.Special` 的 `ContextWindow`,封装 `NcwContextCard`):用 `context_usage` 的分段画满 200 格
    (system → 系统提示、tools → 工具定义、instructions → 「项目说明与提醒」那一档、messages → 对话),窗口取事件里的 `Window`,留给回复的部分和虚线(自动压缩阈值)
    用 Core 的 `ContextWindows.ResolveMaxOutputTokens` / `AutoCompactThreshold`,和 Agent 真正触发压缩的判据是同一个。卡片**只展示**:Arc 自带的发送 / 附加 / 折叠演示全部关掉
    (不给回合与文件、`AutoCompact = false`、保留消息数设成最大),「立即压缩」因此是灰的——真正的压缩只由 Agent 做。
    卡片放在 Avalonia `Popup` 里(浮在输入框卡片上方、右对齐、与卡片同宽、最宽 744,外面包一层 `LayoutTransformControl` 整体缩放到 0.8(占位同步缩小;不能靠缩窄卡片:Arc 只在接近 744 宽时才把图例放到格子右边,更窄会上下堆叠、反而更高),点外面关闭),不用 Arc 的 `Popover`:它的面板最宽 22rem,还会在卡片外面再套一层框。
  - `/` 命令和 `@` 文件候选**浮在卡片上方**(Avalonia `Popup`,覆盖层,和卡片同宽、左对齐),不塞进卡片把它撑高;弹层不抢焦点、不点外面关闭,开关只跟草稿走,键盘(上下 / Enter / Tab)仍在输入框里。命令列各行同宽,说明对齐。
  - 紧凑版(手机 / 宽度 < 480):思考和权限只留图标(权限图标照样着色),模型名截短,上下文只留圆环。
- **交互卡片**(工具审批 / 提问 / 计划批准 / 目标提案)跟着 `interactions` 全量主题;回答带着看到的版本号。被另一台设备先处理时,卡片消失,转录底部提示「已在另一台设备上处理」(谁、什么时候);版本对不上的卡片保留并提示重新确认。
  **差异**:计划批准目前只有「批准 / 要求修改 / 驳回」,「在新会话中批准执行」随计划执行入口一起做。
- **标签栏**现在有会话标签和文件标签(打开了哪些标签是按项目记的纯 UI 状态);终端、Diff 标签随各自功能加入。
- **工具调用行**:转录里的工具调用不是卡片,是一行文字,严格单行(图标 | 工具名:至少 44、至多 160,常见短名各行对齐,`visualize_read_me` / MCP 工具这类长名不被截成「visuali…」| 目标:路径是文件名在前、目录淡色在后,命令取第一行 | 改动量 · 状态 · 环境 · 箭头,靠右)。
  目标是 `*` 列里的**一个** `TextBlock`(文件名与目录是两个 `Run`,整体截断):此前文件名放在嵌套网格的 `Auto` 列,按内容宽度量,长命令会画到右侧的状态 / 环境标识上面(两段文字叠在一起)。执行环境标识(AGENTS.md §5)只在**非本机环境**(SSH 等)的行上画;默认的本机环境不画(每行都写「本机 · local」是噪音),它在整行的悬停提示里。结果一律默认收起(包括失败的,失败只把名称和目标标红),有摘要的才有展开箭头,点开在左侧一条线的缩进块里看。
- **「工作区」分组**(参考 Codex 的「已处理」折叠):一轮(一条用户消息到下一条)**全部完成**之后,除最后一段回复文字以外的操作——思考、工具调用、子代理卡片与汇报、中间的文字——收成一行「工作区 · N 个操作(· M 个失败)」,默认收起,点开在下面平铺出成员(缩进一格)。
  - **派生在 `TranscriptBuilder`**(`groupFinishedWork: true`,只有主对话打开;子代理转录不分组):表头是 `WorkGroupRow`(`Members` / `Actions` / `Failed` 三个数,Client 不产出界面文字),它后面紧跟 `Members` 行就是成员。转录**仍然是平铺的**——面板的行高估算、跟随最新、容器回收都按平铺行工作,不做嵌套。
  - **只在「写完了」时收**:这一轮必须以一段写完的回复文字收尾(停在工具调用上、被中止、还在流的都原样保留,什么都不藏);整个会话没有运行中的 Run(快照里的 `ActiveRunId` 在 Store 里已是终态的不算)、没有待处理的交互、设备没断开;这一轮里还有在跑 / 等审批的工具或还在跑的子代理就整轮不收。成员以外的行(可视化卡片、错误、目标状态、压缩边界)不收,保持原有相对顺序、排在这一组后面。
  - **展开与否是纯 UI 状态**(`ConversationViewModel` 按表头 key 记,不进 Store):收起时成员行**不创建视图模型**(长对话更省),展开时才创建、并标 `InWorkGroup` 让界面缩进。
  - 成员收起后不能让依赖它们的派生消失:「这个会话里改过文件」(检查器自动出现「审查」页签)从完整的行算,不从可见的视图模型算。
  - **差异(相对 Codex)**:表头不显示耗时(转录行里没有可靠的耗时,不画假数字);分组只有一层,不按子任务再分。
- **跟随最新与展开**:用户展开转录里的一行(工具、思考块)时暂停跟随,否则展开让内容变高会触发「内容变高就贴底」把视图拽到最底;暂停持续到用户自己往下滚到底或点「回到最新」(展开动画一开始只高一点点,不能靠「在贴底容差内」恢复跟随)。收起不暂停。

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
- 窄屏(360dp)输入框的工具行以前会换行(权限图标掉到第二行);2026-11 的输入框版式(§6)把工具行改成不换行的网格,权限挪到右侧,这个问题随之消失。

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
- `Ncw.UI.Tests.MobileScreenshotTests`:截图在 `tests/Ncw.UI.Tests/bin/Debug/net11.0/screenshots/mobile-*.png`,断言外壳按宽度切换、页面结构、长按进度、触控版式、导航栈同步。

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
| | `wheel` | `{dy}`:页面没消费的滚轮(CSS 像素,正 = 往下;3.0 新增,见 §8.5「滚轮」) |
| 宿主 → 页面(`__ncwHost.recv`) | `content` / `theme` / `loading` / `env` / `reply` / `viewport` | 内容、主题 token、加载提示、环境(状态 / 数据 / 交互 / 已提交 / 动作清单)、调用结果、可见段起点(3.0 新增,见 §8.5「裁剪」) |

`window.ncw`(冻结):`getState()` / `setState(v)`(↔ `widgets.getState` / `setState`)、`submit(v)`(↔ `widgets.submit`,仅 `interactive` 卡)、`runAction(id, params)`(↔ `widgets.runAction`,先弹确认框)、`openUrl(url)`、`getData()` / `onData(cb)`(↔ `visualize_update_widget`)、`actions()` / `isInteractive()` / `isSubmitted()` / `title()`。**没有 `sendPrompt()`**(同 2.x)。调用返回 Promise,失败 reject 一个带稳定英文码的 `Error`:`offline` / `unavailable` / `closed` / `not_interactive` / `already_submitted` / `declined` / `rate_limited` / `forbidden` / `invalid` / `unsupported` / `failed` / 校验码(`bad-args` / `unknown-method` / `state-too-large`……)/ 设备给的 `widgets.action.*`。

方法与上限(`WidgetLimits`,取自 2.x 的 `WIDGET_LIMITS`):单条消息 256K 字符;`setState` 16K;`submit` 32K;动作 id 64、参数 ≤ 16 个且只能是有界字符串 / 数字 / 布尔、总计 4K;URL 4K,只许 http(s)、不带用户信息、无控制字符;JSON 深度 ≤ 32;高度钳制 24–4000(2.x 是 1400,放宽的原因见 §8.5「裁剪」)。

### 8.5 安全

页面里跑的是模型写的代码,**页面发出的一切都当成不可信输入**。四道防线:

1. **服务端消毒**(`Ncw.Agent.Widgets`,已有)。
2. **外壳的 CSP**(`WidgetShell.Csp`):`default-src 'none'`;`script-src 'nonce-…'` + CDN 白名单(**没有** `unsafe-inline` / `unsafe-eval`);`style-src 'unsafe-inline'` + CDN;`img-src data: blob:`;`font-src data:` + CDN;`connect-src 'none'`;`frame-src` / `object-src` / `base-uri` / `form-action` 全 `'none'`。CDN 白名单与服务端 `WidgetUrls.CdnHosts` **同一张表**(`cdn.jsdelivr.net` / `unpkg.com` / `cdnjs.cloudflare.com` / `esm.sh`,`WidgetHostTests` 核对)。**比 2.x 更严两处**:`img-src` 不放 `https:`(服务端本来就拿掉外链图片,这里不比服务端宽),`connect-src` 默认全拒(2.x 放了 CDN)。
3. **导航与弹窗全拦**(`WidgetNavigationPolicy`):放行的只有外壳自己的加载(`ExpectShellLoad` 到页面「就绪」之间,此时 widget 的代码还没被推进来)和同一页内的锚点;之后任何导航取消,http(s) 目标走「系统浏览器 + 确认框(显示真实地址)」,别的方案(`javascript:` / `file:` / `data:` / `mailto:`)直接丢弃;新窗口永远不开。
4. **桥的白名单与限速**(`WidgetBridge`):只认已知消息类型和四个方法;全局令牌桶(突发 120、60/s)+ 调用令牌桶(突发 12、6/s);累计 100 次违规就**锁死**(之后一切消息丢弃);同一时刻只有一个确认框;动作的「要跑什么」由 Agent 从转录里取,页面只能选 id 和受约束的参数;推给页面的环境里**没有**动作的工具与入参。

不暴露任何本机文件 / 进程能力:桥只有上面四个方法,没有 `download` / `openFile` / `copy` / `complete`(2.x 里这些是渲染宿主的能力,3.0 没有做,见 §8.8)。**与 2.x 的隔离模型不同**:2.x 是不透明源的 `<iframe sandbox>` 嵌在特权渲染进程里;3.0 一个 widget 就是**一个独立的 WebView 页面**(非持久数据存储、没有任何控制端状态、没有 `file://`、没有 cookie 落盘),页面里的原生桥函数(`invokeCSharpAction`)任何脚本都能调——所以桥一侧不信任何东西,而不是靠嵌套 iframe 把脚本和桥隔开。

**空域**:`NativeWebView` 是盖在窗口上的原生视图,Avalonia 画的覆盖层盖不住它。凡是盖在对话区上面的东西打开时调 `NcwWidgetAirspace.Suspend()`,所有 widget 的 WebView 随之隐藏(页面不销毁、状态保留)——桥的确认框、设置页 / 首次启动页 / 数据页、窄屏抽屉的遮罩(`DesktopShell` 监视它们的 `IsVisible`)。同时最多 8 个 WebView(虚拟化列表只实例化屏幕附近的行,这是兜底),超出的先画快照。

**裁剪**(2026-11 修复,真机截图里小组件盖住了输入框和「回到最新」):macOS / Linux 上 `NativeWebView` 是 `NativeControlHost`(只有 Windows 的 WebView2 有合成器路径),
Avalonia 12.1.5 的 `NativeControlHost.GetAbsoluteBounds()` 按控件的**完整**绝对边界摆放原生视图,**不认任何祖先的裁剪**——放在转录的 `ScrollViewer` 里,滚出视口的部分照样画在输入框、标签栏、「回到最新」上面。处理:
- `NcwWidgetSurface` 在布局里占卡片的完整高度(列表高度、滚动都按它算),但只把 WebView 排布在**可见段**上:和所有裁剪祖先(`ClipToBounds`,如 `ScrollContentPresenter`;以及窗口)的交集,
  再减去祖先上声明的底部 inset(`NcwWidgetClip.BottomInset`:「回到最新」显示时转录的滚动容器声明它的高度)。可见段在每一轮布局之后重算(`LayoutUpdated`;滚动只重排祖先,不会重排本控件),布局结束、渲染之前直接 `Arrange` WebView,`NativeControlHost` 在 AfterRender 读到的就是新位置。
- 页面据此把自己滚到可见段的起点:新消息 `ncw:widget:viewport {top}`(宿主 → 页面,3.0 新增,`WidgetMessages.Viewport`);外壳 `html, body { overflow: hidden }`,页面自己不滚(脚本滚动不受影响),滚轮也就不会被困在小组件里。
  报高量的是 `#ncw-root` 的下沿 + `scrollY`,和视口大小、滚动位置无关。因此高度上限从 2.x 的 1400(超出在 WebView 里滚)放宽到 4000(只挡失控的上报,超出部分不显示)。
- 完全看不见、或**横向**被裁(侧栏滑动时;页面宽度跟着 WebView,横向裁剪会让内容重排)就整个隐藏(页面不销毁)。
- 卡片上次报上来的高度按「设备 + 会话 + 调用」记住(最多 256 张):虚拟化把卡片滚出去会销毁 WebView,滚回来重建时直接用上次的高度,不再先占 120 再跳(那一跳会带着列表总高度和滚动位置一起跳)。
- **未在真机上验证**:可见段起点由脚本异步推给页面,快速滚动时卡片上沿可能有一帧内容错位。需要在真窗口里目测。

**滚轮**(2026-11 修复,真机上鼠标停在小组件上滚不动转录):原生视图把滚轮事件整个吃掉,页面不可滚动时 WKWebView 也**不会**把它交还给 Avalonia(真机确认)。处理:
- 外壳脚本在 `window` 上被动监听 `wheel`(从不 `preventDefault`),把**页面没有消费**的那部分交给宿主:`ncw:widget:wheel {dy}`(页面 → 宿主,3.0 新增,`WidgetMessages.Wheel`;行 / 页模式换算成像素)。
  不交的:目标的祖先里有还能朝这个方向滚的元素(`overflow: auto / scroll`,滚到头之后才交,和浏览器的滚动链一致)、被页面脚本 `preventDefault`(图表缩放;分发完才判断)、双指缩放(`ctrlKey`)。
  同一批事件合成一条,下一个任务就发(不等 rAF,少半帧延迟)。
- 桥:滚轮是短消息,先解析;走**自己的令牌桶**(突发 240、240/s),超额直接丢、**不算违规**,不占报高的全局额度,滚得再快也不会把桥锁死。页面伪造滚轮最多能滚动转录,碰不到任何状态。
- 宿主:`NcwWidgetSurface.ForwardWheel` 先冒泡 `WheelForwardedEvent`(转录据此把它当作用户滚动:往上翻就停止跟随最新,和真实滚轮同一条规则),没人标 `Handled` 就滚最近的 `ScrollViewer`(钳在范围内);控件不显示(空域挂起)或 WebView 整个被裁掉时丢弃。
- 验证:桥的单测(校验、独立额度、不锁死)、Headless(滚动、钳制、先冒泡、不显示时丢弃)、真实 WKWebView 冒烟 `wheel-forwarded`(可滚的子元素 / `preventDefault` / 双指缩放留给页面,其余交出,行模式换算)。
  真实触控板 → WKWebView → 转录的整条链路要在真窗口里目测(冒烟里的滚轮是脚本合成的事件)。

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
- WebView 在 `ScrollViewer` 里的裁剪已按 §8.5「裁剪」处理(Headless 里验证了可见段的计算,真窗口里的效果未目测);透明背景、键盘焦点、Windows / Linux 真实行为未验证(§8.1);light 主题下 Arc 的三层背景 token 全是白色,规范里 `background-secondary` 的「面板」层在浅色下只剩描边区分。
- 高度上限 4000 之后的内容不显示(页面自己不滚),没有「展开 / 全屏」。

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
| `Fast` | 160 ms | `DurationFast` | 箭头旋转;减弱级别下代替位移的短淡入;同一位置的状态互换(`ShowTier=Fast`:输入框主按钮、标签的未保存点、文件页的编辑 / 保存按钮、切换会话时的转录淡入、新标签) |
| `Base` | 240 ms | `DurationStandard` | 出现(进入)、状态点 / 卡片描边的颜色渐变 |
| `Slow` | 480 ms | `DurationConsidered` | 预留(目前没有用到) |
| 退出 | 180 ms | `DurationExit` | 所有退出(比进入快) |

- **缓动**:进入用 `EaseEnter`(减速),退出用 `EaseExit`(加速)(`NcwMotion.EnterEasing / ExitEasing`)。
- **展开 / 收起**:高度走 Arc 的 `Smooth` 弹簧(`NcwMotion.ExpandSpec`);弹簧的好处是被打断(快速连点)时连续。
- **幅度**:出现的上移 = **6 px**(`NcwMotion.RiseOffset`,与 Arc 的 Accordion「答案落进 6 px」同一个数);整幅面板(左侧区域、抽屉、检查器)滑动距离 = 面板自己的宽度;箭头 90° / 180°。没有缩放动画(Arc 的弹层 / 对话框自己带淡入 + 缩放)。
- **性能规则**(每条都有对应的实现或测试):
  1. **优先动 `Opacity` / `RenderTransform`**(合成层,不触发布局):出现、消失、滑入滑出、箭头旋转、忙指示、新行到达都只动这两个属性。位移只用 `RenderTransform`。
  2. **虚拟化列表(对话转录、文件树、会话列表)里不动 Width / Height**:转录里**没有**为新行做高度动画;新行只做「淡入 + 6 px 上移」(`Arrive`),而且**只给真正新追加的行**:同一会话、快照已到、一轮里 ≤ 4 行(`ConversationView`,打开会话 / 快照到了 / 重连补齐都是加载历史,不播放);滚动时被实例化的旧行不动,所以**不影响滚动,也不影响「跟随最新」**(位移不触发布局,不产生额外的 Extent 变化)。回收容器时(`ContainerClearing` / 复用)`Rest` 掉没播完的动画。文件树是 Arc `TreeView` 自己的动画(不整树重排)。
  3. **必须动高度的地方限定范围**:`NcwCollapsible` 只用在**有限数量、可见、一次只会被用户点开一两个**的容器上——工具卡片详情、思考块、待办、审批卡片的错误行、设置里的供应商 / 插件行、项目组、Git 分组、审查冲突提示。代价:动画期间每帧改变这一个控件的测量高度,布局代价 = 它自己 + 祖先链(兄弟的测量有缓存);内容按**完整高度**排布后被裁剪,所以动画里文字不会反复换行。真窗口实测见 §9.6。**不做**成逐帧改 Width 的有:左右栏(见下)、设置侧栏导航的 220 → 56 收窄(随窗口宽度走,一次到位)。
  4. **左右栏的滑入滑出不逐帧改列宽**:列宽一次到位(工作区只重排一次),栏自己用 `RenderTransform` 滑动;滑出时栏临时以「覆盖层」形态(跨列、固定宽度)留在原处,滑完才真正隐藏。整幅面板只滑不淡(半透明压在内容上文字会叠在一起);减弱级别换成短淡入。窗口缩放跨过断点引起的显隐**不播放**动画(不是用户的操作)。
  5. **流式输出的文字增量不做逐字动画**:`NcwMarkdown` 整段重排,没有任何包它的动效;只有末尾的「流式小圆点」在呼吸(`Busy`)。
  6. **没有无限循环的样式动画**:以前加载图标是一条永远在跑的样式动画(不看级别、看不见也在跑);现在是 Arc 的 `LiveLoop`,离开界面 / 隐藏 / 滚出视口自动暂停。
  7. **`ShowMotion` 不用在虚拟化列表的行里**:转录里的工具卡、子代理卡、可视化卡等内部的 `IsVisible` 切换**不加** `ShowMotion`——容器被回收换数据时绑定会翻转,滚动时会整屏闪;它们的状态变化只靠颜色渐变(`Border.dot` / 卡片描边)。同一个元素上也不叠加 `ShowMotion` 和 `Busy`(两者都写 `Opacity`)。
  8. **整批重建的集合不用 `ArriveOnAttach`**:标签栏的集合每次变化都 `Clear + Add`(`WorkspaceViewModel.Refresh`),用 `ArriveOnAttach` 会整排闪;视图按 key 认「刚出现的那一个」(和转录的 `Arrive` 同一个思路,见 §9.4)。
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
| 侧边栏 · 项目组、项目的折叠 / 展开 | `NcwCollapsible` | 2026-11 起项目栏换成侧边栏,不再有胶囊和 2×2 网格 |
| 左侧区域(⌘B / 标题栏侧栏按钮) | `RenderTransform` 滑动(§9.2.4) | 窄窗口的抽屉同样滑入滑出,遮罩淡入淡出 |
| 右侧检查器(⌘⌥I) | 同上,向右滑动 | **新增**:< 1440 时是右侧抽屉(上面 §1 的「窄窗口」) |
| 设置 · 供应商行 / 插件行 | `NcwCollapsible` + 旋转箭头 | |
| 管理页 / 设置页 · 用户点出来的整块卡片 | `NcwCollapsible` | 删除 / 卸载 / 撤销 / 轮换 / 退出登录 / 危险操作的确认卡,MCP / Hooks / 定时任务 / 供应商的编辑器卡,导入卡,局域网暴露 / 允许远程 / 需要登录的提示卡;定时任务编辑器里的「每周」与「重复」字段。只用在**由布尔驱动**的卡片上:收起时内容还在,弹簧收起才看得到东西 |
| 设置 · 导航收窄(220 → 56) | 不做动画 | 随窗口宽度,一次到位(见 §9.2.3) |
| 手机 · 抽屉 / 设置分区 | Arc `NavigationStack` / `Sheet` 自带 | 手机壳没有自己的抽屉,设置分组是导航栈里的页面;**没有可折叠的设置分区**,所以没有对应项 |

**出现 / 消失**:

| 位置 | 做法 |
|---|---|
| 对话 · 新追加的消息 / 工具卡 / 审批卡片 | `Arrive`:淡入 + 6 px 上移(§9.2.2 的规则) |
| 审批被处理后的「收起」 | 卡片从转录里消失(行已经不在集合里,虚拟化列表没法为已删除的行做退出动画);「回答没成功」的提示行是带高度的展开 / 收起。**没做**消失行的退出动画 |
| 设置覆盖层 / 首次启动页 / 数据页 | `IsPresent` + `PresenceKind=Fade`:淡入,退出先淡出再隐藏(整窗覆盖层不做位移,否则会露出边) |
| 账户状态条 | `IsPresent`(上移 + 淡入;退出淡出) |
| 各页签内容(检查器的文件 / Git / 审查 / 终端 / 上下文 / 历史 / 子代理、工作区内容、定时任务页、加载中 / 空状态) | `ShowMotion=Fade`:每次从隐藏变可见淡入(首次挂载不播) |
| 侧边栏的待处理数 | `ShowMotion=Fade` |
| 弹层 / 菜单 / 下拉 / 对话框 / toast | Arc 自带(淡入 + 缩放 / 滑入) |
| 加载 / 进度 | 加载文字与图标 `ShowMotion=Fade`;进度条是 Arc `Progress` 自带 |
| 对话 · 切换到另一个会话 | 转录整体 `Arrive(Fade, Fast)`(`ConversationView.OnRowsChanged` 里「会话变了」的分支);第一次打开会话由外层的 `ShowMotion` 负责,不重复;只动 `Opacity`,不影响回到底部与跟随最新 |
| 标签栏 · 新标签(新建会话 / 打开文件) | `Arrive(Fade, Fast)`:`WorkspaceArea` 记下上一批显示的 key,只有「上一批里没有、且和上一批有交集、一次只多一个」的标签播放(换项目 / 首次显示是整批换掉,不播放);整批重建的其它标签 `Rest`。关闭标签时其余标签的补位不做动画 |
| 输入框 · 发送 / 排队 / 停止(同一位置互换) | `ShowMotion=Grow` + `ShowTier=Fast`(0.84 → 1);「插话」按钮 `Fade` + `Fast`。Arc 的按钮自带按下缩放时(元素已有 `RenderTransform`)自动退化成只淡入 |
| 输入框上方 · 排队列表 / 阻断提示 / 附件条 / 失败与上传提示 | `ShowMotion=Rise` / `Fade`;**不做高度**(底部区域变高会让转录视口逐帧变化,同 §9.7) |
| 管理页 / 设置页(MCP、Hooks、Skills、插件、定时任务、网关、存储、用量、命令、模型、账户) | 就绪后的内容区、页签内容(已安装 / 市场 / 活动)、加载 / 空 / 无结果 / 读取失败 / 只读 / 未知、表单校验与保存失败行:`ShowMotion=Fade`;由「有没有数据」驱动的卡片(测试结果、详情、运行详情、「刚安装 / 刚运行 / 已完成」提示、写操作失败条):`ShowMotion=Rise`(只有进入——关掉时数据已经是 null,带高度收起只会看到空卡片在缩) |
| 检查器各分页内部 / 文件页 | 文件树 ↔ 搜索结果、列表 ↔ 子代理转录、unified ↔ split、加载 / 空 / 二进制 / 过大提示、终端的空状态 / 状态条 / 审批卡 / 「已结束」提示、文件页的加载 / 文本 / 图片 / 二进制 / 源码 ↔ 渲染、编辑 ↔ 保存按钮:`ShowMotion=Fade`(按钮与小提示 `Fast`,冲突提示 `Rise`) |
| 侧栏 · 账户面板 / 收件箱空状态 | 已登录 ↔ 未登录、退出登录的确认行互换(`Fast`)、过期 / 需要选团队的提示行(`Rise`)、收件箱空状态(`Fade`) |

**位置过渡**:

| 位置 | 做法 |
|---|---|
| 对话 · 空会话发出第一条消息,输入框从中间回到底部(§11.4) | `NcwMotion.Glide`(FLIP):网格行高一次到位,输入框所在的区域用 `RenderTransform` 从旧位置滑到新位置,走 `ExpandSpring`(和展开 / 收起同一个弹簧,被打断时连续);只在完整级别播放,减弱 / 关闭直接就位;离开界面树时落到终态 |

**状态过渡**:

- **按钮**:`PlainButton` 悬停 / 按下底色渐变(`Instant`,关闭级别没有);Arc 的 `Button` 自带按下缩放与颜色过渡。
- **运行中指示**:`NcwMotion.Busy`——加载图标旋转(0.9 s 一圈)、流式小圆点 / 会话行 / 标签页 / 侧边栏项目行的运行点呼吸(缩放 ≤ 1.25 + 明暗);减弱级别换成 Arc 的「忙」呼吸(只有明暗),关闭级别静止;离开界面 / 隐藏 / 滚出视口自动暂停。
- **颜色**:状态点(`Border.dot`)和卡片状态描边(等待审批 / 失败)用 `Base` 档渐变。**未知(设备断开)不用颜色表达**:未知是旋转的加载图标(规则 7),和「已结束」(✓ / 灰点)是完全不同的视觉;颜色渐变只发生在 ok / warn / bad / mute 之间,所以不会出现「运行中 → 未知」时经过任何像「已结束」的中间色。
- **「长按允许」填充**:**故意是线性的**——填充就是手势的时钟(`HoldGesture` 的进度),加缓动会让条满的时刻和动作触发的时刻对不上,所以不走统一缓动;只有**回退**(松手早了 / 移出 / 取消)走统一的退出缓动,条平滑退回而不是瞬间归零(减弱 / 关闭立即归零)。
- **箭头**:展开 / 收起的箭头是同一个图标旋转(`Fast` 档),不再是两个图标互换;只有完整级别旋转。
- **模型选择器 / 下拉菜单**:Arc `DropdownMenu` / `Select` 自带。

**导航过渡**:

- 手机 `MobileShell` 导航栈的页面推入 / 弹出、跟手的左缘返回手势、底部标签的胶囊滑动:**Arc `NavigationStack` / `Tabbar` 自带**(横向滑动 + 下面一页视差变暗,手势跟手,松手按速度决定);本分支只在**切换标签**时让新标签的栈淡入(`Arrive(Fade)`)。
- 桌面端:页签切换、检查器页签切换 = `ShowMotion=Fade`;设置覆盖层进出 = `IsPresent`(淡入淡出)。设置(2026-11 第二次重做,三栏主从)一次只显示一个分区,切换分区 = 内容区 `ShowMotion=Fade`(减弱 / 关闭级别直接切换,见 07「布局」)。

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
- 关闭标签时其余标签向左补位的动画(集合整批重建;要做得先让 `WorkspaceViewModel` 按 key 增量更新,再加 FLIP)。
- 转录里卡片内部的状态切换(见 §9.2 规则 7)。
- 手机的收件箱 / 扫码配对 / 设备页内部的状态切换,对话框(新建项目、项目图标)里的校验提示:还是直接切换。

## 10. 启动与加载

范围:桌面启动时盖住整个窗口的加载界面,以及手机壳里同一个 ViewModel 的简化版式。**「全屏」是覆盖窗口里的全部内容(含侧边栏和主工作区),不是操作系统全屏**;窗口按钮(Windows / Linux)和顶部 40px 的拖动区域始终可用(§10.7)。
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

- **标识**:采用超维晶格核心(`Hyper-Tesseract`,由 `NcwProgressArc` 自绘):外圈是 `NcwProgressArc`,弧长 = `Fraction`(已经走完了多少个阶段 / 接管步骤),走 Arc 的 `Smooth` 弹簧(无回弹,被打断时从当前位置接着走);圆环内部实时投影 16 顶点的 4D 超正方体线框,点亮的晶格节点比例同样严格等于 `Fraction`。**还在等的整个期间**(`IsIndeterminate` = `Phase == Loading`),4D 晶格经 Arc `LiveLoop` 在 XW / ZW 双平面连续旋转,外圈再叠一小段带拖尾的扫描短弧用 `NcwMotion.Busy=Spin` 旋转:弧长只表示「走完了几步」,一步本身要多久是不知道的(真实的 1.6 GB 库上接管的「备份」一步实测 37 秒,这期间弧长一动不动),而一段不动的弧挨着一段在转的短弧会被误读成「卡住了」,所以**还在等的期间不画进度弧**(`ShowArc` = `!IsIndeterminate`;`DisplayProgress` 与晶格点亮比例照常随真实进度走),只留晶格旋转与转动的短弧证明还在干活,进度由点亮的晶格节点和清单表示;等待结束(失败 / 数据需要用户处理 / 全部完成)时进度弧才画出来;减弱级别下晶格与短弧都停转、换成 Arc 的「忙」明暗呼吸(`ArcBusyPulse`),弧长直接跳到终点;关闭级别下晶格静止在固定倾角、短弧不画(静止的一段弧会被误读成进度)——三档的最终状态完全一致。失败时外圈与晶格换成 `Danger` 色、中心叠放 `TriangleAlert`。
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
| Headless 界面 | 四个断点 × 深浅:第一帧 / 启动 Agent / 连接 / 数据迁移(带确定进度)/ 加载首批数据 / 慢启动 / 失败(Agent 不存在、版本不匹配、主密钥缺失 / 无法使用、展开诊断)/ 数据页让位;手机壳;动画中间帧(ManualFrameClock:标识弧长、勾淡入、淡出透明度);释放;窗口外框与拖动区域;三档动效终态一致;中英文 | `Ncw.UI.Tests/StartupOverlayTests`(71 条),截图在 `bin/Debug/net11.0/screenshots/startup-*.png` |
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

## 11. 可用性改进(2026-11,以用户视角补齐的一批)

这一节记录从「用起来顺不顺手」出发补上的东西。原则不变:事实只有一份、意图只有一个入口、文字走词条;新加的纯函数都有单测,新界面都有截图测试。

### 11.1 快捷键与命令面板

**意图表**:`Ncw.Client/Shortcuts/ShortcutCatalog.cs` 的 `AppCommand` 是桌面上能用键盘 / 命令面板 / 托盘菜单触发的全部意图;`ShellViewModel.Execute(AppCommand)` 是它们唯一的分发点,每个分支只调已有的那一个入口
(新建会话 = `ProjectPanelViewModel.NewSessionCommand`,停止 = 输入框的 `StopCommand`,允许 = 交互卡片的 `AllowOnceCommand`……),返回 false 表示这次什么也没做(没有运行可停、没有待批的审批),视图据此不吞掉按键。
整窗覆盖层(设置、首次启动、数据页、启动加载)盖着时只认「打开 / 关闭设置」。

| 意图 | 默认(macOS / 其他) | 说明 |
|---|---|---|
| 命令面板 | ⌘K / Ctrl+K | 见下 |
| 新建会话 | ⌘N | 当前项目 |
| 搜索 | ⌘P | 打开命令面板(2026-11 起侧边栏没有单独的搜索框,和 ⌘K 是同一个面板) |
| 关闭标签页 | ⌘W | 当前标签;文件标签有未保存的修改照常先确认 |
| 停止运行 | ⌘. | 只有输入框的「停止」可用时才动作 |
| 允许第一个待审批(一次) | ⌘↵ | 当前会话里第一张工具审批卡片;没有就不吞按键(输入框照常) |
| 转到下一项待处理 | ⌘J | 按收件箱(Core `ActivityInboxView`)的顺序循环,打开会话并滚到卡片、高亮 |
| 聚焦输入框 | ⌘L | |
| 显示 / 隐藏左侧栏、右侧面板 | ⌘B、⌥⌘I | 原有的 |
| 打开设置 | ⌘, | 再按一次关闭 |
| 添加项目 | ⇧⌘O | 「新建项目」窗口 |
| 切换到上一个项目 | ⌃Tab | 最近选过的项目表是纯 UI 状态 |
| 审查改动 / 打开 Git | ⇧⌘R / ⇧⌘G | 添加并切到检查器页签,检查器收着时打开它 |
| 打开终端 | (无) | 只在命令面板里 |
| 切换到第 N 个项目 | ⌘1…⌘9 | 侧边栏里的项目顺序 |

**路由**(`Ncw.UI/Controls/NcwShortcuts.cs` 的 `NcwShortcutRouter`):挂在窗口(TopLevel)上的**隧道阶段** KeyDown,先于输入框拿到按键,按偏好里生效的手势表匹配;
原来写在 `DesktopShell.axaml` 里的四条 `KeyBinding` 删掉了(只在焦点落在外壳里时才生效,焦点不在任何控件上时 ⌘B 不灵)。偏好变化时重建手势表;设置页录制快捷键期间不触发任何快捷键。

**改快捷键**(设置 · 本应用 · 快捷键):偏好 `UiPrefs.Shortcuts` 只存用户改过的(意图名 → 规范化的手势文本,空串 = 用户移除了它),生效值由纯函数 `ShortcutCatalog.Resolve` 算出。
录到的组合必须带 ⌘ / Ctrl / Alt(或是功能键),只带 Shift 的会吃掉正常输入,不收;和别的意图冲突时,**被挤掉的那一条清空**并提示一句(用户明确要这个组合);设成和默认一样的手势不留覆盖。
偏好文件里不认识的意图名、不能用的组合在读入时丢掉。显示按平台画成 ⌘⇧K 或 Ctrl+Shift+K(`ShortcutText.Format`)。

**命令面板**(`CommandPaletteViewModel` + `Views/Desktop/CommandPalette.axaml`):遮罩 + 顶部居中的卡片,↑↓ 移动、回车执行、Esc / 点遮罩关闭。
候选:命令(带当前快捷键)、当前项目最近 30 个未归档的会话、其他项目、设置的每个分区(「设置:快捷键」),以及输入两个字符以后向设备查的文件(`files.suggest`,停顿 150 ms,结果是面板自己的 UI 状态,不进 Store)。
排序复用 Core 的 `FuzzyPath.Score`(和 `@` 文件选择器同一套分档);查询为空时只列命令、会话、项目。选中一行交给外壳执行(`Invoked` → `ShellViewModel`),面板自己不执行任何意图。
面板也算「盖在工作区上的覆盖层」,打开时挂起 widget 的 WebView(§8.5 的 airspace)。

**测试**:`Ncw.Client.Tests/ShortcutsAndPaletteTests.cs`(默认手势不冲突、规范化、冲突挤占、恢复默认、偏好文件往返与坏数据、面板排序 / 循环 / 文件查询、外壳分发与「什么也没做」的返回值);
`Ncw.UI.Tests/ShortcutsUiTests.cs`(真实按键经窗口到外壳、改了立即生效、录制时暂停、⌘K → 查询 → 回车执行 → Esc,明暗两套截图 `palette-*`、`settings-shortcuts-*`)。


### 11.2 系统通知、角标与托盘(关窗后继续运行)

用户交代完任务就会切走;Agent 停在审批上时,用户必须知道。

- **事实只有一份**:「什么在等我」就是收件箱(Core 的 `ActivityInboxView`,手机收件箱、⌘J、待处理条用的同一份派生)。桌面的 `AttentionMonitor`(`Ncw.Client/Services`)订阅 `InboxViewModel.Updated`,自己只记「见过哪些 key」。
- **要不要弹**是纯函数 `AttentionPolicy.Select`:只看新 key;断线设备上的条目是未知,不弹(规则 7);窗口在前台而且正打开着那个会话,不弹;三类开关(待处理 / 完成 / 失败)各管各的。
  **基线**:第一台设备连上之后 3 秒内到达的内容(全量交互、activeRuns 陆续到)只记不弹——不能一启动就把已有的待办和完成过的 Run 全弹一遍。
- **角标** = 收件箱的 `BadgeCount`(现在能处理的待办数,断线设备上的不算);**托盘摘要** = 待处理数 + 连着的设备上运行中的顶层 Run 数 + 有没有断线设备。
- **出口**:Client 只给结构化的 `AttentionNotice`(Client 不产出界面文字);`Ncw.UI/Controls/NcwAttention` 按当前语言拼标题(发生了什么)和正文(项目 · 会话,工具摘要 / 计划第一行 / 问题),交给宿主的 `INativeAttention`;窗口不在前台时还请求注意。点通知 → `ShellViewModel.OpenAttention`(选中项目和会话、滚到卡片、高亮)。
- **平台**(`Ncw.Desktop/DesktopAttention.cs`):macOS = Dock 角标、Dock 弹跳一次、`NSUserNotificationCenter` 系统通知(委托是运行时建的 NSObject 子类,`UnmanagedCallersOnly` 回调;只有打成 .app 有 bundle identifier 时系统才给发,`dotnet run` 的开发进程如实报告不支持);
  Windows = 任务栏闪烁(`FlashWindowEx`),系统通知和角标没做(要 AUMID + WinRT toast),如实报告;Linux = 有 `notify-send` 就用它(点通知回不到具体条目)。Windows / Linux 没有真机验证。
- **托盘(菜单栏)图标**:Avalonia `TrayIcon`,菜单里有「打开」「N 项等你处理」(= ⌘J)「M 个任务运行中」「部分设备未连接」「新建会话」「通知设置」「退出」。
- **关窗后继续运行**(D1 的好处终于能被用户用上):托盘开着(默认)时,点窗口的关闭按钮只是藏起窗口(`WindowCloseReason.WindowClosing` 且不是程序关的),`ShutdownMode` 改成 `OnExplicitShutdown`,本机 Agent 照常执行;
  从托盘菜单、Dock 图标(`ActivationKind.Reopen`)、第二个实例的「回到前台」再打开;真正退出用托盘菜单的「退出」或 ⌘Q(应用退出、系统关机这类关闭照常进行)。关掉托盘开关时窗口若藏着会立刻回来,关窗回到「退出应用」。
- **设置 · 本应用 · 通知**:三类系统通知、角标、托盘各一个开关(`UiPrefs.Attention`,缺省全开);做不到的平台如实写明。

测试:`Ncw.Client.Tests/AttentionTests.cs`(规则、基线、去重、角标随开关、点通知打开会话)、`Ncw.UI.Tests/AttentionUiTests.cs`(文字、转发给宿主、通知设置页截图 `settings-notifications-*`)。
没有自动化的:真实的系统通知横幅与点击、Dock 角标、托盘菜单(都要图形会话和打包好的 .app)。
真实启动冒烟(`NCW_MOCK=1` 跑 Debug 桌面程序)发现并修掉一处:macOS 的原生托盘在挂上时读提示文字,为 null 会抛,整个应用起不来;现在建托盘时先给提示文字,托盘起不来时退化成「没有托盘、关窗就退出」,不影响启动。

### 11.3 审批卡片看得到内容;待处理条

- **预览**:工具审批卡片在目标(路径 / 命令第一行)下面画「要做什么」:写 / 改是 diff 行(前缀 + / −、增删统计),多行命令是全文(单行命令已经是目标了,不重复)。
  规则和工具行的摘要是同一个函数(`TranscriptBuilder.ApprovalPreview` 和工具行共用 `Summarize`,只是行数上限 14 而不是 6),放不下的行数写在下面;预览区最高 240,内部滚动。数据全在 `InputJson` 里,**没有改协议**。
- **按钮**:「允许一次」(上面标着当前的 ⌘↵,用户改了快捷键就跟着变)/「总是允许 ▾」(菜单里一项一个记住范围,「本会话总是允许」等,点一项就按这个范围回答,`AlwaysAllowInCommand`)/「拒绝」。
  原来「总是允许」按钮 + 旁边一个范围下拉,看不出谁管谁,而且要点两下。下面一行小字写「总是允许」会记下的规则(设备算好的 `SuggestedRule`)。手机的「拒绝 + 长按允许」不变。
- **待处理条**:输入框上方一条,「这个会话里有 N 项等你处理」「其他会话还有 M 项」;「下一项」在这个会话的交互卡片之间循环定位、高亮(`ConversationViewModel.FocusNextPendingCommand`),
  「去处理」转到别的会话里的下一项(= ⌘J,跳过当前会话)。N 是转录里的交互行数;M 来自收件箱(扣掉当前会话),由外壳在收件箱更新、换会话时填进来。

测试:`Ncw.Client.Tests/ApprovalPreviewTests.cs`;`ConversationScreenshotTests.Approval_cards_cover_*` 加了待处理条的断言,`convo-tools-*` / `convo-approvals-*` 截图里能看到新卡片和条。

### 11.4 出错时说人话;第一次用不卡住

- **错误码对上了**:Agent 写进消息和 Run 终态的错误码是 `MessageCodec.CodeName` 那一套(`auth` / `rate_limit` / `context_length` / `network` / `provider` / `cache_unsupported` / `no_healthy_provider` / `conflict` / `tool_failed` / `aborted`),
  界面原来只认 `rate_limited` / `context_overflow` 两个(对不上),其余一律把设备的英文诊断原样显示。现在 `ErrorTextConverter.Text(code, messageKey)` 每个码一句人话(早期的别名照认,`run.*` 消息 key 也翻),认不出来给通用说明;
  原文(错误码 · 诊断串)收在卡片的「详情」里,可选中复制。
- **同一次失败只画一张**:失败的 Run 会把错误写进最后一条助手消息(持久化的 error 块),Run 终态也带着它;派生转录时(`TranscriptBuilder`)紧挨着的那条消息错误让给 Run 的那一张(它能重试)。Run 的那一张**只给这个会话最近的一个 Run**:之后又跑了 Run(成功或还在跑),旧的失败只剩消息里的 error 块、留在它发生的位置,不再钉在转录末尾(Store 不清理收口的 Run);收口的 Run 镜像里没落库的调用(被丢弃的消息、被改写的历史)也不再画到末尾。
- **能做的事**:可重试的有「重试」;`auth` / `no_healthy_provider` / `cache_unsupported` 是供应商配置问题,给「供应商设置」直达(`ConversationViewModel.ProviderSettingsRequested` → 外壳打开设置的「模型与供应商」)。
- **没有可用的模型**:输入框里那行提示旁边直接给「添加供应商」按钮(同一个意图)。
- **空会话(2026-11-17 改版:输入框居中)**:空会话时**输入框在视觉中心**(大多数人一上来就打字,视线落点就是输入框),发出第一条消息后回到底部,和有内容的会话一样。
  - 输入框上方:标题「在 *项目名* 里做点什么?」(项目名用强调色;`EmptySessionTitleIn` 按 `{0}` 拆成前后两段,语序由译文决定)和一行上下文胶囊——分支、执行环境(设备 · 环境,本机项目只有设备名)、未提交改动数(有才显示)。
    分支和改动数读 git 镜像 `ClientStores.Git`,改动数是 Core `GitStatusSummary` 的总数;镜像**已知且可用**才显示,未知(还没取到 / 设备断开)不拿旧值当现状(规则 7),分离头指针不显示分支。
    git 概览没有推送主题,空会话第一次出现、镜像里还没有这个项目时,`ConversationViewModel` 经 `IGitCommands.RefreshAsync` 拉一次(每个会话最多一次)。
  - 输入框下方:一排起步建议胶囊(解释项目结构 / 跑测试并修复 / 审查未提交的改动 / 先做计划 / 排查问题),胶囊上是短文字,悬停看完整的那句话;点了把完整的那句**填进输入框(不直接发送)**,焦点放进去。再下面一行输入提示(@ 引用文件、/ 命令)。
  - 布局:`ConversationView` 的网格多一行(第 3 行放建议);空的时候第 1 行(标题贴底)和第 3 行(建议贴顶)按 1 : 1.2 分走剩余高度,输入框落在中心偏上;有内容了第 3 行收起成 Auto。
    行高一次到位,同一会话里「空 → 有内容」(发出第一条消息)时输入框用 `NcwMotion.Glide` 从原位置滑到底部(只动 RenderTransform,见 §9.4);换会话 / 快照到达直接就位。
  - 选型:做过四个方案的可交互原型(居中输入 / 项目上下文首页 / 任务卡片网格 / 建议贴近输入框),选了居中输入:改动最小、不依赖新数据(git 概览协议已有),也和主流 AI 工具的首屏一致。

测试:`ConversationScreenshotTests.Compact_boundary_error_rows_and_a_failed_run_offer_retry`(一张卡片、可重试、文字来自错误码、原文在详情里;截图 `convo-errors-*`);`Empty_session_shows_the_empty_state_and_an_idle_composer`(四个宽度 × 深浅色:输入框落在中间、上下文胶囊、发出第一条消息后回到底部;截图 `convo-empty-*`、`convo-empty-sent-*`);`Ncw.Client.Tests/EmptySessionContextTests.cs`(项目 / 环境 / 分支 / 改动数,git 只拉一次,未知时隐藏);`shell-*` 截图。

### 11.5 转录与检查器的细节

- **项目相对路径**:工具行上路径的目录部分在项目里时显示成项目相对的(`ToolRowViewModel.RelativeDir`;项目根本身显示为空,前缀相同但不是子目录的不算),项目外的照原样(那正是该注意的);完整路径仍在整行提示里。
- **失败的调用说清为什么**:失败行下面一行红字,写输出的第一行(`FailureLine`;原来只有红色和 ⊗,要点开才知道)。这是对「工具行严格单行」的一处有意偏离,只对失败行。
- **「审查」页签自动出现**:当前会话里有已完成的写 / 改工具行(`ConversationViewModel.HasFileChanges`,从转录派生)而用户没有自己加过「审查」时,检查器在「文件」后面自动给出「审查」(和子代理页签一样,随会话出现 / 消失,不能手动关;用户自己加的照旧能关)。
  `InspectorTabs.Compose(added, hasSubagents, hasChanges)` 仍是唯一的组成规则。
- **输入框工具行**:思考和权限两个触发器带上前缀(「思考:自动」「权限:询问」),单独一个「自动」「询问」看不出在调什么。

测试:`Ncw.Client.Tests/TranscriptDetailsTests.cs`;截图 `convo-tools-*`(相对路径、失败原因、自动出现的审查页签、带前缀的工具行)。

### 11.6 设置页瘦身

- **搜索**(07 §3 原则 4,此前没做):导航顶上一个搜索框,在每个分区的全部文字(分区名、每一项的名字和说明)里找;匹配和排序是纯函数 `SettingsSearch.Find`(每个词都要在同一分区出现,分区名命中的在前),
  文字由设置页从界面树上按当前语言收集(`SettingsOverlay.BuildSearchIndex`,不另维护一份「哪个设置在哪」的表,页面改了文字搜索自动跟着变;分区页按需创建,搜索时先把没建的分区全部建出来,见 §12)。结果浮在导航上,每条是「分区名 + 命中的那一句」,点一下跳过去。
- **骨架占位**:分区第一次打开才读数据(2026-11 起一次只显示一个分区,见 07「布局」);没读过 / 读取中的分区原来只有标题,看起来像空的或坏了,现在画三条淡色骨架(`ManagementPageViewModel.IsIdle`、`PageStatus`)。
- **「通用」先常用后高级**:「常用」(默认模型、默认权限档位)和「个性化指令」在上面;辅助模型、目标与 Shell、账号轮换与故障转移、压缩、超时与上限、代理收进「高级」(点开才出现,纯 UI 状态)。
  调整时发现一个潜在缺陷:辅助模型下拉框在数据到达之前回写的空值会被当成用户的选择写进设备(改成「跟随默认」);原来因为下拉框在不可见的 ItemsControl 里没实例化而没暴露。
  现在 `ModelPickRow` 只在页面已就绪、而且值在候选里时才写。
- **卡片上不再一排按钮**:MCP 服务器卡片只留一个和状态有关的主操作(失败 / 连接中 →「重连」,已连接 →「资源与提示」),其余(测试、编辑、删除)进「⋯」菜单,删除标红放最后;
  供应商卡片直接放「连通性测试 / 登录 / 导入模型」,编辑、退出登录、删除密钥、删除供应商进「⋯」。
- **设置仍是整窗覆盖层,不是标签页**:D21 / 07 §3 原则 1 写的是「设置是一个标签页」,实现一直是覆盖层(Esc 关闭、⌘, 开关、可搜索、按作用域分组),作用域清楚这个目的已经达到;
  改成标签页要把十几个管理页搬进工作区的标签体系,收益小于代价,这次不改,以覆盖层为准(07 §3 已同步)。

测试:`Ncw.Client.Tests/SettingsSearchTests.cs`、`Ncw.UI.Tests/ShortcutsUiTests.cs` 里的 `SettingsUsabilityTests`(截图 `settings-search-*`),`ManagementScreenshotTests`(`mgmt-general-*`、`mgmt-mcp-*` 里能看到新布局和骨架)。

### 11.7 单机用户不背多设备的概念

多数人只有桌面内置的那一台本机设备、没有 SSH 环境。纯函数 `Ncw.Core/Domain/DeviceDimension`(设置页、模型页、侧边栏共用一份)决定「设备」这个维度要不要露出来:
不止一台设备、有非本机设备(远程 / 云端)、有 SSH 环境里的项目,才显示。藏起来的是:设置页每个分区页头的「设备 · 已连接 · 所有者」那一排(连接不正常、不是 owner 时照常显示,规则 7)、
「设备」分组标题处和「模型与供应商」页里只有一项的设备切换器、侧边栏项目名后面的「本机」。审批卡片和工具行上的执行环境标识不受影响(AGENTS.md §5)。

测试:`Ncw.Core.Tests/DeviceDimensionTests.cs`、`Ncw.Client.Tests/TranscriptDetailsTests.cs` 里的 `SingleDeviceTests`。截图用的模拟数据有三台设备,所以截图里照常显示。

### 11.8 小处

- (2026-11 起侧边栏、手机项目列表、「更改图标」预览都不再画默认首字母图标,见 §2;`RailProjectViewModel.Initials` 只留作数据)默认首字母取两个字母(`RailProjectViewModel.Initials`:多个词取前两个词的首字母,驼峰取首字母和下一个大写字母,否则前两个字母;中日韩文字和 emoji 仍取一个字),「Avalonia」「Agent」不再都是「A」。
- (已被 2026-11 的侧边栏取代:「定时任务」现在是侧边栏顶部的全局入口。)

### 11.9 这一批没做的

- **启动失败页的「下载安装包」**:仓库里没有正式的下载地址(后端 / 更新 feed 都还没上线),不编一个地址;仍是「重试 / 查看诊断 / 复制诊断信息 / 退出」。
- **「先显示轻量窗口、延后创建外壳」**(§10.13 的 1.4 秒):要改 `MainWindow` 的延迟装配,牵动焦点、快捷键路由(现在挂在窗口上)和大量直接创建 `DesktopShell` 的测试,单独做。
- **手机远程链路**(云端 Agent、推送、默认中继):依赖还不存在的后端,不在这一批。
- **Windows / Linux 的系统通知与角标**(§11.2):需要真机,如实报告不支持。
- **真实的系统通知横幅、Dock 角标、托盘菜单、关窗后藏到菜单栏**:没有自动化测试(要图形会话和打包好的 .app),只验证了代码路径和界面层。

## 12. 按需创建(内存)

2026-11 测量(macOS,Release 桌面端刚启动、只看主界面):托管堆约 74 MB,其中大头不是数据,是 Avalonia + Arc 的界面对象——每个控件都带着一串样式实例(`StyleInstance` / `StyleClassActivator`)和资源表达式(`DynamicResourceExpression`,Arc token 都是动态资源)。
控件 `IsVisible=false` 只是不测量、不绘制,样式和绑定照样在,所以**看不见的整页内容常驻,和看得见的一样占内存**。当时常驻的看不见的部分:设置的 18 个分区页全部建好、只靠 `IsVisible` 切换(为了让设置搜索能从界面树上收文字),检查器的 7 个页同样如此,首次使用引导也一直在。

规则:**整块的、多数时候看不见的内容按需创建**,用 `Ncw.UI.Controls.NcwDeferred`:

- 内容写在 `<ui:NcwDeferred><DataTemplate x:DataType="…">…</DataTemplate></ui:NcwDeferred>` 里,`x:DataType` 是宿主所在处的 DataContext 类型;宿主照旧绑定 `IsVisible`、`ui:NcwMotion.ShowMotion`、`Tag`。
- **第一次被测量时建出内容**:看不见的控件(自己或任何祖先不可见)不会被测量,所以「第一次被测量」就是「真的要显示了」——祖先变为可见(检查器栏展开、设置页打开)也能触发,不需要另外监听可见性。
- 建出来以后跟着宿主显示 / 隐藏;`Release()` 丢掉内容回到空壳,`Realize()` 不看可见性直接建出内容。什么时候释放由使用方决定。
- 模板内容在定义它的 XAML 里加载,资源查找沿用原来的位置;不要为了「省一份样式」去掉视图自己合并的 `NcwTheme` / `NcwStyles`(检查器各页的 XAML 在加载时要从 `InspectorPanel` 合并的那份里查资源,去掉后配色全丢,2026-11 试过并撤回)。

现在用在:

| 位置 | 何时建 | 何时释放 |
|---|---|---|
| 设置的各分区页(`SettingsOverlay`) | 分区第一次显示时;**搜索时一次建出全部**(搜索索引仍从界面树上收文字,§11.6) | 设置页关闭、退出动画放完(`IsPresent` 把 `IsVisible` 置为假)时全部释放,下次打开重建 |
| 检查器的各页(`InspectorPanel`) | 第一次切到该页时 | 不释放(终端、Git、审阅的界面状态要留着) |
| 首次使用引导(`OnboardingOverlay`) | 引导打开时 | 不释放(只出现一次) |

配套:`SettingsViewModel` 在查询为空时不建搜索索引(关设置时清空搜索框不能把刚释放的页面又建回来)。

按需创建后页面在数据**之后**才建,暴露了三处原来被「页面一启动就建好」掩盖的问题(2026-11 逐像素比对全部截图发现,已修):
- 插件提示卡的文字语言:界面语言是插件页创建时才告诉 ViewModel 的,之前到达的提示按 en-US 建好后,语言变了也不重建(运行中切换语言同样如此)。现在 `PluginsViewModel.Locale` 变化时重建提示。
- 项目设置的「保存 / 还原」:下拉框在创建时先回写空值再写回原值,`Dirty` 原来是「动过就算」。现在按值和读到的设置比较(`ProjectSettingsViewModel.Edit`)。
- 管理页的 `IsReadOnly`(= 内容在显示 且 不能写)在 `State` 变化时没有通知:离线 / 只读时原来会多出一条过时的「设备没有连接。」,MCP 空列表的空状态原来不显示。补上通知后新旧一致。

效果(headless 外壳 1500×860、mock 数据,`GC.GetTotalMemory` 前后差):外壳托管内存 88 MB → 41 MB,逻辑树节点 3145 → 987。

测试:`Ncw.UI.Tests/DeferredContentTests.cs`(祖先变可见时建、建过不重复建、释放后重建;设置页只建显示过的分区、搜索时建全部、关闭后释放且**被释放的页面能被 GC 回收**(防止开关设置累积泄漏);检查器只建切到过的页)。
