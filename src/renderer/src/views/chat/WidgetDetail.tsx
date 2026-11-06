/**
 * `widget` 形态的详情渲染器 —— **生成期**那一段。
 *
 * 需求:模型是从头开始逐字写 `widget_code` 的,而这一段可能持续十几秒。
 * 这十几秒里用户该看到那张图**正在长出来**,而不是一个转圈的工具卡片。
 * 所以这个渲染器的输入是"半截入参"(`parts.tsx` 用 `parsePartialJson` 投影
 * 出来的部分对象),输出是一个已经开始渲染的 `WidgetFrame`。
 *
 * ## 它和卡片那条路的分工
 *
 * | 阶段 | 谁渲染 | 代码从哪来 | `final` |
 * |---|---|---|---|
 * | 参数还在流 / 工具刚开跑 | 这里 | 半截入参的 `widget_code` | false |
 * | 工具跑完 | `CardRenderer` 的 widget 分支 | `output.card.code`(完整) | true |
 * | 工具失败 | 这里 | —— 只显示失败原因 | —— |
 *
 * 两条路的汇合点是同一个 `WidgetFrame`,所以"边写边渲染"与"重开对话看成品"
 * 是同一段渲染代码。**分界由 `card` 在不在决定**(`ToolDetail` 里那句
 * `if (card !== undefined)`),这里不需要自己判断进度。
 *
 * ★ `final` 恒为 false:脚本只在卡片那条路上执行。在这里执行的话,
 * 半截代码里的 `getElementById('chart')` 拿到 null,而失败的表现是
 * "图没画出来、也不报错";而且卡片紧接着还会再执行一次,同一段脚本跑两遍。
 *
 * ★ 半截入参里 **`title` 可能还没到**(模型的字段顺序不保证),而 iframe 的
 * `title` 属性不能为空 —— 空的无障碍名字会被读屏软件念成路径。所以退化到
 * 一个 i18n 文案。
 */
import type { ReactNode } from 'react'
import { pick } from '../../../../shared/domain/tool-presenter'
import { useI18n } from '../../i18n'
import { OutputBlock, type DetailProps } from './ToolDetail'
import { WidgetFrame } from './WidgetFrame'
import { inferWidgetFormat } from '../../../../shared/domain/widget'

/**
 * 半截入参里的字符串数组。
 *
 * 与 `shared/domain/tool-presenter.ts` 的 `pickArray` 同类但**更窄**:
 * 这里只收"已经是字符串的项" —— 流式中途数组里可能挂着一个还没写完的对象,
 * 把它塞进 `loadingMessages` 会在 iframe 里显示成 `[object Object]`。
 */
function strings(input: unknown, key: string): string[] {
  if (typeof input !== 'object' || input === null) return []
  const value = (input as Record<string, unknown>)[key]
  if (!Array.isArray(value)) return []
  return value.filter((item): item is string => typeof item === 'string' && item.trim() !== '')
}

export function WidgetDetail({ input, output, isError }: DetailProps): ReactNode {
  const { t } = useI18n()
  const title = pick(input, 'title')

  /*
    失败时**不渲染 widget**。失败的两条路是"参数不合法"和"widget_code 超长",
    两种情况下入参里的 `widget_code` 要么是残缺的、要么是那份被拒绝的巨型代码
    —— 把它画出来会给用户一个"看起来成功了"的假象,而真正需要他看见的是
    那句失败原因。
  */
  if (isError) return <OutputBlock output={output} isError maxLines={20} />

  const code = pick(input, 'widget_code')
  const loading = strings(input, 'loading_messages')
  // `visualize_edit_widget` 生成期没有代码也没有提示 —— 不画一块空白,等成品卡片
  if (code === '' && loading.length === 0) return null
  const declared = pick(input, 'format')
  // tsx 生成期只显示加载提示(半截 TSX 不可渲染),html/svg 照旧边写边画
  const format = declared === 'tsx' || declared === 'html' || declared === 'svg' ? declared : inferWidgetFormat(code)
  return (
    <WidgetFrame
      code={code}
      final={false}
      format={format}
      title={title === '' ? t('widget.untitled') : title}
      loadingMessages={loading}
    />
  )
}
