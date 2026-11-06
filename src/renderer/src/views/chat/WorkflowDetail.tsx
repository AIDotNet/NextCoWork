/**
 * `workflow` 形态的详情区 —— 选出「此刻该画哪一份状态」,交给懒加载的画布。
 *
 * 三个来源,按新鲜度取第一个有的:
 * 1. 运行中的整份快照(`ToolCallState.workflow`,易失)
 * 2. 落盘的最终状态(`output.workflow`)
 * 3. 从入参现推的结构,全部 `pending`(参数刚提交、或者这次调用被中断了没有结果)
 *
 * ★ 失败且三者都拿不到(入参本身就不成图)时退回普通的失败输出 ——
 *   工具已经在结果里说了图哪里不对,那句话比一张空画布有用。
 */
import { lazy, Suspense, type ReactNode } from 'react'
import { workflowFromInput, type WorkflowState } from '../../../../shared/agent/workflow'
import { useI18n } from '../../i18n'
import { OutputBlock, type DetailProps } from './ToolDetail'

const WorkflowCanvas = lazy(() => import('./WorkflowCanvas'))

export function WorkflowDetail({ input, output, isError, workflow }: DetailProps): ReactNode {
  const { t } = useI18n()
  const state: WorkflowState | null = workflow ?? output?.workflow ?? workflowFromInput(input)
  if (state === null) {
    return isError ? <OutputBlock output={output} isError maxLines={20} /> : (
      <p className="px-1 text-[12px] text-fg-faint">{t('workflow.unavailable')}</p>
    )
  }
  return (
    <Suspense fallback={<div className="h-[160px] w-full animate-pulse rounded-[10px] bg-tint motion-reduce:animate-none" />}>
      <WorkflowCanvas state={state} />
    </Suspense>
  )
}
