/**
 * 每秒走一格的「现在」,**只在 `active` 时走** —— 没东西在跑时面板开着也不该每秒重渲。
 *
 * ★ 子代理卡片和活动胶囊以前各写了一份(一个叫 `useNow`,一个是内联的 setInterval),
 *   两处的起点不同,同一个子代理的耗时在两处会差出一秒。计时口径只留这一份。
 */
import { useEffect, useState } from 'react'

export function useTicker(active: boolean): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (!active) return
    setNow(Date.now())
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [active])
  return now
}
