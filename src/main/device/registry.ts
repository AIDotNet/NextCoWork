/**
 * 控制端本地的「已配对设备清单」。这是 controller-local 的数据(计划:设备清单、配对凭据属于控制端本机),
 * 不进业务库、不随设备数据备份。
 *
 * - 清单(非机密:端点、证书、信任根指纹)存成 `devices.json`(0600,原子写)。
 * - **控制端私钥只进安全存储**(`SecretPort`,桌面用主进程凭据库),清单里没有它。
 * - 「移除连接」只删本机这一份凭据,**不会**通知设备撤销配对 —— 撤销在设备上做,两件事分开。
 * - 清单损坏时拒绝使用并保留原文件,不静默重建(那会让用户丢掉所有配对而不自知)。
 */
import { randomUUID } from 'node:crypto'
import { chmodSync, existsSync, mkdirSync, readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { z } from 'zod'
import { DeviceProtocolError } from '../../shared/device/errors'
import { deviceIdSchema } from '../../shared/device/identity'
import type { PairedCredentials } from '../../transport/node/pairing-client'

export interface SecretPort {
  get(ref: string): Promise<string | null>
  set(ref: string, value: string): Promise<void>
  remove(ref: string): Promise<void>
}

/** 自部署中转(可选):只在直连不通时作为备用路径。中转只转发密文,身份与机密性仍由设备证书的端到端握手保证。 */
const relaySchema = z.strictObject({
  url: z.string().max(512).superRefine((value, ctx) => {
    try {
      const url = new URL(value)
      if (url.protocol !== 'wss:' || url.username !== '' || url.password !== '' || url.search !== '' || url.hash !== '') throw new Error('bad')
    } catch { ctx.addIssue({ code: 'custom', message: 'Relay must be a credential-free wss:// URL' }) }
  }),
  /** 中转用自签证书时的信任根(PEM);缺省走系统信任库。 */
  caPem: z.string().max(8192).optional()
})
export type DeviceRelay = z.infer<typeof relaySchema>

const deviceEntrySchema = z.strictObject({
  id: deviceIdSchema,
  label: z.string().min(1).max(128),
  /** 邀请里的直连端点(host:port)。 */
  endpoints: z.array(z.string().min(3).max(260)).min(1).max(8),
  caPem: z.string().min(1).max(8192),
  certificatePem: z.string().min(1).max(8192),
  trustedCaPublicKeySha256: z.string().regex(/^[0-9a-f]{64}$/),
  pairedAt: z.number().int().nonnegative(),
  relay: relaySchema.optional()
})
const indexSchema = z.strictObject({
  version: z.literal(1),
  controllerId: deviceIdSchema,
  /** 本端所选设备;null = 本机。 */
  selected: deviceIdSchema.nullable(),
  devices: z.array(deviceEntrySchema).max(64)
})
type Index = z.infer<typeof indexSchema>
export type RegisteredDevice = z.infer<typeof deviceEntrySchema>

const secretRef = (id: string): string => `controller-device-key:${id}`

export class DeviceRegistry {
  private index: Index
  private readonly file: string

  constructor(private readonly dir: string, private readonly secrets: SecretPort, private readonly now: () => number = Date.now) {
    this.file = join(dir, 'devices.json')
    this.index = this.load()
  }

  private load(): Index {
    if (!existsSync(this.file)) return { version: 1, controllerId: `ncw-${randomUUID()}`, selected: null, devices: [] }
    let raw: unknown
    try { raw = JSON.parse(readFileSync(this.file, 'utf8')) } catch {
      throw new DeviceProtocolError('internal', 'The paired device list is unreadable and was left untouched')
    }
    const parsed = indexSchema.safeParse(raw)
    if (!parsed.success) throw new DeviceProtocolError('internal', 'The paired device list is unreadable and was left untouched')
    return parsed.data
  }

  private save(): void {
    mkdirSync(this.dir, { recursive: true })
    const temp = `${this.file}.${process.pid}.tmp`
    writeFileSync(temp, `${JSON.stringify(this.index, null, 2)}\n`, { mode: 0o600 })
    try { renameSync(temp, this.file) } catch (error) { rmSync(temp, { force: true }); throw error }
    try { chmodSync(this.file, 0o600) } catch { /* Windows 无 POSIX 权限位。 */ }
  }

  /** 本控制端在所有设备上的身份;首次使用时生成并持久化(设备按它登记配对)。 */
  controllerId(): string {
    if (!existsSync(this.file)) this.save()
    return this.index.controllerId
  }

  list(): RegisteredDevice[] { return this.index.devices.map((device) => ({ ...device, endpoints: [...device.endpoints] })) }
  get(id: string): RegisteredDevice | undefined { return this.list().find((device) => device.id === id) }
  selected(): string | null { return this.index.selected }

  /** 新增或(同一台设备重新配对时)替换。私钥先进安全存储,再写清单:中途失败不会留下没有私钥的条目。 */
  async add(credentials: PairedCredentials, label: string): Promise<RegisteredDevice> {
    const previous = this.index.devices.find((device) => device.id === credentials.deviceId)
    const entry: RegisteredDevice = deviceEntrySchema.parse({
      id: credentials.deviceId, label, endpoints: credentials.device, caPem: credentials.caPem,
      certificatePem: credentials.certificatePem, trustedCaPublicKeySha256: credentials.trustedCaPublicKeySha256, pairedAt: this.now(),
      // 重新配对不丢用户配过的中转。
      ...(previous?.relay !== undefined && { relay: previous.relay })
    })
    await this.secrets.set(secretRef(entry.id), credentials.privateKeyPem)
    this.index = { ...this.index, devices: [...this.index.devices.filter((device) => device.id !== entry.id), entry] }
    this.save()
    return entry
  }

  /** 设置或清除(`null`)这台设备的备用中转。URL 必须是不带凭据/查询的 wss。 */
  setRelay(id: string, relay: DeviceRelay | null): RegisteredDevice {
    const current = this.index.devices.find((device) => device.id === id)
    if (current === undefined) throw new DeviceProtocolError('not_found', 'Device is not paired')
    const { relay: _previous, ...rest } = current
    let next: RegisteredDevice
    if (relay === null) next = rest
    else {
      const parsed = relaySchema.safeParse(relay)
      if (!parsed.success) throw new DeviceProtocolError('invalid_request', 'Relay must be a credential-free wss:// URL')
      next = { ...rest, relay: parsed.data }
    }
    this.index = { ...this.index, devices: this.index.devices.map((device) => device.id === id ? next : device) }
    this.save()
    return next
  }

  async credentials(id: string): Promise<PairedCredentials | undefined> {
    const entry = this.index.devices.find((device) => device.id === id)
    if (entry === undefined) return undefined
    const privateKeyPem = await this.secrets.get(secretRef(id))
    if (privateKeyPem === null) throw new DeviceProtocolError('unauthorized', 'The device key is missing; pair the device again')
    return { deviceId: id, privateKeyPem, certificatePem: entry.certificatePem, caPem: entry.caPem,
      trustedCaPublicKeySha256: entry.trustedCaPublicKeySha256, device: [...entry.endpoints] }
  }

  /** 选择本端使用的设备;`null` = 本机。设备必须在清单里。 */
  select(id: string | null): void {
    if (id !== null && !this.index.devices.some((device) => device.id === id)) throw new DeviceProtocolError('not_found', 'Device is not paired')
    this.index = { ...this.index, selected: id }
    this.save()
  }

  /** 移除本机保存的这份连接(含私钥)。返回是否确实存在。不影响设备上的配对记录。 */
  async remove(id: string): Promise<boolean> {
    const existed = this.index.devices.some((device) => device.id === id)
    if (!existed) return false
    this.index = { ...this.index, selected: this.index.selected === id ? null : this.index.selected,
      devices: this.index.devices.filter((device) => device.id !== id) }
    this.save()
    await this.secrets.remove(secretRef(id))
    return true
  }
}
