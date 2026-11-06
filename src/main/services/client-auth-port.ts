/**
 * 「当前设备上的平台账号」端口。
 *
 * ★ 技能市场安装要平台访问令牌;而登录服务(`ipc/client-auth.ts`)牵着账号切换、插件宿主等一大片桌面
 *   专属逻辑。把「是否已登录」「取访问令牌」抽成端口,技能逻辑就不必为此把整个登录服务(及其 Electron 依赖)
 *   拉进来。桌面入口装上真实实现;无头服务不装 = 未登录(设备上的账号归设备,配对不要求官方登录)。
 */
export interface ClientAuthPort {
  authenticated(): boolean
  accessToken(): Promise<string | null>
}

let port: ClientAuthPort | null = null

export function installClientAuthPort(next: ClientAuthPort | null): void {
  port = next
}

export function isClientAuthenticated(): boolean {
  return port?.authenticated() ?? false
}

export async function clientAccessToken(): Promise<string | null> {
  return port === null ? null : port.accessToken()
}
