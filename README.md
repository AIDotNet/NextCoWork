# NextCoWork

**A desktop workspace for coding agents.**

NextCoWork brings agent-driven development to the desktop: multiple workspaces side by side, a full workbench (editor, terminal, Git, browser), and an agent kernel with a permission model you can actually trust — all in one native app for macOS, Windows and Linux.

[English](./README.md) | [简体中文](./README.zh-CN.md)

## Features

- **Workspaces & sessions** — open multiple workspaces in tabs, run parallel agent sessions per workspace, and keep streaming state isolated per session.
- **Agent kernel** — streaming replies, tool calls with a per-call approval chain, plan mode, subagents, background tasks, todo reconciliation, and scheduled runs.
- **Inline visualization** — the agent can render live HTML/SVG widgets (charts, diagrams, interactive UI) directly inside the conversation, sandboxed in their own iframe and following the app theme.
- **Built-in workbench** — file explorer and editors (including plugin-provided custom editors), a real PTY terminal, and background command execution.
- **Git integration** — status, diff, log and branches; stage, commit, create and switch branches.
- **Built-in browser** — per-workspace browser partitions that share login state with your tabs.
- **Skills & MCP** — SKILL.md-based skill packages plus Model Context Protocol support.
- **Plugins** — a manifest-driven plugin system with per-call capability gates. Plugins can contribute commands, menus, keybindings, custom editors, views, web apps, agent tools and bundled skills.
- **Native experience** — light/dark themes, accent colors, motion-level control, auto-update, and full Simplified Chinese / English localization.

## Model access

Sign in with your NextCoWork account to use the built-in model channel. Create an account at <https://nextco.work>, then sign in from the first-run welcome screen or from **Settings → Account** — authorization completes in your browser. Once signed in, the **NextCoWork** provider is added for you (base URL `https://nextco.work/v1`, OpenAI Responses by default) and your model list syncs on first sign-in; refresh it any time with **Fetch model list from provider** on the Models page. Usage is metered against your wallet balance, shown on the same Account page. Prefer a provider of your own — OpenAI, Anthropic, DeepSeek, a local gateway? Add it in **Settings → Models** with your own key.

## Download

Installers are published for:

- macOS (Apple Silicon & Intel, `.dmg`)
- Windows (`.exe` NSIS installer)
- Linux (`.AppImage`)

Visit <https://nextco.work> for the latest stable release.

## Run a device service

The agent does not have to run inside the app. A headless device service (no Electron, Node.js 24+) can hold the database, the workspaces, Git and the terminals on another machine and keep running while the desktop is closed; the desktop pairs with it over a certificate-pinned connection.

```bash
npm i -g @aidotnet/nextcowork-device-service
ncw-device-service diagnose
ncw-device-service --data-dir ~/ncw-device --host 0.0.0.0 --port 7801 --pairing-port 7800 --advertise nas.lan --invite
```

Paste the printed `ncwpair1.…` invitation into **Settings → Devices → Add device**, check the fingerprint, then pair. Installing it as a system service (systemd / LaunchDaemon / WinSW), taking over an existing desktop data root, and troubleshooting are covered in [docs/device-service.md](./docs/device-service.md).

## Development

Requirements: Node.js `^20.19.0 || >=22.12.0` and npm.

```bash
npm install        # also runs patch-package and electron-builder install-app-deps
npm run dev        # start the app with hot reload
```

Verification (the same suite CI runs):

```bash
npm run typecheck  # typecheck main + renderer + shared
npm test           # vitest
npm run lint       # eslint
```

Packaging:

```bash
npm run dist       # build installers for the current platform
npm run dist:dir   # unpacked build, for quick checks
```

## Project structure

```
src/main        Electron main process — agent kernel, plugin host, IPC, storage, updater
src/preload     Context bridge between renderer and main
src/renderer    React UI — chat, workbench, terminal, Git, browser, settings
src/shared      Types and pure functions shared across processes
packages/       Plugin ecosystem — plugin-api (types), plugin-cli, create-nextcowork-plugin
examples/       Sample plugins (custom editors, web apps, …)
resources/      App icons, bundled skills, plugin runtime
```

## Writing plugins

Scaffold a plugin that is ready to build and publish:

```bash
npm create nextcowork-plugin@latest
```

- Type definitions: [`@aidotnet/plugin-api`](./packages/plugin-api)
- Build / package / publish: [`@aidotnet/plugin-cli`](./packages/plugin-cli) (binary `nextcowork-plugin`)
- Working examples live in [`examples/`](./examples)

A plugin is a `package.json` manifest plus optional code: declare what you contribute (commands, custom editors, views, agent tools, web apps, skills, …) and the capabilities you need; the host enforces per-call permission gates at runtime.

## License

This project is licensed under the [Apache License 2.0](./LICENSE).

The plugin ecosystem packages under `packages/` (`@aidotnet/plugin-api`, `@aidotnet/plugin-cli`, `create-nextcowork-plugin`) are distributed under the MIT license — see each package's `package.json`.
