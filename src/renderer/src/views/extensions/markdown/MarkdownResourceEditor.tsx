/**
 * 命令 / 子代理的编辑器。上半是 frontmatter 表单，下半是正文（CodeMirror）。
 *
 * ★ 复用 `views/files/CodeEditor`：它已经接好了主题、语言高亮和 Mod-S 保存。
 *   正文上限 64KB，在 textarea 里编辑那么长的东西是折磨。
 */
import { ArrowLeft } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import type {
  MarkdownResourceFile,
  MarkdownResourceKind
} from '../../../../../shared/domain/markdown-resource'
import { Button } from '../../../components/arc/button/button'
import { Dialog } from '../../../components/ui/Dialog'
import { IconButton } from '../../../components/ui/IconButton'
import { useI18n } from '../../../i18n'
import { CodeEditor } from '../../files/CodeEditor'
import { validate, type Frontmatter } from './frontmatter-form'

export function MarkdownResourceEditor({
  kind,
  file,
  frontmatter,
  body,
  onBody,
  onSave,
  onDelete,
  onClose,
  saving,
  error,
  fields
}: {
  kind: MarkdownResourceKind
  file: MarkdownResourceFile
  frontmatter: Frontmatter
  body: string
  onBody: (body: string) => void
  onSave: () => void
  onDelete: () => void
  onClose: () => void
  saving: boolean
  error: string | null
  /** 各 kind 自己的 frontmatter 表单。改 frontmatter 的回调包在里面，这里不用再拿一份。 */
  fields: ReactNode
}): ReactNode {
  const { t } = useI18n()
  const [confirmDelete, setConfirmDelete] = useState(false)
  const invalid = validate(kind, frontmatter, body)

  const save = (): void => {
    if (invalid !== null || saving) return
    /*
      ★ 文件里有解析器读不懂的语法时，保存会把它们丢掉 —— 值在 parse 阶段就没了，
      序列化器无从恢复。这种情况必须当面确认，不能默默写出去。
      这和 `local-settings.ts` 那条「读不懂就拒绝写」是同一条原则的两种表达：
      那边直接拒绝，这边因为用户明确要编辑，改成告知 + 确认。
    */
    if (file.skipped.length > 0 && !window.confirm(t('ext.confirmLossy', { list: file.skipped.join('\n') }))) return
    onSave()
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className="flex shrink-0 items-center gap-2 border-b border-hairline px-4 py-2">
        <IconButton label={t('ext.back')} size={26} width={34} onClick={onClose} className="rounded-pill bg-tint">
          <ArrowLeft size={14} />
        </IconButton>
        <span className="truncate text-[13px] text-fg">{file.name}</span>
        <span className="truncate text-[11px] text-fg-faint">{file.path}</span>
        <div className="ml-auto flex items-center gap-2">
          <Button type="button" variant="danger" size="sm" onClick={() => setConfirmDelete(true)}>
            {t('ext.delete')}
          </Button>
          <Button type="button" variant="primary" size="sm" onClick={save} disabled={invalid !== null || saving}>
            {t('ext.save')}
          </Button>
        </div>
      </div>

      {(error !== null || invalid !== null) && (
        <p className="shrink-0 border-b border-hairline px-4 py-1.5 text-[11px] text-danger" role="alert">
          {error ?? t(invalid as 'ext.error.emptyBody')}
        </p>
      )}

      {file.skipped.length > 0 && (
        <p className="shrink-0 border-b border-hairline px-4 py-1.5 text-[11px] text-warning" role="status">
          {t('ext.lossyWarning', { count: String(file.skipped.length) })}
        </p>
      )}

      <div className="shrink-0 border-b border-hairline px-4 py-3">{fields}</div>

      <div className="min-h-0 flex-1">
        <CodeEditor value={body} onChange={onBody} path={file.path} onSave={save} />
      </div>

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
