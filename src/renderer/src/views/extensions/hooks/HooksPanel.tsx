/**
 * 钩子面板。和命令 / 子代理不同，钩子不是「一个文件一条」，而是两份 JSON 文件里
 * 的两段数组 —— 所以它不复用 `ResourcePanel`，列表按事件分组，编辑走 Dialog。
 */
import { AlertTriangle, Play, Plus, Webhook } from 'lucide-react'
import { useCallback, useEffect, useState, type ReactNode } from 'react'
import {
  BLOCKING_HOOK_EVENTS,
  HOOK_EVENTS,
  HOOK_TYPES,
  defaultTimeoutMs,
  type HookEvent,
  type HookDiagnostic,
  type HookListItem,
  type HookRunReport,
  type HookScope,
  type HookType
} from '../../../../../shared/domain/hook'
import { HOOK_TEMPLATES, findHookTemplate } from '../../../../../shared/domain/hook-templates'
import {
  modelSelectionKey,
  parseModelSelectionKey
} from '../../../../../shared/domain/model-selection'
import type { InvokeReq } from '../../../../../shared/ipc/contract'
import { Button } from '../../../components/arc/button/button'
import { useChatModelGuard } from '../../../components/useChatModelGuard'
import { Dialog } from '../../../components/ui/Dialog'
import { EmptyState } from '../../../components/arc/empty-state/empty-state'
import { NumberInput } from '../../../components/ui/NumberInput'
import SegmentedControl from '../../../components/arc/segmented-control/segmented-control'
import { Select } from '../../../components/ui/Select'
import { TextArea } from '../../../components/ui/TextArea'
import { TextInput } from '../../../components/ui/TextInput'
import { Switch } from '../../../components/arc/switch/switch'
import { useI18n } from '../../../i18n'
import { modelOptions } from '../../../settings/pages/model/enabled-models'
import {
  deleteHook,
  hookDiagnostics,
  listHooks,
  onHooksChanged,
  saveHook,
  setHookEnabled,
  testHook
} from '../../../services/hooks'
import { useModelsStore } from '../../../stores/models'
import { useWindowStore } from '../../../stores/window'
import {
  draftToUpsert,
  emptyHookDraft,
  templateToDraft,
  validateHook,
  warnHook,
  type HookDraft
} from './hook-form'

/** 列表里那一行的主标题：两支各有一个「正文」字段。 */
function hookBody(row: HookListItem): string {
  return row.type === 'prompt' ? row.prompt : row.command
}

/**
 * 草稿里那一对 `(别名, 供应商)` 在复合下拉里的键。
 *
 * ★ 空别名 = 「回落到本次 run 的模型」,而那时**供应商那一半没有意义**(单独一个
 *   providerId 配不出任何一条绑定,见 `draftToUpsert`),所以键也必须是空的。
 */
function draftModelKey(draft: HookDraft): string {
  const model = draft.model.trim()
  if (model === '') return ''
  const providerId = draft.modelProviderId.trim()
  return modelSelectionKey(providerId === '' ? undefined : providerId, model)
}

export function HooksPanel(): ReactNode {
  const { t } = useI18n()
  const workspaceId = useWindowStore((s) => s.activeWorkspaceId)
  const models = useModelsStore((s) => s.models)
  const providers = useModelsStore((s) => s.providers)
  const modelsLoaded = useModelsStore((s) => s.loaded)
  const loadModels = useModelsStore((s) => s.load)
  const [rows, setRows] = useState<HookListItem[]>([])
  const [diagnostics, setDiagnostics] = useState<HookDiagnostic[]>([])
  const [error, setError] = useState<string | null>(null)

  const [editing, setEditing] = useState<{ draft: HookDraft; id?: string; scope: HookScope } | null>(null)
  const [testing, setTesting] = useState(false)
  /**
   * 试运行的结果。★ **prompt 型也是真发一次请求**(`hooks:test`),不再在本地拼。
   *
   * 拼 `$ARGUMENTS` 的那一段示例 payload 只能有一份 —— 判定器真正看到的那段
   * 展开是主进程做的,渲染层再拼一份「差不多的」,两段就会各自漂移:用户照着
   * 预览改好了 prompt,真跑起来收到的却是另一个形状,而预览本身没有任何症状。
   * 那正是「预览」这个词最坏的一种失效方式。
   */
  const [testResult, setTestResult] = useState<HookRunReport | null>(null)

  /*
    需求:prompt 型钩子的模型选择器只列文本模型,存量钩子里钉着图片模型的在
    **打开选择器时**校正成第一个文本模型并提示(见 `useChatModelGuard` 文件头;
    改的是编辑草稿,落盘仍走「保存」)。
  */
  const guardChatModel = useChatModelGuard(
    models,
    providers,
    {
      model: editing?.draft.model ?? '',
      modelProviderId: editing?.draft.modelProviderId.trim() === ''
        ? undefined
        : editing?.draft.modelProviderId
    },
    (model, modelProviderId) => {
      if (editing === null) return
      setEditing({ ...editing, draft: { ...editing.draft, model, modelProviderId } })
    }
  )

  useEffect(() => {
    if (!modelsLoaded) void loadModels()
  }, [modelsLoaded, loadModels])

  const test = (): void => {
    if (editing === null || validateHook(editing.draft) !== null) return
    setTesting(true)
    setTestResult(null)
    const base = {
      scope: editing.scope,
      event: editing.draft.event,
      timeoutMs: Math.round(editing.draft.timeoutSeconds * 1000),
      ...(workspaceId === null ? {} : { workspaceId })
    }
    /*
      ★ 两支共用一个频道(`hooks:test`),靠 `type` 判别 —— 见 `services/hooks.ts`。
        prompt 型那一支走的是同样的往返:预览由主进程给,这里只负责显示。
    */
    const request: InvokeReq<'hooks:test'> = editing.draft.type === 'prompt'
      ? { ...base, type: 'prompt', prompt: editing.draft.prompt.trim() }
      : { ...base, type: 'command', command: editing.draft.command.trim() }
    void testHook(request)
      .then(setTestResult)
      // ★ 失败时只把主进程那句人话显示出来,不新编文案;非 Error 的抛出物落回一条已有提示。
      .catch((e: unknown) => setError(e instanceof Error ? e.message : t('ext.error.loadFailed')))
      .finally(() => setTesting(false))
  }

  const refresh = useCallback(() => {
    void listHooks(workspaceId ?? undefined).then(setRows).catch(() => setError(t('ext.error.loadFailed')))
    void hookDiagnostics(workspaceId ?? undefined).then(setDiagnostics).catch(() => undefined)
  }, [workspaceId, t])

  useEffect(() => {
    refresh()
    return onHooksChanged(refresh)
  }, [refresh])

  const save = (): void => {
    if (editing === null) return
    const { draft, id, scope } = editing
    if (validateHook(draft) !== null) return
    setError(null)
    void saveHook(scope, draftToUpsert(draft, id), workspaceId ?? undefined)
      .then(() => { setEditing(null); refresh() })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
  }

  const remove = (row: HookListItem): void => {
    void deleteHook(row.scope, row.id, workspaceId ?? undefined)
      .then(refresh)
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
  }

  const invalid = editing === null ? null : validateHook(editing.draft)
  const warning = editing === null ? null : warnHook(editing.draft)

  /**
   * 试运行那一屏要显示的东西。
   *
   * ★ 有 `preview` 就是 prompt 型：主进程已经把 `$ARGUMENTS` 展开好了,原样显示。
   *   没有则是命令型,按老样子给结果行 + stdout / stderr。
   */
  const testOutput = testResult === null
    ? null
    : testResult.preview ?? `${t('hooks.testResult', {
      outcome: testResult.outcome,
      code: testResult.exitCode === null ? '-' : String(testResult.exitCode),
      ms: String(testResult.durationMs)
    })}${testResult.stdout === '' ? '' : `\n\nstdout:\n${testResult.stdout}`}${
      testResult.stderr === '' ? '' : `\n\nstderr:\n${testResult.stderr}`}`

  /** prompt 型那一格存的是一对 `(别名, 供应商)`,候选也只能是复合键。 */
  const currentModelKey = editing === null ? '' : draftModelKey(editing.draft)
  /*
    ★ 空选项的文案跟设置里那几栏用同一句「跟随对话」:空在这里的含义就是
      「回落到本次 run 的模型」,和 `settings.subagent.model` 那一栏逐字相同。
  */
  const promptModelOptions = [
    { value: '', label: t('models.followConversation') },
    ...modelOptions(models.filter((m) => m.enabled !== false), providers)
  ]
  /*
    ★ 文件里那一对**未必**还在这张候选表里(钉的那家被停用 / 被删,或者只写了别名
      没写供应商)。不补一个选项的话,下拉框会显示成空白 —— 用户以为没配过,随手
      一选就把「不钉供应商」这件事默默改掉了;而钉着的那家是会被重新启用的,替用户
      清掉那一行不可逆。补上之后选中态才对得上,用户不碰它就原样存回去。
  */
  if (
    editing !== null &&
    currentModelKey !== '' &&
    !promptModelOptions.some((o) => o.value === currentModelKey)
  ) {
    const model = editing.draft.model.trim()
    const providerId = editing.draft.modelProviderId.trim()
    promptModelOptions.splice(1, 0, {
      value: currentModelKey,
      label: providerId === ''
        ? t('ext.field.modelAnyProvider', { alias: model })
        : t('ext.field.modelUnavailable', { alias: `${providerId} · ${model}` })
    })
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className="flex shrink-0 items-center gap-2 px-4 py-2">
        <span className="text-[12px] text-fg-faint">{t('hooks.runNote')}</span>
        <Button
          type="button"
          variant="primary"
          size="sm"
          className="ml-auto"
          onClick={() => {
            setTestResult(null)
            setEditing({ draft: emptyHookDraft('PreToolUse'), scope: workspaceId === null ? 'global' : 'project' })
          }}
        >
          <Plus size={13} />
          {t('ext.new')}
        </Button>
      </div>

      {error !== null && <p className="shrink-0 px-4 pb-1 text-[11px] text-danger" role="alert">{error}</p>}
      {diagnostics.map((d, index) => (
        <p key={`${d.path}:${index}`} className="shrink-0 px-4 pb-1 text-[11px] text-warning" role="status">
          <AlertTriangle size={11} className="mr-1 inline" />
          {d.messageKey === undefined ? d.message : t(d.messageKey, d.messageParams)}{d.path === '' ? '' : ` — ${d.path}`}
        </p>
      ))}

      {rows.length === 0 ? (
        <div className="flex min-h-0 flex-1 items-center justify-center">
          <EmptyState icon={<Webhook size={26} />} title={t('ext.hooks.empty')} description={t('ext.hooks.emptyHint')} />
        </div>
      ) : (
        <div className="min-h-0 flex-1 overflow-y-auto px-4 pb-4">
          {HOOK_EVENTS.filter((e) => rows.some((r) => r.event === e)).map((event) => (
            <section key={event} className="mb-3">
              <h2 className="mb-1 text-[11px] uppercase tracking-wide text-fg-faint">{event}</h2>
              <ul className="flex flex-col gap-1">
                {rows.filter((r) => r.event === event).map((row) => (
                  <li key={`${row.scope}:${row.id}`} className="flex items-center gap-3 rounded-lg px-3 py-2 hover:bg-tint">
                    <button
                      type="button"
                      className="min-w-0 flex-1 text-left"
                      onClick={() => {
                        // ★ 换一条草稿 = 上一次的试运行结果跟它没有任何关系,清掉。
                        setTestResult(null)
                        setEditing({
                          id: row.id,
                          scope: row.scope,
                          draft: {
                            ...emptyHookDraft(row.event, row.type),
                            matcher: row.matcher ?? '',
                            ...(row.type === 'prompt'
                              ? {
                                prompt: row.prompt,
                                model: row.model ?? '',
                                modelProviderId: row.modelProviderId ?? ''
                              }
                              : { command: row.command }),
                            timeoutSeconds: row.timeoutMs / 1000,
                            description: row.description ?? '',
                            enabled: row.enabled
                          }
                        })
                      }}
                    >
                      <span className="block truncate font-mono text-[12px] text-fg">{hookBody(row)}</span>
                      <span className="block truncate text-[11px] text-fg-faint">
                        {row.type === 'prompt' ? `${t('hooks.type.prompt')} · ` : ''}
                        {row.matcher ?? t('hooks.anyTool')}
                        {row.description === undefined ? '' : ` · ${row.description}`}
                      </span>
                    </button>
                    <span className="shrink-0 rounded-pill bg-tint px-2 py-0.5 text-[10px] text-fg-muted">
                      {row.scope === 'project' ? t('ext.scope.project') : t('ext.scope.global')}
                    </span>
                    <Switch
                      checked={row.enabled}
                      aria-label={t('ext.toggleLabel', { name: hookBody(row) })}
                      onCheckedChange={(v) => {
                        void setHookEnabled(row.scope, row.id, v, workspaceId ?? undefined).then(refresh)
                      }}
                    />
                    <Button type="button" variant="danger" size="sm" onClick={() => remove(row)}>{t('ext.delete')}</Button>
                  </li>
                ))}
              </ul>
            </section>
          ))}
        </div>
      )}

      <Dialog
        title={t('hooks.editTitle')}
        open={editing !== null}
        onClose={() => setEditing(null)}
        width={560}
        footer={
          <>
            <Button type="button" variant="secondary" size="sm" onClick={() => setEditing(null)}>{t('common.cancel')}</Button>
            <Button type="button" variant="primary" size="sm" onClick={save} disabled={invalid !== null}>{t('ext.save')}</Button>
          </>
        }
      >
        {editing !== null && (
          <div className="flex flex-col gap-3">
            {/*
              模板只**填充表单**，不直接保存 —— 钩子执行本机命令，哪怕内容无害，
              「装了应用就开始跑」这件事也该由用户点头。选完还能改、还能试运行。
            */}
            <label className="flex flex-col gap-1">
              <span className="text-[12px] text-fg-muted">{t('hooks.field.template')}</span>
              <Select
                inModal
                value=""
                ariaLabel={t('hooks.field.template')}
                options={[
                  { value: '', label: t('hooks.template.pick') },
                  ...HOOK_TEMPLATES.map((tpl) => ({
                    value: tpl.id,
                    label:
                      t(`hooks.template.${tpl.id}` as 'hooks.template.danger-guard') +
                      (tpl.platform === 'darwin' ? ' (macOS)' : '')
                  }))
                ]}
                onValueChange={(id) => {
                  const tpl = findHookTemplate(id)
                  if (tpl === undefined) return
                  setTestResult(null)
                  setEditing({
                    ...editing,
                    draft: templateToDraft(tpl, t(`hooks.template.${tpl.id}` as 'hooks.template.danger-guard'))
                  })
                }}
              />
            </label>

            {/*
              ★ 类型选在事件**之前**：它决定下面那一屏长什么样（命令还是 prompt、
              默认超时是 60 秒还是 30 秒）。放在后面的话用户会先填完再发现填错了地方。
            */}
            <label className="flex flex-col gap-1">
              <span className="text-[12px] text-fg-muted">{t('hooks.field.type')}</span>
              <SegmentedControl
                value={editing.draft.type}
                label={t('hooks.field.type')}
                onValueChange={(value) => {
                  // 换类型 = 上一次那份结果属于另一种钩子,清掉。
                  setTestResult(null)
                  /*
                    ★ 只换 `type` 和默认超时，**两段正文都留着**：用户来回切时
                      他已经写好的那一段不该被清掉（切回来发现空了是最恼人的表单行为）。
                      保存时 `draftToUpsert` 只取其中一支。
                  */
                  setEditing({
                    ...editing,
                    draft: {
                      ...editing.draft,
                      type: value as HookType,
                      timeoutSeconds: defaultTimeoutMs(editing.draft.event, value as HookType) / 1000
                    }
                  })
                }}
                options={HOOK_TYPES.map((v) => ({
                  value: v,
                  label: t(v === 'prompt' ? 'hooks.type.prompt' : 'hooks.type.command')
                }))}
              />
            </label>

            <label className="flex flex-col gap-1">
              <span className="text-[12px] text-fg-muted">{t('hooks.field.event')}</span>
              <Select
                inModal
                value={editing.draft.event}
                ariaLabel={t('hooks.field.event')}
                options={HOOK_EVENTS.map((e) => ({ value: e, label: `${e} — ${t(`hooks.event.${e}` as 'hooks.event.PreToolUse')}` }))}
                onValueChange={(v) =>
                  setEditing({ ...editing, draft: { ...editing.draft, event: v as HookEvent } })
                }
              />
            </label>

            {/*
              ★ `Stop` 的「阻断」和 PreToolUse / UserPromptSubmit **方向相反**：
              后两个拦的是「别发生」，Stop 拦的是收尾 —— 拦住的结果是这一轮**继续跑**。
              不说清会让用户照着前两个的直觉配出一个他没想到的死循环。
            */}
            {editing.draft.event === 'Stop' && BLOCKING_HOOK_EVENTS.includes('Stop') && (
              <p className="text-[11px] text-warning" role="status">{t('hooks.blocking.stopNote')}</p>
            )}

            <label className="flex flex-col gap-1">
              <span className="text-[12px] text-fg-muted">{t('hooks.field.matcher')}</span>
              <TextInput
                value={editing.draft.matcher}
                onChange={(v) => setEditing({ ...editing, draft: { ...editing.draft, matcher: v } })}
                placeholder={t('hooks.field.matcherHint')}
                ariaLabel={t('hooks.field.matcher')}
                invalid={invalid === 'hooks.error.badMatcher'}
              />
            </label>

            {editing.draft.type === 'prompt' ? (
              <>
                <label className="flex flex-col gap-1">
                  <span className="text-[12px] text-fg-muted">{t('hooks.prompt.label')}</span>
                  <TextArea
                    value={editing.draft.prompt}
                    onCommit={(v) => setEditing({ ...editing, draft: { ...editing.draft, prompt: v } })}
                    placeholder={t('hooks.prompt.placeholder')}
                    ariaLabel={t('hooks.prompt.label')}
                    rows={4}
                  />
                </label>
                {/*
                  ★ 判定模型是**一对** `(别名, 供应商)`,和设置里那几栏同一种东西 ——
                    模型名 / 供应商名是**领域值**,不翻译。空着 = 回落到本次 run 的模型。
                    两个裸输入框拼不出这一对:用户填了别名却不知道那一半落在哪家,
                    而同一别名挂在不同家上时计费是不同的(`modelSelectionKey`)。
                */}
                <label className="flex flex-col gap-1">
                  <span className="text-[12px] text-fg-muted">{t('hooks.prompt.model')}</span>
                  <Select
                    inModal
                    value={currentModelKey}
                    options={promptModelOptions}
                    ariaLabel={t('hooks.prompt.model')}
                    onOpenChange={guardChatModel}
                    onValueChange={(key) => {
                      const { alias, modelProviderId } = parseModelSelectionKey(key)
                      // 空键 = 两半一起清掉:只留一个 providerId 配不出任何一条绑定。
                      setEditing({
                        ...editing,
                        draft: { ...editing.draft, model: alias, modelProviderId: modelProviderId ?? '' }
                      })
                    }}
                  />
                </label>
              </>
            ) : (
              <label className="flex flex-col gap-1">
                <span className="text-[12px] text-fg-muted">{t('hooks.field.command')}</span>
                <TextArea
                  value={editing.draft.command}
                  onCommit={(v) => setEditing({ ...editing, draft: { ...editing.draft, command: v } })}
                  placeholder="./scripts/guard.sh"
                  ariaLabel={t('hooks.field.command')}
                  rows={3}
                />
              </label>
            )}

            <div className="flex items-center gap-3">
              <label className="flex items-center gap-2">
                <span className="text-[12px] text-fg-muted">{t('hooks.field.timeout')}</span>
                <NumberInput
                  value={editing.draft.timeoutSeconds}
                  onCommit={(v) => setEditing({ ...editing, draft: { ...editing.draft, timeoutSeconds: v } })}
                  ariaLabel={t('hooks.field.timeout')}
                  min={1}
                  max={600}
                />
              </label>
              <SegmentedControl
                value={editing.scope}
                onValueChange={(v) => setEditing({ ...editing, scope: v as HookScope })}
                label={t('ext.scopeFilter')}
                options={[
                  { value: 'global', label: t('ext.scope.global') },
                  ...(workspaceId === null ? [] : [{ value: 'project', label: t('ext.scope.project') }])
                ]}
              />
            </div>

            {warning !== null && (
              <p className="text-[11px] text-warning" role="status">{t(warning as 'hooks.warn.weakMatcher')}</p>
            )}
            {invalid !== null && (
              <p className="text-[11px] text-danger" role="alert">{t(invalid as 'hooks.error.emptyCommand')}</p>
            )}
            {/* ★ 那是「它会跑本机命令」的提醒 —— 对 prompt 型说这句话是错的。 */}
            {editing.draft.type === 'command' && (
              <p className="text-[11px] text-fg-faint">{t('hooks.dangerNote')}</p>
            )}

            {/*
              ★ 试运行是这一屏性价比最高的控件：钩子的失败模式（脚本路径不对、
              shell 语法、超时）全都只有真跑一次才暴露，而配好之后要等到某次
              工具调用才会触发，那时候错误早就淹没在别的事情里了。

              ★ prompt 型也走同一条频道：它只预览 `$ARGUMENTS` 的替换结果，不真调模型。
              那段展开由主进程给（渲染层不再自己拼一份示例 payload）—— 理由见
              `testResult` 那段。
            */}
            <div className="flex items-center gap-2 border-t border-hairline pt-3">
              <Button type="button" variant="secondary" size="sm" onClick={test} disabled={invalid !== null || testing}>
                <Play size={13} />
                {testing
                  ? t('hooks.testing')
                  : editing.draft.type === 'prompt' ? t('hooks.prompt.preview') : t('hooks.test')}
              </Button>
              {editing.draft.type === 'command' && workspaceId === null && (
                <span className="text-[11px] text-fg-faint">{t('hooks.testNeedsWorkspace')}</span>
              )}
              {editing.draft.type === 'prompt' && (
                <span className="text-[11px] text-fg-faint">{t('hooks.prompt.previewNote')}</span>
              )}
            </div>
            {testOutput !== null && (
              <pre className="max-h-40 overflow-auto whitespace-pre-wrap rounded-lg bg-surface-input p-2 font-mono text-[11px] text-fg-muted">
                {testOutput}
              </pre>
            )}
          </div>
        )}
      </Dialog>
    </div>
  )
}
