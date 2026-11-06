/**
 * 终端的 RPC。三条规矩:
 *  1. 终端属于设备:控制端断开不杀它(见 `TerminalManager`)。
 *  2. **输入过租约闸**:`write / resize / kill` 必须是 `terminal:<id>` 租约的当前持有者并带当前 fencing
 *     token —— 交接之后旧持有者迟到的按键不会插进新持有者的输入。观看(`attach / list`)不需要租约。
 *  3. 身份来自已认证连接(`context.binding.controllerId`),params 里没有任何身份字段。
 *
 * 仅本地工作区:远程(SSH)工作区的终端需要逐页审批流程,不经这里开放。
 */
import { randomUUID } from 'node:crypto'
import { z } from 'zod'
import { getWorkspaceEnvironment } from '../../../main/runtime'
import { store } from '../../../main/state/store'
import { DeviceProtocolError } from '../../../shared/device/errors'
import {
  DEVICE_METHODS, leaseResult, okResult, terminalsAttachParams, terminalsCreateParams, terminalsDetachParams, terminalsKillParams,
  terminalsListParams, terminalsResizeParams, terminalsWriteParams
} from '../../../shared/device/methods'
import type { DeviceValue } from '../../../shared/device/value'
import type { LeaseManager } from '../lease'
import { TerminalManager, type OpenedTerminal, type TerminalEvent } from '../terminals'
import type { DeviceRpcRouter } from '../rpc'

const anyValue = z.custom<DeviceValue>(() => true)
const plain = (value: unknown): DeviceValue => JSON.parse(JSON.stringify(value ?? null)) as DeviceValue

/** 生产用的终端管理器:通过工作区环境的 `openTerminal`(node-pty)起 shell。 */
export function createTerminalManager(): TerminalManager {
  return new TerminalManager({
    newId: () => `term-${randomUUID()}`,
    open: async (workspaceId, size): Promise<OpenedTerminal> => {
      const workspace = store.getWorkspace(workspaceId)
      if (workspace === undefined) throw new DeviceProtocolError('not_found', 'Workspace does not exist')
      // 先看工作区声明的环境,再去解析它:SSH 等远程环境的解析本身就可能触发凭据/连接流程。
      if (workspace.environment !== undefined && workspace.environment.kind !== 'local') {
        throw new DeviceProtocolError('not_configured', 'Remote workspaces do not expose terminals here')
      }
      const environment = getWorkspaceEnvironment(workspaceId)
      if (environment.remote) throw new DeviceProtocolError('not_configured', 'Remote workspaces do not expose terminals here')
      environment.assertReady()
      const cwd = environment.rootPath
      const process = await environment.openTerminal({ cols: size.cols, rows: size.rows, cwd })
      const shell = environment.terminalShell ?? environment.platform.shell
      return { process, cwd, shell, title: environment.path.basename(cwd) || shell }
    }
  })
}

export function registerTerminalMethods(router: DeviceRpcRouter, terminals: TerminalManager, leases: LeaseManager): () => void {
  router.execution(DEVICE_METHODS.terminalsCreate, terminalsCreateParams,
    z.strictObject({ terminal: anyValue, lease: leaseResult }), async (execution, p) => {
      const terminal = await terminals.create(p.workspaceId, p.cols, p.rows)
      // 创建者自动成为输入者;之后可以 handoff。
      const lease = leases.acquire('terminal', terminal.id, execution.binding.controllerId)
      return { terminal: plain(terminal), lease: { resource: lease.resource, token: lease.token, expiresAt: lease.expiresAt } }
    })

  router.query(DEVICE_METHODS.terminalsList, terminalsListParams, anyValue, (_context, p) => plain(terminals.list(p.workspaceId)))

  router.query(DEVICE_METHODS.terminalsAttach, terminalsAttachParams, anyValue, (context, p) => {
    const connection = context.connection
    const streamId = `terminal:${p.id}`
    let last = -1
    const publish = (event: TerminalEvent): void => {
      const previous = last
      last = event.seq
      connection.push({ streamId, cursor: String(event.seq), previousCursor: String(previous), event: event.kind === 'data' ? 'terminals.data' : 'terminals.exit',
        data: event.kind === 'data' ? { seq: event.seq, chunk: event.chunk } : { seq: event.seq, code: event.code } })
    }
    const attached = terminals.attach(p.id, publish)
    last = attached.seq
    const handle = attached
    // 重复 attach 同一个终端 = 替换订阅,不叠加。
    connection.subscribe(streamId, () => handle.dispose())
    return plain({ info: attached.info, data: attached.data, truncated: attached.truncated, seq: attached.seq })
  })

  router.query(DEVICE_METHODS.terminalsDetach, terminalsDetachParams, okResult, (context, p) => {
    context.connection.unsubscribe(`terminal:${p.id}`)
    return { ok: true as const }
  })

  router.query(DEVICE_METHODS.terminalsWrite, terminalsWriteParams, okResult, (context, p) => {
    leases.check('terminal', p.id, context.binding.controllerId, p.token)
    terminals.write(p.id, p.data)
    return { ok: true as const }
  })

  router.query(DEVICE_METHODS.terminalsResize, terminalsResizeParams, okResult, (context, p) => {
    leases.check('terminal', p.id, context.binding.controllerId, p.token)
    terminals.resize(p.id, p.cols, p.rows)
    return { ok: true as const }
  })

  router.execution(DEVICE_METHODS.terminalsKill, terminalsKillParams, okResult, (execution, p) => {
    leases.check('terminal', p.id, execution.binding.controllerId, p.token)
    terminals.kill(p.id)
    return { ok: true as const }
  })

  return () => terminals.shutdown()
}
