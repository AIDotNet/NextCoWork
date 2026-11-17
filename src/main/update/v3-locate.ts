/**
 * 找这台电脑上装好的 NextCoWork 3.x,给「数据已由 3.0 接管」对话框的「打开 3.0」按钮用。
 *
 * 3.x 用 Velopack 安装,装出来的应用里一定有 `sq.version`(Velopack 的版本清单);2.x(electron-builder)没有。
 * 靠这个文件区分,而不是靠路径:macOS 上 3.0 和 2.x 的 bundle 名字、bundle id 都一样(`NextCoWork.app`,
 * `com.nextcowork.app`),3.0 正常就装在 2.x 原来的位置。
 *
 * - macOS:`/Applications/NextCoWork.app`、`~/Applications/NextCoWork.app`,`Contents/MacOS/sq.version`。
 *   排除当前正在运行的这个 bundle。
 * - Windows:Velopack 装到 `%LocalAppData%\NextCoWork\current\`(2.x 的 NSIS 在 `%LocalAppData%\Programs\` 下,不冲突)。
 * - Linux:AppImage 放在哪都可以,找不到 → 返回 null(对话框只给「下载 3.0」)。
 *
 * 本文件零 Electron;文件系统由调用方注入,便于测试。
 */
import { join, sep } from 'node:path'

export interface V3App {
  /** 要启动的东西:macOS 是 `.app` 目录,Windows 是可执行文件 */
  path: string
}

export interface LocateDeps {
  platform: string
  home: string
  localAppData: string | undefined
  /** 当前进程的可执行文件(`process.execPath`),用来排除自己 */
  execPath: string
  exists: (path: string) => boolean
}

export function findV3App(deps: LocateDeps): V3App | null {
  if (deps.platform === 'darwin') {
    for (const app of ['/Applications/NextCoWork.app', join(deps.home, 'Applications', 'NextCoWork.app')]) {
      if (deps.execPath.startsWith(app + sep) || deps.execPath.startsWith(app + '/')) continue
      if (deps.exists(join(app, 'Contents', 'MacOS', 'sq.version'))) return { path: app }
    }
    return null
  }
  if (deps.platform === 'win32' && deps.localAppData) {
    const current = join(deps.localAppData, 'NextCoWork', 'current')
    const exe = join(current, 'NextCoWork.exe')
    if (deps.exists(join(current, 'sq.version')) && deps.exists(exe) && exe !== deps.execPath) return { path: exe }
    return null
  }
  return null
}
