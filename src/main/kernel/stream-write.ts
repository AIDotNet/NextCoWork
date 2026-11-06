/**
 * 流式写文件 —— `KernelFs.writeStream` 的两份实现共用这一段。
 *
 * ★★ **为什么不放在两个环境里各写一遍。** 本地(`environment/local.ts`)与 SSH
 * (`environment/ssh/sftp.ts`)的"怎么打开一个文件、怎么写一块"完全不同,但
 * **这段逻辑(临时名 → 边拉边限流 → rename → 失败清理)两边必须逐字相同**:
 * 它守的是同一组不变式 —— 上限、不覆盖旧文件、中断留不下半截。
 * 抄两份的话,下次修其中一处(比如"超限时该先 unlink 再抛")另一处就落后,
 * 而症状是"本地存视频没问题,SSH 上留下一个打不开的半截文件"。
 *
 * ★ 两个环境的差异收在 `sink` 这个回调里:本地是 fs 的 write,SSH 是 sftp 的
 * `writeData`。它拿到的是一块 `Uint8Array`,返回是否已消费完(或抛错)。
 */
import { randomUUID } from 'node:crypto'
import { dirname, join } from 'node:path'

export interface StreamSink {
  /** 写一块。实现必须**有背压**地写完再返回。 */
  write(chunk: Uint8Array): Promise<void>
  /** 把已写内容刷到盘上;不支持时静默返回。 */
  sync(): Promise<void>
  /** 关闭句柄(失败路径也要能调到,且不抛)。 */
  close(): Promise<void>
}

export interface StreamTarget {
  openSink(path: string, exclusive: boolean, mode: number): Promise<StreamSink>
  rename(source: string, destination: string, replace: boolean): Promise<void>
  unlink(path: string): Promise<void>
  exists(path: string): Promise<boolean>
  mkdirp(path: string): Promise<void>
}

/**
 * 把 `source` 写进 `absPath`。
 *
 * ★ 返回 `{ ok: false, reason }` 而不是抛:**"视频太大了"和"磁盘满了"对调用方
 * 是同一类事**(这一条写失败了),而工具层要把它们翻成一句给模型看的话。
 * 抛异常会让 `defineTool` 把它包成 `Tool execution failed: …` —— 那句话对
 * "文件太大"这种完全可以预期的失败来说太难读了。
 */
export async function writeStreamToFile(
  target: StreamTarget,
  absPath: string,
  source: ReadableStream<Uint8Array>,
  options: { exclusive?: boolean; mode?: number; maxBytes: number },
  signal: AbortSignal
): Promise<{ ok: true; size: number } | { ok: false; reason: string }> {
  const exclusive = options.exclusive === true
  /*
    ★ **覆盖与不覆盖是两条不同的路径**,而这是刻意的:
    - 不覆盖(`exclusive`):直接以独占方式打开**目标本身** —— "已存在就失败"
      由文件系统原子判定,没有 TOCTOU 窗口;
    - 覆盖:写**临时名**,成功才 rename 盖过去 —— 于是"写到一半断了"不会
      毁掉用户原来那个文件(这一点在视频上尤其重要:几百兆的写入失败概率不低)。

    ★ 临时名以 `.` 开头:它不会被 `ncw://` 寻址到(fileName 段校验不过),
      也与附件那边"残片按前缀认"的清理规则一致。
  */
  const temp = exclusive ? absPath : join(dirname(absPath), `.${randomUUID()}.part`)
  await target.mkdirp(absPath)

  let sink: StreamSink | null = null
  let total = 0
  const cleanup = async (): Promise<void> => {
    try { await sink?.close() } catch { /* 已经关了 */ }
    if (!exclusive) {
      try { await target.unlink(temp) } catch { /* 尽力而为 */ }
    } else {
      // 独占那条失败时目标文件本身就是半截的 —— 删掉它,不留垃圾
      try { await target.unlink(temp) } catch { /* 尽力而为 */ }
    }
  }

  try {
    sink = await target.openSink(temp, exclusive, options.mode ?? 0o600)
    const reader = source.getReader()
    for (;;) {
      signal.throwIfAborted()
      const { done, value } = await reader.read()
      if (done) break
      if (value === undefined) continue
      total += value.byteLength
      if (total > options.maxBytes) {
        await reader.cancel().catch(() => {})
        await cleanup()
        return { ok: false, reason: `The video exceeds the ${sizeText(options.maxBytes)} limit.` }
      }
      await sink.write(value)
    }
    await sink.sync()
    await sink.close()
    sink = null
    if (total === 0) {
      await cleanup()
      return { ok: false, reason: 'The video was empty.' }
    }
    if (!exclusive) {
      // ★ 到这里源已经完整读完了,才允许覆盖目标
      await target.rename(temp, absPath, true)
    }
    return { ok: true, size: total }
  } catch (error) {
    await cleanup()
    if (signal.aborted) throw error
    const message = error instanceof Error ? error.message : String(error)
    return { ok: false, reason: `Writing the video failed: ${message}` }
  }
}

function sizeText(bytes: number): string {
  return `${String(Math.round(bytes / 1024 / 1024))} MB`
}
