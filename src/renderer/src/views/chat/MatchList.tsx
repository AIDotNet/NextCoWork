/**
 * `Grep` / `Glob` 展开后的命中清单。
 *
 * ★ 每条命中的**文件名是可点的**(有工作区入口时):找到了东西,下一步一定是
 * 去看它 —— 让用户把路径复制出来再去文件树里翻,是这一步最常见的浪费。
 * 拿不到入口(只读子代理面板)时退化成纯文本,不画点了没反应的链接(§5)。
 *
 * ★ 行号跟在路径后面、压暗;命中内容等宽且**不折行**(同 `CodeBlock` 的理由:
 * 代码靠缩进和列对齐读)。认不出形状的行原样摆着,见 `search-matches.ts`。
 */
import type { ReactNode } from 'react'
import { base } from '../../../../shared/domain/tool-presenter'
import { useI18n } from '../../i18n'
import { cn } from '../../lib/cn'
import { DETAIL_CARD_CLASS } from './detail-card'
import { parseMatches } from './search-matches'
import { useWorkspaceFile } from './workspace-file'

/** 一次搜索可能命中几千行;清单在渲染前截断,理由同 `CodeBlock` */
const MAX_ROWS = 60

export function MatchList({ content }: { content: string }): ReactNode {
  const { t } = useI18n()
  const { open } = useWorkspaceFile()
  const all = parseMatches(content)
  if (all.length === 0) return null
  const rows = all.slice(0, MAX_ROWS)
  const omitted = all.length - rows.length

  return (
    <div
      data-testid="match-list"
      className={cn(DETAIL_CARD_CLASS, 'scroll-thin max-h-[min(50vh,420px)] overflow-auto px-2.5 py-1.5 text-[12.5px]')}
    >
      {rows.map((row, index) => {
        const path = row.path
        return (
          <div key={`${path ?? ''}:${row.line ?? 0}:${index}`} className="flex min-w-0 items-baseline gap-2 py-px">
            {path !== undefined && (
              open === undefined
                ? (
                  <span data-testid="match-path" className="shrink-0 truncate text-fg" title={path}>
                    {base(path)}
                  </span>
                )
                : (
                  <button
                    type="button"
                    data-testid="match-path"
                    title={t('chat.tool.openFile', { path })}
                    onClick={() => open(path)}
                    className="shrink-0 cursor-pointer truncate text-fg hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/50"
                  >
                    {base(path)}
                  </button>
                )
            )}
            {row.line !== undefined && (
              <span className="shrink-0 font-mono text-[11.5px] text-fg-faint tabular-nums">:{row.line}</span>
            )}
            {row.text !== undefined && row.text !== '' && (
              <span className={cn(
                'min-w-0 flex-1 truncate font-mono text-[11.5px]',
                path === undefined ? 'text-fg-faint' : 'text-fg-muted'
              )}>
                {row.text.trim()}
              </span>
            )}
          </div>
        )
      })}
      {omitted > 0 && (
        <div className="pt-0.5 text-[11.5px] text-fg-faint">
          {t('chat.tool.linesOmitted', { count: omitted }).trim()}
        </div>
      )}
    </div>
  )
}
