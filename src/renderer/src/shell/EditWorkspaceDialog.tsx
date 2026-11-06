/**
 * 编辑工作区 —— 外层 Tab 条「+」菜单里每个工作区行悬停出现的铅笔按钮打开的弹窗
 * （`OuterTabBar` 的 `onEditWorkspace`）。三件事收在一起：改名字、改这个工作区
 * 新对话默认落在哪个模型/思考强度、以及把它从工作区列表里删掉。
 *
 * ★ 删除不在这里直接发 IPC —— 它要先经过 `onDelete` 把「这个工作区可能还开着
 * 一张外层 Tab」这件事交给 `AppShell` 处理（未保存的文档改动要走它自己的
 * `confirmDocumentChanges` 确认流程，这个弹窗管不到那一层）。`onDelete` 返回
 * `false` 时（用户在那一步取消，或者 IPC 本身失败）弹窗留在原地，不假装成功。
 *
 * ★ 删除确认走「按钮标签二段式」（照 `Sidebar.tsx` 会话删除那套），不是叠一层
 * 新 `Dialog`：两个 `Dialog` 同时挂着时，`Dialog.tsx` 的 Escape 监听各自独立
 * 注册在 `document` 上、互不知道对方存在 —— 按一次 Esc 两层会一起关，而不是
 * 只关最上面那层。单个按钮的二段式没有这个坑。
 */
import { Folder, Trash2 } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import { THINKING_LEVELS, type ThinkingLevel } from '../../../shared/agent/run-request'
import { modelSelectionKey, parseModelSelectionKey } from '../../../shared/domain/model-selection'
import type { Workspace } from '../../../shared/domain/workspace'
import { Button } from '../components/arc/button/button'
import { Dialog } from '../components/ui/Dialog'
import SegmentedControl from '../components/arc/segmented-control/segmented-control'
import { Select } from '../components/ui/Select'
import { TextInput } from '../components/ui/TextInput'
import { useChatModelGuard } from '../components/useChatModelGuard'
import { useI18n } from '../i18n'
import { updateWorkspace } from '../services/app'
import { modelOptions } from '../settings/pages/model/enabled-models'
import { useModelsStore } from '../stores/models'

/** 每个字段一个 label + 可选提示行 —— 同 `ScheduledFeature.tsx` / `McpServerDialog.tsx` 里各自那份 Field，本仓库目前没有把它提到 `components/ui` 的公共版本。 */
function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }): ReactNode {
  return (
    <label className="block">
      <div className="mb-1 text-[12px] text-fg-muted">{label}</div>
      {children}
      {hint !== undefined && <div className="mt-1 text-[11px] text-fg-faint">{hint}</div>}
    </label>
  )
}

/** 纯 UI 观感锚点（截图里的「5/32」），不是后端校验——`workspace:update` 和双击改名一样不限字符集或长度。 */
const NAME_MAX_LENGTH = 32

export function EditWorkspaceDialog({
  workspace,
  onClose,
  onDelete
}: {
  /** null = 弹窗关闭。传对象而不是 id，调用方（AppShell）已经在 `workspaces` 里查过一次，这里不用再查一遍。 */
  workspace: Workspace | null
  onClose: () => void
  /** 交给 AppShell：先收掉这个工作区可能开着的外层 Tab，再真正从记录里删除。 */
  onDelete: (workspaceId: string) => Promise<boolean>
}): ReactNode {
  const { t } = useI18n()
  const models = useModelsStore((s) => s.models)
  const providers = useModelsStore((s) => s.providers)
  const modelsLoaded = useModelsStore((s) => s.loaded)
  const loadModels = useModelsStore((s) => s.load)
  useEffect(() => {
    if (!modelsLoaded) void loadModels()
  }, [modelsLoaded, loadModels])

  const [name, setName] = useState('')
  const [model, setModel] = useState('')
  const [modelProviderId, setModelProviderId] = useState<string | undefined>(undefined)
  const [thinking, setThinking] = useState<ThinkingLevel>('auto')
  const [saving, setSaving] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [armDelete, setArmDelete] = useState(false)
  const [error, setError] = useState<string | null>(null)

  /*
    需求:工作区默认模型也只列文本模型,存量里选中了图片模型的在**打开选择器时**
    校正成第一个文本模型并提示(见 `useChatModelGuard` 文件头;改的是这个弹窗的
    草稿态,落盘仍走「保存」—— 那是这个弹窗一贯的语义)。
  */
  const guardChatModel = useChatModelGuard(
    models,
    providers,
    { model, modelProviderId },
    (nextModel, nextProviderId) => {
      setModel(nextModel)
      setModelProviderId(nextProviderId)
    }
  )

  /*
    需求：换一个工作区（或者弹窗整个关掉）都要把草稿和「已经按过一次删除」的
    武装状态一起清掉。只在 `workspace !== null` 时回填字段、却忘了在
    `workspace === null` 分支里也把 `armDelete` 归零的话，症状是：
    A 工作区按过一次「删除工作区」还没点第二下就关掉弹窗，下次编辑 B 工作区，
    footer 上的按钮已经是「确认删除」——用户点一下就真删了 B。
  */
  useEffect(() => {
    setArmDelete(false)
    setError(null)
    if (workspace === null) return
    setName(workspace.name)
    setModel(workspace.settings.defaultModel)
    setModelProviderId(workspace.settings.defaultModelProviderId)
    setThinking(workspace.settings.defaultThinking)
  }, [workspace?.id])

  const modelOptionList = [
    { value: '', label: t('models.followConversation') },
    ...modelOptions(models.filter((m) => m.enabled !== false), providers)
  ]
  const thinkingOptions = THINKING_LEVELS.map((level) => ({
    value: level,
    label: t(`chat.thinkingLevel.${level}`)
  }))

  const save = async (): Promise<void> => {
    if (workspace === null || saving) return
    const trimmed = name.trim()
    if (trimmed === '') return
    setSaving(true)
    setError(null)
    try {
      await updateWorkspace({
        id: workspace.id,
        name: trimmed,
        settings: {
          defaultModel: model,
          /*
            ★ 无条件写，不能 `...(modelProviderId === undefined ? {} : { defaultModelProviderId })`——
            主进程 `workspace:update` 是深合并，漏写这一项时旧的供应商会原样留下，
            拼出「新别名 + 旧供应商锁」。和 `Composer.tsx` 的 `toSettings()` 是
            同一条不变式，理由抄自那里，别在这里各写各的。
          */
          defaultModelProviderId: modelProviderId,
          defaultThinking: thinking
        }
      })
      onClose()
    } catch {
      setError(t('workspace.saveFailed'))
    } finally {
      setSaving(false)
    }
  }

  const remove = async (): Promise<void> => {
    if (workspace === null || deleting) return
    if (!armDelete) {
      setArmDelete(true)
      return
    }
    setDeleting(true)
    setError(null)
    try {
      const ok = await onDelete(workspace.id)
      if (ok) onClose()
      else {
        setArmDelete(false)
        setError(t('workspace.deleteFailed'))
      }
    } catch {
      setArmDelete(false)
      setError(t('workspace.deleteFailed'))
    } finally {
      setDeleting(false)
    }
  }

  return (
    <Dialog
      open={workspace !== null}
      title={t('workspace.edit')}
      onClose={onClose}
      width={560}
      footer={
        <div className="flex w-full items-center justify-between">
          <Button
            type="button"
            variant="danger"
            size="sm"
            disabled={deleting}
            onClick={() => { void remove() }}
          >
            <Trash2 size={13} />
            {armDelete ? t('common.confirmDelete') : t('workspace.delete')}
          </Button>
          <div className="flex items-center gap-2">
            <Button variant="secondary" type="button" size="sm" onClick={onClose}>{t('common.cancel')}</Button>
            <Button type="button" size="sm" variant="primary" disabled={saving || name.trim() === ''} onClick={() => { void save() }}>
              {t('common.save')}
            </Button>
          </div>
        </div>
      }
    >
      <div className="flex flex-col gap-3">
        <Field label={t('workspace.name')} hint={t('workspace.nameHint')}>
          <TextInput
            value={name}
            onChange={(value) => setName(value.slice(0, NAME_MAX_LENGTH))}
            ariaLabel={t('workspace.name')}
          />
        </Field>

        <Field label={t('workspace.path')}>
          {/*
            只读展示，不是 TextInput —— 工作区根目录没有可用的 IPC 去改
            (方案 §9：渲染层永不指定工作区根，选目录只能靠 `workspace:pick`
            重新走一遍对话框、建出一个新工作区)。画成能输入的控件会是一句
            点了没反应的假承诺。
          */}
          <div className="flex h-8 items-center gap-2 rounded-[8px] border border-border bg-surface-field px-2.5 text-[13px] text-fg-muted">
            <Folder size={14} className="shrink-0 text-fg-faint" />
            <span className="min-w-0 flex-1 truncate" title={workspace?.rootPath}>{workspace?.rootPath}</span>
          </div>
        </Field>

        <Field label={t('workspace.defaultModel')} hint={t('workspace.defaultModelHint')}>
          <div className="flex flex-col gap-2">
            <Select
              value={modelSelectionKey(modelProviderId, model)}
              options={modelOptionList}
              ariaLabel={t('workspace.defaultModel')}
              inModal
              onOpenChange={guardChatModel}
              onValueChange={(value) => {
                const selected = parseModelSelectionKey(value)
                setModel(selected.alias)
                setModelProviderId(selected.modelProviderId)
              }}
            />
            <SegmentedControl
              label={t('workspace.thinkingLevel')}
              value={thinking}
              options={thinkingOptions}
              onValueChange={(v) => setThinking(v as ThinkingLevel)}
              className="flex-wrap"
            />
          </div>
        </Field>

        {armDelete && (
          <p className="text-[12px] text-danger">
            {t('workspace.deleteConfirm', { name: workspace?.name ?? '' })}
          </p>
        )}
        {error !== null && <p role="alert" className="text-[12px] text-danger">{error}</p>}
      </div>
    </Dialog>
  )
}
