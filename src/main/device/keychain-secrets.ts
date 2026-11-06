/**
 * 控制端私钥的安全存储:系统钥匙串(Electron `safeStorage`)加密后,落在本机 `controller/secrets.json`。
 *
 * 为什么不用业务库里的凭据表:那张表属于**设备数据**(随设备备份/接管/迁移),而控制端私钥属于这台电脑,
 * 不该跟着业务数据走,更不该被备份导出。
 *
 * ★ 系统钥匙串不可用时 `set` 直接抛错 —— 绝不降级为明文落盘;配对会失败并提示,而不是留下不安全的私钥。
 * `safeStorage` 以参数注入:这个文件不 import Electron,测试用假实现。
 */
import { chmodSync, existsSync, mkdirSync, readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { z } from 'zod'
import { DeviceProtocolError } from '../../shared/device/errors'
import type { SecretPort } from './registry'

export interface KeychainPort {
  isEncryptionAvailable(): boolean
  encryptString(plain: string): Buffer
  decryptString(encrypted: Buffer): string
}

const fileSchema = z.strictObject({ version: z.literal(1), entries: z.record(z.string(), z.string()) })

export function keychainSecretPort(dir: string, keychain: KeychainPort): SecretPort {
  const file = join(dir, 'secrets.json')
  const read = (): Record<string, string> => {
    if (!existsSync(file)) return {}
    let raw: unknown
    try { raw = JSON.parse(readFileSync(file, 'utf8')) } catch {
      throw new DeviceProtocolError('internal', 'The controller key store is unreadable and was left untouched')
    }
    const parsed = fileSchema.safeParse(raw)
    if (!parsed.success) throw new DeviceProtocolError('internal', 'The controller key store is unreadable and was left untouched')
    return parsed.data.entries
  }
  const write = (entries: Record<string, string>): void => {
    mkdirSync(dir, { recursive: true })
    const temp = `${file}.${process.pid}.tmp`
    writeFileSync(temp, `${JSON.stringify({ version: 1, entries })}\n`, { mode: 0o600 })
    try { renameSync(temp, file) } catch (error) { rmSync(temp, { force: true }); throw error }
    try { chmodSync(file, 0o600) } catch { /* Windows 无 POSIX 权限位。 */ }
  }
  return {
    async get(ref) {
      const stored = read()[ref]
      if (stored === undefined) return null
      if (!keychain.isEncryptionAvailable()) throw new DeviceProtocolError('internal', 'The system keychain is not available')
      return keychain.decryptString(Buffer.from(stored, 'base64'))
    },
    async set(ref, value) {
      if (!keychain.isEncryptionAvailable()) throw new DeviceProtocolError('internal', 'The system keychain is not available; the device key was not saved')
      write({ ...read(), [ref]: keychain.encryptString(value).toString('base64') })
    },
    async remove(ref) {
      const entries = read()
      if (!(ref in entries)) return
      const { [ref]: _removed, ...rest } = entries
      write(rest)
    }
  }
}
