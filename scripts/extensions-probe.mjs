/**
 * 扩展面板探针 —— 验证「扩展」入口和它那四个 Tab。
 *
 * CDP 驱动那套是从 `screenshot.mjs` 抄下来的(同一个理由:Electron 没实现 CDP 的
 * Browser 域,真窗口尺寸改不了,只能覆盖视口再靠 `captureBeyondViewport` 重绘)。
 *
 * ★ **`--user-data-dir` 一个人管着两件事,少传一次两件一起出问题**:
 *
 *   - **单实例锁**。`src/main/index.ts` 里 `app.requestSingleInstanceLock()` 排在
 *     `app.setPath('userData', …)` **前面**,所以取锁那一刻用的就是命令行给的这个值。
 *     不传它的话,开发实例会和已安装的 NextCoWork.app 抢同一把锁 —— 表现极具
 *     迷惑性:Electron 照常打印 `DevTools listening`,但 CDP 的 `/json` 永远是空的
 *     (窗口压根没建),进程静默退出且 exit code 是 0。
 *
 *   - **数据落在哪**。`resolveDataRoot()` 认这个开关:传了就以它为数据根,
 *     不传就是用户真实的 `~/.next-cowork` —— 那等于直接写进开发者自己的库。
 *     (历史注记:早先数据根从 cwd 派生,靠 mkdtemp 出来的临时 cwd 隔离;
 *     现在 cwd 只决定「工作区在哪」。)
 *
 * 跑法:`npm run build && node scripts/extensions-probe.mjs`
 */
import { spawn } from 'node:child_process'
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises'
import { createServer } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { setTimeout as sleep } from 'node:timers/promises'
import electron from 'electron'

const OUT = '/tmp/nextcowork-extensions'
const VIEWPORT = { width: 1264, height: 900 }

/**
 * 找一个没人用的调试端口。
 *
 * ★ 写死一个端口的代价是**上一次跑剩下的进程会把这一次挡在门外**，而症状和
 *   「单实例锁没绕开」一模一样（CDP 连不上），排查时极容易走错方向。
 */
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

/** 按可见文字点一个按钮。找不到就抛 —— 静默不点会让后面的断言错得莫名其妙。 */
const clickButton = (text) => `(() => {
  const b = [...document.querySelectorAll('button')].find((x) => x.textContent?.trim() === ${JSON.stringify(text)})
  if (!b) throw new Error('找不到按钮:' + ${JSON.stringify(text)})
  b.click()
  return true
})()`

let child = null
let failed = false
let testProject = null
const results = []
const check = (label, ok, detail = '') => {
  results.push({ label, ok, detail })
  console.log(`${ok ? '✅' : '❌'} ${label}${detail ? ` — ${detail}` : ''}`)
  if (!ok) failed = true
}

try {
  await mkdir(OUT, { recursive: true })
  const env = { ...process.env }
  delete env.ELECTRON_RUN_AS_NODE
  delete env.ELECTRON_NO_ATTACH_CONSOLE
  const projectRoot = process.cwd()
  testProject = await mkdtemp(join(tmpdir(), 'nextcowork-ext-'))

  const port = await freePort()
  /*
    ★ `detached: true` 是为了收尾能杀掉**整棵**进程树。Electron 会派生一堆
    helper 进程，只 `child.kill()` 的话主进程走了、helper 还占着调试端口 ——
    下一次跑就会卡在「CDP 连不上」，而那个症状和单实例锁没绕开长得一模一样。
    （和 `environment/local.ts` 给钩子加 detached 是同一个问题。）
  */
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

  // 首屏可能停在登录页 —— 走「免登录使用」进主界面。
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
  check('侧边栏有「扩展」入口', true)
  await cdp.shoot('00-sidebar')

  await cdp.eval(clickButton('扩展'))
  await sleep(700)

  const header = await cdp.eval(`document.querySelector('h1')?.textContent?.trim() ?? null`)
  check('点开后标题是「扩展」', header === '扩展', `实际:${header}`)

  // 四个 Tab 是 Segmented(role=radio)。逐个点过去,各截一张。
  const tabs = ['技能', '命令', '子代理', '钩子']
  const labels = await cdp.eval(
    `[...document.querySelectorAll('button[aria-pressed]')].map((r) => r.textContent?.trim())`
  )
  check('四个 Tab 都在', tabs.every((x) => labels.includes(x)), `实际:${JSON.stringify(labels)}`)

  /*
    Skills Tab 应当有**两条** header:外层那条(返回 + 标题 + 四个 Tab)，
    加上 SkillsFeature 自己那条只剩右侧按钮的工具条(`chromeless` 去掉的是
    返回键和标题，不是整个 header)。三条就说明 chromeless 没生效。
  */
  const headerCount = await cdp.eval(`document.querySelectorAll('header').length`)
  check('Skills Tab 的 header 没有重复', headerCount === 2, `实际 ${headerCount} 条`)
  const backButtons = await cdp.eval(
    `[...document.querySelectorAll('header button')].filter((b) => b.getAttribute('aria-label') === '返回').length`
  )
  check('只有一个返回按钮', backButtons === 1, `实际 ${backButtons} 个`)
  await cdp.shoot('01-skills')

  for (const [i, name] of tabs.entries()) {
    if (i === 0) continue
    await cdp.eval(`(() => {
      const r = [...document.querySelectorAll('button[aria-pressed]')].find((x) => x.textContent?.trim() === ${JSON.stringify(name)})
      if (!r) throw new Error('找不到 Tab:' + ${JSON.stringify(name)})
      r.click()
      return true
    })()`)
    await sleep(400)
    const body = await cdp.eval(`document.body.innerText`)
    check(`切到「${name}」有内容`, body.includes(name), '')
    await cdp.shoot(`0${String(i + 1)}-${['skills', 'commands', 'agents', 'hooks'][i]}`)
  }

  // 钩子的新建弹层：模板下拉必须在（模板是这一屏最先要看到的东西）。
  await cdp.eval(clickButton('新建'))
  await sleep(600)
  const dialogOpen = await cdp.eval(`document.querySelector('[role="dialog"]') !== null`)
  check('钩子新建弹层能打开', dialogOpen === true)
  const hasTemplatePicker = await cdp.eval(
    `[...document.querySelectorAll('[role="dialog"] *')].some((e) => e.textContent?.trim() === '从模板开始')`
  )
  check('弹层里有「从模板开始」', hasTemplatePicker === true)
  const hasTestButton = await cdp.eval(
    `[...document.querySelectorAll('[role="dialog"] button')].some((b) => b.textContent?.trim() === '试运行')`
  )
  check('弹层里有「试运行」', hasTestButton === true)
  await cdp.shoot('05-hook-dialog')
  await cdp.eval(clickButton('取消'))
  await sleep(300)

  // 回到技能 Tab，确认 Skills 的操作按钮还在（chromeless 只该去掉返回键和标题）。
  await cdp.eval(`(() => {
    const r = [...document.querySelectorAll('button[aria-pressed]')].find((x) => x.textContent?.trim() === '技能')
    r.click(); return true
  })()`)
  await sleep(500)
  const skillButtons = await cdp.eval(
    `[...document.querySelectorAll('button')].map((b) => b.textContent?.trim()).filter(Boolean)`
  )
  check('Skills 的操作按钮仍在', skillButtons.some((b) => b.includes('刷新')), `按钮:${JSON.stringify(skillButtons.slice(0, 12))}`)

  console.log(`\n${failed ? '有断言失败' : '全部通过'} — 图在 ${OUT}/`)
} catch (error) {
  failed = true
  console.error(`探针挂了:${error.message}`)
} finally {
  if (child?.pid !== undefined) {
    try { process.kill(-child.pid, 'SIGTERM') } catch { child.kill() }
  }
  if (testProject) await rm(testProject, { recursive: true, force: true })
  process.exit(failed ? 1 : 0)
}
