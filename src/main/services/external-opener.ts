/**
 * 「用系统浏览器打开一个地址」的端口。
 *
 * ★ 桌面入口装上 `shell.openExternal`;无头设备服务**没有**系统浏览器可开,调用会得到明确的错误,
 *   而不是静默什么都不发生。这样 `ipc/client-auth.ts` 等只在登录流程里要它的模块,不必为此值导入 Electron。
 */
let opener: ((url: string) => Promise<void>) | null = null

export function installExternalOpener(open: ((url: string) => Promise<void>) | null): void {
  opener = open
}

export async function openExternalUrl(url: string): Promise<void> {
  if (opener === null) throw new Error('No system browser is available on this device')
  await opener(url)
}
