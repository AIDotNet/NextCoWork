/**
 * 子代理的结构化编辑器 —— 新建和编辑走的是同一个,差别只有「有没有源文件」。
 *
 * ★ 为什么不继续用通用的 Markdown 资源编辑器:那一套把 `tools` / `model` 做成
 *   裸文本输入,而这两个字段写错的后果都不对称地严重 —— `tools` 里一个拼错的
 *   名字会让 `agent/load.ts` 把**整条子代理作废**(界面上只留一条诊断,用户看到的
 *   是「我的子代理不见了」),`model` 写错则是静默落回默认模型。能勾的就别让人打。
 *
 * ★ 两个默认值必须**摆在控件上**,不能只写在 placeholder 里:可用工具默认
 *   「全部」、模型默认「继承默认」。这两个正是绝大多数人要的,而「留空 = 继承」
 *   这种话没人读。
 *
 * ★ 「编辑源码」保留,但它是**同一份草稿的另一个视图**:切换时通过
 *   `fileFromForm` / `formFromFile` 往返一次,而不是各存一套状态。两套状态的话,
 *   在哪一边改的就只有哪一边算数,而用户完全看不出来。
 */
import { ArrowLeft, Sparkles, Trash2 } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { AGENT_TOOL_CHOICES, AGENT_COLORS } from '../../../../../shared/domain/agent-def'
import type {
  MarkdownResourceFile,
  MarkdownResourceScope
} from '../../../../../shared/domain/markdown-resource'
import {
  modelSelectionKey,
  parseModelSelectionKey
} from '../../../../../shared/domain/model-selection'
import { INHERIT_THINKING, SUBAGENT_THINKING_CHOICES } from '../../../../../shared/domain/subagent-thinking'
import { Button } from '../../../components/arc/button/button'
import { Dialog } from '../../../components/ui/Dialog'
import { IconButton } from '../../../components/ui/IconButton'
import SegmentedControl from '../../../components/arc/segmented-control/segmented-control'
import { Select } from '../../../components/ui/Select'
import { TextArea } from '../../../components/ui/TextArea'
import { TextInput } from '../../../components/ui/TextInput'
import { useI18n } from '../../../i18n'
import { cn } from '../../../lib/cn'
import { generateAgent } from '../../../services/agents'
import { useModelsStore } from '../../../stores/models'
import { MarkdownResourceEditor } from '../markdown/MarkdownResourceEditor'
import { readField, setField, setListField, type Frontmatter } from '../markdown/frontmatter-form'
import {
  AGENT_COLOR_HEX,
  applyDraft,
  fileFromForm,
  formFromFile,
  unknownTools,
  validateAgentForm,
  type AgentForm
} from './agent-form'

export interface AgentSavePayload {
  scope: MarkdownResourceScope
  name: string
  frontmatter: Frontmatter
  body: string
}

function Row({ label, children, required = false }: { label: string; children: ReactNode; required?: boolean }): ReactNode {
  return (
    <div className="flex items-start gap-3">
      <span className={cn('mt-1.5 w-[84px] shrink-0 text-[12px]', required ? 'text-danger' : 'text-fg-muted')}>
        {label}{required && '*'}
      </span>
      <div className="min-w-0 flex-1">{children}</div>
    </div>
  )
}

export function AgentEditor({
  file,
  taken,
  onSave,
  onDelete,
  onClose,
  saving,
  error,
  workspaceId
}: {
  /** `revision === ''` = 还没落盘的新建。 */
  file: MarkdownResourceFile
  /** 已有的 `scope:name`,用来当场查重 —— 见下面 `nameTaken` 的注释。 */
  taken: ReadonlySet<string>
  onSave: (payload: AgentSavePayload) => void
  onDelete: () => void
  onClose: () => void
  saving: boolean
  error: string | null
  workspaceId: string | null
}): ReactNode {
  const { t } = useI18n()
  const models = useModelsStore((s) => s.models)
  const providers = useModelsStore((s) => s.providers)
  const isNew = file.revision === ''

  const [form, setForm] = useState<AgentForm>(() => formFromFile(file.name, file.frontmatter, file.body))
  /** 未知键的载体。★ 表单不认识的键全靠它原样带回文件里。 */
  const [baseFm, setBaseFm] = useState<Frontmatter>(file.frontmatter)
  const [scope, setScope] = useState<MarkdownResourceScope>(file.scope)
  const [sourceMode, setSourceMode] = useState(false)
  const [source, setSource] = useState<{ fm: Frontmatter; body: string }>({ fm: file.frontmatter, body: file.body })

  const [attempted, setAttempted] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [asking, setAsking] = useState(false)
  const [requirement, setRequirement] = useState('')
  const [generating, setGenerating] = useState(false)
  const [genError, setGenError] = useState<string | null>(null)

  const invalid = validateAgentForm(form)
  /*
    ★ 自己查一遍重名,而不是等后端。后端那条路是 `writeResourceFile` 的
    `conflict` —— 同一个词既表示「重名」又表示「文件在别处被改过」,冒出来的
    那句「文件已被改动」在改名这个场景里完全对不上,用户会以为是自己手滑了。
  */
  const nameTaken = (isNew || form.name !== file.name || scope !== file.scope)
    && taken.has(`${scope}:${form.name.trim()}`)
  const blocked = invalid ?? (nameTaken ? 'ext.error.agentNameTaken' : null)
  /*
    ★ 只有「即时更新的控件」造成的问题才配禁用保存。`TextArea` 是**失焦才提交**的:
    描述或正文还空着就把保存按钮禁掉的话,用户打完最后一个字直接去点保存,点到的是
    一个禁用按钮 —— 禁用元素连 mousedown 都不发,焦点不动、草稿不提交,那颗按钮
    于是**永远亮不起来**。名字和工具来自 `TextInput`/复选框,当场就更新,禁用是安全的。
  */
  const hardBlocked =
    blocked !== null && blocked !== 'ext.error.agentNeedsDescription' && blocked !== 'ext.error.emptyBody'

  const extraTools = unknownTools(form)
  const toDraft = (): AgentSavePayload => ({
    scope,
    name: form.name.trim(),
    ...fileFromForm({ ...form, name: form.name.trim() }, baseFm)
  })

  const save = (): void => {
    setAttempted(true)
    if (blocked !== null || saving) return
    onSave(toDraft())
  }

  const toSource = (): void => {
    const file = fileFromForm({ ...form, name: form.name.trim() }, baseFm)
    setSource({ fm: file.frontmatter, body: file.body })
    setSourceMode(true)
  }

  const toForm = (): void => {
    setForm(formFromFile(form.name, source.fm, source.body))
    setBaseFm(source.fm)
    setSourceMode(false)
  }

  const generate = (): void => {
    // ★ 同上:需求框也是失焦才提交的,所以这里拦空值,而不是把按钮禁掉。
    if (requirement.trim() === '') {
      setGenError(t('ext.agents.generateEmpty'))
      return
    }
    setGenerating(true)
    setGenError(null)
    void generateAgent(requirement, workspaceId ?? undefined)
      .then((draft) => {
        setForm((current) => applyDraft(current, draft))
        setAsking(false)
        setRequirement('')
      })
      // ★ 主进程抛的已经是一句人话(见 `main/agent-draft.ts`),原样显示。
      .catch((e: unknown) => setGenError(e instanceof Error ? e.message : String(e)))
      .finally(() => setGenerating(false))
  }

  const dirty = form.description.trim() !== '' || form.prompt.trim() !== ''
  const askToGenerate = (): void => {
    setGenError(null)
    setAsking(true)
  }

  /*
    ★ 一条**绑定**一个选项,不按别名去重 —— 同一个别名可以挂在好几家上,而那几家
    计费不同(按量 / 订阅)。去重之后用户看到的是一个 `deepseek-v4-flash`,
    却不知道自己钉的是哪条线;而 `value` 是 `providerId/alias`,天然唯一,
    两条同名的选项本身不存在。标签**永远**带上供应商,不搞「只有撞名时才加后缀」
    那一套:这一格存的就是一对,把一半藏起来等于又把「选哪一家」抹掉了。
  */
  const currentModelKey = form.model === '' ? '' : modelSelectionKey(
    form.modelProviderId === '' ? undefined : form.modelProviderId,
    form.model
  )
  const modelOptions = [
    { value: '', label: t('ext.field.inheritDefault') },
    ...models
      .filter((m) => m.enabled !== false)
      .map((m) => ({
        value: modelSelectionKey(m.providerId, m.alias),
        label: `${providers.find((p) => p.id === m.providerId)?.name ?? m.providerId} · ${m.alias}`
      }))
  ]
  /*
    ★ 文件里那一对**未必**还在上面这张表里,而两种落空的含义完全不同:

    - 只写了 `model:` 没写 `modelProviderId:`(从 Claude Code 粘过来的文件**总是**
      这个形状)—— 那是个**合法且有意**的状态:只认别名,由路由器按优先级择优。
      不给它一个选项的话,下拉框会显示空白,用户以为没设过,随手一选就把
      「不钉供应商」这件事默默改掉了。
    - 钉的那家已经删了 / 停用了 —— 这个得**看得出来**,但同样不能自动抹掉:
      供应商是会被重新启用的,替用户清掉那一行是不可逆的。

    两种都补一个选项在最前面,选中态才对得上,而用户不碰它就原样存回去。
  */
  if (currentModelKey !== '' && !modelOptions.some((o) => o.value === currentModelKey)) {
    modelOptions.splice(1, 0, {
      value: currentModelKey,
      label: form.modelProviderId === ''
        ? t('ext.field.modelAnyProvider', { alias: form.model })
        : t('ext.field.modelUnavailable', { alias: `${form.modelProviderId} · ${form.model}` })
    })
  }

  const generateDialog = (
    <Dialog
      title={t('ext.agents.generateTitle')}
      description={t('ext.agents.generateHint')}
      open={asking}
      onClose={() => setAsking(false)}
      footer={
        <>
          <Button type="button" variant="secondary" size="sm" onClick={() => setAsking(false)}>{t('common.cancel')}</Button>
          <Button
            type="button"
            variant="primary"
            size="sm"
            onClick={generate}
            disabled={generating}
          >
            <Sparkles size={13} />
            {generating ? t('ext.agents.generating') : t('ext.agents.generateAction')}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-2">
        <TextArea
          value={requirement}
          onCommit={setRequirement}
          rows={5}
          disabled={generating}
          placeholder={t('ext.agents.generatePlaceholder')}
          ariaLabel={t('ext.agents.generateTitle')}
        />
        {dirty && <p className="text-[11px] text-fg-faint">{t('ext.agents.generateOverwrite')}</p>}
        {genError !== null && <p className="text-[11px] text-danger" role="alert">{genError}</p>}
      </div>
    </Dialog>
  )

  if (sourceMode) {
    /*
      ★ 源码视图直接复用通用编辑器:那条「读不懂的语法保存会丢,得当面确认」
      和 Mod-S 保存都在它里面,重做一遍只会分叉。
    */
    return (
      <>
        <MarkdownResourceEditor
          kind="agent"
          file={{ ...file, name: form.name.trim(), scope }}
          frontmatter={source.fm}
          body={source.body}
          onBody={(body) => setSource((s) => ({ ...s, body }))}
          onSave={() => onSave({ scope, name: form.name.trim(), frontmatter: source.fm, body: source.body })}
          onDelete={onDelete}
          onClose={onClose}
          saving={saving}
          error={error}
          fields={
            <div className="flex flex-col gap-2">
              <Button type="button" variant="secondary" size="sm" onClick={toForm} className="self-start">{t('ext.agents.formMode')}</Button>
              <SourceFields fm={source.fm} set={(fm) => setSource((s) => ({ ...s, fm }))} />
            </div>
          }
        />
        {generateDialog}
      </>
    )
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className="flex shrink-0 items-center gap-2 border-b border-hairline px-4 py-2">
        <IconButton label={t('ext.back')} size={26} width={34} onClick={onClose} className="rounded-pill bg-tint">
          <ArrowLeft size={14} />
        </IconButton>
        <span className="truncate text-[13px] text-fg">{isNew ? t('ext.agents.newTitle') : file.name}</span>
        <span className="truncate text-[11px] text-fg-faint">{file.path}</span>
        <div className="ml-auto flex items-center gap-2">
          <Button type="button" variant="secondary" size="sm" onClick={askToGenerate} disabled={generating}>
            <Sparkles size={13} />
            {generating ? t('ext.agents.generating') : t('ext.agents.generate')}
          </Button>
          <Button type="button" variant="secondary" size="sm" onClick={toSource}>{t('ext.agents.sourceMode')}</Button>
          {!isNew && (
            <Button type="button" variant="danger" size="sm" onClick={() => setConfirmDelete(true)}>
              <Trash2 size={13} />
              {t('ext.delete')}
            </Button>
          )}
          <Button type="button" variant="primary" size="sm" onClick={save} disabled={saving || hardBlocked}>
            {t('ext.save')}
          </Button>
        </div>
      </div>

      {(error !== null || (attempted && blocked !== null)) && (
        <p className="shrink-0 border-b border-hairline px-4 py-1.5 text-[11px] text-danger" role="alert">
          {error ?? t(blocked as 'ext.error.emptyBody')}
        </p>
      )}

      <div className="min-h-0 flex-1 overflow-y-auto px-4 py-3">
        <div className="flex max-w-[760px] flex-col gap-3">
          <Row label={t('ext.field.name')} required>
            <div className="flex items-center gap-2">
              <TextInput
                value={form.name}
                onChange={(v) => setForm({ ...form, name: v })}
                size="sm"
                invalid={nameTaken || (attempted && blocked === 'ext.error.agentBadName')}
                placeholder={t('ext.field.nameHint')}
                ariaLabel={t('ext.field.name')}
              />
              <SegmentedControl
                value={scope}
                onValueChange={(value) => setScope(value as MarkdownResourceScope)}
                label={t('ext.scopeFilter')}
                options={[
                  { value: 'global', label: t('ext.scope.global') },
                  ...(workspaceId === null ? [] : [{ value: 'project' as const, label: t('ext.scope.project') }])
                ]}
              />
            </div>
          </Row>

          <Row label={t('ext.field.color')}>
            <div className="flex items-center gap-1.5">
              <button
                type="button"
                onClick={() => setForm({ ...form, color: '' })}
                aria-label={t('ext.field.colorNone')}
                aria-pressed={form.color === ''}
                className={cn(
                  'h-5 rounded-pill border px-2 text-[11px]',
                  form.color === '' ? 'border-accent text-fg' : 'border-border text-fg-faint'
                )}
              >
                {t('ext.field.colorNone')}
              </button>
              {AGENT_COLORS.map((color) => (
                <button
                  key={color}
                  type="button"
                  onClick={() => setForm({ ...form, color })}
                  aria-label={color}
                  aria-pressed={form.color === color}
                  style={{ backgroundColor: AGENT_COLOR_HEX[color] }}
                  className={cn(
                    'h-5 w-5 rounded-full border-2 transition-transform',
                    form.color === color ? 'border-fg scale-110' : 'border-transparent'
                  )}
                />
              ))}
            </div>
          </Row>

          <Row label={t('ext.field.model')}>
            <Select
              value={currentModelKey}
              options={modelOptions}
              onValueChange={(v) => {
                const picked = parseModelSelectionKey(v)
                setForm({
                  ...form,
                  model: v === '' ? '' : picked.alias,
                  modelProviderId: picked.modelProviderId ?? ''
                })
              }}
              ariaLabel={t('ext.field.model')}
              className="max-w-[320px]"
            />
          </Row>

          <Row label={t('ext.field.thinking')}>
            <Select
              value={form.thinking === '' ? INHERIT_THINKING : form.thinking}
              options={SUBAGENT_THINKING_CHOICES.map((value) => ({
                value,
                label: value === INHERIT_THINKING ? t('ext.field.inheritDefault') : t(`chat.thinkingLevel.${value}`)
              }))}
              onValueChange={(v) => setForm({ ...form, thinking: v === INHERIT_THINKING ? '' : v })}
              ariaLabel={t('ext.field.thinking')}
              className="max-w-[320px]"
            />
          </Row>

          <Row label={t('ext.field.description')} required>
            <TextArea
              value={form.description}
              onCommit={(v) => setForm({ ...form, description: v })}
              rows={2}
              placeholder={t('ext.field.agentDescriptionHint')}
              ariaLabel={t('ext.field.description')}
            />
          </Row>

          <Row label={t('ext.field.tools')}>
            <div className="flex flex-col gap-2">
              <SegmentedControl
                value={form.toolsMode}
                onValueChange={(value) => setForm({ ...form, toolsMode: value as 'all' | 'custom' })}
                label={t('ext.field.tools')}
                className="self-start"
                options={[
                  { value: 'all', label: t('ext.field.toolsAll') },
                  { value: 'custom', label: t('ext.field.toolsCustom') }
                ]}
              />
              {form.toolsMode === 'custom' && (
                <div className="grid grid-cols-4 gap-x-3 gap-y-1.5">
                  {AGENT_TOOL_CHOICES.map((tool) => (
                    <label key={tool} className="flex items-center gap-1.5 text-[12px] text-fg-muted">
                      <input
                        type="checkbox"
                        checked={form.tools.includes(tool)}
                        onChange={(e) =>
                          setForm({
                            ...form,
                            tools: e.target.checked
                              ? [...form.tools, tool]
                              : form.tools.filter((item) => item !== tool)
                          })
                        }
                      />
                      {tool}
                    </label>
                  ))}
                </div>
              )}
              {form.toolsMode === 'custom' && extraTools.length > 0 && (
                <p className="text-[11px] text-fg-faint">{t('ext.field.toolsUnknown', { list: extraTools.join('、') })}</p>
              )}
            </div>
          </Row>

          <Row label={t('ext.field.prompt')} required>
            <TextArea
              value={form.prompt}
              onCommit={(v) => setForm({ ...form, prompt: v })}
              rows={14}
              placeholder={t('ext.field.promptHint')}
              ariaLabel={t('ext.field.prompt')}
            />
          </Row>
        </div>
      </div>

      {generateDialog}

      <Dialog
        title={t('ext.deleteTitle', { name: file.name })}
        description={t('ext.deleteHint')}
        open={confirmDelete}
        onClose={() => setConfirmDelete(false)}
        footer={
          <>
            <Button type="button" variant="secondary" size="sm" onClick={() => setConfirmDelete(false)}>{t('common.cancel')}</Button>
            <Button type="button" variant="danger" size="sm" onClick={() => { setConfirmDelete(false); onDelete() }}>
              {t('ext.delete')}
            </Button>
          </>
        }
      >
        <p className="text-[12px] text-fg-muted">{file.path}</p>
      </Dialog>
    </div>
  )
}

/** 源码视图上半那排裸字段。★ 名字和作用域不在里面 —— 它们是文件的身份,由表单管。 */
function SourceFields({ fm, set }: { fm: Frontmatter; set: (fm: Frontmatter) => void }): ReactNode {
  const { t } = useI18n()
  return (
    <div className="flex flex-col gap-2">
      <label className="flex items-center gap-2">
        <span className="w-[92px] shrink-0 text-[12px] text-danger">{t('ext.field.description')}*</span>
        <TextInput
          value={readField(fm, 'description')}
          onChange={(v) => set(setField(fm, 'description', v))}
          size="sm"
          invalid={readField(fm, 'description').trim() === ''}
          placeholder={t('ext.field.agentDescriptionHint')}
          ariaLabel={t('ext.field.description')}
        />
      </label>
      <label className="flex items-center gap-2">
        <span className="w-[92px] shrink-0 text-[12px] text-fg-muted">{t('ext.field.tools')}</span>
        <TextInput
          value={readField(fm, 'tools')}
          onChange={(v) => set(setListField(fm, 'tools', v.split(',').map((s) => s.trim()).filter((s) => s !== '')))}
          size="sm"
          placeholder={t('ext.field.toolsHint')}
          ariaLabel={t('ext.field.tools')}
        />
      </label>
      <label className="flex items-center gap-2">
        <span className="w-[92px] shrink-0 text-[12px] text-fg-muted">{t('ext.field.model')}</span>
        <TextInput
          value={readField(fm, 'model')}
          onChange={(v) => set(setField(fm, 'model', v))}
          size="sm"
          placeholder={t('ext.field.modelHint')}
          ariaLabel={t('ext.field.model')}
        />
      </label>
    </div>
  )
}
