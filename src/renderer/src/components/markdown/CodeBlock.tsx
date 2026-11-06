/**
 * markdown 围栏 → 共享代码卡的接线。
 *
 * 需求:围栏比别处的代码块多两件只有 markdown 才有的事 —— 「预览 / 源码」切换
 * (mermaid 图、宿主注入的 `codeRenderers`)和流式期间强制显示源码。卡本身
 * (工具栏、折行开关、复制、高亮正文)是 `components/code/CodeBlock` 那一张,
 * 全仓库只有那一份;这里只决定「正文是源码还是预览」,并把切换按钮塞进它的
 * `actions` 插槽。
 */
import { memo, useState, type ReactNode } from 'react'
import { CodeBlock as CodeCard } from '../code'
import { useI18n } from '../../i18n'
import { useMarkdownEnvironment, type MarkdownCodeProps } from './MarkdownProvider'
import { MermaidBlock } from './MermaidBlock'

export const CodeBlock = memo(function CodeBlock(props: MarkdownCodeProps): ReactNode {
  const { code, language, streaming } = props
  const { t } = useI18n()
  const { codeRenderers } = useMarkdownEnvironment()
  const [source, setSource] = useState(false)
  const Renderer = codeRenderers?.[language] ?? (language === 'mermaid' ? MermaidBlock : undefined)
  const showSource = !Renderer || source || streaming

  return (
    <CodeCard
      variant="fence"
      code={code}
      language={language}
      streaming={streaming}
      actions={Renderer && <div className="code-card-views">
        <button type="button" aria-pressed={!showSource} disabled={streaming} onClick={() => setSource(false)}>{t(language === 'mermaid' ? 'markdown.diagram' : 'markdown.preview')}</button>
        <button type="button" aria-pressed={showSource} onClick={() => setSource(true)}>{t('markdown.source')}</button>
      </div>}
      status={Renderer && streaming
        ? <div className="markdown-render-status" role="status">{t('markdown.diagramStreaming')}</div>
        : undefined}
    >
      {/* 不给 children = 用卡自己的源码正文(连带那个折行开关);给了就是预览 */}
      {showSource || Renderer === undefined ? undefined : <Renderer {...props} />}
    </CodeCard>
  )
})
