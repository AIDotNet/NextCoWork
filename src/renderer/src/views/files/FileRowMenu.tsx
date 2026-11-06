/**
 * 文件树一行的操作菜单 —— 右键和行尾 `…` 打开的是**同一份**。
 *
 * 需求(对着参考截图):
 *
 * 1. 第一行「在 X 中打开」—— X 是用户在「设置 › 通用 › 文件」里选的默认打开方式
 *    (`AppSettings.defaultOpenTarget`),没选 / 选的那个已卸载时按 `pickPrimaryTarget` 落点。
 * 2. 第二行「打开方式 ›」—— 其余所有可用程序收进二级菜单(默认那一个打头)。
 * 3. 然后是「另存为… / 复制路径 / 添加到聊天」,再往下才是原有的文件管理动作。
 *
 * ★ 一份菜单、两个入口:右键与 `…` 以前是两套(`…` 是每行一个 `Menu`),合并成由
 *   `FilesView` 持有的**一个** `ContextMenu`。两套的代价是条目顺序慢慢分叉;而每行一个
 *   `Menu` 实例还意味着二级菜单要在几百行里各挂一份 portal 与 `containsTarget`。
 *
 * ★ 「打开方式」那一段**只在本机工作区画**(调用方传 `local`):SSH 工作区里的文件
 *   不在本机磁盘上,本机的 VS Code 打开它只会打开一个不存在的路径。「另存为」「复制路径」
 *   (绝对路径)同理。「复制相对路径」「添加到聊天」远端照样成立,所以不受 `local` 限制。
 */
import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import {
  ChevronRight,
  Copy,
  Download,
  ExternalLink,
  FilePlus2,
  FolderOpen,
  FolderPlus,
  MessageSquarePlus,
  MoveRight,
  Pencil,
  Trash2
} from 'lucide-react'
import type { FileEntry } from '../../../../shared/domain/file-tree'
import {
  pickPrimaryTarget,
  submenuTargets,
  targetsForEntry,
  type WorkspacePathKind
} from '../../../../shared/domain/open-target'
import { OpenTargetItem, openTargetLabel } from '../../components/OpenWithMenu'
import { useOpenTargets } from '../../components/useOpenTargets'
import { ContextMenu, type ContextMenuPosition } from '../../components/ui/ContextMenu'
import { MenuItem, MenuSeparator } from '../../components/ui/Menu'
import { Submenu } from '../../components/ui/Submenu'
import { useI18n } from '../../i18n'
import { copyText, getSettings } from '../../services/app'
import { copyWorkspacePath, saveWorkspaceFileAs } from '../../services/open-with'
import { toast } from '../../stores/toast'
import { addFileToChat } from './add-to-chat'
import type { FileOperationTarget } from './file-operations'

/** 菜单宽度。量参考截图:「在 Visual Studio Code 中打开」一行不截断的最小值 */
export const FILE_ROW_MENU_WIDTH = 232

export function FileRowMenu({
  workspaceId,
  entry,
  position,
  local,
  onOperation,
  onDelete,
  onReveal,
  onClose
}: {
  workspaceId: string
  /** 菜单针对的那一行 */
  entry: FileEntry
  /** 视口坐标:右键时是指针位置,`…` 打开时是按钮下沿 */
  position: ContextMenuPosition
  /** 本机工作区才出「打开方式 / 另存为 / 复制绝对路径」,见文件头 ★ */
  local: boolean
  /** 新建 / 重命名 / 复制到 / 移动到 —— 都要弹 `FileOperationDialog`,由 `FilesView` 接管 */
  onOperation: (operation: Exclude<FileOperationTarget['operation'], 'delete'>) => void
  /** 已经过菜单内的二次确认,直接执行 */
  onDelete: () => void
  /** 只在远端工作区画(见下方那一行的 ★) */
  onReveal: () => void
  /** 面板淡出结束后调用;调用方据此卸载本组件 */
  onClose: () => void
}): ReactNode {
  const { t } = useI18n()
  const targets = useOpenTargets()
  const preferred = usePreferredTarget()
  const [confirmingDelete, setConfirmingDelete] = useState(false)
  /** `focus`:由键盘 / 点击打开时把焦点移进子菜单,悬停打开时不抢(见 `Submenu`) */
  const [submenu, setSubmenu] = useState<{ focus: boolean } | null>(null)
  // ★ 锚点用 state 而不是 ref:子菜单要在锚点挂上之后**重新渲染**一次才量得到位置。
  //   `setAnchor` 本身引用稳定,当 callback ref 用不会每次渲染都触发一轮 null → node。
  const [anchor, setAnchor] = useState<HTMLButtonElement | null>(null)
  const submenuNode = useRef<HTMLDivElement | null>(null)
  const containsTarget = useCallback((target: Node): boolean => submenuNode.current?.contains(target) === true, [])
  const directory = entry.kind === 'dir'

  const copyPath = (kind: WorkspacePathKind): void => {
    // 远端工作区只有相对路径可给 —— 主进程那条 `workspace:copyPath` 要本机根目录才算得出
    const write = local ? copyWorkspacePath(workspaceId, entry.path, kind) : copyText(entry.path)
    void write
      .then(() => toast.success(t('openWith.copied'), 'copy-path'))
      // 静默失败的表现是用户粘出上一次复制的内容,却以为是这个文件的路径(同 `OpenWithItems`)
      .catch(() => toast.error(t('openWith.openFailed'), 'copy-path'))
  }

  const saveAs = (): void => {
    // false = 用户在系统对话框里点了取消,不是失败
    void saveWorkspaceFileAs(workspaceId, entry.path).catch(() => toast.error(t('openWith.saveFailed'), 'save-as'))
  }

  const addToChat = (): void => {
    if (!addFileToChat(workspaceId, { name: entry.name, path: entry.path })) {
      toast.error(t('files.menu.addToChatFailed'), 'add-to-chat')
    }
  }

  return (
    <ContextMenu
      position={position}
      label={t('files.manage.actions', { name: entry.name })}
      width={FILE_ROW_MENU_WIDTH}
      onClose={onClose}
      containsTarget={containsTarget}
    >
      {(close) => {
        /* 子菜单不在面板里(portal),关菜单时要一起收掉,否则它会多停留一整段淡出时间 */
        const closeAll = (): void => {
          setSubmenu(null)
          close()
        }
        const act = (run: () => void): void => {
          closeAll()
          run()
        }
        const usable = targets === null ? [] : targetsForEntry(targets, directory)
        const primary = targets === null || preferred === null ? null : pickPrimaryTarget(targets, preferred, directory)
        return (
          /*
            指针移到父菜单的**别的**行上就收起子菜单 —— 挂在外层一次,而不是每一行各写一个
            onHover(那样新加一行时总会忘)。移到「打开方式」那一行自己不收。
            ★ 子菜单虽然 portal 到了 body,React 事件仍沿**组件树**冒泡到这里 —— 不排除它的话,
              指针一进子菜单就把子菜单自己收掉了。
          */
          <div
            onPointerOver={(event) => {
              const target = event.target as Node
              if (submenu === null || anchor === null || anchor.contains(target) || containsTarget(target)) return
              setSubmenu(null)
            }}
          >
            {local && (
              <>
                {targets === null || preferred === null ? (
                  // 还没探测回来:一行提示,而不是让第一行晚一拍才「跳」出来
                  <MenuItem disabled onSelect={() => undefined}>{t('common.loading')}</MenuItem>
                ) : (
                  <>
                    {primary !== null && (
                      <OpenTargetItem
                        workspaceId={workspaceId}
                        path={entry.path}
                        target={primary}
                        label={t('openWith.openIn', { name: openTargetLabel(t, primary) })}
                        close={closeAll}
                      />
                    )}
                    {usable.length > 0 && (
                      <MenuItem
                        icon={<ExternalLink size={14} />}
                        buttonRef={setAnchor}
                        onHover={() => setSubmenu((current) => current ?? { focus: false })}
                        onSelect={() => setSubmenu({ focus: true })}
                      >
                        <span className="flex items-center gap-2">
                          <span className="min-w-0 flex-1 truncate">{t('openWith.label')}</span>
                          <ChevronRight size={13} className="shrink-0 text-fg-faint" aria-hidden />
                        </span>
                      </MenuItem>
                    )}
                  </>
                )}
                <MenuSeparator />
                {!directory && (
                  <MenuItem icon={<Download size={14} />} onSelect={() => act(saveAs)}>
                    {t('openWith.saveAs')}
                  </MenuItem>
                )}
                <MenuItem icon={<Copy size={14} />} onSelect={() => act(() => copyPath('absolute'))}>
                  {t('openWith.copyPath')}
                </MenuItem>
              </>
            )}
            <MenuItem icon={<Copy size={14} />} onSelect={() => act(() => copyPath('relative'))}>
              {t('openWith.copyRelative')}
            </MenuItem>
            <MenuItem icon={<MessageSquarePlus size={14} />} onSelect={() => act(addToChat)}>
              {t('files.menu.addToChat')}
            </MenuItem>
            <MenuSeparator />
            {directory && (
              <>
                <MenuItem icon={<FilePlus2 size={14} />} onSelect={() => act(() => onOperation('create-file'))}>
                  {t('files.manage.newFile')}
                </MenuItem>
                <MenuItem icon={<FolderPlus size={14} />} onSelect={() => act(() => onOperation('create-directory'))}>
                  {t('files.manage.newDirectory')}
                </MenuItem>
                <MenuSeparator />
              </>
            )}
            <MenuItem icon={<Pencil size={14} />} onSelect={() => act(() => onOperation('rename'))}>
              {t('files.manage.rename')}
            </MenuItem>
            <MenuItem icon={<Copy size={14} />} onSelect={() => act(() => onOperation('copy'))}>
              {t('files.manage.copy')}
            </MenuItem>
            <MenuItem icon={<MoveRight size={14} />} onSelect={() => act(() => onOperation('move'))}>
              {t('files.manage.move')}
            </MenuItem>
            {/*
              ★ 本机工作区不再单列这一行:「打开方式 ›」里的「文件管理器」做的是同一件事
              (`showItemInFolder`),同一个动作在一份菜单里出现两次,用户会以为它们不一样
              —— 改版前 `OpenWithItems omitReveal` 防的也是这个,只是当时留的是这一行。
              远端工作区的这一行是「在文件树里扎到它的父目录」,和本机那个不是一回事,照留。
            */}
            {!local && (
              <MenuItem icon={<FolderOpen size={14} />} onSelect={() => act(onReveal)}>
                {t('files.manage.reveal')}
              </MenuItem>
            )}
            <MenuSeparator />
            <MenuItem
              danger
              icon={<Trash2 size={14} />}
              onSelect={() => {
                // 两步确认留在菜单里(与改版前的行菜单一致):第一下只换文案,第二下才删
                if (confirmingDelete) act(onDelete)
                else setConfirmingDelete(true)
              }}
            >
              {confirmingDelete ? t('common.confirmDelete') : t('files.manage.delete')}
            </MenuItem>
            {submenu !== null && anchor !== null && (
              <Submenu
                anchor={anchor}
                label={t('openWith.label')}
                autoFocus={submenu.focus}
                panelRef={(node) => {
                  submenuNode.current = node
                }}
              >
                {submenuTargets(usable, primary).map((target) => (
                  <OpenTargetItem
                    key={target.id}
                    workspaceId={workspaceId}
                    path={entry.path}
                    target={target}
                    close={closeAll}
                  />
                ))}
              </Submenu>
            )}
          </div>
        )
      }}
    </ContextMenu>
  )
}

/**
 * 默认打开方式,**在菜单打开时向主进程读一次**。
 *
 * ★ 为什么不从 `App.tsx` 顺着 props 传下来(AGENTS.md §9 的常规做法):设置目前只流到
 *   设置浮层,文件树在 Dock → 注册表 → FilesTab 那条链的最末端,为一个字段把 settings
 *   穿过 `AppShell` / `views/registry.tsx` 两个热点文件不划算。这里读到的值只活到菜单
 *   关闭,每次打开都重新读 —— 它不是一份会过期的镜像,主进程仍是唯一权威。
 *   `null` = 还没读回来;读失败按「自动」处理(第一行照样有落点)。
 */
function usePreferredTarget(): string | null {
  const [value, setValue] = useState<string | null>(null)
  useEffect(() => {
    let alive = true
    void getSettings()
      .then((settings) => { if (alive) setValue(settings.defaultOpenTarget) })
      .catch(() => { if (alive) setValue('') })
    return () => { alive = false }
  }, [])
  return value
}
