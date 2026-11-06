import { app, clipboard, dialog, shell } from 'electron'
import { copyFileSync, lstatSync } from 'node:fs'
import { basename, isAbsolute, join, resolve } from 'node:path'
import { DEFAULT_APP_TARGET_ID, REVEAL_TARGET_ID, type OpenTarget, type WorkspacePathKind } from '../../shared/domain/open-target'
import { type WorkspaceFileRequest } from '../../shared/domain/workspace-file'
import { IpcError } from './errors'
import { getWorkspaceEnvironment } from '../runtime'
import { EnvironmentError } from '../environment/errors'
import { remoteFileFailure } from '../environment/files'
import { displayPath } from '../kernel/tool/path-guard'
import { listOpenTargets as systemOpenTargets, openWithTarget } from '../system/open-with'

import {
  checkedPath, fail, installWorkspaceTrash, remoteEditor, translateError, workspaceRoot
} from '../services/workspace-files'

// 无 Electron 的核心(读/写/变更/回收站列表)在 services;这里只留要碰系统 UI 的那一半,并原样重新导出,
// 旧调用点的 `from './workspace-files'` 一行都不用改。
// 桌面的「删除」= 系统废纸篓;失败不降级为永久删除。
installWorkspaceTrash((path) => shell.trashItem(path))

export {
  installWorkspaceTrash, listWorkspaceRecovery, mutateWorkspaceDocument, mutateWorkspaceFile,
  readWorkspaceDocument, readWorkspaceFile, writeWorkspaceDocument, writeWorkspaceFile
} from '../services/workspace-files'

export async function revealWorkspaceDocument(req: WorkspaceFileRequest): Promise<void | { remote: true; path: string; parent: string; name: string }> {
  const editor = remoteEditor(req.workspaceId)
  if (!editor) { revealWorkspaceFile(req); return }
  try {
    const environment = getWorkspaceEnvironment(req.workspaceId)
    const key = environment.key
    const path = await editor.checkedPath(req.path, true)
    await environment.fs.stat(path)
    environment.assertReady()
    /**
     * ★ 迟到的返回不开 tab。`environment.key` 里带着 generation,重连就换一把。
     *
     * 中间这几次 await 期间工作区可能已经重连(甚至换了连接):那时算出来的
     * `parent` 属于**上一个**环境的根,渲染层照开不误,用户会得到一棵扎在陈旧路径上、
     * 每一项都列不出来的树。`assertReady()` 只保证手里这个环境对象自己还活着,
     * 不保证它还是这个工作区**当前**的环境 —— 所以要按 runtime 里 MCP 那套显式比 key。
     */
    if (getWorkspaceEnvironment(req.workspaceId).key !== key) throw new EnvironmentError('conflict')
    /**
     * ★ 用 realpath 后的根来比:`checkedPath` 返回的是 realpath 过的路径,而
     * `environment.rootPath` 是配置里的原样写法。根自身是软链时(BSD 的 /home →
     * /usr/home、macOS 的 /var → /private/var),拿两者直接比会把工作区内的文件
     * 判成越界。
     */
    const root = await environment.fs.realpath(environment.rootPath)
    /**
     * ★ 工作区相对写法里,根是**空串**不是 `'.'`。
     *
     * `display()` 对根返回 `'.'`(它是给人看的写法),而这条返回值是给机器用的:
     * 渲染层拿 `parent` 当 files tab 的 rootPath,`listWorkspaceDir` 再拿它拼每一项的
     * path。`'.'` 是真串,拼出来就是 `./a.txt` —— 而 `checkedPath` 明令拒绝 `.` 段,
     * 于是那棵树里的文件一个也打不开。根**下的每一个文件**都会走到这条路径上。
     */
    const relative = (absolute: string): string | undefined => {
      /**
       * ★ 必须走 `display()` 而不是 `relative()`。
       *
       * `relative()` 没有 `inside()` 判断:对工作区外的目标它产出 `../../etc` 这种字符串,
       * 渲染层原样当成 rootPath 开一个 files tab,而 `listWorkspaceDir` 只 resolve、不查
       * `outside`,于是工作区外的目录被整棵列出来。越界就不返回扎根指令 —— 文件树本来
       * 也表达不了工作区外的位置。注意围栏只针对 reveal 这个 UI 入口,Agent 通过工具
       * 显式访问绝对路径是被允许的行为,不在这里拦。
       */
      const shown = environment.path.display(root, absolute)
      if (environment.path.isAbsolute(shown)) return undefined
      return shown === '.' ? '' : shown
    }
    const name = environment.path.basename(path)
    // 根自己没有「在树里选中」这一说 —— 它就是树根。扎在根上,不选中任何一行。
    if (path === root) return { remote: true, path: '', parent: '', name }
    /**
     * ★ 目录也扎在**父目录**上并选中它自己,和本机 `showItemInFolder` 一致。
     *
     * 原先目录是扎在自己身上的,于是 `selectedPath` 恰好等于树根 —— 而树根永远不是
     * 树里的一行,「在文件管理器中显示」对任何目录都没有任何可见效果。
     */
    const parent = relative(environment.path.dirname(path))
    const selected = relative(path)
    if (parent === undefined || selected === undefined) return
    return { remote: true, path: selected, parent, name }
  } catch (error) {
    // 远端 stat 抛的是裸 ENOENT,不归一化就会在界面上显示成「读写失败」
    try { remoteFileFailure(error) } catch (failure) { translateError(failure) }
  }
}

export function revealWorkspaceFile(req: WorkspaceFileRequest): void {
  try {
    const target = checkedPath(workspaceRoot(req.workspaceId), req.path, true, 'follow')
    lstatSync(target)
    shell.showItemInFolder(target)
  } catch (error) {
    translateError(error)
  }
}

/**
 * 「打开方式」下拉里那几项。
 *
 * ★ **不发路径给渲染层,只发 id + 产品名 + 图标 id**(见 `shared/domain/open-target.ts`)。
 * 菜单是渲染层画的,而绝对路径不进渲染层 —— 这是 `workspace-file.ts` 顶上那条
 * 约定,「复制绝对路径」因此也由主进程直接写剪贴板(见下面的 `copyWorkspacePath`)。
 *
 * ★ 与 `workspace:revealFile` 不同,这条**不需要工作区**:菜单内容只取决于这台
 * 机器上装了什么,和当前打开的是哪个工作区无关。渲染层在远端工作区里不画这个
 * 菜单(那些文件不在本机磁盘上),判断在调用点。
 */
export function listOpenTargets(): OpenTarget[] {
  return systemOpenTargets()
}

/**
 * 用某个程序打开工作区里的一个文件。
 *
 * 两条不变式,和这个文件里其它入口逐条对齐:
 *
 * 1. **路径仍走 `checkedPath`** —— 渲染层递进来的 id 查表即用,但路径照样是
 *    不可信输入(工具卡片给出的绝对路径也落在这条路上)。
 * 2. **目标 id 只用来查表。** 渲染层指定不了要跑什么命令,只能从
 *    `listOpenTargets()` 给过的那几个里挑一个。
 *
 * ★ **不存在的目标静默忽略。** 这一条不是偷懒:磁盘上的文件可能在这几次
 * `await` 之间被别的程序删掉/改名,那时编辑器照样能起来、只是打开一个空缓冲;
 * 而把「文件没了」报成「打开失败」会让用户去查编辑器装没装。真实失败
 * (编辑器被卸载、没权限)由 `open-with.ts` 那侧清缓存,下一次探测就不再列出它。
 */
export async function openWorkspaceFileWith(req: { workspaceId: string; path: string; targetId: string }): Promise<void> {
  let target: string
  try {
    target = checkedPath(workspaceRoot(req.workspaceId), req.path, true, 'follow')
    lstatSync(target)
  } catch {
    return
  }
  /*
    需求:「打开方式」子菜单里的「文件管理器」「默认应用」两项要真的能用 ——
    它们不是可执行文件,`system/open-with.ts` 那张表里查不到,原先落进那边的
    `unknown open target` 分支,表现是点了「文件管理器」只弹一句「无法用这个程序打开」。
    两者都是 Electron 的 `shell` 能力,所以在这一层分流,而不是让 `system/open-with.ts`
    去依赖 Electron(那个文件只管真实进程)。
  */
  if (req.targetId === REVEAL_TARGET_ID) {
    shell.showItemInFolder(target)
    return
  }
  if (req.targetId === DEFAULT_APP_TARGET_ID) {
    // ★ openPath 失败不抛,返回一段错误文本(没有关联程序时就是这种)。不检查的话
    //   用户点了没反应、也没有任何提示。
    const failure = await shell.openPath(target)
    if (failure !== '') throw new IpcError('unknown', failure)
    return
  }
  await openWithTarget(req.targetId, target)
}

/**
 * 文件树右键「另存为…」:把工作区里的一个文件复制到用户在系统对话框里挑的位置。
 *
 * 和 `app:saveTextFile` 同一条约定:**落点由 showSaveDialog 产出**,渲染层只给
 * 工作区相对路径;源文件照样走 `checkedPath`(软链、越界一律拒)。只做本机工作区 ——
 * 远端文件不在本机磁盘上,`workspaceRoot` 会直接拒掉。
 *
 * 返回 false = 用户取消;调用方据此区分「没存」和「存失败」(后者抛错)。
 * 不回传目标路径:渲染层用不着它,也就不必让一条本机绝对路径进渲染层。
 */
export async function saveWorkspaceFileAs(req: { workspaceId: string; path: string }): Promise<boolean> {
  let source: string
  try {
    source = checkedPath(workspaceRoot(req.workspaceId), req.path, false, 'follow-dirs')
    if (!lstatSync(source).isFile()) fail('not-file')
  } catch (error) {
    translateError(error)
  }
  const result = await dialog.showSaveDialog({
    defaultPath: join(app.getPath('downloads'), basename(source))
  })
  if (result.canceled || !result.filePath) return false
  // ★ 挑了源文件自己:复制到自身在部分平台上会先截断再读,等于把文件清空。
  if (resolve(result.filePath) === source) return true
  try {
    copyFileSync(source, result.filePath)
  } catch (error) {
    translateError(error)
  }
  return true
}

/**
 * 把一条路径写进系统剪贴板,**返回真正写进去的那一串**。
 *
 * ★ 为什么要主进程做:绝对路径不能进渲染层(这个文件的既有约定),
 *   而「复制相对路径」在**工作区外**的文件上只能是绝对路径 —— 判定要拿到
 *   真实根目录才做得对,那也只有主进程有。
 *
 * ★ 返回值不是装饰:工作区外的文件复制出来的是绝对路径,界面若还显示
 *   「已复制相对路径」,用户会以为拿到的是一条相对路径,粘到别处才发现不对。
 */
export async function copyWorkspacePath(req: { workspaceId: string; path: string; kind: WorkspacePathKind }): Promise<string> {
  const root = workspaceRoot(req.workspaceId)
  const target = checkedPath(root, req.path, true, 'follow')
  lstatSync(target)
  /*
    ★ 工作区相对路径(文件树给的)复制**逻辑路径**,不复制 realpath:经软链目录进来的文件
    (#182)realpath 落在工作区外,`displayPath` 会把它算成一条外部绝对路径,
    「复制相对路径」于是拿不到相对路径。`checkedPath` 已在词法上拒掉 `..`,原样用即可。
  */
  const logical = req.path !== '' && !isAbsolute(req.path)
  const text = req.kind === 'relative'
    ? (logical ? req.path : displayPath(root, target))
    : (logical ? join(root, req.path) : target)
  clipboard.writeText(text)
  return text
}
