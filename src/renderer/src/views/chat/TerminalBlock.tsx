/**
 * 展开一条命令之后的那张**终端卡**。
 *
 * ★★ 需求:点开 `Bash` 行看到的要是终端该有的样子,而不是一段被折行的文本。
 * 具体是五件事,少一件就又变回「一段文字」:
 *
 *   1. **有终端的形状**:一条标题栏(提示符图标 + 「终端」+ 状态)压着一块
 *      **深色的正文区**。这一条是这次补的 —— 之前正文直接坐在卡片底色上,
 *      于是那张卡和代码卡、命中清单长得一模一样,唯独不像终端。
 *   2. **输出不折行**,横向滚动。命令行工具的输出大量是按列对齐的
 *      (`ls -l`、`git status -sb`、PowerShell 的 Format-Table),折行会把列打散,
 *      而那张表的全部信息就在列对齐上。
 *   3. **命令折行**。它是一条逻辑上的长句,不是表格;把一条三百字符的 ssh 命令
 *      藏进横向滚动里,等于让用户拖着看完它。
 *   4. **两条流分色**。stderr 在真终端里也是红的;混成一种颜色时,
 *      「命令成功但打了警告」和「命令失败」长得一模一样。
 *   5. **信封标签不出现**(`<stdout>`)。它是写给模型的,见 `terminal-output.ts`。
 *
 * ★ 正文底色用 `canvas`,标题栏用 `surface-raised`:这两个值正是
 * `views/terminal/TerminalView.tsx` 里那套 xterm 主题的 background(深 #1e2020 /
 * 浅 #faf9f5)和它周围的面板色 —— 内联终端和真终端因此是同一个底,换主题一起走。
 * 在转录里它不会"看不见",因为外面那张卡有描边(`detail-card.ts`)。
 *
 * ★ 不用 xterm:xterm 是给**交互式 PTY** 用的,一个实例要建 canvas/WebGL 渲染器和
 * 自己的缓冲区。一轮回复里可能有几十条 Bash 调用,全挂上会在滚动转录时明显卡顿 ——
 * 而这里要的只是「一段已经结束、不可交互的输出」。
 */
import { Check, Copy, SquareTerminal } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import { useI18n } from '../../i18n'
import { cn } from '../../lib/cn'
import type { ToolOutput } from '../../../../shared/agent/message'
import { copyText } from '../../services/app'
import { DETAIL_CARD_CLASS } from './detail-card'
import { parseTerminalOutput } from './terminal-output'

/** 超过这个行数就截断:一条 `npm ci` 能吐几千行,全画出来会把转录撑垮 */
const MAX_LINES = 200

function clip(text: string, max: number): { text: string; omitted: number } {
  const lines = text.split('\n')
  if (lines.length <= max) return { text, omitted: 0 }
  return { text: lines.slice(0, max).join('\n'), omitted: lines.length - max }
}

export function TerminalBlock({
  command,
  output,
  isError = false
}: {
  /** 命令原文。`BashOutput` 那种「回读后台输出」没有命令,留空即可 */
  command?: string
  output: ToolOutput | undefined
  isError?: boolean
}): ReactNode {
  const { t } = useI18n()
  const [copied, setCopied] = useState(false)

  useEffect(() => {
    if (!copied) return
    const timer = setTimeout(() => setCopied(false), 2000)
    return () => clearTimeout(timer)
  }, [copied])

  const parsed = output === undefined ? undefined : parseTerminalOutput(output.content)
  const hasCommand = command !== undefined && command !== ''
  if (!hasCommand && parsed === undefined) return null

  /*
    复制给的是**命令 + 输出**,不是只有命令:用户复制一段终端内容,
    十次里有九次是要把它贴给别人看「我跑了什么、它回了什么」。
  */
  const copyPayload = [
    hasCommand ? `$ ${command}` : '',
    parsed?.notice ?? '',
    ...(parsed?.sections ?? []).map((s) => s.text)
  ].filter((part) => part !== '').join('\n')

  return (
    <div data-testid="terminal-block" className={DETAIL_CARD_CLASS}>
      {/* 标题栏:说清这块是终端,并给失败一个不用读输出就能看见的落点 */}
      <div className="flex items-center gap-2 border-b border-hairline px-2.5 py-1 text-[12px] text-fg-faint">
        <SquareTerminal size={13} aria-hidden className="shrink-0 text-accent-soft" />
        <span className="min-w-0 flex-1 truncate">{t('chat.tool.title.bash')}</span>
        {isError && (
          <span data-testid="terminal-failed" className="shrink-0 text-danger">
            {t('chat.tool.failedStatus')}
          </span>
        )}
        <button
          type="button"
          data-testid="terminal-copy"
          title={t('markdown.copy')}
          aria-label={t('markdown.copy')}
          onClick={() => { void copyText(copyPayload).then(() => setCopied(true)).catch(() => {}) }}
          className="inline-flex h-5 w-5 shrink-0 cursor-pointer items-center justify-center rounded-[5px] text-fg-faint transition-colors hover:text-fg motion-reduce:transition-none"
        >
          {copied ? <Check size={12} /> : <Copy size={12} />}
        </button>
      </div>

      <div className="scroll-thin max-h-[min(50vh,420px)] overflow-auto bg-canvas px-3 py-2 font-mono text-[12.5px] leading-[1.6]">
        {hasCommand && (
          // 命令折行:见文件头第 3 条。`break-all` 是给没有空格的超长路径/URL 留的后路
          <div className="selectable whitespace-pre-wrap break-all text-fg">
            <span aria-hidden className="select-none text-accent-soft">$ </span>
            {command}
          </div>
        )}
        {parsed !== undefined && (
          <div className="min-w-max">
            {/* 提示行(退出码、超时)不是命令吐的,所以和输出分开画 */}
            {parsed.notice !== '' && (
              <div className={cn('selectable whitespace-pre-wrap', isError ? 'text-danger' : 'text-fg-faint')}>
                {parsed.notice}
              </div>
            )}
            {parsed.sections.map((section, index) => {
              const { text, omitted } = clip(section.text, MAX_LINES)
              return (
                <div
                  key={`${section.stream}:${index}`}
                  data-stream={section.stream}
                  className={cn(
                    // ★ 这里是 `whitespace-pre`(不折行)—— 文件头第 2 条
                    'selectable whitespace-pre',
                    section.stream === 'stderr' ? 'text-danger/90' : 'text-fg-muted'
                  )}
                >
                  {text}
                  {omitted > 0 && (
                    <span className="block whitespace-pre-wrap text-fg-faint">
                      {t('chat.tool.linesOmitted', { count: omitted }).trim()}
                    </span>
                  )}
                </div>
              )
            })}
            {output?.truncated === true && (
              <div className="whitespace-pre-wrap text-fg-faint">
                {t('chat.tool.truncated', { bytes: output.originalBytes ?? '?' })}
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  )
}
