/**
 * 消息里那张 `TodoWrite` 卡片展开后的样子 —— **先报这次更新改了什么,完整清单收在折叠里**。
 *
 * 需求:模型每轮发的是**完整清单**(工具契约如此),所以一张接一张的卡片看上去几乎一样,
 * 而真正值得一眼看见的是「它这次动了什么」:新开始了哪一项、完成了哪一项、
 * 尤其是有没有**悄悄丢掉一项**(前后两份清单各自都自洽,界面上完全看不出来)。
 * 完整清单仍然要给 —— 它是用户在整个转录里唯一会反复回看的状态,只是不必默认铺开。
 *
 * ★ **不复用输入框上方那条的标题行。** 那条的标题是「任务清单 · N/M 已完成」+ 进度环 +
 * 百分比,而卡片自己的标题行已经写了「更新任务清单」和同一份 N/M —— 展开后再报一遍
 * 是同一屏里的第三处重复。列表本体仍然复用 `TaskChecklistRows`,两份清单不会分叉。
 *
 * ★ 清单从**卡片自己的入参**收窄(`previewTodos`),不是从转录里那份
 * `latestTodosFrom` 取的:两者在「最近一次成功的调用」这个判据上有细微差别(压缩过
 * 的历史、还没配对的回执),而卡片要画的是**这一次调用** —— 它的 `callId` 就在手边,
 * 转录那一侧用来回答的只是另一个问题:「这次调用之前那份是什么」。
 */
import { ChevronRight, ListChecks } from 'lucide-react'
import type { ReactNode } from 'react'
import { useTodoHistory } from './todo-history'
import { TaskChecklistRows, TaskChecklistShell } from './TaskChecklist'
import { previewTodos } from './todo-preview'
import { todoPlan } from '../../../../shared/agent/todo'
import { useI18n } from '../../i18n'
import { cn } from '../../lib/cn'

export function TodoWriteChecklist({
  callId,
  input,
  className
}: {
  /** 转录里这次调用的 id —— 用它去问「这次之前那份清单是什么」。 */
  callId: string | undefined
  /** 流式中的半截 JSON 或内核严格解析后的入参,收窄规则见 `todo-preview.ts`。 */
  input: unknown
  /** 外层定位的覆盖位(卡片详情里要抹掉输入框上方那套居中/限宽)。 */
  className?: string
}): ReactNode {
  const { t } = useI18n()
  const previous = useTodoHistory(callId)
  const todos = previewTodos(input)
  const plan = todoPlan(todos, previous)

  /*
    需求:没有变化的那一档**不报** —— 四条都写「0 项」等于什么也没说,而标题行那行
    本来就该一眼读完。真的什么都没变时给一句明确的话,而不是留一个空行。

    ★ 「不知道上一份是什么」(没有 provider / 老转录)与「确实没变」必须分开:
    前者不许说「清单无变化」—— 那是一句它没有依据的断言(见 `todo-history.tsx`)。
  */
  const chips: string[] = []
  if (plan.newlyStarted > 0) chips.push(t('chat.todoUpdate.started', { count: plan.newlyStarted }))
  if (plan.newlyCompleted > 0) chips.push(t('chat.todoUpdate.completed', { count: plan.newlyCompleted }))
  if (plan.added.length > 0) chips.push(t('chat.todoUpdate.added', { count: plan.added.length }))
  if (plan.removed.length > 0) chips.push(t('chat.todoUpdate.removed', { count: plan.removed.length }))
  const delta = chips.length > 0
    ? chips.join(' · ')
    : previous === undefined
      ? undefined
      : t('chat.todoUpdate.noChange')

  /*
    高亮的判据是**状态变了或本次新增**,不是「出现在 added 里」:
    `added` 里的项必然是新状态,不必重复;而「pending → in_progress」这种同一项
    的状态迁移不在任何一个计数里,却是最该被看见的那一行。
  */
  const highlight = new Set<string>([
    ...plan.added.map((item) => item.content),
    ...todos
      .filter((item) => {
        const before = previous?.find((p) => p.content === item.content)
        return before !== undefined && before.status !== item.status
      })
      .map((item) => item.content)
  ])

  const done = todos.filter((item) => item.status === 'completed').length

  return (
    <div data-testid="todo-write-checklist">
      <TaskChecklistShell
        defaultCollapsed
        className={className}
        list={<TaskChecklistRows todos={todos} isActive={false} highlight={highlight} />}
        header={({ toggle, collapsed, listId }) => (
          <button
            type="button"
            aria-expanded={!collapsed}
            aria-controls={listId}
            onClick={toggle}
            className="flex w-full min-w-0 items-center gap-2.5 px-2.5 py-2 text-left"
          >
            <ListChecks aria-hidden size={14} className="shrink-0 text-fg-faint" />
            <span className="min-w-0 flex-1">
              {delta !== undefined && (
                <span className="block truncate text-[12.5px] text-fg">{delta}</span>
              )}
              {/*
                需求:光有「完成 1 项」看不出清单还剩多少 —— 报出**当前**完成度。
                ★ 这一行与输入框上方那条的标题是同一个数,但两处不可能同时出现在一屏:
                  上方那条是「此刻的清单」,这张是「某一次历史更新」。
                  说「不知道上一份」时它退化成第一行(不能留一个空标题行)。
              */}
              <span className={cn(
                'block truncate text-fg-faint',
                delta === undefined ? 'text-[12.5px] text-fg' : 'mt-0.5 text-[11px]'
              )}>
                {t('chat.taskChecklist', { done, total: todos.length })}
              </span>
            </span>
            <span className={cn(
              'shrink-0 text-[11px]',
              collapsed ? 'text-fg-faint' : 'text-fg-muted'
            )}>
              {collapsed ? t('chat.todoUpdate.showFull') : t('chat.todoUpdate.hideFull')}
            </span>
            <ChevronRight
              aria-hidden
              size={13}
              className={cn(
                'shrink-0 text-fg-faint transition-transform motion-reduce:transition-none',
                !collapsed && 'rotate-90'
              )}
            />
          </button>
        )}
      />
      {/*
        需求:被丢掉的项必须单独列出来 —— 它们在下面的完整清单里**已经不存在了**,
        只在标题行的计数里出现一次是不够的:用户会想知道「丢的是哪一条」。
      */}
      {plan.removed.length > 0 && (
        <ul className="mt-1 space-y-0.5 px-1" data-testid="todo-dropped">
          {plan.removed.map((item, index) => (
            <li
              key={`${String(index)}:${item.content}`}
              className="flex min-w-0 items-center gap-1.5 text-[11.5px] text-warning"
            >
              <span aria-hidden>−</span>
              <span className="min-w-0 flex-1 truncate">{item.content}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
