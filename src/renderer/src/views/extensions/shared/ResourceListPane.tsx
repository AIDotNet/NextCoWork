/**
 * 资源列表那半边:搜索框 + 作用域筛选 + 新建按钮 + 表格 + 错误行。
 *
 * ★ 从 `ResourcePanel` 里抽出来,是因为**新建流程分叉了**:命令那一套是「先弹框
 *   问名字和作用域,再进编辑器」,而子代理的名字和作用域已经在它自己的结构化
 *   表单里,再问一遍是多一道无意义的门。分叉的只有新建之后那一步,列表这半边
 *   两边一模一样 —— 所以抽的是列表,不是整块面板。
 */
import { Plus, Search } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { Button } from '../../../components/arc/button/button'
import SegmentedControl from '../../../components/arc/segmented-control/segmented-control'
import { TextInput } from '../../../components/ui/TextInput'
import { useI18n } from '../../../i18n'
import { filterRows, type ScopeFilter } from './filter'
import { ResourceTable, type ResourceRow } from './ResourceTable'

export function ResourceListPane<T extends ResourceRow>({
  rows,
  error,
  onNew,
  onOpen,
  onToggle,
  icon,
  emptyTitle,
  emptyHint,
  openReadOnly = false,
  hideToggle = false
}: {
  rows: readonly T[]
  error: string | null
  onNew: () => void
  onOpen: (row: T) => void
  onToggle: (row: T, enabled: boolean) => void
  icon: ReactNode
  emptyTitle: string
  emptyHint: string
  openReadOnly?: boolean
  hideToggle?: boolean
}): ReactNode {
  const { t } = useI18n()
  // 搜索词和筛选是**这半边自己的** UI 状态,不值得往上提:提上去之后每个
  // 调用点都要再声明一对,而它们的生命周期和这块列表完全一致。
  const [query, setQuery] = useState('')
  const [scopeFilter, setScopeFilter] = useState<ScopeFilter>('all')
  const visible = filterRows(rows, query, scopeFilter)

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className="flex shrink-0 items-center gap-2 px-4 py-2">
        <TextInput
          value={query}
          onChange={setQuery}
          size="sm"
          icon={<Search size={13} />}
          placeholder={t('ext.searchPlaceholder')}
          ariaLabel={t('ext.searchPlaceholder')}
          className="max-w-[220px]"
        />
        <SegmentedControl
          value={scopeFilter}
          onValueChange={(value) => setScopeFilter(value as ScopeFilter)}
          label={t('ext.scopeFilter')}
          options={[
            { value: 'all', label: t('ext.scope.all') },
            { value: 'global', label: t('ext.scope.global') },
            { value: 'project', label: t('ext.scope.project') }
          ]}
        />
        <Button type="button" variant="primary" size="sm" className="ml-auto" onClick={onNew}>
          <Plus size={13} />
          {t('ext.new')}
        </Button>
      </div>

      {error !== null && (
        <p className="shrink-0 px-4 pb-1 text-[11px] text-danger" role="alert">{error}</p>
      )}

      <ResourceTable
        rows={visible}
        icon={icon}
        emptyTitle={emptyTitle}
        emptyHint={emptyHint}
        onOpen={onOpen}
        onToggle={onToggle}
        openReadOnly={openReadOnly}
        hideToggle={hideToggle}
      />
    </div>
  )
}
