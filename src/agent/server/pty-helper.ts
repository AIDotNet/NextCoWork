/**
 * node-pty 的原生 helper(`spawn-helper`)必须带执行位,伪终端才拉得起来。
 *
 * 桌面端由 electron-builder 重新编译 node-pty,执行位一直是好的;但**独立部署**的包不是这样:
 * npm ≥ 12 默认不执行依赖的 install script(`allow-scripts` 之外一律拦掉,只 warn 不报错),
 * 于是从 tgz / `npm i -g` 装出来的 node-pty 只剩 644 的 `prebuilds/<平台>-<架构>/spawn-helper`,
 * `pty.spawn()` 直接失败在 `posix_spawnp failed` —— 报错里看不出真正的原因,也不知道该改哪一个文件。
 *
 * 所以这里在使用之前自己补一次执行位。判据与 node-pty 的 `lib/utils.js:loadNativeModule` 一致:
 * helper 与 `pty.node` 在**同一个目录**,而目录按 `build/Release` → `build/Debug` → `prebuilds/<平台>-<架构>`
 * 的顺序找 —— 补错目录等于没补。
 *
 * 已经可执行时一个字节都不改(别去动 asar/已签名的副本);没有 node-pty、平台没有 helper 时也不算问题,
 * 真正的结论由调用方(pty 探测、终端本身)给出。**不抛异常**:补不上执行位只是终端不可用,不该拦住服务启动。
 */
import { chmodSync, existsSync, statSync } from 'node:fs'
import { createRequire } from 'node:module'
import { dirname, join } from 'node:path'

export interface PtyHelperOptions {
  /** 默认 `process.platform`;用来判断这条链路是否成立(Windows 不看执行位)。 */
  platform?: NodeJS.Platform
  /** 默认 `process.arch`;prebuilds 目录名的一部分。 */
  arch?: string
  /** 默认按产物自身位置解析;测试里注入假包目录。 */
  resolve?: (specifier: string) => string
}

const resolveFromHere = (specifier: string): string => createRequire(import.meta.url).resolve(specifier)

/**
 * 返回 `null` = 不用管(已可执行 / 没装 / 这个平台用不着);返回字符串 = **补不上的原因**,
 * 调用方应当原样转述给用户(里面已经带上要执行的那条命令)。
 */
export function ensurePtyHelperExecutable(options: PtyHelperOptions = {}): string | null {
  const platform = options.platform ?? process.platform
  if (platform === 'win32') return null
  const arch = options.arch ?? process.arch
  const resolve = options.resolve ?? resolveFromHere
  try {
    let root: string
    try {
      root = dirname(resolve('node-pty/package.json'))
    } catch {
      return null // 没装 node-pty:谁用谁报,不在这里编一个结论
    }
    for (const relative of ['build/Release', 'build/Debug', `prebuilds/${platform}-${arch}`]) {
      const native = join(root, relative)
      if (!existsSync(join(native, 'pty.node'))) continue
      const helper = join(native, 'spawn-helper')
      if (!existsSync(helper)) return null
      const mode = statSync(helper).mode
      if ((mode & 0o111) !== 0) return null
      chmodSync(helper, mode | 0o111)
      return null
    }
    return null // 没有原生模块:node-pty 自己会在 import 时报「装不上」
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error)
    return `node-pty's spawn-helper could not be made executable (${reason})`
  }
}
