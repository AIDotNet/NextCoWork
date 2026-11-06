/**
 * 插件页探针 —— 验「安装进度」与「更新入口」两件事的**真实链路**。
 *
 * CDP 驱动那套整个抄自 `extensions-probe.mjs`(连 `--user-data-dir` 一个开关
 * 同时管着单实例锁和数据根这件事也一样,见那边的文件头)。这里只换了断言。
 *
 * ## 不往生产代码里塞调试出口
 *
 * 更省事的写法是给 window 挂一个 store 引用,然后直接灌假状态。这里不那么做:
 * 那条出口会一直留在产品里,而它验的是「我灌进去的东西显示对不对」——
 * 主进程有没有真的把事件发出来、preload 有没有把它送过来,一概验不到。
 * 所以这里走真实路径:**装一个真的插件**,监听真实的 `plugins:installProgress`。
 *
 * ## 本地包只有 installing 一个阶段,这是对的
 *
 * 本地包没有下载。要看百分比得连市场,而那需要登录 —— 不该是一个本地探针
 * 的前提。下载那一段的节流与百分比由 `readDownload` 的形状保证,真实验证
 * 留给「限速到 1Mbps 装一次 Excalidraw」那一步。
 *
 * 跑法:`npm run build && node scripts/plugin-progress-probe.mjs`
 */
import { spawn } from 'node:child_process'
import { cp, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { createServer } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { setTimeout as sleep } from 'node:timers/promises'
import electron from 'electron'

const OUT = '/tmp/nextcowork-plugin-progress'
const VIEWPORT = { width: 1264, height: 900 }

function freePort() {
  return new Promise((resolve, reject) => {
    const server = createServer()
    server.on('error', reject)
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address()
      server.close(() => resolve(port))
    })
  })
}

class Cdp {
  #ws
  #id = 0
  #waiting = new Map()

  static async attach(wsUrl) {
    const c = new Cdp()
    c.#ws = new WebSocket(wsUrl)
    c.#ws.addEventListener('message', (ev) => {
      const msg = JSON.parse(ev.data)
      const w = c.#waiting.get(msg.id)
      if (w === undefined) return
      c.#waiting.delete(msg.id)
      if (msg.error) w.reject(new Error(JSON.stringify(msg.error)))
      else w.resolve(msg.result)
    })
    await new Promise((res, rej) => {
      c.#ws.addEventListener('open', res, { once: true })
      c.#ws.addEventListener('error', () => rej(new Error('CDP 连不上')), { once: true })
    })
    return c
  }

  send(method, params = {}) {
    const id = ++this.#id
    this.#ws.send(JSON.stringify({ id, method, params }))
    return new Promise((resolve, reject) => this.#waiting.set(id, { resolve, reject }))
  }

  async eval(expression) {
    const r = await this.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true })
    if (r.exceptionDetails) throw new Error(`页面里抛了:${r.exceptionDetails.exception?.description ?? '?'}`)
    return r.result.value
  }

  async shoot(name) {
    const { data } = await this.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true })
    const path = `${OUT}/${name}.png`
    await writeFile(path, Buffer.from(data, 'base64'))
    console.log(`📸 ${path}`)
    return path
  }
}

async function until(label, fn, timeoutMs = 60_000) {
  const deadline = Date.now() + timeoutMs
  for (;;) {
    const v = await fn()
    if (v !== null && v !== undefined && v !== false) return v
    if (Date.now() > deadline) throw new Error(`超时等待「${label}」`)
    await sleep(200)
  }
}

const clickButton = (text) => `(() => {
  const b = [...document.querySelectorAll('button')].find((x) => x.textContent?.trim() === ${JSON.stringify(text)})
  if (!b) throw new Error('找不到按钮:' + ${JSON.stringify(text)})
  b.click()
  return true
})()`

/**
 * 把 examples 里那个插件复制一份,版本改低。
 *
 * ★ 不动仓库里那一份 —— 探针改了源码树的话,跑完不管成败都留下脏文件,
 * 而下一次 `git status` 看到它的人不知道它是谁改的。
 * ★ 排除 node_modules 与 .zip:装的时候整个目录都要复制进插件根,
 * 带上它们就是几百 MB 的无谓拷贝。
 */
async function stageOldVersion(projectRoot, into) {
  const src = join(projectRoot, 'examples', 'acme.excalidraw')
  await cp(src, into, {
    recursive: true,
    filter: (path) => !path.includes('node_modules') && !path.endsWith('.zip')
  })
  const manifestPath = join(into, 'package.json')
  const manifest = JSON.parse(await readFile(manifestPath, 'utf8'))
  const realVersion = manifest.version
  manifest.version = '0.1.0'
  await writeFile(manifestPath, JSON.stringify(manifest, null, 2))
  return realVersion
}

let child = null
let failed = false
let testProject = null
const check = (label, ok, detail = '') => {
  console.log(`${ok ? '✅' : '❌'} ${label}${detail ? ` — ${detail}` : ''}`)
  if (!ok) failed = true
}

try {
  await mkdir(OUT, { recursive: true })
  const env = { ...process.env }
  delete env.ELECTRON_RUN_AS_NODE
  delete env.ELECTRON_NO_ATTACH_CONSOLE
  const projectRoot = process.cwd()
  testProject = await mkdtemp(join(tmpdir(), 'nextcowork-plugin-'))
  const oldPackage = join(testProject, 'pkg-old')
  const realVersion = await stageOldVersion(projectRoot, oldPackage)
  console.log(`· 拿 examples 那个插件做样本,版本 ${realVersion} → 0.1.0`)

  const port = await freePort()
  child = spawn(electron, [projectRoot, `--remote-debugging-port=${port}`, `--user-data-dir=${join(testProject, '.ud')}`], {
    cwd: testProject,
    env,
    stdio: ['ignore', 'pipe', 'pipe'],
    detached: true
  })
  child.stderr.on('data', (b) => {
    const s = String(b)
    if (/error|Error/.test(s)) process.stderr.write(`[main] ${s}`)
  })

  const target = await until('渲染进程 CDP target', async () => {
    try {
      const list = await (await fetch(`http://127.0.0.1:${port}/json`)).json()
      return list.find((t) => t.type === 'page' && t.webSocketDebuggerUrl) ?? null
    } catch {
      return null
    }
  })

  const cdp = await Cdp.attach(target.webSocketDebuggerUrl)
  await cdp.send('Runtime.enable')
  await cdp.send('Page.enable')
  await cdp.send('Emulation.setDeviceMetricsOverride', { ...VIEWPORT, deviceScaleFactor: 1, mobile: false })
  await sleep(500)

  const needsAuth = await cdp.eval(
    `[...document.querySelectorAll('button')].some((b) => b.textContent?.includes('免登录使用'))`
  )
  if (needsAuth) {
    await cdp.eval(clickButton('免登录使用'))
    await sleep(1500)
  }

  await until('侧边栏出现', () =>
    cdp.eval(`[...document.querySelectorAll('button')].some((b) => b.textContent?.trim() === '扩展')`)
  )
  await cdp.eval(clickButton('扩展'))
  await sleep(700)
  await cdp.eval(`(() => {
    const r = [...document.querySelectorAll('button[aria-pressed]')].find((x) => x.textContent?.trim() === '插件')
    if (!r) throw new Error('找不到「插件」Tab')
    r.click(); return true
  })()`)
  await sleep(800)
  check('插件页打开没崩', await cdp.eval(`document.body.innerText.length > 0`))
  const buttons = await cdp.eval(
    `[...document.querySelectorAll('button')].map((b) => b.textContent?.trim()).filter(Boolean)`
  )
  check('有「检查更新」按钮', buttons.includes('检查更新'), `按钮:${JSON.stringify(buttons.slice(0, 8))}`)
  await cdp.shoot('01-empty')

  // ── 真实安装:挂上监听,再走真正的 IPC ──
  await cdp.eval(`(() => {
    window.__probe = { progress: [] }
    window.nextcowork.on('plugins:installProgress', (e) => { window.__probe.progress.push(e) })
    return true
  })()`)

  const installResult = await cdp.eval(`(async () => {
    const r = await window.nextcowork.invoke('plugins:installPackage', { path: ${JSON.stringify(oldPackage)} })
    return r.ok ? 'ok' : JSON.stringify(r.error)
  })()`)
  check('本地包装得上', installResult === 'ok', installResult)
  await sleep(1200)

  const phases = await cdp.eval(`window.__probe.progress.map((e) => e.phase)`)
  const keys = await cdp.eval(`[...new Set(window.__probe.progress.map((e) => e.key))]`)
  check('真的收到了进度事件', phases.length > 0, `阶段:${JSON.stringify(phases)}`)
  check('走过 installing → done', phases.includes('installing') && phases.includes('done'), JSON.stringify(phases))
  check('key 带上了路径不是固定值', keys.every((k) => k.startsWith('local:/')), JSON.stringify(keys))

  const installedVersion = await cdp.eval(`(() => {
    const t = document.body.innerText
    return t.includes('0.1.0') ? '0.1.0' : t.slice(0, 120)
  })()`)
  check('装进去的是 0.1.0', installedVersion === '0.1.0', installedVersion)
  await cdp.shoot('02-installed-old')

  // ── 把它打开,再覆盖装一次 → 开关必须还是开的(Part 0 那个 bug) ──
  const pluginId = await cdp.eval(`(async () => {
    const r = await window.nextcowork.invoke('plugins:list', undefined)
    return r.ok ? (r.data.plugins[0]?.id ?? null) : null
  })()`)
  if (pluginId !== null) {
    const enabledBefore = await cdp.eval(`(async () => {
      await window.nextcowork.invoke('plugins:grantPermissions', { pluginId: ${JSON.stringify(pluginId)}, permissions: ['workspace.read', 'workspace.write'] })
      const r = await window.nextcowork.invoke('plugins:setEnabled', { pluginId: ${JSON.stringify(pluginId)}, enabled: true })
      return r.ok ? r.data.plugins[0]?.enabled : 'failed'
    })()`)
    check('先把插件打开', enabledBefore === true, String(enabledBefore))

    const enabledAfter = await cdp.eval(`(async () => {
      const r = await window.nextcowork.invoke('plugins:installPackage', { path: ${JSON.stringify(oldPackage)} })
      return r.ok ? r.data.plugins[0]?.enabled : 'failed:' + JSON.stringify(r.error)
    })()`)
    check('★ 覆盖安装之后插件还是开着的', enabledAfter === true, String(enabledAfter))
    await sleep(600)
    await cdp.shoot('03-after-reinstall')
  }

  // ── 更新检测:要市场连得上,连不上就跳过(这不是回归) ──
  const updates = await cdp.eval(`(async () => {
    const r = await window.nextcowork.invoke('plugins:checkUpdates', { force: true })
    return r.ok ? r.data : 'failed:' + JSON.stringify(r.error)
  })()`)
  if (typeof updates === 'string') {
    console.log(`⏭  跳过更新断言 —— 市场连不上(${updates.slice(0, 80)})`)
  } else if (updates.length === 0) {
    console.log('⏭  跳过更新断言 —— 市场上没有这个插件的更高版本')
  } else {
    const found = updates[0]
    check('检测到了更新', found.currentVersion === '0.1.0', JSON.stringify(found))
    check('本地装的不算市场来源', found.fromMarket === false, `fromMarket=${String(found.fromMarket)}`)
    await sleep(500)
    const text = await cdp.eval(`document.body.innerText`)
    check('行内 ↑ 徽标出现', text.includes(`↑ ${found.latestVersion}`), '')
    // ★ 本地来源的不进「全部更新」—— 横幅不该出现
    check('本地来源不计入「全部更新」横幅', !text.includes('个插件可更新'), '')
    /*
      ★ 上面探针已经把这两条能力批过了,所以详情页**不该**还挂着
      「这一版要新增能力」。这一条钉的是一个真实出现过的自相矛盾:
      同一屏里,上半截说要新增 workspace.write,下半截那条能力已经是绿的、
      写着「撤销」—— `escalatedPermissions` 是一份会过期的快照,
      批/撤之后必须重算(见 `stores/plugins.ts` 的 `revoke`)。
    */
    check('批过的能力不再报成「新增」', !text.includes('这一版要新增能力'), '')
    await cdp.shoot('04-update-badge')
  }

  console.log(`\n${failed ? '有断言失败' : '全部通过'} — 图在 ${OUT}/`)
} catch (error) {
  failed = true
  console.error(`探针挂了:${error.message}`)
} finally {
  if (child?.pid !== undefined) {
    // ★ SIGTERM 杀不干净 Electron,残留的 helper 会占住调试端口
    try { process.kill(-child.pid, 'SIGKILL') } catch { child.kill('SIGKILL') }
  }
  if (testProject) await rm(testProject, { recursive: true, force: true })
  process.exit(failed ? 1 : 0)
}
