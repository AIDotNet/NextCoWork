# C# 编码规范

本文件是 3.0 分支 C# 代码的写法约定。能机器检查的部分在仓库根目录的 `.editorconfig`,构建时执行;这里写规则的来由、取舍,以及 `.editorconfig` 管不到的约定。
架构层面的硬性约束(状态管理、协议、Agent 边界、AOT、界面文字、Arc token……)在 `AGENTS.md` §2–§6,本文不重复,冲突时以 `AGENTS.md` 为准。

规则是从现有代码统计出来的(2026-11-17,1528 个 `.cs` 文件、32 万行):绝大多数条目全仓库已经一致,`.editorconfig` 只是把既成的写法固定下来。

## 1. 怎么执行

- `Directory.Build.props` 打开了 `EnforceCodeStyleInBuild` 和 `GenerateDocumentationFile`,加上已有的 `TreatWarningsAsErrors`:`.editorconfig` 里严重级别为 `warning` 的规则,违反就构建失败,CI 一样拦下。
- 严重级别的含义:

  | 级别 | 含义 |
  | --- | --- |
  | `warning` | 强制。构建失败。 |
  | `suggestion` | 推荐。IDE 里有提示,构建不拦。新代码照着写,改到旧代码时顺手修。 |
  | `silent` | 只提供重构,不提示。两种写法都可以。 |
  | `none` | 明确不要这条规则。 |

- 选项(`csharp_style_var_elsewhere = true`)只写取值,严重级别一律用 `dotnet_diagnostic.<ID>.severity` 单独写。不用 `true:warning` 这种后缀写法——两处都能写级别时,读的人分不清哪个生效。
- 生成的 XML 文档(`GenerateDocumentationFile` 的产物)不进发布目录(`PublishDocumentationFile(s)=false`)。打开它只是为了让构建期能检查多余的 `using`(IDE0005)。
- 本地格式化:`dotnet format NextCoWork.slnx`(按 `.editorconfig` 改写空白、using 顺序和能自动修复的风格规则)。只想看不想改:加 `--verify-no-changes`。
- 「待清理」的规则见 §9。

## 2. 格式

- UTF-8、LF、文件末尾有换行、不留行尾空格;C# 4 空格缩进,XML / AXAML / JSON / MSBuild / YAML 2 空格。
- Allman 大括号:`{` 独占一行。`else` / `catch` / `finally` 另起一行。
- 控制语句多行时必须有大括号;整条写在一行的 `if (x is null) return;` 可以。
- 允许单行的块和语句,用来写对齐的表格:`{ get; init; }`、`case "enter": return ("Enter", 13);`。
- 对象初始化器允许一行写多个成员:`new() { Id = id, RunId = run }`。
- 不连续空两行;两个 `}` 之间不空行。
- 死循环写 `while (true)`,不写 `for (;;)`(格式化器会把后者改成 `for (; ; )`)。
- 行宽不设硬上限(Roslyn 没有这条规则,而且中文注释按字符数算并不准)。代码行以 120 列左右为参考,超长的参数表、初始化器按「一项一行」折开;注释可以更长,但一段话超过两行就该拆。

## 3. 文件与命名空间

- 一律文件作用域命名空间:`namespace Ncw.Agent.Catalog;`。
- `using` 写在命名空间之外,`System.*` 在最前,其余按字母序,组之间不空行。不写多余的 `using`。
- 命名空间**不要求**和目录一致:目录只是程序集内的归类,命名空间跟逻辑模块走(例如 `src/Ncw.Agent.Session/Session/` 下是 `Ncw.Agent.Kernel.Session`)。新文件放进已有目录时,沿用同目录文件的命名空间。
- 一个文件一个主要类型,文件名 = 类型名。紧密相关的小类型(一个服务的选项、结果 record、快照)可以和主类型放在同一个文件里,放在主类型之前。
- 大类型按职责拆成 `partial`,文件名 `类型名.职责.cs`(`BrowserPage.Input.cs`、`AgentSession.Preview.cs`、`FileService.Mutate.cs`)。
- 只在一个文件里用的辅助类型用 `file` 修饰。
- 不用 `#region`。
- `global using` 只用于测试项目的公共 `Support` 命名空间;产品代码不加。

## 4. 命名

| 符号 | 写法 | 例子 |
| --- | --- | --- |
| 命名空间、类型、方法、属性、事件、本地函数、枚举成员 | PascalCase | `ProviderService`、`FetchModelsAsync` |
| 接口 | `I` + PascalCase | `IExecutionEnvironment` |
| 类型参数 | `T` + PascalCase | `T`、`TKey`、`TResult` |
| 常量字段 | PascalCase(不用 ALL_CAPS) | `const int MaxRetries = 3;` |
| `static readonly` 字段 | PascalCase | `static readonly JsonSerializerOptions Options` |
| 私有字段(实例、可变 static) | `_camelCase`(不用 `s_` / `m_`) | `private readonly ProviderFileStore _file;` |
| public / internal / protected 字段 | PascalCase(只该出现在 struct、互操作和计数器里) | `internal int InFlight;` |
| 参数、局部变量、局部常量 | camelCase | `var clean = raw.Trim();`、`const string body = "…";` |

- 不写 `this.`:私有字段靠 `_` 前缀区分。
- 返回 `Task` / `ValueTask` 且可等待的方法以 `Async` 结尾(`RevealCredentialAsync`)。例外:协议方法处理器表、事件处理器、xUnit 测试方法、实现第三方接口时对方规定的名字。这条不进 `.editorconfig`——构建里 IDE1006 只有一个严重级别,没法单独把它降成推荐。
- `CancellationToken` 参数叫 `ct`,放在参数表最后(全仓库约 2000 处如此)。
- 缩写当普通单词处理:`HttpClient`、`JsonNode`、`OAuthLogin`、`SshEnvironment`;两个字母的缩写保持大写:`IO`、`UI`。
- 布尔属性 / 方法读起来是一个判断:`IsRunning`、`HasPending`、`CanRetry`。
- 测试方法用句子式命名,单词之间用下划线:`A_disconnected_device_is_unknown_and_does_not_count_toward_the_badge`(`.editorconfig` 在 `tests/**` 下关掉了成员的 PascalCase 规则)。

## 5. 语言特性

- **`var`**:一律用。只有类型推不出来(`null` 初值、需要接口类型)时才写显式类型。
- **预定义类型**:`string`、`int`、`string.Empty`、`int.Parse`,不写 `String`、`Int32`。
- **可访问性**:显式写出来(接口成员除外)。修饰符顺序:`public private protected internal file new static abstract virtual sealed override readonly extern unsafe volatile async required partial`。
- **null**:`is null` / `is not null`,不用 `== null` / `!= null`;`??`、`?.`、`??=`、`handler?.Invoke(...)`。参数校验用 `ArgumentNullException.ThrowIfNull` / `ArgumentException.ThrowIfNullOrEmpty`。
  `!`(null 宽恕)只用在编译器确实推不出、而逻辑上一定非空的地方,旁边写一句为什么。
- **模式匹配**:`if (x is Foo foo)` 代替 `as` + null 判断、`is` + 强转;`is not`;能写成 switch 表达式的多分支写成 switch 表达式。
- **对象创建**:类型明显时用目标类型 `new()`;集合用集合表达式 `[]`(新代码;旧的 `new List<T>()` 不强改)。
- **类型**:
  - 没有子类的类一律 `sealed`(CA1852 强制 internal / private 类型;public 类型同样照做)。
  - 数据载体(DTO、快照、结果、事件负载)用 `sealed record`;需要值语义的小结构用 `readonly record struct`。
  - 依赖只是存起来用的服务,优先主构造函数;需要校验、转换参数或参数会被改写时,用普通构造函数 + `readonly` 字段。
  - 只在构造里赋值的字段标 `readonly`;不捕获外部状态的 lambda / 本地函数标 `static`。
  - 必须由调用方给的属性用 `required` + `init`。
- **表达式主体**:属性、索引器、访问器、lambda 能用 `=>` 就用;方法、运算符、本地函数只在一行写得下时用;构造函数不用。
- **三元表达式**:不强制把 `if / else` 改成 `?:`,哪个读起来清楚用哪个。嵌套三元不超过一层。
- **括号**:运算优先级不直观时(`a + b * c`、`x && y || z`)加括号。
- **字符串**:比较、`StartsWith`、`IndexOf` 显式给 `StringComparison`(协议字段、路径、标识一律 `Ordinal` / `OrdinalIgnoreCase`);格式化给 `CultureInfo.InvariantCulture`。界面文字只走 `NcwTexts`(`AGENTS.md` §2)。
- **时间**:取当前时间经 `TimeProvider`(便于测试和对账),不直接 `DateTime.UtcNow`;时间点用 `DateTimeOffset` 或 Unix 毫秒,不用本地时间。
- **锁**:用 .NET 9+ 的 `System.Threading.Lock`(`private readonly Lock _gate = new();`),不再 `new object()`。
- **JSON**:`JsonSerializerOptions` 缓存成 `static readonly`,不每次 new(CA1869 强制);序列化走源生成,不用反射(`AGENTS.md` §2)。

## 6. 异步

- 一路 async 到底:不 `.Result`、不 `.Wait()`、不 `GetAwaiter().GetResult()`。确实要在同步上下文里等(进程入口、`Dispose`)时写一句为什么。
- 不写 `async void`,事件处理器除外;事件处理器里要 `try / catch`,异常不能逃出去。
- 能取消的操作都接 `CancellationToken ct`,并一路传下去。
- **`ConfigureAwait(false)`**:Agent(`Ncw.Agent.*`)、协议(`Ncw.Protocol`)、`Ncw.Core`、`Ncw.Client` 的服务层写;会回到 UI 线程的代码(`Ncw.UI`、`Ncw.Desktop`、ViewModel 里要更新绑定属性的路径)不写。
  分析器 CA2007 没有打开——它分不清这两种情况。
- 后台任务不能「发射后不管」:要么等它,要么交给有生命周期的宿主(`IHostedService`、Run 注册表),异常要有去处。
- 热路径上返回大多同步完成的结果时用 `ValueTask`,其余用 `Task`。

## 7. 注释与文档

- 注释用中文,和设计文档一致;标识符、协议字段、代码片段保持原文,用 `<c>…</c>` 包起来。
- 注释写「为什么」和「不做什么」,不复述代码。移植自 2.x 的实现在 `<summary>` 里注明来源(`2.x ipc/provider.ts`),行为不同要写明原因(`AGENTS.md` §5)。
- `///` 文档注释:public 类型和非显而易见的成员写 `<summary>`;不要求每个 public 成员都有(CS1591 关闭),也不要求 `<param>` 写全(CS1573 关闭)。
  写了的就要正确:`<see cref="…"/>` 能解析、XML 合法、放在能挂文档的位置上(CS1574 / CS1570 / CS0419 / CS1587 / CS1734,待清理后改为强制)。
- 引用设计文档写成 `docs/design/09 §4` 这样的路径 + 小节号,方便检索。
- `TODO` 后面跟上在哪个里程碑或条件下处理;不留没有下文的 `TODO`。
- `#pragma warning disable` 只针对具体的诊断 ID,紧跟着 `restore`,并在同一行或上一行写原因。

## 8. 测试

- xUnit v3。测试类与被测类同名加 `Tests`(`ProviderServiceTests`);测试方法用句子式命名(§4)。
- 一个测试只验证一件事;准备 / 执行 / 断言之间空一行,不写 `// Arrange` 之类的标签。
- 夹具、假实现放在测试项目的 `Support/` 下,用 `global using` 引入。
- 时间、随机数、网络都经可替换的抽象(`TimeProvider`、`IUpstreamHttp`……),测试里不睡眠等待;必须等异步事件时用带超时的等待辅助。
- `tests/**` 下关掉了:成员 PascalCase(测试方法名)、未使用参数(IDE0060)、可标 static(CA1822)。其余规则和产品代码一样。

## 9. 待清理的规则

下面这些规则的约定已经定了,但现有代码还有违规,所以暂时是 `suggestion`。数字是 2026-11-17 在 `HEAD`(e66d8997)上统计的处数。
清理一条就把 `.editorconfig` 里的级别改成 `warning`、删掉「待清理」注释、同步更新这张表。

| 规则 | 内容 | 处数 | 清理方式 |
| --- | --- | ---: | --- |
| IDE0005 | 多余的 `using` | 270 | `dotnet format style NextCoWork.slnx --diagnostics IDE0005 --severity info` |
| IDE0055 | 格式 | 183 | 先把 `for (;;)` 改成 `while (true)`,再 `dotnet format whitespace NextCoWork.slnx` |
| IDE1006 | 命名 | 44 | 手工改:私有字段补 `_`、去掉两处 `s_`、测试里 PascalCase 的局部常量、`using var _r = …` |
| IDE0007 | 用 `var` | 38 | `dotnet format style … --diagnostics IDE0007` |
| IDE0031 | `?.` 简化 null 判断 | 27 | `dotnet format style … --diagnostics IDE0031` |
| IDE0004 | 多余的强转 | 23 | `dotnet format style … --diagnostics IDE0004` |
| CS1574 / CS1734 / CS1570 / CS0419 / CS1587 | 文档注释的 cref / XML 错误 | 23 / 24 / 5 / 5 / 4 | 手工改 |
| CA1310 | 字符串比较缺 `StringComparison` | 14 | 手工改(都在测试里) |
| IDE2000 / IDE2002 | 连续空行 / `}` 之间的空行 | 6 / 1 | `dotnet format style …` |
| IDE0075 / IDE0100 | 可简化的条件 / 多余的 `== true` | 4 / 4 | `dotnet format style …` |
| IDE0036 / IDE0051 / IDE0011 / IDE0044 / IDE0034 / IDE0019 / IDE0018 / IDE0074 / CA1827 | 零星 | 各 1–2 | 手工改 |

做法:每条规则单独一个提交(`style: 清理 IDE0005`),便于审阅和回退;不和功能改动混在一起。清理前先确认工作区里没有别人正在改的同一批文件。

## 10. 改规范

- 改 `.editorconfig` 的规则时同步改本文;把一条规则从 `suggestion` 升到 `warning` 之前,先在全仓库清到零违规,否则所有人的构建会一起失败。
- 新增强制规则时,在提交说明里写清楚统计出来的现状(多少处符合、多少处违规)和为什么选这边。
- `.editorconfig` 只在 `NextCoWork.slnx` 上验证过;`NextCoWork.Mobile.slnx`(Android 宿主)要单独构建确认。
