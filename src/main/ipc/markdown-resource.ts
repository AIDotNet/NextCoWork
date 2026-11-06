/**
 * 命令 / 子代理的**编辑** handler —— 扩展面板里那个编辑器的接线员。
 *
 * 分工同 `ipc/skills.ts`:解析与序列化在内核(`kernel/markdown-resource.ts`,
 * 那一侧不认识 electron),这里只做四件事 —— 算路径、挡越界、写盘、广播。
 *
 * ★ 命令和子代理共用这一份。它们在磁盘上是同构的(两层目录、`<name>.md`、
 *   frontmatter + 正文),差别只有目录名、文件名正则和前置块的键序 ——
 *   那三样做成参数,比复制两份几乎一样的读写逻辑要稳:一份里修了路径校验
 *   而另一份忘了,是这类重复代码最典型的下场。
 */
import { promises as nodeFs } from 'node:fs'
import { join } from 'node:path'
import { AGENT_NAME_RE } from '../../shared/domain/agent-def'
import { COMMAND_NAME_RE } from '../../shared/domain/command'
import { LOCAL_SETTINGS_DIRNAME } from '../../shared/domain/local-settings'
import { isBuiltinModeId, MODE_ID_RE } from '../../shared/domain/mode'
import { EnvironmentError } from '../environment/errors'
import { EnvironmentFiles } from '../environment/files'
import type {
  MarkdownResourceFile,
  MarkdownResourceKind,
  MarkdownResourceSave,
  MarkdownResourceScope
} from '../../shared/domain/markdown-resource'
import {
  readResourceFile,
  renderResourceFile,
  writeResourceFile
} from '../kernel/markdown-resource'
import { getEnvironments, getHost, getTools, getWorkspaceEnvironment } from '../runtime'
import { windows } from '../window/registry'

interface KindSpec {
  /** `<appData>/<dir>/` 与 `<workspaceRoot>/.next-cowork/<dir>/` */
  dir: string
  nameRe: RegExp
  changedChannel: 'commands:changed' | 'agents:changed' | 'modes:changed'
}

const SPEC: Record<MarkdownResourceKind, KindSpec> = {
  command: { dir: 'commands', nameRe: COMMAND_NAME_RE, changedChannel: 'commands:changed' },
  agent: { dir: 'agents', nameRe: AGENT_NAME_RE, changedChannel: 'agents:changed' },
  mode: { dir: 'modes', nameRe: MODE_ID_RE, changedChannel: 'modes:changed' }
}

export function broadcastResourceChanged(kind: MarkdownResourceKind): void {
  windows.emitToAll(SPEC[kind].changedChannel, undefined)
}

/**
 * 名字 → 绝对路径。
 *
 * ★ 项目级**相对工作区根一次算出来**,不要先算 `<工作区>/.next-cowork/<dir>` 再往里拼:
 *   `resolveWithin` 会对传进去的那个根做 realpath,而它在用户第一次保存之前并不存在
 *   —— 那样会当场抛 ENOENT,症状是「工作区作用域怎么都存不进去,全局却可以」
 *   (全局那条走 `join`,不 realpath;而且 `writeResourceFile` 本来就 mkdirp,
 *   两边自相矛盾)。相对工作区根算则两头都对:工作区根一定存在,还不存在的那几层
 *   由 canonical 的「退到最近的存在祖先」补齐,软链逃逸(`<工作区>/.next-cowork`
 *   或 `<dir>` 指向工作区外)照样会被判掉。
 *
 * ★ 项目级走 `environment.path`,不是 `node:path.join` ——
 *   远程工作区的路径在远端主机上(还可能是 win32 形态),拼本地路径拿到的是一个不存在的位置。
 *
 * ★ `name` 来自渲染层,是**不可信输入**。两道闸都必须在:
 *   1. 正则:挡掉 `../`、绝对路径、空串、超长
 *   2. `resolveWithin`:挡掉软链逃逸(正则过得去的名字也可能落在目录外)
 *   少任何一道,一次「删除」就能删到工作区外面去。
 */
async function resourcePath(
  kind: MarkdownResourceKind,
  scope: MarkdownResourceScope,
  name: string,
  workspaceId?: string
): Promise<{ file: string; root: string }> {
  if (!SPEC[kind].nameRe.test(name)) throw new Error('名字不合法')
  if (scope === 'project') {
    if (!workspaceId) throw new EnvironmentError('unbound')
    const environment = getWorkspaceEnvironment(workspaceId)
    const relative = `${LOCAL_SETTINGS_DIRNAME}/${SPEC[kind].dir}`
    return {
      file: await environment.path.resolveWithin(environment.rootPath, `${relative}/${name}.md`),
      /* 只用于删除前的落点复核,不保证存在 —— 第一次保存之前它确实还没有。 */
      root: environment.path.join(environment.rootPath, relative)
    }
  }
  const root = join(getHost().paths.userData(), SPEC[kind].dir)
  // 全局层没有 WorkspacePaths，自己拼 + 落盘前再用 realpath 复核（见 assertInside）。
  return { file: join(root, `${name}.md`), root }
}

/** 项目级且远程时用环境的 fs，其余用宿主的。 */
function fsFor(scope: MarkdownResourceScope, workspaceId?: string): ReturnType<typeof getHost>['fs'] {
  if (scope !== 'project' || !workspaceId) return getHost().fs
  const environment = getWorkspaceEnvironment(workspaceId)
  return environment.remote ? environment.fs : getHost().fs
}

export async function getResource(req: {
  kind: MarkdownResourceKind
  scope: MarkdownResourceScope
  name: string
  workspaceId?: string
}): Promise<MarkdownResourceFile> {
  const { file } = await resourcePath(req.kind, req.scope, req.name, req.workspaceId)
  const content = await readResourceFile(fsFor(req.scope, req.workspaceId), file)
  if (content === null) throw new Error('文件不存在')
  return { kind: req.kind, scope: req.scope, name: req.name, path: file, ...content }
}

function frontmatterList(value: unknown): string[] | undefined {
  if (typeof value === 'string') return value.split(',').map((item) => item.trim()).filter(Boolean)
  if (Array.isArray(value) && value.every((item) => typeof item === 'string')) return value.map((item) => item.trim()).filter(Boolean)
  return undefined
}

function validateModeSave(req: MarkdownResourceSave): void {
  if (isBuiltinModeId(req.name)) throw new Error('Built-in modes are read-only.')
  if (typeof req.frontmatter['name'] !== 'string' || req.frontmatter['name'].trim() === '') throw new Error('Mode name is required.')
  if (typeof req.frontmatter['description'] !== 'string' || req.frontmatter['description'].trim() === '') throw new Error('Mode description is required.')
  if (req.body.trim() === '') throw new Error('Mode prompt is required.')

  const tools = frontmatterList(req.frontmatter['tools'])
  const required = frontmatterList(req.frontmatter['requiredTools'])
  if (req.frontmatter['tools'] !== undefined && (tools === undefined || tools.length === 0)) throw new Error('Mode tools must be a non-empty list.')
  if (req.frontmatter['requiredTools'] !== undefined && (required === undefined || required.length === 0)) throw new Error('Mode required tools must be a non-empty list.')
  if (required !== undefined) {
    if (tools === undefined) throw new Error('Required tools must also be listed in tools.')
    const allowed = new Set(tools)
    const missing = required.filter((tool) => !allowed.has(tool))
    if (missing.length > 0) throw new Error(`Required tools must also be listed in tools: ${missing.join(', ')}`)
  }

  const available = new Set(getTools().info().map((tool) => tool.internalId))
  const unknown = [...(tools ?? []), ...(required ?? [])].filter((tool) => !available.has(tool))
  if (unknown.length > 0) throw new Error(`Unknown mode tools: ${[...new Set(unknown)].join(', ')}`)
}

export async function saveResource(req: MarkdownResourceSave): Promise<MarkdownResourceFile> {
  if (req.kind === 'mode') validateModeSave(req)
  const { file } = await resourcePath(req.kind, req.scope, req.name, req.workspaceId)
  const fs = fsFor(req.scope, req.workspaceId)
  const text = renderResourceFile(req.kind, req.frontmatter, req.body)

  const outcome = await writeResourceFile(fs, file, text, req.revision)
  if (!outcome.ok) {
    throw new Error(
      outcome.reason === 'conflict'
        ? '文件已被改动，请重新打开再保存'
        : '写入失败'
    )
  }
  broadcastResourceChanged(req.kind)

  const content = await readResourceFile(fs, file)
  if (content === null) throw new Error('写完却读不回来')
  return { kind: req.kind, scope: req.scope, name: req.name, path: file, ...content }
}

export async function deleteResource(req: {
  kind: MarkdownResourceKind
  scope: MarkdownResourceScope
  name: string
  workspaceId?: string
}): Promise<void> {
  if (req.kind === 'mode' && isBuiltinModeId(req.name)) throw new Error('Built-in modes are read-only.')
  const { file, root } = await resourcePath(req.kind, req.scope, req.name, req.workspaceId)

  if (req.scope === 'project' && req.workspaceId) {
    const lease = getEnvironments().acquire(req.workspaceId)
    try {
      if (lease.environment.remote) {
        await new EnvironmentFiles(lease.environment).mutate({
          workspaceId: req.workspaceId,
          path: file,
          operation: 'delete'
        })
        broadcastResourceChanged(req.kind)
        return
      }
    } finally {
      lease.release()
    }
  }

  /*
    ★ 删之前再用 `realpath` 复核一次落点。正则和 `resolveWithin` 挡的是
    「名字」，而这一步挡的是「文件本身是个软链」—— `commands/x.md -> ~/.ssh/id_rsa`
    在前两道闸下都是合法的，只有解析真实路径才看得出来。
    照搬 `skills.ts` 卸载那段已有的正确答案。
  */
  const resolved = await nodeFs.realpath(file).catch(() => file)
  const realRoot = await nodeFs.realpath(root).catch(() => root)
  if (!resolved.startsWith(realRoot + '/')) throw new Error('文件位置无效')
  await nodeFs.rm(resolved, { force: true })
  broadcastResourceChanged(req.kind)
}
