import { FitAddon } from '@xterm/addon-fit'
import { WebLinksAddon } from '@xterm/addon-web-links'
import { WebglAddon } from '@xterm/addon-webgl'
import { Terminal } from '@xterm/xterm'
import '@xterm/xterm/css/xterm.css'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Play, ShieldCheck, X } from 'lucide-react'
import type { Workspace } from '../../../../shared/domain/workspace'
import type { TerminalIntent } from '../../../../shared/domain/terminal'
import {
  createTerminal,
  prepareTerminal,
  approveTerminal,
  getTerminalBuffer,
  onTerminalData,
  onTerminalExit,
  resizeTerminal,
  writeTerminal
} from '../../services/terminal'
import type { InnerTab } from '../../../../shared/domain/tab'
import { Button } from '../../components/arc/button/button'
import { useI18n } from '../../i18n'
import { connectionErrorKey } from '../../services/connections'
import { openExternal } from '../../services/app'

// 苹果风格:配色取自 macOS「终端」的 Basic 描述文件 + 系统色(systemRed / systemGreen …)
const LIGHT_THEME = {
  background: '#ffffff',
  foreground: '#1d1d1f',
  cursor: '#0071e3',
  cursorAccent: '#ffffff',
  selectionBackground: '#b3d7ff',
  black: '#1d1d1f',
  red: '#c41a16',
  green: '#007400',
  yellow: '#826b28',
  blue: '#0b4f79',
  magenta: '#9c2191',
  cyan: '#3e8087',
  white: '#e5e5ea',
  brightBlack: '#6e6e73',
  brightRed: '#ff3b30',
  brightGreen: '#28a745',
  brightYellow: '#b58900',
  brightBlue: '#0071e3',
  brightMagenta: '#af52de',
  brightCyan: '#32ade6',
  brightWhite: '#ffffff'
}

const DARK_THEME = {
  background: '#1c1c1e',
  foreground: '#f5f5f7',
  cursor: '#0a84ff',
  cursorAccent: '#1c1c1e',
  selectionBackground: '#3f638b',
  black: '#1c1c1e',
  red: '#ff453a',
  green: '#30d158',
  yellow: '#ffd60a',
  blue: '#0a84ff',
  magenta: '#bf5af2',
  cyan: '#64d2ff',
  white: '#d1d1d6',
  brightBlack: '#8e8e93',
  brightRed: '#ff6961',
  brightGreen: '#4ce07a',
  brightYellow: '#ffe066',
  brightBlue: '#409cff',
  brightMagenta: '#da8fff',
  brightCyan: '#8ae0ff',
  brightWhite: '#ffffff'
}

function isDarkTheme(): boolean {
  return document.documentElement.dataset.theme === 'dark'
}

export function TerminalView({ tab, workspace }: { tab: Extract<InnerTab, { kind: 'terminal' }>; workspace: Workspace }): ReactNode {
  const { t } = useI18n()
  const hostRef = useRef<HTMLDivElement | null>(null)
  const xtermRef = useRef<Terminal | null>(null)
  const fitRef = useRef<FitAddon | null>(null)
  const [status, setStatus] = useState<'starting' | 'connected' | 'exited' | 'error' | 'awaiting-approval' | 'declined'>('starting')
  const [intent, setIntent] = useState<TerminalIntent | null>(null)
  const startRef = useRef<((approval?: string) => Promise<void>) | null>(null)
  const pendingIntent = useRef<TerminalIntent | null>(null)

  useEffect(() => {
    const host = hostRef.current
    if (!host) return
    let cancelled = false
    let connected = false
    const terminal = new Terminal({
      allowTransparency: true,
      convertEol: true,
      cursorBlink: true,
      cursorStyle: 'bar',
      fontFamily: "'SF Mono', 'JetBrains Mono', Menlo, Consolas, monospace",
      fontSize: 13,
      lineHeight: 1.28,
      scrollback: 5000,
      theme: isDarkTheme() ? DARK_THEME : LIGHT_THEME
    })
    const fit = new FitAddon()
    terminal.loadAddon(fit)
    /*
      需求(#178):`npm run dev` 打出的本地预览地址要能 ⌘+点击(非 mac 为 Ctrl+点击)
      跳系统浏览器,与 macOS「终端」/ VS Code 一致。必须带修饰键:裸点击会和
      选中文本、全屏 TUI 的鼠标事件抢。
    */
    const isMac = navigator.platform.toLowerCase().includes('mac')
    terminal.loadAddon(new WebLinksAddon((event, uri) => {
      if (!(isMac ? event.metaKey : event.ctrlKey)) return
      event.preventDefault()
      void openExternal(uri).catch(() => {})
    }))
    terminal.open(host)
    /*
      GPU 渲染。需求:xterm 6 的默认 DOM 渲染器逐字符改 DOM,而 codex / claude
      这类全屏 TUI 每帧清屏重画 —— 在 DOM 渲染器上表现为整屏高频闪烁。
      WebGL 渲染器把重绘压到一次 canvas 绘制,这是消除 TUI 闪烁的正解。

      ★ 必须兜底:webgl 上下文创建失败(无 GPU / 远程桌面 / 上下文数耗尽)会抛,
        GPU 重置时走 onContextLoss —— 两条路都退回 DOM 渲染器:慢,但能跑。
        不能让渲染器选择权变成「终端打不开」。
    */
    try {
      const webgl = new WebglAddon()
      webgl.onContextLoss(() => { webgl.dispose() })
      terminal.loadAddon(webgl)
    } catch {
      // 回落 DOM 渲染器。
    }
    xtermRef.current = terminal
    fitRef.current = fit

    const fitAndResize = (): void => {
      if (cancelled) return
      try {
        fit.fit()
        if (connected && terminal.cols > 0 && terminal.rows > 0) resizeTerminal(tab.ref.terminalId, terminal.cols, terminal.rows)
      } catch {
        // xterm can be measured while its panel is animating from 0px; the next observer tick retries.
      }
    }
    const observer = new ResizeObserver(fitAndResize)
    observer.observe(host)
    requestAnimationFrame(fitAndResize)

    let hydrated = false
    let lastSeq = 0
    const queued: Array<{ seq: number; chunk: string }> = []
    const offData = onTerminalData(({ id, seq, chunk }) => {
      if (id !== tab.ref.terminalId || cancelled || seq <= lastSeq) return
      if (!hydrated) {
        queued.push({ seq, chunk })
        return
      }
      lastSeq = seq
      terminal.write(chunk)
    })
    const offExit = onTerminalExit(({ id, code }) => {
      if (id !== tab.ref.terminalId || cancelled) return
      connected = false
      setStatus('exited')
      terminal.write(`\r\n\x1b[90m[${t('ssh.terminal.exited', { code })}]\x1b[0m\r\n`)
    })
    const dataDisposable = terminal.onData((data) => { if (connected) writeTerminal(tab.ref.terminalId, data) })

    const hydrateBuffer = async (): Promise<void> => {
      if (hydrated) return
      const buffer = await getTerminalBuffer(tab.ref.terminalId).catch(() => null)
      if (cancelled) return
      if (buffer && buffer.data.length > 0) terminal.write(buffer.data)
      lastSeq = buffer?.seq ?? 0
      hydrated = true
      for (const item of queued.sort((left, right) => left.seq - right.seq)) {
        if (item.seq <= lastSeq) continue
        lastSeq = item.seq
        terminal.write(item.chunk)
      }
      queued.length = 0
    }
    const start = async (approval?: string): Promise<void> => {
      if (cancelled) return
      setStatus('starting')
      try {
        const request = {
          workspaceId: workspace.id,
          id: tab.ref.terminalId,
          cwd: approval ? pendingIntent.current?.cwd ?? workspace.rootPath : workspace.rootPath,
          cols: terminal.cols || 100,
          rows: terminal.rows || 28
        }
        const prepared = approval ? { kind: 'ready' as const, terminal: await createTerminal({ ...request, approval }) } : await prepareTerminal(request)
        if (cancelled) {
          if (prepared.kind === 'approval') void approveTerminal(prepared.intent.id, false).catch(() => {})
          return
        }
        await hydrateBuffer()
        if (cancelled) return
        if (prepared.kind === 'approval') {
          pendingIntent.current = prepared.intent
          setIntent(prepared.intent)
          setStatus('awaiting-approval')
          return
        }
        pendingIntent.current = null
        setIntent(null)
        const info = prepared.terminal
        connected = info.alive
        setStatus(info.alive ? 'connected' : 'exited')
        fitAndResize()
      } catch (error) {
        if (cancelled) return
        connected = false
        await hydrateBuffer()
        setStatus('error')
        terminal.write(`\x1b[31m${t('ssh.terminal.failed', { error: t(connectionErrorKey(error)) })}\x1b[0m\r\n`)
      }
    }
    startRef.current = start
    void start()

    return () => {
      cancelled = true
      startRef.current = null
      if (pendingIntent.current) void approveTerminal(pendingIntent.current.id, false).catch(() => {})
      pendingIntent.current = null
      observer.disconnect()
      offData()
      offExit()
      dataDisposable.dispose()
      terminal.dispose()
      xtermRef.current = null
      fitRef.current = null
    }
  }, [tab.ref.terminalId, workspace.id, workspace.rootPath, t])

  const approve = async (): Promise<void> => {
    if (!intent || status !== 'awaiting-approval') return
    setStatus('starting')
    try {
      const grant = await approveTerminal(intent.id, true)
      if (grant) await startRef.current?.(grant)
    } catch (error) {
      setStatus('error')
      xtermRef.current?.write(`\r\n${t(connectionErrorKey(error))}\r\n`)
    }
  }
  const decline = (): void => {
    if (intent) void approveTerminal(intent.id, false).catch(() => {})
    pendingIntent.current = null
    setIntent(null)
    setStatus('declined')
  }

  /*
    ★ 布局上的两条约束,缺一条都会让终端尺寸进入自激循环:
    1. 根节点必须 `min-w-0`。xterm 把 `.xterm-screen` 写成 `cols × 字宽` 的定宽像素,
       flex 子项默认 `min-width: auto` 会被它撑开 → ResizeObserver → fit 多算一列 →
       再撑开……每帧 +1 列、永不停止;外层 `overflow-hidden` 把多出来的部分裁掉,
       所以肉眼只看到**跑 claude / codex 时整屏持续闪烁**(每次 resize 都是一次
       SIGWINCH,全屏 TUI 会清屏重画;普通 shell 几乎不重画,所以看不出来)。
    2. 内边距不能加在 `hostRef` 上。FitAddon 量的是 xterm 父元素的 computed
       width/height,border-box 下包含 padding,只扣 `.xterm` 自身的 padding ——
       padding 放在父元素上会让它多算 ~3 列 / 1 行,右侧被裁,也正是第 1 条循环的推力。
       所以 padding 落在外面这层 wrapper 上,`hostRef` 保持零 padding。
  */
  return (
    <div className="terminal-surface flex min-h-0 min-w-0 flex-1 flex-col" data-terminal-status={status}>
      {status === 'awaiting-approval' && intent && (
        <div className="shrink-0 border-b border-border px-4 py-3 text-[12px]">
          <div className="mb-2 flex items-center gap-2 text-fg"><ShieldCheck size={15} />{t('ssh.terminal.title')}</div>
          <dl className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1 text-fg-muted">
            <dt>{t('ssh.terminal.connection')}</dt><dd className="break-all">{intent.connection}</dd>
            <dt>{t('ssh.terminal.cwd')}</dt><dd className="break-all font-mono">{intent.cwd}</dd>
            <dt>{t('ssh.terminal.shell')}</dt><dd className="break-all font-mono">{intent.shell}</dd>
          </dl>
          <div className="mt-3 flex flex-wrap gap-2">
            <Button type="button" size="sm" variant="primary" onClick={() => { void approve() }}><ShieldCheck size={13} />{t('ssh.terminal.allow')}</Button>
            <Button type="button" size="sm" variant="secondary" onClick={decline}><X size={13} />{t('common.cancel')}</Button>
          </div>
        </div>
      )}
      {(status === 'error' || status === 'exited' || status === 'declined') && (
        <div className="shrink-0 border-b border-border px-3 py-2">
          <Button type="button" size="sm" variant="secondary" onClick={() => { void startRef.current?.() }}><Play size={13} />{t(status === 'declined' ? 'ssh.terminal.request' : 'ssh.terminal.restart')}</Button>
        </div>
      )}
      <div className="flex min-h-0 min-w-0 flex-1 flex-col px-3 py-2">
        <div ref={hostRef} className="terminal-host min-h-0 min-w-0 flex-1" />
      </div>
    </div>
  )
}
