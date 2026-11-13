# AGENTS.md(3.0 / Avalonia 分支)

本文件是在本分支工作的 Agent(以及人)的**硬性约束**。标注「必须」的条目是合并的前提条件。

## 0. 当前阶段

M0 设计评审。`docs/design/` 没有评审通过之前,不写业务代码。

## 1. 范围(必须)

- 3.0 只做 GUI。不新增任何面向用户的命令行入口(命令行控制端、插件 CLI、`ncw-agent` 的用户子命令)。
  `tools/` 下的构建和发布脚本只给开发和 CI 用,不对用户发布。
- 3.0 的 Agent 只有两种部署形态:桌面内置(`--mode local`)和云端(`--mode cloud`)。不做独立设备服务。

## 2. 技术栈(必须)

- .NET 10;Avalonia `[12.1.5, 13)`;TKCode.Arc **锁定**在 `1.1.0-preview.6`。所有版本只写在 `Directory.Packages.props` 里。
- 注册方式是 `AppBuilder.UseArc(arc => arc.UseArcControls() …)`,`App.axaml` 里不放 Fluent 或 Simple 主题。
- 业务视图只引用 `Ncw.UI.Controls` 封装,不直接引用 `arc:` 控件(封装层自己除外)。
- 和 Avalonia 同名的控件(Button、TreeView、Slider……)在 XAML 里必须加 `arc:` 前缀,在 C# 里用别名。
- 不写死颜色值,一律使用 Arc token(`{arc:ArcDesignTokenResource …}`)。
- 不启用 `InvariantGlobalization`。不使用依赖反射的 MVVM 和序列化,必须兼容 AOT 和裁剪。

## 3. 状态统一管理(必须)

继承 2.x 的七条规则,C# 里的对应位置见 `docs/design/01-architecture.md` §5。简述如下:

1. 一个事实只有一个真源(在 Agent),控制端只在 `Ncw.Client/Stores/` 的**一个** Store 里保留一份镜像。key 带上 `deviceId`。
2. View 和 code-behind 只能放纯 UI 状态。ViewModel 不自己订阅协议事件。只传 id,不传状态快照。
3. 同一个事实在多处展示时,派生逻辑只能是 `Ncw.Core/Domain/` 下的一个纯函数,Agent 和客户端共用。
4. 同一个用户意图只对应 `Ncw.Client/Actions/` 里的一个方法。
5. 「运行中」类的状态必须有全量权威广播兜底,收到后对账;首帧和后续广播用同一个类型。
6. 状态单调:终态压过 running,重放的旧事件不能把状态改回运行中。
7. 派生视图跟随真源。设备断开时状态标为 `unknown`,不判成结束,也不继续计时。

## 4. 协议(必须)

- 协议定义只有一个来源:`src/Ncw.Protocol`。客户端和 Agent 直接引用它,不允许在别处另写一份帧或方法的类型。
- 协议是 3.0 全新设计(`docs/design/09-protocol.md`),**不参考、不兼容 2.x 的设备协议**。
- 不自研密码学:远程连接用 TLS 1.3 双向认证(证书钉扎);配对码验证用现成库的 SRP-6a;经中继时在隧道里跑同样的 TLS。
- 3.0 先实现本机和局域网承载;中继以后作为新的承载加入,不得为它修改 L1 以上的协议。
- 线上格式由按协议版本冻结的黄金向量保护;改线上格式必须升协议版本或加能力开关,并同步更新黄金向量和两端测试。
- 每个方法必须在方法表里声明种类、所需角色和所需能力;`command` 类方法必须带幂等键。

## 5. Agent(必须)

- Agent 用 C# 实现。2.x 的 TypeScript 代码只作为参考实现,不拷进本分支。
- 客户端项目不能引用 `Ncw.Agent.*`;Agent 项目不能引用 Avalonia 或客户端项目。
- `ncw-agent` 必须能以 NativeAOT 发布,发布时零裁剪警告。
- 移植一个 2.x 模块时,对应的单测要一起移植,并在 `docs/parity.md` 里登记;行为差异要写明原因。
- 数据库沿用 2.x 的 schema 和 `NCK1` 凭据格式,只做增量迁移。
- 工具、Hooks、插件宿主 API 只能通过项目的 `IExecutionEnvironment` 访问文件和进程,不允许直接用 `System.IO` 或 `Process` 碰设备本地(执行环境实现自身除外)。
- 涉及执行的审批和工具卡片必须带上执行环境标识(设备 + 环境)。
- 模型目录的真源在 Agent(`ModelCatalogService`),控制端只通过协议读取,不自己请求 models.dev。

## 6. 全栈 C# 与 JS 执行(必须)

- 整个仓库只用 C#(.NET 10)。不允许引入 Node、npm 包或 TypeScript 构建步骤,构建和发布工具也用 C# 写(`tools/`)。
- 需要执行 JS 时只能用 Jaunt,而且只能经过 `Ncw.Agent.Plugins` 的宿主,不允许在别处自己 `new JsEngine()`。
- 插件代码一律设置 `JsLimits` 并使用 `JsProfile.Hardened`;宿主 API 只能显式绑定,每个绑定都要先做权限检查。

## 7. 常用命令(骨架建好后补全)

- 构建:`dotnet build NextCoWork.slnx`
- 测试:`dotnet test`
- 桌面运行:`dotnet run --project src/Ncw.Desktop`
- Agent 运行:`dotnet run --project src/Ncw.Agent -- --mode local`
