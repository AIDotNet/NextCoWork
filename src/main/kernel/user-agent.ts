/**
 * 出站请求的 User-Agent —— **应用自报家门的那一行。**
 *
 * 形状是 `NextCoWork/0.1.1 (darwin; arm64)`:一个纯应用标识,不伪装成浏览器。
 * 不写它的话,Electron 会自己拼一个默认值,里面是 `nextcowork/0.1.1` 混着
 * `Chrome/…` 和 `Electron/…`(小写的应用名来自 package.json 的 `name`)。
 *
 * ★ **只装在「我们自己发的 API 请求」上**:模型上游(`upstream/router.ts`)、
 * 模型列表(`upstream/model-list.ts`)、搜索服务(`main/search/service.ts`)。
 * 这三条的对面都是 API,认的是 Key,不看 UA。
 *
 * ★★ **有一个例外,而且是反方向的:** OpenCode Go 把「用自己的 User-Agent,
 * 不是通用 SDK 或 HTTP 库名」列为**准入条件**之一(三条要求见
 * `shared/domain/presets.ts` 那条预设旁边的注释)。所以对那家而言,这一行不是
 * 可有可无的自报家门 —— 退回 Electron 的默认 UA 会让它开始拒,
 * 而报错里不会提到 UA 一个字。
 *
 * ★★ **另外三条路径刻意不动,每一条都有具体的失败模式:**
 *
 * | 不动的地方 | 改了会怎样 |
 * |---|---|
 * | 内置浏览器的 `<webview>` | 对面是给人看的网站。非浏览器 UA 会被 Cloudflare 之类挡在门外,或者拿到降级页面 —— 症状是「这个站打不开」,而没有一条线索指向 UA |
 * | `WebFetch` / `Browser` 工具 | 同上,它们抓的也是网页,不是 API |
 * | OAuth 的两条通道(`kernel/oauth/**`) | 上游对客户端标识很可能有白名单 —— 同一件事的 `originator` 已经踩过这条线(见 `oauth/issuers/chatgpt.ts` 文件头)。改错的表现是 403 或者静默降级 |
 *
 * ★ 2026-09-23 起有一处**按家分的**覆盖:codex(ChatGPT 订阅线)自带
 * `codex_cli_rs/<版本>` 的私货 UA,挂在 `oauth/issuers/chatgpt.ts` 的
 * `transport.headers`(业务请求)与 `oauthHeaders`(换 token / 刷新)上 ——
 * 和 kimi / zcode 是同一模式,不是这张表说的「全局改 UA」;
 * 授权 URL 那一跳仍然走浏览器 UA,表里第 1 行的理由照旧成立。
 *
 * 所以这里**不是** `app.userAgentFallback`:那一处是全局默认值,会把上面三条
 * 连同这三条一起改掉,而它们要的恰好是相反的东西。
 *
 * ★ install 一次的模块状态,和 `search/service.ts` 的 `installSearchConfig` 是同一个
 * 模式:版本号只有主进程拿得到(`app.getVersion()`),而内核不许 import electron。
 */

/** 没 install 过时的版本段。刻意是个一眼假的值,不是一个像模像样的 `0.0.0` */
const UNINSTALLED = 'unknown'

let appVersion = UNINSTALLED

/** `NextCoWork/0.1.1 (darwin; arm64)` */
export function userAgent(): string {
  return `NextCoWork/${appVersion} (${process.platform}; ${process.arch})`
}

/**
 * 主进程启动时装一次。
 *
 * 装晚了不报错,只是在那之前发出去的请求版本段是 `unknown` —— 所以调用点在
 * `main/index.ts` 的最前面那一段,和单实例锁、userData 改路径排在一起。
 */
export function installUserAgent(version: string): void {
  appVersion = version
}
