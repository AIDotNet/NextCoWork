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
- **完成**:`[打开本机文件夹]`(走 `ProjectActions.AddLocalProjectAsync`,和项目栏的「添加项目」同一个意图);**连接 SSH 主机、创建云端 Agent 这两项只是占位**(「稍后在设置里」/「即将推出」,不可点)——云端 Agent 属于 M6,SSH 主机的添加入口随执行环境设置页(06)接。
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

1. **设置是一个标签页**,在主工作区里打开,不再是 2.x 那种覆盖层。手机上在「我」页里以导航栈的形式进入。
2. **明确作用域**。因为有多台设备,每一项设置都属于以下三个作用域之一,并在页面上标出来:
   - **本应用**:只影响这个控制端(外观、快捷键、通知)。布局类设置通过账户同步到其他控制端。
   - **设备**:存在某个 Agent 上(模型、扩展、执行环境、数据)。页面顶部有设备切换器 `[本机 ▾]`。设备离线时这部分只读。
   - **项目**:存在项目里(权限、指令、Hooks 覆盖)。
3. **改了立即生效**。不需要「保存」按钮,改动后弹出带「撤销」的提示。
4. **可以搜索**。左上角搜索框可以搜到每一项设置,包括它的说明文字。
5. **遵守状态规则**。设置的值不在组件里缓存:本应用的设置在 `PrefsStore`,设备的设置以 Agent 为准。

### 信息架构

```
┌─────────────────────┬──────────────────────────────────────────────────┐
│ 🔍 搜索设置           │  模型 · 设备 [💻 本机 ▾]                             │
│                     │  ──────────────────────────────────────────────  │
│ 本应用               │  供应商                                    [+ 添加] │
│   账户               │   ● NextCoWork        已登录 · 32 个模型           │
│   通用               │   ● Anthropic         密钥 ••••3f2a                │
│   外观               │                                                  │
│   快捷键             │  模型目录                                           │
│   通知               │   来源 models.dev · 上次更新 2 小时前 · 2,314 个模型  │
│ 设备 [本机 ▾]         │   [立即更新]   自动更新 [每 6 小时 ▾]                 │
│ ▸ 模型               │                                                  │
│   Agent 配置         │  默认模型                                           │
│   执行环境           │   对话 [claude-… ▾]   子代理 [… ▾]   标题生成 [… ▾]    │
│   扩展               │                                                  │
│   连接与远程          │                                                  │
│   数据               │                                                  │
│ 项目 [NextCoWork ▾]  │                                                  │
│   项目设置           │                                                  │
│ ───                 │                                                  │
│   诊断               │                                                  │
│   关于               │                                                  │
└─────────────────────┴──────────────────────────────────────────────────┘
```

| 分组 | 作用域 | 内容 |
|---|---|---|
| 账户 | 本应用 | 账户信息、已登录的控制端、退出登录(**不涉及支付**) |
| 通用 | 本应用 | 语言、启动行为、关窗后本机 Agent 是否继续运行 |
| 外观 | 本应用 | 主题、强调色、字体、界面密度、动效级别(**动效已实现**:跟随系统 / 完整 / 减弱 / 关闭,存在 `UiPrefs.Motion`,见 [03 §9](03-ui-layout.md#9-动效统一的动效系统);其余项未做) |
| 快捷键 | 本应用 | 查看和修改快捷键,可以录制 |
| 通知 | 本应用 | 系统通知和推送:审批、Run 完成、Run 失败 |
| 模型 | 设备 | 供应商、模型目录(models.dev 的状态和更新,见 [02 A8](02-agent.md#a8-模型目录modelsdev-自动更新))、默认模型、别名 |
| Agent 配置 | 设备 | Profile、默认权限模式、子代理、上下文压缩 |
| 执行环境 | 设备 | SSH 主机、密钥、已知主机(见 [06](06-environments-ssh.md)) |
| 扩展 | 设备 | Skills、MCP、插件、Hooks |
| 连接与远程 | 设备 | **允许远程连接**开关、端口、仅允许局域网地址、**中继地址**(默认空 = 关闭,带注册状态;见 [09 §9](09-protocol.md#9-中继m6已实现ncwrelay--relaytransport))、配对码和二维码、本机局域网地址列表、mDNS 广播、已配对的控制端(改角色、吊销)、开放网关(见 [09 §3](09-protocol.md#3-局域网远程连接)) |
| 数据 | 设备 | 数据目录、备份、导出、2.x 接管记录、回退到 2.x |
| 项目设置 | 项目 | 权限规则、指令文件、Hooks 覆盖、执行环境 |
| 诊断 | 本应用加设备 | 日志查看和导出、连接测试、导出诊断包(替代 2.x 的 `diagnose` 命令) |
| 关于 | 本应用 | 版本、**更新通道**(稳定版 / 测试版;从「通用」挪到这里,更新相关的都在一处)、检查更新、下载进度、立即重启并更新、必需更新的阻断提示、签名校验失败提示、上次检查时间(**已实现**,见 [04 §7.4](04-update-install.md#74-自更新运行时m7ncw-core--ncw-clientupdates--ncw-desktopupdates));许可证未做 |

### 布局

- 桌面端:左侧导航 220px,内容区最大宽度用 Arc 的 `ContentMaxWidthMD`(760)。每一行是「标题 + 说明 + 控件」。
  危险操作(删除数据、回退)放在每页最底下的「危险区」里,需要二次确认。
- 手机端:分组列表,点击后推入详情页。设备作用域的页面顶部同样有设备切换器。

### 模型页(已实现的部分)

「设备 · 模型与供应商」页:顶部设备切换器;供应商列表(启停、编辑、删除、连通性测试、OAuth 登录 / 退出)、凭据(**只写不读**:输入框保存后立即清空,列表只显示后四位)、
模型别名(启停、重命名、删除、从上游导入)、模型目录状态(来源、上次更新、手动「立即更新」、自动更新开关与间隔)。OAuth 的授权页和配对码由**控制端**展示并打开浏览器(设备可能在另一台电脑上)。
设备的事实都在 Store(`providers` / `catalog` 主题的镜像),页面不缓存配置;凭据摘要(后四位)是按需读取的一次性结果。非 owner 角色只读。「默认模型」(对话 / 子代理 / 标题生成)和别名的高级字段(协议覆盖、思考配置)随 Agent 配置页一起做。


### 设备 / 项目作用域的管理页(已实现的部分)

「设备」分组下的 **通用、技能、命令、Hooks、MCP 服务器、用量、存储、网关** 和「项目」分组下的 **项目设置**,与模型页同一结构:页头有设备切换器(项目页再加项目切换器,两者是 `SettingsScope` 这一份纯 UI 状态)、本控制端在该设备上的角色和连接状态。
窄于 760px 时左侧导航收成 56px 的图标栏(文字藏起来,图标有提示)。

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
