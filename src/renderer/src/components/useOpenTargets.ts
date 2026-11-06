/**
 * 「这台机器上能用什么打开文件」的渲染层读口 —— 带会话级单飞缓存。
 *
 * 需求:「打开方式」下拉(`OpenWithMenu`)、文件树右键菜单(`views/files/FileRowMenu`)、
 * 设置页的「默认打开方式」三处要的是**同一份**探测结果。缓存原先是
 * `OpenWithMenu.tsx` 的模块级变量;第二、三个读者出现后挪到这里,
 * 否则每个读者各自一份缓存,同一次扫描会被跑好几遍。
 *
 * ★ 下面两条 ★ 注释是随缓存一起从 `OpenWithMenu.tsx` 搬过来的,理由没变。
 */
import { useEffect, useState } from 'react'
import type { OpenTarget } from '../../../shared/domain/open-target'
import { listOpenTargets } from '../services/open-with'

/*
  ★ 十几颗按钮挂载时同时去问主进程,等于同一次扫描跑十几遍(macOS 上那是一串
    目录列举)。所以模块级缓存一份 promise,**失败不缓存** —— 失败被记住的话,
    用户装完 IDE 要重启应用才看得见。

  ★ 缓存**不随菜单开关失效**:装了新 IDE 的用户重启应用后自然会重新扫。
    做「每次打开都重扫」的代价是每一次点开菜单都要等一轮目录列举。
*/
/**
 * 探测结果的单飞缓存。★ 存的是 promise 本身,不是结果 —— 十几颗按钮在同一帧里
 * 挂载时,它们拿到的必须是**同一个** promise。
 */
let targets: Promise<OpenTarget[]> | null = null

export function loadOpenTargets(): Promise<OpenTarget[]> {
  targets ??= listOpenTargets().catch((error: unknown) => {
    targets = null
    throw error
  })
  return targets
}

/**
 * 挂载即读。`null` = 还没回来;探测失败按空表处理(菜单据此只画不依赖探测的那几行)。
 *
 * 读者都是「面板打开时才挂载」的条目,所以「挂载即探测」正好是「打开菜单才探测」。
 */
export function useOpenTargets(): OpenTarget[] | null {
  const [found, setFound] = useState<OpenTarget[] | null>(null)
  useEffect(() => {
    let alive = true
    void loadOpenTargets()
      .then((list) => { if (alive) setFound(list) })
      .catch(() => { if (alive) setFound([]) })
    return () => { alive = false }
  }, [])
  return found
}
