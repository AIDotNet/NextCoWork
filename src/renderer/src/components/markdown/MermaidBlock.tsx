import { useEffect, useId, useState, type ReactNode } from 'react'
import { useI18n } from '../../i18n'
import { useAppearance } from '../../theme/useAppearance'
import type { MarkdownCodeProps } from './MarkdownProvider'
import { CodeSource } from '../code/CodeSource'
import { canRenderMermaid } from './mermaid-policy'

// Mermaid has process-wide configuration. Serialize renders from different messages.
let renderQueue: Promise<unknown> = Promise.resolve()

/**
 * 从渲染好的 SVG 里量出它的固有尺寸,原样挂到 `<img>` 的 width/height 上。
 *
 * ★ 不挂的话这张图会**长两次**:`<img src=blob:>` 刚挂载时浏览器还不知道它多大,
 *   盒子只有 padding 那么高;等 blob 解码完才撑到真实高度。对话流那边每一次
 *   高度变化都要重新贴底一次,少一次跳就少一次出错的机会(见 Thread.tsx 的注释)。
 *   有了 width/height,元素一挂载就是最终高度,解码只是把像素填进去。
 */
function sizeOf(svg: string): { width: number; height: number } | null {
  const viewBox = /viewBox="([\d.\-\s]+)"/.exec(svg)?.[1]?.trim().split(/\s+/)
  if (viewBox?.length !== 4) return null
  const width = Number(viewBox[2])
  const height = Number(viewBox[3])
  return width > 0 && height > 0 ? { width, height } : null
}

export function MermaidBlock({ code, language }: MarkdownCodeProps): ReactNode {
  const { t } = useI18n()
  const appearance = useAppearance()
  const id = `diagram-${useId().replace(/[^a-zA-Z0-9-]/g, '')}`
  const [result, setResult] = useState<{ url?: string; failed?: boolean; size?: { width: number; height: number } } | null>(null)
  useEffect(() => {
    let active = true
    let objectUrl: string | undefined
    const render = async (): Promise<void> => {
      if (!active) return
      try {
        if (!canRenderMermaid(code)) throw new Error('Unsupported diagram content')
        const { default: mermaid } = await import('mermaid')
        if (!active) return
        const styles = getComputedStyle(document.documentElement)
        const color = (name: string): string => styles.getPropertyValue(`--color-${name}`).trim()
        mermaid.initialize({
          startOnLoad: false, securityLevel: 'strict', suppressErrorRendering: true,
          theme: 'base',
          themeVariables: {
            darkMode: appearance === 'dark', primaryColor: color('tint'), primaryTextColor: color('fg'),
            primaryBorderColor: color('accent-soft'), lineColor: color('fg-muted'),
            secondaryColor: color('surface'), tertiaryColor: color('surface-raised'),
            edgeLabelBackground: color('canvas'), clusterBkg: color('surface'), clusterBorder: color('border'),
          },
          fontFamily: 'sans-serif', htmlLabels: false, flowchart: { htmlLabels: false },
          maxTextSize: 50_000, maxEdges: 500,
          // Source directives must not override security or enable HTML labels.
          secure: ['secure', 'securityLevel', 'startOnLoad', 'maxTextSize', 'maxEdges', 'suppressErrorRendering', 'htmlLabels', 'flowchart', 'dompurifyConfig', 'themeCSS', 'themeVariables', 'fontFamily'],
        })
        const { svg } = await mermaid.render(id, code)
        if (!active) return
        // An image, not live SVG/HTML: no scripts, click handlers, or bound callbacks.
        objectUrl = URL.createObjectURL(new Blob([svg], { type: 'image/svg+xml' }))
        setResult({ url: objectUrl, size: sizeOf(svg) ?? undefined })
      } catch {
        if (active) setResult({ failed: true })
      }
    }
    renderQueue = renderQueue.then(render, render)
    return () => { active = false; if (objectUrl) URL.revokeObjectURL(objectUrl) }
  }, [code, appearance, id])

  /*
    ★ **只有「从没渲出来过」才显示占位条。** 重渲(换主题、源码变了)期间继续挂着
    上一张图 —— 退回占位条会让这块从 600px 塌成一行、渲完再撑回去,就是那一「闪」。
    而每一次塌陷/撑开都是一次高度突变,对话流那边就得跟着重新贴一次底。
    代价是重渲期间看到的是旧图,几百毫秒,比闪一下强。
  */
  if (result === null) {
    return <div className="markdown-render-status" role="status">{t('markdown.diagramLoading')}</div>
  }
  if (result.failed) return <>
    <div className="markdown-render-status" role="status">{t('markdown.diagramFailed')}</div>
    <CodeSource code={code} language={language} />
  </>
  return <div className="markdown-diagram scroll-thin"><img src={result.url} alt={t('markdown.diagramLabel')} width={result.size?.width} height={result.size?.height} /></div>
}
