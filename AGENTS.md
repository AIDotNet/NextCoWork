# AGENTS.md(3.0 / Avalonia 分支)

本文件是在本分支工作的 Agent(以及人)的**硬性约束**。标注「必须」的条目是合并的前提条件。

## 0. 当前阶段

M0 设计评审。`docs/plan/` 没有评审通过之前,不写业务代码。

## 1. 技术栈(必须)

- .NET 10;Avalonia `[12.1.5, 13)`;TKCode.Arc **锁定**在 `1.1.0-preview.6`。所有版本只写在 `Directory.Packages.props` 里。
- 注册方式是 `AppBuilder.UseArc(arc => arc.UseArcControls() …)`,`App.axaml` 里不放 Fluent 或 Simple 主题。
- 业务视图只引用 `Ncw.UI.Controls` 封装,不直接引用 `arc:` 控件(封装层自己除外)。
- 和 Avalonia 同名的控件(Button、TreeView、Slider……)在 XAML 里必须加 `arc:` 前缀,在 C# 里用别名。
- 不写死颜色值,一律使用 Arc token(`{arc:ArcDesignTokenResource …}`)。
- 不启用 `InvariantGlobalization`。不使用依赖反射的 MVVM 和序列化,必须兼容 AOT 和裁剪。

## 2. 状态统一管理(必须)

继承 2.x 的七条规则,C# 里的对应位置见 `docs/plan/01-architecture.md` §5。简述如下:

1. 一个事实只有一个真源(在 Agent),控制端只在 `Ncw.Core/Stores/` 的**一个** Store 里保留一份镜像。key 带上 `deviceId`。
2. View 和 code-behind 只能放纯 UI 状态。ViewModel 不自己订阅协议事件。只传 id,不传状态快照。
3. 同一个事实在多处展示时,派生逻辑只能是 `Ncw.Core/Domain/` 下的一个纯函数。
4. 同一个用户意图只对应 `Ncw.Client/Actions/` 里的一个方法。
5. 「运行中」类的状态必须有全量权威广播兜底,收到后对账;首帧和后续广播用同一个类型。
6. 状态单调:终态压过 running,重放的旧事件不能把状态改回运行中。
7. 派生视图跟随真源。设备断开时状态标为 `unknown`,不判成结束,也不继续计时。

## 3. 协议(必须)

- 协议定义只有一个来源:`protocol/`。不在 C# 或 TS 里手写帧和方法的类型。
- 改协议必须同时更新 schema、黄金向量和两端的测试。

## 4. 常用命令(骨架建好后补全)

- 构建:`dotnet build NextCoWork.slnx`
- 测试:`dotnet test`
- 桌面运行:`dotnet run --project src/Ncw.Desktop`
