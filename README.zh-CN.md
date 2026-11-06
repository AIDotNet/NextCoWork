# NextCoWork

**编码型 Agent 的桌面工作台。**

NextCoWork 把 Agent 驱动的开发带到桌面端：多工作区并排开、一套完整工作台（编辑器、终端、Git、浏览器），以及一个权限模型真正可信的 Agent 内核 —— 全部收在一个原生应用里，支持 macOS、Windows 和 Linux。

[English](./README.md) | [简体中文](./README.zh-CN.md)

## 功能特性

- **工作区与会话** —— 多工作区以标签页并排打开，每个工作区可并行运行多个 Agent 会话，流式状态按会话隔离。
- **Agent 内核** —— 流式回复、带逐次审批链的工具调用、计划模式、子代理、后台任务、任务清单对账，以及定时运行。
- **内联可视化** —— Agent 可以在对话流里直接渲染会动的 HTML/SVG（图表、示意图、交互式 UI），运行在独立沙箱 iframe 里并跟随应用主题。
- **内置工作台** —— 文件树与编辑器（含插件提供的自定义编辑器）、真正的 PTY 终端、后台命令执行。
- **Git 集成** —— 状态、diff、日志、分支；暂存、提交、建分支、切分支。
- **内置浏览器** —— 按工作区分区，与你的浏览器标签页共享登录状态。
- **Skills 与 MCP** —— 基于 SKILL.md 的技能包，以及 Model Context Protocol 支持。
- **插件系统** —— 清单驱动、逐调用能力门控。插件可贡献命令、菜单、快捷键、自定义编辑器、视图、Web 应用、Agent 工具和捆绑技能。
- **原生体验** —— 深浅主题、强调色、动效档位、自动更新，简体中文 / English 完整双语言。

## 模型接入

登录 NextCoWork 账户即可使用内置模型渠道。在 <https://nextco.work> 注册后，从首次启动的欢迎页或**设置 → 账户**里点「登录」，授权在浏览器中完成。登录成功后会自动添加 **NextCoWork** 供应商（地址 `https://nextco.work/v1`，默认 OpenAI Responses 协议），并在首次登录时同步模型列表；之后可在「模型」页用「从服务商拉取模型列表」随时刷新。用量从同一账户页显示的余额中扣除。想用自己的服务商 —— OpenAI、Anthropic、DeepSeek，或者本地网关？在**设置 → 模型**里填自己的密钥即可。

## 下载

安装包发布平台：

- macOS（Apple Silicon 与 Intel，`.dmg`）
- Windows（`.exe` NSIS 安装器）
- Linux（`.AppImage`）

请访问 <https://nextco.work> 获取最新稳定版。

## 运行设备服务

Agent 不必跑在应用里。无界面的设备服务（无 Electron，要求 Node.js 24+）可以把业务库、工作区、Git 与终端放在另一台机器上，关掉桌面它也继续跑；桌面用固定证书的连接与它配对。

```bash
npm i -g @aidotnet/nextcowork-device-service
ncw-device-service diagnose
ncw-device-service --data-dir ~/ncw-device --host 0.0.0.0 --port 7801 --pairing-port 7800 --advertise nas.lan --invite
```

把打印出来的 `ncwpair1.…` 邀请粘贴到**设置 → 设备 → 添加设备**，核对指纹后配对。装成开机常驻的服务（systemd / LaunchDaemon / WinSW）、从已有桌面数据根原地接管、以及排障，见 [docs/device-service.md](./docs/device-service.md)。

## 开发

环境要求：Node.js `^20.19.0 || >=22.12.0` 与 npm。

```bash
npm install        # 会顺带执行 patch-package 和 electron-builder install-app-deps
npm run dev        # 以热重载方式启动应用
```

验证（与 CI 跑的同一套）：

```bash
npm run typecheck  # 主进程 + 渲染层 + shared 类型检查
npm test           # vitest
npm run lint       # eslint
```

打包：

```bash
npm run dist       # 为当前平台构建安装包
npm run dist:dir   # 免安装目录版，用于快速检查
```

## 项目结构

```
src/main        Electron 主进程 —— Agent 内核、插件宿主、IPC、存储、更新器
src/preload     渲染层与主进程之间的 context bridge
src/renderer    React 界面 —— 聊天、工作台、终端、Git、浏览器、设置
src/shared      跨进程共享的类型与纯函数
packages/       插件生态 —— plugin-api（类型）、plugin-cli、create-nextcowork-plugin
examples/       示例插件（自定义编辑器、Web 应用等）
resources/      应用图标、内置技能、插件运行时
```

## 编写插件

一条命令脚手架出可直接构建发布插件：

```bash
npm create nextcowork-plugin@latest
```

- 类型定义：[`@aidotnet/plugin-api`](./packages/plugin-api)
- 构建 / 打包 / 发布：[`@aidotnet/plugin-cli`](./packages/plugin-cli)（可执行名 `nextcowork-plugin`）
- 可运行的示例见 [`examples/`](./examples)

插件是一份 `package.json` 清单加可选代码：声明你要贡献什么（命令、自定义编辑器、视图、Agent 工具、Web 应用、技能……）以及需要哪些能力；运行时由宿主逐调用执行权限门控。

## 许可证

本项目基于 [Apache License 2.0](./LICENSE) 许可。

`packages/` 下的插件生态包（`@aidotnet/plugin-api`、`@aidotnet/plugin-cli`、`create-nextcowork-plugin`）以 MIT 许可分发 —— 见各包的 `package.json`。
