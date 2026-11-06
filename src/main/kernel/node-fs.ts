/**
 * `KernelFs` 的纯 Node 实现 —— 端口的**真实默认值**,不是测试替身。
 *
 * 单独一个文件而不是塞进 `host.ts`:`host.ts` 是**端口的定义**,读它的人想知道
 * 「内核对外界开了哪些口子」;实现细节(断链软链怎么办、二进制怎么探)混进去
 * 会把那份清单埋掉。`node-spawn.ts` 同理。
 *
 * ★ 零 electron import —— 所以 `electronHost()` 不必覆盖 fs,vitest 里也是同一份实现。
 * `path-guard.ts` 早就直接 `import { realpathSync } from 'node:fs'`,「内核不碰 node
 * 内置模块」这条从来不成立;成立的是「内核不碰 electron」。
 */
import { promises as fsp } from 'node:fs'
import { dirname } from 'node:path'
import type { KernelFs } from './host'
import { writeStreamToFile, type StreamSink, type StreamTarget } from './stream-write'

/** 二进制探测的默认取样长度。够看清 ELF/PNG/zip 头和前几行文本,又不至于读大。 */
const SNIFF_BYTES = 4096

export function nodeFs(): KernelFs {
  return {
    readFile: (abs) => fsp.readFile(abs, 'utf8'),

    writeFile: (abs, content) => fsp.writeFile(abs, content, 'utf8'),

    /**
     * ★ `withFileTypes` 之后**对软链再 stat**,而不是直接信 `d.isDirectory()`。
     *
     * `readdir` 给的是 lstat 语义:指向目录的软链会被报成「文件」,于是它在树里
     * 展不开、在 walk 里被当成叶子。`stat` 跟随软链,与 `resolveInWorkspace` 的
     * realpath 语义一致 —— 两处必须是同一个世界观。
     *
     * 而 stat 会对**断掉的**软链抛错,所以那一项吞掉异常按文件处理:
     * 一个坏软链不该让整次列目录失败(`ipc/workspace.ts` 已经写下过这条教训)。
     *
     * 原先是**逐项**都 stat;现在只对软链 stat。非软链项的 lstat 与 stat 结果相同,
     * 所以上面那条世界观不变,而 `Grep`/`Glob`/`@` 检索遍历一个两万文件的仓库
     * 少掉两万次系统调用。文件系统报不出类型(DT_UNKNOWN)时 Node 自己会补 lstat,
     * 仍落在「非软链可信、软链再 stat」这两条路径里。
     */
    async readDir(abs) {
      const raw = await fsp.readdir(abs, { withFileTypes: true })
      return Promise.all(
        raw.map(async (d) => {
          if (!d.isSymbolicLink()) return { name: d.name, isDir: d.isDirectory() }
          try {
            const st = await fsp.stat(`${abs}/${d.name}`)
            return { name: d.name, isDir: st.isDirectory() }
          } catch {
            return { name: d.name, isDir: false }
          }
        })
      )
    },

    async stat(abs) {
      const st = await fsp.stat(abs)
      return { size: st.size, mtimeMs: st.mtimeMs, isDir: st.isDirectory() }
    },

    realpath: (abs) => fsp.realpath(abs),

    /**
     * ★ 只读前 `maxBytes` 个**字节**,不经过 utf8 解码。
     *
     * 二进制探测必须在解码之前做完:走 `readFile` 的话,一个 300MB 的 .pack 会先被
     * 解成一堆 U+FFFD 再被判定为二进制 —— 判断是对的,代价是主进程刚才卡了两秒
     * 并分配了 600MB。所以是 open + read(n) + close,不是 readFile 之后 slice。
     */
    async readFileBytes(abs, maxBytes = SNIFF_BYTES) {
      const fh = await fsp.open(abs, 'r')
      try {
        const buf = Buffer.allocUnsafe(maxBytes)
        const { bytesRead } = await fh.read(buf, 0, maxBytes, 0)
        return new Uint8Array(buf.buffer, buf.byteOffset, bytesRead)
      } finally {
        await fh.close()
      }
    },

    /**
     * 建**父目录**,不是建 `abs` 自己。
     *
     * 调用方是 `write_file`,它手上的是目标文件的路径。让它自己 `dirname` 一次的话,
     * 下一个调用方多半会忘 —— 症状是模型写 `src/a/b/c.ts` 永远 ENOENT,而它会
     * 反复重试同一个调用。
     */
    async mkdirp(abs) {
      await fsp.mkdir(dirname(abs), { recursive: true })
    },

    /**
     * ★ 独立一个方法,而不是让调用方 `stat().catch(() => false)`。
     *
     * 用抛错表达「不存在」的话,每个调用点都要写一次 try/catch,而其中一个漏写
     * 就会把 ENOENT 变成一次工具崩溃。存在性是个问句,不是个异常。
     */
    async exists(abs) {
      try {
        await fsp.access(abs)
        return true
      } catch {
        return false
      }
    },

    /**
     * 需求:`SaveImage` 在本地工作区写图片字节。`exclusive` 映射到 `wx`,
     * 让「不覆盖」由文件系统原子地判定,而不是先 exists 再写(两步之间可能被抢先)。
     */
    async writeBytes(abs, bytes, options = {}) {
      await fsp.writeFile(abs, bytes, { flag: options.exclusive === true ? 'wx' : 'w', ...(options.mode === undefined ? {} : { mode: options.mode }) })
    },

    /**
     * 需求:`SaveVideo` 把几十到几百兆的视频写进工作区(见 `host.ts` 上那段理由)。
     * 本地这一份就是把 `fsp.open` 拿到的 FileHandle 包成 `StreamSink`。
     */
    writeStream: (abs, source, options, signal) =>
      writeStreamToFile(localStreamTarget(), abs, source, options, signal)
  }
}

/** 本地工作区的流式写目标 —— 逻辑在 `stream-write.ts`,这里只提供文件系统动作。 */
function localStreamTarget(): StreamTarget {
  return {
    async openSink(path, exclusive, mode): Promise<StreamSink> {
      const handle = await fsp.open(path, exclusive ? 'wx' : 'w', mode)
      return {
        write: async (chunk) => { await handle.write(chunk) },
        sync: async () => { await handle.sync() },
        close: async () => { await handle.close() }
      }
    },
    rename: async (from, to, replace) => {
      void replace
      await fsp.rename(from, to)
    },
    unlink: (path) => fsp.unlink(path),
    exists: async (path) => {
      try { await fsp.stat(path); return true } catch { return false }
    },
    mkdirp: async (path) => { await fsp.mkdir(dirname(path), { recursive: true }) }
  }
}
