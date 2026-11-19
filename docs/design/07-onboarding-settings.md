# 07 · 首次启动、登录与设置页

## 1. 首次启动

### 桌面端

```
启动 ─▶ 发现 2.x 数据? ──是──▶ 接管页(进度 / 失败重试,见 04 §4)──┐
          │否                                                        │
          ▼                                                          ▼
       欢迎页 ─▶ 登录(或「使用自己的模型供应商」/ 跳过)─▶ 打开第一个项目 ─▶ 主界面
```

```
┌──────────────────────────────────────────────┐
│                  ◧ NextCoWork                 │
│          面向编码 Agent 的工作空间              │
│                                              │
│   [  使用 NextCoWork 账户登录  ]               │  ← 打开系统浏览器
│   [  使用自己的模型供应商  ]                    │  ← 直接进模型设置,稍后再登录
│      跳过                                     │
│                                              │
│   正在等待浏览器完成登录… [取消] [改用登录码]    │  ← 登录发起后显示
└──────────────────────────────────────────────┘
```

「打开第一个项目」有三个选项:打开本机文件夹、连接 SSH 主机(见 [06](06-environments-ssh.md))、创建云端 Agent。

### 实现状态(首次启动,桌面端)

`OnboardingViewModel`(`Ncw.Client/ViewModels/Account/`)+ `OnboardingOverlay`:欢迎 → 数据 → 选模型供应商 → 完成,四个进度点。
- **什么时候出现**:本控制端的账户记录不存在(`AccountMode.Undecided`)。出现之后一直开着直到用户点完成;中途退出应用,下次因为已经做过决定而不再出现(设置里随时可以补)。没有账户服务的外壳(测试、模拟数据)里状态是 `Loading`,不弹出。
- **欢迎**:`[使用 NextCoWork 账户登录]`(浏览器 + PKCE)、`[使用自己的模型供应商]`(= 离线,并让下一步直接引导去配置)、`[跳过]`;登录进行中 / 失败的卡片(等浏览器 → 取消 / 改用登录码;登录码大号显示;失败原因 + 重试)和设置页「账户」是**同一个控件** `AccountLoginStatus`(同一份状态,规则 3)。账户做出决定(登录成功 / 离线 / 跳过)时**自动前进**。
- **数据**:只读 `DataGateViewModel`——数据页(`DataGateOverlay`)仍然是**整窗覆盖、优先级最高**,接管进行中 / 被挡住时它盖在首次启动流程上面,所以这一步实际看到的是「就绪」:接管过 2.x 显示迁移了多少项目和会话,全新安装显示「全新开始」,本机 Agent 还没连上显示「正在启动」(不是「没有数据」)。接管中 / 被挡住的分支(含重试)在 ViewModel 里有、有测试,但被数据页遮住,屏幕上看不到。
- **选模型供应商**:两张卡——NextCoWork 账户(已登录显示「模型已可用」,否则提示之后在「设置 · 账户」登录)和自己的供应商(`[添加供应商]` 打开设置的「模型」页,流程停在这一步)。
- **完成**:`[打开本机文件夹]`(走 `ProjectActions.AddLocalProjectAsync`,和侧边栏项目树的「+」同一个意图);**连接 SSH 主机、创建云端 Agent 这两项只是占位**(「稍后在设置里」/「即将推出」,不可点)——云端 Agent 属于 M6,SSH 主机的添加入口随执行环境设置页(06)接。
- **窄屏 / 矮窗口**:内容居中、最大宽度 440,外层 `ScrollViewer`,600 宽下没有溢出。

### 手机端

登录 → 没有任何设备时,给出两个选项:「连接我的电脑」(扫码配对)和「创建云端 Agent」。

## 2. 登录:授权码 + PKCE + 本机回环回调

桌面登录与 2.x(`ipc/client-auth.ts`)和线上后端(`ClientOAuth`)用**同一套**:授权码 + PKCE,浏览器跳回本机回环地址上的临时监听。
后端对桌面客户端 `nextcowork-desktop` 只放行 `http://127.0.0.1:{端口}/callback`(端口任意、无查询串、不能写 `localhost`),不认自定义 scheme;授权页是网页路由 `/client/authorize`,不在 `/api` 下。

```
1. 客户端生成 code_verifier、code_challenge(S256)、state(一次性,10 分钟过期),
   并在 127.0.0.1 上起一个临时监听(端口由系统分配)
2. 打开系统浏览器:
   https://nextco.work/client/authorize?client_id=nextcowork-desktop
     &redirect_uri=http://127.0.0.1:{端口}/callback&response_type=code
     &code_challenge=…&code_challenge_method=S256&state=…&scope=…
3. 用户在浏览器里登录并同意 → 浏览器跳转到 http://127.0.0.1:{端口}/callback?code=…&state=…
4. 监听校验 state,把回调交给账户服务,给浏览器一个「可以关闭此页面」的页面,然后关掉监听
5. 客户端用 code + code_verifier + 同一个 redirect_uri 换取令牌
6. 刷新令牌存在系统钥匙串(Keychain / DPAPI / libsecret / Keystore)
7. 桌面端把一个委托令牌交给本机 Agent(Agent 调用模型通道和配置同步时要用)
```

**为什么桌面不用 `nextcowork://auth/callback`**:3.0 早期设计假设后端会为桌面客户端放行自定义 scheme,但线上后端一直只认回环回调(授权页和 `POST /api/client/oauth/authorize` 都按 `ClientOAuth.ValidRedirect` 判无效请求),已发布的 2.x 也这样登录,桌面沿用它不依赖后端新版本;
自定义 scheme 还有个弱点:同一台电脑上任何程序都能注册同名 scheme,而回环监听只在登录期间存在、只收一次 state 匹配的回调。后端仓库已另外放行 `nextcowork://auth/callback`(逐字匹配,**待部署**),留给手机和以后;`nextcowork://` 在桌面只用于配对和打开链接(下面的路由表)。

**备用方式(登录码)**:`POST /api/client/oauth/device`(RFC 8628;`nextcowork-desktop` 与 `tack-server` 共用,`ClientGrant` 可组合)取用户码和设备码,用户在 `https://nextco.work/client/device` 核对并按住批准(确认页对 NextCoWork 有专门文案),客户端轮询令牌端点。**后端仓库已实现,待部署**;部署前线上的老后端会拒绝(`invalid_client`),客户端显示「登录码暂时不可用」。

### 各平台注册 `nextcowork://`

| 平台 | 做法 |
|---|---|
| macOS | Info.plist 的 `CFBundleURLTypes`。Avalonia 通过 `IActivatableLifetime` 收到 OpenUri 激活(要验证 Avalonia 12 的具体 API) |
| Windows | Velopack 安装钩子写入 `HKCU\Software\Classes\nextcowork`,卸载时删掉;链接作为启动参数传入,再走单实例转发 |
| Linux | `.desktop` 文件声明 `MimeType=x-scheme-handler/nextcowork`。AppImage 首次运行时把 `.desktop` 写进 `~/.local/share/applications` 并设为默认处理程序 |
| iOS | 优先用 `ASWebAuthenticationSession`(不需要全局注册 scheme,回调直接回到 App);深链同时注册 URL scheme 和 Universal Links |
| Android | Custom Tabs 加 intent-filter `nextcowork://auth/callback`(或者 App Links) |

### 跳转链接路由(白名单)

| 路由 | 用途 |
|---|---|
| `nextcowork://auth/callback` | 预留给手机的 Universal Link / App Link 回跳(后端目前没有对应的手机客户端;桌面 client_id 虽已放行这个地址,桌面不用)。**桌面登录不走它**:没有进行中的浏览器登录监听时一律被忽略 |
| `nextcowork://pair?invite=…` | 配对。用系统相机扫配对二维码后直接跳进 App |
| `nextcowork://open?…` | 打开项目或会话(后续版本) |

其他路由一律忽略。所有来自跳转链接的操作(配对、打开)都要用户在界面上确认,不能自动执行。

### 账户状态归属

- 每个控制端各自登录,各自持有账户会话(存在系统钥匙串里)。这是该控制端「账户」这个事实的唯一真源。
- 本机 Agent 拿到的是委托令牌,只用于模型通道和配置同步。退出登录时,委托令牌同步撤销。
- 云端 Agent 的账户身份由编排服务签发(见 [02 A6](02-agent.md#a6-云端-agent))。

### 实现状态(登录与账户,M4)

代码:`Ncw.Client/Accounts/`(`Pkce`、`NcwLinkParser`、`IAccountApi` / `HttpAccountApi`、`AccountService`、`LinkRouter`、`IDelegationTarget`)、`Ncw.Client/Stores/AccountState.cs`(账户会话的**唯一一份**镜像,`ClientStores.Account`)、`Ncw.Desktop/`(`PlatformSecretStore`、`AccountVault`、`SingleInstance`、`UrlSchemeRegistration`)、Agent 侧 `Ncw.Agent.Host/Accounts/` 与 09 §8.4「账户委托」。

- **状态机**:`AccountMode`(`Loading` / `Undecided` / `Offline` / `Authenticated`)× `SessionHealth`(`Ok` / `Refreshing` / `Unreachable` / `Expired`)× 进行中的登录(`LoginProgress`:方法 + 阶段 + 截止时间;失败是稳定 key `account.login.*`)× 每台设备上委托令牌的状态(`Pending` / `Attached` / `Failed`,key 带 deviceId,设备断开时移除 = 未知,规则 7)。展示**只读这一个 Store**;令牌不进 Store、不进 DTO、不进日志、不进异常消息(访问令牌只在 `AccountService` 内存里,刷新令牌在钥匙串里)。状态条显示什么只有一个纯函数 `AccountBanner.Of`(首屏状态条和设置页的账户页共用,规则 3)。
- **单调**:`Generation` 在登录完成 / 退出 / 失效时加一,所有异步结果(刷新、换码、控制端列表)带着发起时的代数回来,对不上就丢弃——退出登录之后迟到的刷新结果复活不了会话,也写不回钥匙串(规则 6)。
- **PKCE**:`code_verifier` 48 个随机字节(64 个 base64url 字符)、`code_challenge = BASE64URL(SHA256(verifier))`、`state` 24 个随机字节,随机数只用 `RandomNumberGenerator`;有 RFC 7636 附录 B 的已知向量测试。一次登录一份一次性的 state / verifier,10 分钟过期(`AccountOptions.LoginTtl`),再次发起顶掉上一次。
- **回环回调监听(`LoopbackLoginCallbackListener`)**:一次浏览器登录一个,`TcpListener` 只绑 `127.0.0.1`、端口由系统分配(不用 `HttpListener`:Windows 上它对 `127.0.0.1` 前缀要预留 URL ACL);只收 `GET /callback`(别的路径 404、别的方法 405);`Host` 头必须是 `127.0.0.1:{端口}`(防 DNS 重绑定);请求头 ≤ 8 KB、5 秒读完;参数交给 `NcwLinkParser` 同一套严格解析(重复参数、控制字符、非法百分号编码一律 400);**state 常数时间比较,对不上回 400 并继续等**;一次登录只把一个匹配的回调交给账户服务;回给浏览器的页面没有外链、`Referrer-Policy: no-referrer`、`Cache-Control: no-store`,文字由 `NcwApp` 注入(`Ncw.Client` 不产出界面文字,缺省只显示 ✓ / ✕)。监听在登录完成 / 失败 / 取消 / 过期 / 被新的登录顶掉 / 退出时关闭;监听起不来 = `account.login.browserFailed`,不打开浏览器。换码请求的 `redirect_uri` 逐字使用监听给出的那一个。
- **回跳处理**:`NcwLinkParser` 是严格白名单(scheme 正好是 `nextcowork`、host + 路径只有 `auth/callback` / `pair` / `open`,不带用户信息 / 端口 / 片段,总长 ≤ 4096、参数 ≤ 16 个、单个值 ≤ 2048,重复参数名、控制字符、空白、非法百分号编码、非 ASCII 冒充的主机一律拒绝)。`AccountService.HandleLinkAsync`(监听把通过 state 校验的回调交给它,服务里再校验一次):没有进行中的浏览器登录(或本机监听还没就绪)→ 忽略(陌生回调什么都不改);state 常数时间比较,**对不上 = 拒绝且不影响正在等的登录**(别人构造的回调不能把登录打断);同一个回调只能用一次(重放被忽略);过期 → 失败;`error=access_denied` → `account.login.denied`。`pair?invite=` 只记下邀请并触发 `PairRequested`,**由界面让用户确认后才配对**;`open` 只解析、不执行(后续版本)。
- **设备授权码备用方式(RFC 8628)**:界面「改用登录码」:取短码(`user_code`)和验证地址显示给用户,同时尽力打开验证页,后台按 `interval` 轮询,`slow_down` 加 5 秒,`access_denied` / `expired_token` / 设备码自己的有效期到了都是终态;连续网络失败 3 次放弃;`Unsupported`(老后端没有这个端点)给出「登录码暂时不可用」。设备码不进状态。
- **刷新**:访问令牌离过期不到 2 分钟就刷新(`AccountOptions.RefreshMargin`),后台每分钟检查一次(单飞);刷新令牌会轮换,新的存回钥匙串。刷新令牌被拒(401 / `invalid_grant`)= `Expired`:清钥匙串、撤销本机 Agent 的委托令牌、**保留账户资料**(界面显示「登录已过期」和姓名,可重新登录或继续离线);连不上 = `Unreachable`:会话和钥匙串都不动,本地功能照常,下个节拍重试。启动时钥匙串里没有刷新令牌(用户删了 / 换了机器)按「已过期」处理,**不会悄悄登录**。
- **团队**:登录后读团队;只有一个直接选定,多个则 `ContextRequired`(要选才能用模型通道 / 市场 / 同步,此时不交付委托令牌),没有团队 = 登录失败并撤销令牌。
- **钥匙串**:macOS 用 Keychain 通用口令(服务 `NextCoWork Account`,已有就原地更新——刷新令牌每次轮换都要写,这一点在真实钥匙串上有测试);Windows 用 DPAPI(当前用户)加密的文件;**Linux 的 libsecret 未接入:拒绝保存**,本次登录只在内存里有效,界面提示「此系统暂时无法安全保存登录状态」,下次启动要重新登录——**绝不降级为明文文件**。非机密记录(决定、用户资料、团队)在客户端目录的 `account.json`(0600、原子写)。
- **离线 / 退出登录**:「使用自己的模型供应商」和「跳过」都是 `Offline`(持久化,下次不再弹首次启动);退出登录 = 界面立刻回到 `Offline`,清内存令牌、钥匙串、资料,撤销本机 Agent 的委托令牌,服务端的刷新令牌尽力撤销(失败不拦着)。
- **委托令牌**:登录完成、每次刷新、换团队之后,以及本机设备(重新)连上时,经 `account.attachDelegatedToken` 交给本机 Agent。后端能换专用的委托令牌(RFC 8693 令牌交换,`DelegatePath`)就用它;**后端不支持(404 / 405 / 501)时退回把访问令牌本身交给本机 Agent**(同一台电脑、同一个用户、短期;B13 到位后自然切换,不用改客户端)。只有**本机通道**的设备才有这一步,远程设备和云端 Agent 的账户身份另有来源(02 A6)。
- **单实例转发**:`SingleInstance`——主实例独占客户端目录下的锁文件(`FileShare.None`,进程崩溃由操作系统释放),在当前用户专属的命名管道(`CurrentUserOnly`)上监听;后启动的进程把 `link <uri>` / `activate` 转过去再退出。收到的内容不可信:只收第一行、≤ 16 KB、读取 5 秒期限,不解释,交给 `NcwLinkParser`。macOS 的 OpenUri 激活经 Avalonia 的 `IActivatableLifetime`(`ProtocolActivatedEventArgs`)进同一个收件箱 `NcwLinkInbox`。
- **各平台注册**:macOS 靠 `tools/Ncw.Build` 的 bundle 写进 Info.plist(`CFBundleURLTypes`,已有);Windows 运行时写 `HKCU\Software\Classes\nextcowork`(Velopack 安装钩子以后接管,卸载时删);Linux 运行时写 `~/.local/share/applications/nextcowork-url-handler.desktop` 并 `xdg-mime default`(AppImage 用 `APPIMAGE` 指向的文件)。注册只做「尽力而为」;用 `dotnet` 主机启动(开发)或设了 `NCW_REGISTER_URL_SCHEME=0` 不会去改系统注册。
- **账户后端(`IAccountApi` / `HttpAccountApi`)**:沿用 2.x 的真实端点(`/api/client/oauth/token`、`/revoke`、`/api/client/account`、`/api/client/context/options|select`、`/api/client/usage`),响应容忍 `{ data: … }` 外壳,10 秒超时,令牌只在请求头 / 体里。**与线上后端一致的部分**:授权页 `https://nextco.work/client/authorize`、`client_id = nextcowork-desktop`、`redirect_uri = http://127.0.0.1:{端口}/callback`、`scope` 沿用 2.x 的完整串(后端是**全等匹配**,少一个插件市场就 401),所有请求带 `User-Agent: NextCoWork/3.x`(后端按 `NextCoWork/` 前缀识别桌面客户端、给会话起设备名)。**后端仓库已实现、待部署的端点**(部署前线上得到 404 / 405 = `Unsupported`,客户端优雅降级):设备授权 `POST /api/client/oauth/device`(RFC 8628 §3.1,轮询走令牌端点的 `urn:ietf:params:oauth:grant-type:device_code`)、委托令牌 `POST /api/client/oauth/delegate`(没有就退回访问令牌)、控制端列表与撤销 `GET|DELETE /api/client/sessions[/{id}]`。这些在 05 §6 登记为 B11 / B13 / B14 / B15。
- **没做(等后端或真机)**:账户配置同步 `configSync`(B10 `RailLayout` 跨端分组)只留接口位置和本地快照语义,后端到位前**保持关闭**;模型通道的托管供应商 `nextcowork`(`/v1/models` 同步成别名)随后端接入——Agent 已经能从 `DelegatedAccount` 拿令牌;钱包 / 余额 / 充值(3.0 不涉及支付);手机端的 `ASWebAuthenticationSession` / Custom Tabs 回跳(属于 M5 的宿主);各平台协议注册的真机验证(见 04 §7)。

## 3. 设置页(重新设计)

### 设计原则

1. ~~设置是一个标签页~~ **设置是整窗覆盖层**(Esc 关闭、⌘, 开关;原计划做成工作区标签页,实现一直是覆盖层,作用域清楚的目的已达到,以覆盖层为准,见 [03 §11.6](03-ui-layout.md#116-设置页瘦身))。手机上在「我」页里以导航栈的形式进入。
2. **明确作用域**。因为有多台设备,每一项设置都属于以下三个作用域之一,并在页面上标出来:
   - **本应用**:只影响这个控制端(外观、快捷键、通知)。布局类设置通过账户同步到其他控制端。
   - **设备**:存在某个 Agent 上(模型、扩展、执行环境、数据)。页面顶部有设备切换器 `[本机 ▾]`。设备离线时这部分只读。
   - **项目**:存在项目里(权限、指令、Hooks 覆盖)。
3. **改了立即生效**。不需要「保存」按钮,改动后弹出带「撤销」的提示。
4. **可以搜索**。左上角搜索框可以搜到每一项设置,包括它的说明文字(**已实现**,03 §11.6)。
5. **遵守状态规则**。设置的值不在组件里缓存:本应用的设置在 `PrefsStore`,设备的设置以 Agent 为准。

### 信息架构(2026-11 重做:方案 D「三栏主从」)

17 个分区合成 11 个导航项(`SettingsPage`);原来的分区(`SettingsSection`)保留,成为导航项下的「子分区」——搜索结果、命令面板、手机端、跳转都还按分区走,
分区归哪个导航项只有一个纯函数 `SettingsNav.PageOf`(Ncw.Client,纯 UI 映射,不是设备上的事实)。

```
┌──────────────────┬───────────────────────┬───────────────────────────────────────┐
│ 设置          ✕  │ 模型与供应商      [+]  │ Anthropic        设备 · 本机            │
│ 🔍 搜索设置       │ ───────────────────── │ ───────────────────────────────────── │
│ 本应用            │ ◎ 模型目录             │ anthropic · ••••3f2a · 12 个模型        │
│   账户            │ ● NextCoWork   32 模型 │ [连通性测试] [导入模型] [⋯]             │
│   外观            │▌● Anthropic    12 模型 │                                       │
│   快捷键          │ ○ OpenAI       已停用  │ 模型                                   │
│   通知            │ ● Ollama       未连接  │  claude-sonnet-4.5  200K  视觉  [◉]    │
│   关于            │                       │  claude-haiku-4.5   200K        [◉]    │
│ 设备              │                       │                                       │
│  [本机 ▾]         │                       │                                       │
│   通用            │                       │                                       │
│ ▸ 模型与供应商     │                       │                                       │
│   扩展            │  ← 列表栏:只有「一组东西」 │                                       │
│   连接            │    或有子分区的导航项才有  │                                       │
│   数据            │                       │                                       │
│ 项目              │                       │                                       │
│  [NextCoWork ▾]   │                       │                                       │
│   项目设置         │                       │                                       │
└──────────────────┴───────────────────────┴───────────────────────────────────────┘
```

| 导航项 | 作用域 | 子分区(列表栏) | 内容 |
|---|---|---|---|
| 账户 | 本应用 | — | 账户信息、已登录的控制端、退出登录(**不涉及支付**) |
| 外观 | 本应用 | — | 主题、强调色、字体、界面密度、动效级别(**动效已实现**:跟随系统 / 完整 / 减弱 / 关闭,存在 `UiPrefs.Motion`,见 [03 §9](03-ui-layout.md#9-动效统一的动效系统);其余项未做) |
| 快捷键 | 本应用 | — | 查看和修改快捷键,可以录制(**已实现**,见 [03 §11.1](03-ui-layout.md#111-快捷键与命令面板)) |
| 通知 | 本应用 | — | 系统通知和推送:审批、Run 完成、Run 失败;应用图标角标;菜单栏 / 托盘图标与「关窗后继续运行」(**桌面已实现**,见 [03 §11.2](03-ui-layout.md#112-系统通知角标与托盘关窗后继续运行);手机推送未做) |
| 关于 | 本应用 | — | 版本、**更新通道**、检查更新、下载进度、立即重启并更新、必需更新的阻断提示、签名校验失败提示、上次检查时间(**已实现**,见 [04 §7.4](04-update-install.md#74-自更新运行时m7ncw-core--ncw-clientupdates--ncw-desktopupdates));许可证未做 |
| 通用 | 设备 | — | 默认模型、权限档位、个性化指令;高级:辅助模型、压缩、超时、代理…… |
| 模型与供应商 | 设备 | **模型目录 + 每个供应商**(真正的主从:选中一个供应商,右边是它的详情) | 供应商(启停、凭据、连通性测试、OAuth、导入模型、模型别名)、模型目录(models.dev 状态与更新,见 [02 A8](02-agent.md#a8-模型目录modelsdev-自动更新)) |
| 扩展 | 设备 | 技能 · 命令 · Hooks · MCP 服务器 · 插件 | 原来的五个分区,内容不变 |
| 连接 | 设备 / 本应用 | 连接与远程 · 开放网关 · 已连接的设备 | **允许远程连接**、端口、仅局域网、**中继地址**([09 §9](09-protocol.md#9-中继m6已实现ncwrelay--relaytransport))、配对码、已配对的控制端;开放网关([09 §3](09-protocol.md#3-局域网远程连接));「已连接的设备」是本应用作用域(这个控制端配对过的其他电脑),它的页头标「本应用」 |
| 数据 | 设备 | 用量 · 存储 | 用量统计;数据目录、备份、导出、2.x 接管记录、回退到 2.x(危险区) |
| 项目设置 | 项目 | — | 权限规则、指令文件、Hooks 覆盖、执行环境 |

未做:执行环境(SSH 主机,见 [06](06-environments-ssh.md))、诊断(日志、连接测试、诊断包)仍未实现,做的时候分别进「设备」和「本应用」分组。

### 布局

- 桌面端三栏:**导航**(220px)→ **列表栏**(240px,只有带子分区的导航项和「模型与供应商」才有)→ **内容**(最大宽 760,`ContentMaxWidthMD`)。每一行是「标题 + 说明 + 控件」。
  - **一次只显示一个分区**(2026-11 起,替代「所有分区排在一个滚动区里」):导航和滚动位置不再需要双向同步,原来那套「用户操作才写回、排版后最多拨回 4 次」的逻辑整个删掉,
    `FluidTabControl` / `NcwSideNav` 也不再使用。「当前分区」的真源仍是 `SettingsViewModel.Section`;导航项、列表栏的选中都从它派生。切换分区时内容滚回顶部。
  - **点导航项**:回到这个导航项上次看的子分区(这次打开设置之内记住,纯 UI 状态),没看过就是第一个子分区。搜索结果、命令面板直接改 `Section`,导航和列表栏跟着走。
  - **数据按需**:分区第一次成为当前分区时读它的数据,这次打开里读过就不再读;重新打开设置全部重读;网关密钥照旧在离开网关分区时收起。
  - **设备 / 项目切换器只有一份**:设备切换器在导航的「设备」分组标题下面,项目切换器在「项目」分组标题下面;各页页头里的切换器在设置页里用样式关掉(`PageHeader.ShowSwitchers`)。
    「模型与供应商」页原来自带一个设备切换器,在桌面设置页里改为跟随这一份(`SettingsViewModel` 把 `Pages.Scope.DeviceId` 同步给 `Models.DeviceId`)。
  - **模型与供应商的主从**:列表栏第一项「模型目录」,下面是供应商(状态点 + 名字 + 一行摘要),选中项是纯 UI 状态 `ModelsSettingsViewModel.SelectedProviderId`(null = 模型目录);
    供应商被删掉时回到「模型目录」。导入模型、OAuth 进度、操作失败这些提示放在详情区顶部,和选中哪一项无关;**新建 / 编辑供应商是弹窗**(见下文「添加供应商」,2026-11 起替代详情区顶部的展开表单)。手机端仍用原来的整页卡片(同一个 ViewModel,同一个弹窗)。
  - **窄窗口**:窄于 760px 时导航收成 56px 的图标栏(文字、分组标题、切换器藏起来,图标有提示);窄于 1000px 时列表栏收窄到 200px。
  危险操作(删除数据、回退)放在每页最底下的「危险区」里,需要二次确认。
- 手机端:分组列表,点击后推入详情页(仍按原来的分区,不受导航项合并影响)。设备作用域的页面顶部同样有设备切换器。
- 测试:`Ncw.Client.Tests/SettingsNavTests.cs`(分区 ↔ 导航项、列表栏、回到上次的子分区、供应商选择与回退)、`Ncw.UI.Tests/SettingsTests.cs`(截图 `settings-models-catalog`、`settings-models-provider`)、
  `ManagementScreenshotTests.Nav_switches_pages_and_collapses_to_icons_on_narrow_windows`。

### 模型页(已实现的部分)

「设备 · 模型与供应商」页:顶部设备切换器;供应商列表(启停、编辑、删除、连通性测试、OAuth 登录 / 退出)、凭据(**只写不读**:输入框保存后立即清空,列表只显示后四位)、
模型别名(启停、编辑、删除、从上游导入)、模型目录状态(来源、上次更新、手动「立即更新」、自动更新开关与间隔)。OAuth 的授权页和配对码由**控制端**展示并打开浏览器(设备可能在另一台电脑上)。
设备的事实都在 Store(`providers` / `catalog` 主题的镜像),页面不缓存配置;凭据摘要(后四位)是按需读取的一次性结果。非 owner 角色只读。「默认模型」(对话 / 子代理 / 标题生成)随 Agent 配置页一起做。

**编辑模型(2026-11)**:别名行的铅笔打开「编辑模型」弹窗(替代原来只能改名的输入框),协议不变——`providers.models.rename` + `providers.models.update`(09)。

- **字段**:基本(别名、显示名称;上游模型 id 只读)、上下文(上下文窗口、最大输出,单位 token)、能力与协议(图片输入;请求协议:跟随供应商 / 三种协议之一)、
  思考(模式:不支持 / 总是思考 / 开关 / 预算 / 强度档位;按模式出现「默认开启」「默认预算」「支持的强度」「默认强度」)。支持的强度里勾上「关」= 这个模型可以关掉思考
  (没勾时对话里选「关」,设备会明确报错而不是悄悄回到默认强度)。
- **预填有效值**:窗口、输出、图片、思考模式和强度档位取设备算好的有效值(模型候选 `ModelOptionDto`,别名 + 目录 + 归属规则只在设备算一份),不取别名里存着的原始值——
  没改过的字段可能还在跟随 models.dev,存着的只是导入时的占位缺省。思考配置没被别名接管时,模式多一项「跟随模型目录」(预选它,不发任何思考字段)。
- **只发改了的字段**:和打开时比,变了的才放进 `models.update`;设备把它们记进 `catalogOverrides`(归供应商,不再跟随目录),没动的继续跟随目录。
  思考配置在别名里原有的高级键(`parameterPath`、`effortMap`、`enabledValue` 等,导入 / 2.x 带来的)原样保留,只改模式和默认值。
- **保存**:先改名(撞名直接报错,其余字段还没动),再一次 `models.update`;任何一步失败,弹窗不关、原因显示在里面。客户端先拦明显不对的输入:别名为空、窗口 < 1024、输出 > 窗口、预算 < 1024、强度模式一个可用档位都没有。
- 还没做:把某个字段「恢复跟随目录」(要协议加清除 `catalogOverrides` 的参数);请求补丁 `requestAdapter` 的编辑。

### 供应商自动发现与导入(2026-11)

用户多半已经在别的客户端里配好了 key。设备(Agent)在**它自己那台机器上**找这些配置,控制端只看到摘要,导入也在设备上完成——**密钥明文从不经过协议**(09 `providers.discovery.*`)。

- **来源**(只读,找不到 / 读不了 / 格式不对就跳过,不报错):
  | 来源 | 位置 | 读什么 |
  |---|---|---|
  | Claude Code | `~/.claude/settings.json` 的 `env` | `ANTHROPIC_BASE_URL`、`ANTHROPIC_AUTH_TOKEN` / `ANTHROPIC_API_KEY`、`ANTHROPIC_MODEL` 等模型变量(DeepSeek、Kimi、智谱的 Anthropic 兼容端点就是这样配进 Claude Code 的) |
  | Claude Code | `~/.claude.json` 的 `primaryApiKey` | 用 Console 登录时 Claude Code 生成的 API Key |
  | Codex | `~/.codex/config.toml` + `~/.codex/auth.json` | `model_providers.<id>` 的 `base_url` / `env_key` / `wire_api`、`model`;`auth.json` 的 `OPENAI_API_KEY`;`env_key` 指向的环境变量 |
  | cc-switch | `~/.cc-switch/config.json` | 它为 Claude Code / Codex 存的每一套供应商(国内用户常用来切 DeepSeek、Kimi、智谱……) |
  | opencode | `~/.local/share/opencode/auth.json` | `type: "api"` 的条目(按 opencode 的供应商 id 对上已知服务的地址) |
  | Gemini CLI | `~/.gemini/.env` | `GEMINI_API_KEY` / `GOOGLE_API_KEY`、`GEMINI_MODEL` |
  | Qwen Code | `~/.qwen/.env`、`~/.qwen/settings.json` | `OPENAI_API_KEY` + `OPENAI_BASE_URL` + `OPENAI_MODEL`;`settings.json` 里 `security.auth` 的 `apiKey` / `baseUrl`、`model.name` |
  | Crush | `~/.config/crush/crush.json`、`~/.local/share/crush/crush.json` | `providers.<id>` 的 `type`、`base_url`、`api_key`(`$NAME` 换成环境变量)、`models[].id` |
  | Continue | `~/.continue/config.json`、`~/.continue/config.yaml` | `models[]` 的 `provider`、`model`、`apiKey`、`apiBase` |
  | Aider | `~/.aider.conf.yml` | `openai-api-key` + `openai-api-base`、`anthropic-api-key`、`api-key: [<供应商>=<key>, …]`、`model` |
  | Shell 配置 | `~/.zshenv`、`~/.zprofile`、`~/.zshrc`、`~/.bash_profile`、`~/.bashrc`、`~/.profile`、`~/.env`、`~/.config/fish/config.fish` | `export NAME=值` / fish 的 `set -gx NAME 值`,只认字面值(含 `$`、反引号的跳过,不执行任何东西);同名后面的文件为准。**这一来源同时补给其他来源查环境变量**(Codex 的 `env_key`、Crush 的 `$NAME`、opencode 的 `{env:NAME}`),解决 macOS 桌面应用拿不到 shell 变量的问题 |
  | 环境变量 | Agent 进程的环境 | `DEEPSEEK_API_KEY`、`OPENAI_API_KEY`(+`OPENAI_BASE_URL`)、`ANTHROPIC_API_KEY`(+`ANTHROPIC_BASE_URL`)、`MOONSHOT_API_KEY`、`OPENROUTER_API_KEY`、`DASHSCOPE_API_KEY`、`ZHIPUAI_API_KEY`、`SILICONFLOW_API_KEY`、`GROQ_API_KEY`、`XAI_API_KEY`、`GEMINI_API_KEY`,以及 Mistral、Together、Fireworks、Cerebras、Perplexity、NVIDIA、阶跃、混元、千帆、魔搭、零一万物、百川、火山方舟、MiniMax 的 `*_API_KEY`(一家可以有几个常见名字,例如 `KIMI_API_KEY`、`GLM_API_KEY`、`ARK_API_KEY`) |
  | 本地服务 | `~/.ollama`、`~/.lmstudio` 目录存在 | Ollama(`http://localhost:11434/v1`)、LM Studio(`http://localhost:1234/v1`),不要 key;只看目录在不在,不探测端口 |
- **不导入订阅登录**:Claude Code 的 Claude 订阅 OAuth、Codex 的 ChatGPT 登录令牌不读也不搬(那是别的应用的登录会话,不是 API Key);Codex 的 ChatGPT 账号走我们自己的 `codex` 账号登录。
- **地址 → 名字和协议**:已知服务一张表(DeepSeek、Kimi、智谱、通义、OpenRouter、SiliconFlow……,按主机名认;本机地址按端口认 Ollama 11434 / LM Studio 1234),认不出的用主机名当名字。各客户端自己的供应商名(Continue 的 `provider`、opencode / Crush 的 id、Aider 的 `api-key` 前缀)按同一张表的别名对上。Anthropic 兼容端点用 `anthropic` 协议,其余按来源(Codex 的 `wire_api = responses` → `openai-responses`)。
- **去重**:同一地址 + 同一把 key 只出现一次(多个来源都有时保留第一个来源,其余记进「也见于」);和已有供应商地址相同、key 也相同 → `imported`(不能再导);地址相同 key 不同或建议的 id 被占 → `conflict`(导入时 id 加后缀,不覆盖已有的)。比较 key 只在设备上做。
- **界面**:模型页列表栏「模型目录」下面一项「从其他应用导入」,有可导入的候选时带个数;打开模型页(owner、设备支持这个能力)时自动扫一次。详情里每条候选一行:来源、名字、地址、`••••后四位`、模型提示、状态;勾选后「导入所选」,导入完成选中第一个新供应商。可「重新扫描」。手机整页里同样一张卡片。
- **环境变量的局限**:macOS 上从访达启动的应用拿不到 shell 配置文件里 `export` 的变量,所以「环境变量」这一来源在桌面上经常是空的;「Shell 配置」来源直接读那几个文件来补(只认字面值,`source` 别的文件、`$(…)` 算出来的读不到)。
- **cc-switch 的局限**:只读它的 `config.json`;新版本如果把配置搬进了 SQLite 库,这一来源就读不到(其余来源不受影响)。
- **鉴权头**:导入的 Anthropic 兼容端点按我们 `anthropic` 协议的约定发 `x-api-key`;Claude Code 里用 `ANTHROPIC_AUTH_TOKEN`(Bearer)配的第三方端点多数两种都认,不认的导入后连通性测试会报出来。
- 实现:Agent `Ncw.Agent.Catalog/Providers/Discovery/`(`DiscoveryReaders` 各来源的纯函数、`KnownServices`、`ProviderDiscovery` 扫描 / 导入;读文件经 `IDiscoveryHost`,测试换成内存的);
  协议 `ProviderDiscoveryMethods`;客户端 `ProviderDiscoveryViewModel`(候选是一次性查询结果,不进 Store)、界面 `Models/ProviderDiscoveryPanel`。
- 测试:`Ncw.Agent.Catalog.Tests/ProviderDiscoveryTests`(各来源、去重、状态、导入、坏文件、订阅令牌不读)、`Ncw.Protocol.Tests/ProviderDiscoveryDtoTests`(冻结向量、方法表、DTO 里没有能装 key 的字段)、
  `Ncw.Client.Tests/ProviderDiscoveryViewModelTests`、`Ncw.Integration.Tests/ConfigLinkTests.Discovered_providers_*`(真 Agent:线上字节里没有明文、导入后 key 在设备上)、`Ncw.UI.Tests/SettingsTests`(截图 `settings-models-discovery-*`)。


### 添加供应商(2026-11)

列表栏「供应商」旁的「+」(手机整页是「添加供应商」按钮)打开弹窗;「编辑」用同一个弹窗。方案是在可交互的方案页里选定的:**两步式布局 + 卡片网格 + 顶部导入提示 + 一个端点一个模版 + 保存并测试**。

- **模版**:真源在 Agent(`providers.templates.list`,09 §8.4),和自动发现共用同一张已知服务表(`KnownServices`),每家补上分组、拿 key 的地址、其余端点(`ProviderTemplates`;新加的已知服务没补就在类型初始化时失败,测试先撞上)。
  **一个端点一个模版**:DeepSeek、Kimi、智谱、Z.ai、MiniMax 的 Anthropic 兼容端点,Kimi、通义、MiniMax、硅基流动的国际站,各是一张卡片,副标题写端点标签(「Anthropic 兼容」「国际版」)+ 主机名。
  控制端每次打开模型页查一次(一次性结果,不进 Store);老设备不报能力时弹窗直接是「自定义」表单。暂不含账号登录(OAuth)类的模版:各家登录实现还没移植(`docs/parity.md`)。
- **宽度**:640(三列卡片、表单两列字段都放得下)。Arc 的 `Dialog.Size` 只有 360 / 440 / 560,这里在这个弹窗上覆盖 Large 档的 `DialogSurface.PanelWidthLG` token;
  窗口窄时 Arc 自己收到「窗口宽 − 32」,内容宽度 < 560 时网格变两列。两步之间宽度不变。
- **高度**:标题栏和按钮行不动,中间一块自己滚动,最大高度 = 窗口高 − 260(下限 240),按钮行永远在窗口里。
- **第一步(选模版)**:顶部一行「在这台设备上找到 N 个已配置的供应商 · 去导入」(只在自动发现有可导入的候选时出现;点了关弹窗、切到「从其他应用导入」),
  搜索框(按名字、id、地址、端点标签,不分大小写),分组网格:常用 → 国际 → 国内 → 聚合平台 → 本机 → 其他(认不出的分组 + 最后一张「自定义」)。搜不到时给「改用自定义」。
- **第二步(表单)**:页头是返回、logo、名字(带端点标签)、地址;要 key 的模版只露一个 API Key 输入框 + 「获取 API Key」(浏览器打开模版给的地址);本机服务换成一句「不需要 API Key,确认它在设备那台电脑上运行」。
  名字、标识、地址、协议、启用收在「高级」里(折起来时右边写「协议 · 主机名」);「自定义」和编辑一直展开。标识按模版 id 建议,已被占用就加 `-2`、`-3`(纯函数 `ProviderTemplateMatch.SuggestId`)。
- **保存并测试**:写供应商 → 存 key(只写不读,送出就清空)→ 测一次连接(`providers.test`)。通过就关弹窗、在列表里选中它;没通过时原因留在弹窗里(「已保存,但连接测试没通过:……」),
  可以改了再试(同一个标识,是更新),或「保留并关闭」。新建要 key 的模版不填 key 先拦住(自定义不拦:有的网关不要 key);标识被占在客户端就拦住。
  表单有改动时点遮罩不关,Esc 和「取消」照常关。
- **logo**:`Assets/ProviderLogos/<服务>.svg`,取自 models.dev 的单色 logo(MIT,许可证在同目录);`NcwProviderLogo` 只解析 logo 里用到的那部分 SVG(path 的填充 / 描边 / 透明度 / 变换),
  不引入 SVG 库,**颜色跟随 Arc 前景 token**,不带品牌色(深浅主题和强调色预设下一致)。没有 logo 的服务(百川、零一万物、混元、千帆)和认不出的地址画名字首字母。
  列表栏、详情页头、发现候选、弹窗共用;已有供应商的 logo 按地址对上模版(纯函数 `ProviderTemplateMatch.Find`:完整地址 → 同主机同协议 → 同主机,本机按端口),停用的变淡。
- 实现:Agent `Ncw.Agent.Catalog/Providers/Templates/ProviderTemplates`;协议 `ProviderTemplatesMethods`;Core `ProviderTemplateMatch`;客户端 `ProviderEditorViewModel`(模版分组 `ProviderTemplateItems.Group` 是纯函数)、
  `ModelsSettingsViewModel.NewProviderAsync` / `EditAsync`、`IDialogService.ShowProviderEditorAsync`;界面 `Views/Dialogs/ProviderEditorDialog` + `ProviderEditorFooter`、`Controls/NcwProviderLogo`。
- 测试:`Ncw.Agent.Catalog.Tests/ProviderTemplatesTests`(每个已知服务都有默认模版、id 唯一、一个端点一条、分组和地址)、`Ncw.Protocol.Tests/ProviderTemplatesDtoTests`(冻结向量、方法表)、
  `Ncw.Core.Tests/ProviderTemplateMatchTests`、`Ncw.Client.Tests/ProviderEditorViewModelTests`(分组、搜索、填表、拦截、保存并测试、失败不关、编辑、老设备)、
  `Ncw.Integration.Tests/ConfigLinkTests`(真 Agent 给模版、两边常量一致)、`Ncw.UI.Tests/ProviderEditorDialogTests`(内置 logo 都能解析、宽 640 / 窄窗口、截图 `provider-editor-*`)。

### 设备 / 项目作用域的管理页(已实现的部分)

「设备」分组下的 **通用、技能、命令、Hooks、MCP 服务器、用量、存储、网关** 和「项目」分组下的 **项目设置**,与模型页同一结构:页头有设备切换器(项目页再加项目切换器,两者是 `SettingsScope` 这一份纯 UI 状态)、本控制端在该设备上的角色和连接状态。
窄于 760px 时左侧导航收成 56px 的图标栏(文字和分组标题的字藏起来,分组项留作空行,图标有提示)。在桌面设置页里,页头不再各带切换器(见上文「布局」)。

- **事实在 Store,页面只派生**:设备设置 `Store.Settings`、技能 / 命令 / Hooks 清单 `Skills / Commands / Hooks`、MCP `Mcp`、定时任务 `Schedules`;用量、存储统计、网关状态、项目设置是这一页自己的查询结果(不进 Store)。
- **统一的页面状态机** `ManagementPageViewModel`:加载中 / 就绪 / 读取失败(带稳定 key,可重试)/ **设备未知**(设备断开,规则 7:不显示成「没有」)/ 角色读不了。所有「能不能做」只来自纯函数 `ManagementAccess`:viewer 只读、member 可选装技能与读取、owner 全部;密钥与危险操作还要求**本机通道**。按钮禁用并有一行说明原因。
- **立即生效 vs 草稿**:开关与下拉立即写入(失败时界面回到设备的值);数字 / 文本 / 代理 / 个性化在草稿里改、点「应用」整块写入(**与上文「不需要保存按钮」有出入**:一项不合法设备会拒绝整个补丁,所以先在界面里按设备的范围校验)。
- **只写不读**:代理密码(仅本机 owner)、Hook 环境变量、MCP 密钥只显示键名与「已设置」;MCP 写密钥是整张 map 一次写完,所以表单要求「全填或全空」。网关密钥只在本机 owner 点「显示」后短暂持有,离开页面即清掉。
- **危险操作**(清理 / 清空历史 / 清除数据 / 恢复备份 / 导入):owner + 仅本机 + 预览拿一次性令牌 → 展示将被删除 / 覆盖的数量 → 勾选「我已了解」→ 执行;令牌 10 分钟过期,过期不发请求、要求重新预览。其余删除 / 卸载 / 轮换密钥是页内的二次确认卡片。
- **定时任务**在侧栏「定时任务」入口打开,盖在主工作区上(选了别的会话就让位)。规则校验与「接下来 5 次触发」预览用 Core 的 `ScheduleMath` / `ScheduleValidation`(Agent 的 `ScheduleCalculator` 委托给同一份,不再各算一份)。
- **没有做**:设备设置里没有「指令文件目录」字段,「个性化指令」(名称 / 背景 / 指令)是最接近的一项。

### 插件设置页(已实现)

「设备 · 插件」(`PluginsViewModel`,`SettingsSection.Plugins`;三个标签:已安装 / 市场 / 活动与诊断),同一套 `ManagementPageViewModel` 状态机与 `ManagementAccess` / `Gate`:

- **事实在 Store**:清单、授权、诊断、插件在等用户回答的提示都是 `Store.Plugins` 的镜像(`plugins` 主题全量 + 单调 `Rev`);设备断开时镜像是「未知」,页面清空清单 / 提示 / 诊断而不显示成「没有插件」,也不继续把旧的「运行中」当现状(规则 7)。活动日志、市场、更新检测、配置值是本页自己的查询结果,不进 Store。行对象按插件 id 跨快照复用:展开状态和配置草稿是纯 UI 状态。
- **角色**:读取要 member(清单里有命令白名单 / 域名,活动里有插件做过什么)——**viewer 按协议读不了,所以整页是「读不了」(`Forbidden`)而不是其他设置页那样的「只读」**,并且不发任何请求;member 可答复提示、运行插件命令、浏览市场、检查更新,写操作(启停 / 卸载 / 授权 / 撤销 / 配置 / 安装 / 更新)禁用并说明原因;owner 全开。角色在页面打开时变化,所有按钮的可用性随之更新。
- **已安装**:状态点 + 说明(idle / activating / active / disabled / pending-approval / error / faulted);启停开关(`error` 状态装载失败,不能启用);卸载是页内二次确认卡片;从 zip 安装上限 3 MB——选到超限文件**不读文件、不发请求**,直接提示 `plugins.packageTooLarge`;新装的插件是禁用的、没有授权,装完自动展开让用户逐条批准。
- **权限**:必选 / 可选逐项列出,已授予 / 未批准,逐项授予与撤销;每个能力有人话说明(`PluginPerm*`),敏感能力(`process`、`workspace.write`、`scm.write`、`secrets`、`agent.intercept`)以及命令白名单 `allowedCommands`、域名 `hostPermissions` 用危险色高亮;`pending-approval` 的插件列出还差哪几条必选能力并给「全部批准」;**撤销必选能力 = 插件停下来**,所以先弹二次确认卡片(撤销可选能力不确认)。
- **配置表单**:按清单的配置 schema(boolean / string / number / enum)渲染;标题与说明是插件自己的词条(`%key%` → 该插件 `Messages` 按「当前语言 → 同语种 → en-US → 去掉 `%` 的 key」解析);开关与枚举立即写,字符串 / 数字是草稿、点「应用」写;设备拒绝(`plugins.configurationInvalid`)回显在字段上并保留草稿;「恢复默认」= `Value = null`;设备侧的变化只更新没有未应用草稿的字段。
- **市场**:浏览 / 搜索 / 分类 / 分页 / 详情(版本、权限升级、自带技能);安装 owner;**设备没有账户令牌时 `plugins.authRequired` → 提示卡 +「去登录」**(页面只暴露 `SignInRequested` 事件,由外壳接到账户登录,令牌经 `account.attachDelegatedToken` 到位后再点安装即可,不用重启);更新检测显示每个插件新增的必选能力,本地装的(`FromMarket = false`)标注「不计入全部更新」。
- **活动与诊断**:最近的宿主调用(不含参数原文)、全部插件的诊断(`plugins.diag.*` 翻译 + 英文原文 + 路径,按 level 着色)、出错停止(faulted)的插件与「恢复」——**恢复 = 用现有的 `plugins.setEnabled` 先禁用再启用**(设备上没有单独的重启方法)。
- **待处理提示**(置顶卡片):`confirm` / `input` / `pick` / `permissions` / `exec` 五种;文字全是插件词条的 key;**执行类审批显示命令行与「设备 + 环境」(名字、类型、环境 id、设备 id)并用危险色**(AGENTS.md §5);member 起可答复,取消 = 全部缺省;`plugins.interactionNotFound`(别的控制端先回答了 / 超时)当作「已在另一台设备上处理」的竞态提示,不是故障,并立即对账。
- **插件命令**:每个展开的插件列出已注册的命令(标题经词条解析),member 可运行(以选中的项目为根)。**未做**:命令面板入口只留占位(文字 + 禁用按钮),命令面板本身不在本批;实时工具卡片的按钮动作(`plugins.cardAction`)属于对话转录里的卡片,不在设置页,Client 的 `PluginsActions.CardActionAsync` 已就绪。
