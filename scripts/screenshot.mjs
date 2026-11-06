/**
 * 给界面评审用的截图器 —— 把打包后的应用跑起来,在几个状态上各截一张。
 *
 * 这个工程的验收标准是「像不像 `docs/images/` 里那 29 张」,而那种比较**必须看图**。
 * 描述「侧边栏 297px、面板间距 8px」谁也判断不了对不对,两张图并排放就一眼看得见。
 *
 * 复用 `e2e-probe.mjs` 那套 CDP 驱动(见该文件顶部的说明),只多做两件事:
 *   - 把**真窗口**调成 1264×1141 —— 参考截图就是这个尺寸,不对齐尺寸的并排比较
 *     是没有意义的。注意**不能用 `Emulation.setDeviceMetricsOverride`**:视口一旦
 *     大于真窗口,`captureScreenshot` 会把窗口外那块填成上一帧的残留,截出来是
 *     一张自己叠自己的图,而它看起来很像「布局重复渲染」这种 UI bug。
 *   - `Page.captureScreenshot`
 *
 * 跑法:`npm run build && node scripts/screenshot.mjs`,图落在 /tmp/nextcowork-shots/
 */
import { spawn } from 'node:child_process'
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { setTimeout as sleep } from 'node:timers/promises'
import electron from 'electron'

const PORT = 9334
const OUT = '/tmp/nextcowork-shots'
/** 参考截图的尺寸。并排比较的前提是同一个视口。 */
const VIEWPORT = { width: 1264, height: 1141 }
const DATA_VIEWPORT = { width: 1513, height: 1095 }
const MIN_DATA_VIEWPORT = { width: 900, height: 600 }

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
    const r = await this.send('Runtime.evaluate', {
      expression,
      returnByValue: true,
      awaitPromise: true
    })
    if (r.exceptionDetails) {
      throw new Error(`页面里抛了:${r.exceptionDetails.exception?.description ?? '?'}`)
    }
    return r.result.value
  }

  /** 走 preload 白名单修改截图状态，保留和真实设置页相同的数据链路。 */
  invoke(channel, req) {
    return this.eval(
      `window.nextcowork.invoke(${JSON.stringify(channel)}, ${JSON.stringify(req ?? null)})`
    )
  }

  async shoot(name) {
    // `captureBeyondViewport` 是这里的关键:视口被 override 成比真窗口大之后,
    // 默认的截图只画得出真窗口那一块,剩下的填上一帧的残留 ——
    // 截出来是一张自己叠自己的图,而它看起来非常像「布局重复渲染」的 UI bug。
    const { data } = await this.send('Page.captureScreenshot', {
      format: 'png',
      captureBeyondViewport: true
    })
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

let child = null
let failed = false
let testProject = null

try {
  await mkdir(OUT, { recursive: true })

  const env = { ...process.env }
  delete env.ELECTRON_RUN_AS_NODE
  delete env.ELECTRON_NO_ATTACH_CONSOLE
  const projectRoot = process.cwd()
  testProject = await mkdtemp(join(tmpdir(), 'nextcowork-shot-project-'))
  const userData = join(testProject, '.electron-user-data')

  child = spawn(
    electron,
    [projectRoot, `--remote-debugging-port=${PORT}`, `--user-data-dir=${userData}`],
    { cwd: testProject, env, stdio: ['ignore', 'pipe', 'pipe'] }
  )

  const target = await until('渲染进程 CDP target', async () => {
    try {
      const list = await (await fetch(`http://127.0.0.1:${PORT}/json`)).json()
      return list.find((t) => t.type === 'page' && t.webSocketDebuggerUrl) ?? null
    } catch {
      return null
    }
  })

  const cdp = await Cdp.attach(target.webSocketDebuggerUrl)
  await cdp.send('Runtime.enable')
  await cdp.send('Page.enable')

  // Electron 没有实现 CDP 的 Browser 域(`Browser.getWindowForTarget wasn't found`),
  // 真窗口的尺寸从这里改不了 —— 只能覆盖视口,再靠 `captureBeyondViewport` 让它
  // 真的重绘那么大一块(见 `shoot`)。
  await cdp.send('Emulation.setDeviceMetricsOverride', {
    ...VIEWPORT,
    deviceScaleFactor: 1,
    mobile: false
  })
  await sleep(400)
  const actual = await cdp.eval('({ w: innerWidth, h: innerHeight })')
  if (actual.w !== VIEWPORT.width || actual.h !== VIEWPORT.height) {
    throw new Error(`视口没调成 ${VIEWPORT.width}×${VIEWPORT.height},实际 ${actual.w}×${actual.h}`)
  }
  console.log(`视口 ${actual.w}×${actual.h}`)

  await until('对话输入框', () =>
    cdp.eval(`document.querySelector('[data-testid="composer-input"]') !== null`)
  )
  // 截图数据必须来自一条真的成功请求；生产默认的 RoutinAI 不带密钥，
  // 会只生成一条 0 Token 鉴权失败记录。通过正式 IPC 建隔离的 demo provider，
  // 和 e2e-probe 使用同一条受控演示上游，不影响真实用户配置。
  for (const [channel, request] of [
    ['provider:upsert', {
      id: 'shot-demo',
      name: 'RoutinAI',
      protocol: 'anthropic',
      baseUrl: 'https://demo.invalid',
      credentialRef: 'ignored-by-main-process',
      priority: 0,
      enabled: true
    }],
    ['provider:setCredential', { providerId: 'shot-demo', apiKey: 'sk-demo-not-a-real-key' }],
    ['provider:setAliases', { providerId: 'shot-demo', models: ['claude-fable-5-1'] }],
    ['settings:update', { defaultModel: 'claude-fable-5-1' }]
  ]) {
    const result = await cdp.invoke(channel, request)
    if (result?.ok !== true) {
      throw new Error(`${channel} 失败:${result?.error?.message ?? JSON.stringify(result)}`)
    }
  }
  await until('截图演示模型生效', () =>
    cdp.eval(`document.querySelector('[data-testid="composer-send"]')?.title === 'claude-fable-5-1'`)
  )
  await sleep(400) // 让字体和过渡落定,否则截到半渲染的一帧
  await cdp.shoot('01-empty')

  // 打一段字但不发送 —— 这一张是给「输入区」看的:模型 pill、权限档位、发送按钮
  await cdp.eval(`
    (() => {
      const el = document.querySelector('[data-testid="composer-input"]')
      const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set
      setter.call(el, '看一下这个工程的结构')
      el.dispatchEvent(new Event('input', { bubbles: true }))
      return true
    })()
  `)
  await sleep(200)
  await cdp.shoot('02-typed')

  await cdp.eval(`document.querySelector('[data-testid="composer-send"]').click()`)

  // 生成中的那一帧 —— 状态行、思考块、工具卡片都只在这个窗口里出现
  await until('流式开始', () =>
    cdp.eval(`document.querySelector('[data-testid="chat-status"]')?.dataset.status === 'running'`)
  )
  await sleep(700)
  await cdp.shoot('03-streaming')

  await until('run 收尾', () =>
    cdp.eval(`
      (() => {
        const s = document.querySelector('[data-testid="chat-status"]')?.dataset.status
        return s !== undefined && s !== 'running'
      })()
    `)
  )
  await sleep(400)
  await cdp.shoot('04-done')

  // 侧边栏收起 —— 红绿灯要落到外层 Tab 条上,这是最容易做错的一处布局
  await cdp.eval(`
    (() => {
      const btn = [...document.querySelectorAll('button')].find(
        (b) => b.getAttribute('aria-label') === '收起侧边栏'
      )
      if (!btn) throw new Error('找不到收起侧边栏按钮')
      btn.click()
      return true
    })()
  `)
  await sleep(400)
  await cdp.shoot('05-collapsed')

  /*
    设置 › 连接 —— 四个真接了运行时的子页各一张,用来和参考图并排比。

    ★ **按 aria-label / 按可见文字点,不按 class 点。** class 是随时会变的样式细节,
    而 label 是无障碍契约的一部分,本来就该稳定;拿 class 当选择器的截图脚本
    会在一次纯样式改动之后无声地截错东西。
  */
  const clickLabel = async (label) => {
    const ok = await cdp.eval(`
      (() => {
        const b = [...document.querySelectorAll('button')].find(
          (x) => x.getAttribute('aria-label') === ${JSON.stringify(label)}
        )
        if (!b) return false
        b.click()
        return true
      })()
    `)
    if (!ok) throw new Error(`找不到 aria-label 为「${label}」的按钮`)
  }

  /** 左侧导航按可见文字点 —— 那些按钮没有 aria-label,文字就是它的名字 */
  const clickNav = async (text) => {
    const ok = await cdp.eval(`
      (() => {
        const b = [...document.querySelectorAll('[role="dialog"] nav button')].find(
          (x) => x.textContent.trim() === ${JSON.stringify(text)}
        )
        if (!b) return false
        b.click()
        return true
      })()
    `)
    if (!ok) throw new Error(`设置里找不到「${text}」这一页`)
  }

  /** 子 Tab 是 Arc 的 SegmentedControl,每一段是带 aria-pressed 的按钮 */
  const clickSub = async (text) => {
    const ok = await cdp.eval(`
      (() => {
        const b = [...document.querySelectorAll('button[aria-pressed]')].find(
          (x) => x.textContent.trim() === ${JSON.stringify(text)}
        )
        if (!b) return false
        b.click()
        return true
      })()
    `)
    if (!ok) throw new Error(`连接页下找不到「${text}」这个子 Tab`)
  }

  await clickLabel('展开侧边栏')
  await sleep(300)
  await clickLabel('设置')
  await until('设置浮层', () =>
    cdp.eval(`document.querySelector('[role="dialog"][aria-label="设置"]') !== null`)
  )
  await sleep(300)
  await clickNav('连接')
  await sleep(300)

  for (const [name, sub] of [
    ['06-connection-mcp', 'MCP'],
    ['07-connection-search', '搜索服务'],
    ['08-connection-gateway', '开放网关'],
    ['09-connection-network', '网络']
  ]) {
    await clickSub(sub)
    // 列表要等一次 IPC 往返(mcp:list / websearch:list),不等就截到「正在读取」
    await sleep(600)
    await cdp.shoot(name)
  }

  /*
    两张**验证图**,不是评审图 —— 它们各自钉住一个已经修过的 bug,
    而这两个 bug 都只在「动起来」的时候才看得见,静态截图看不出来:

    10:添加 MCP 服务器的弹窗。它渲染在设置内容区里,而那个容器带 `mask-image`,
       于是 `position: fixed` 的包含块变成了那块滚动区(见 Dialog.tsx 的文件头)。
       症状是弹窗被压进内容区、标题随内容滚没。这张图必须看见弹窗**盖住整个窗口**。
    11:搜索服务的拖动。抓手是行里的小把手,`useDragReorder` 曾经拿它的
       `parentElement` 当列表算几何,于是行与行互相压住。除了截图,下面还**读一遍
       落定后的顺序**并断言 —— 顺序是 `priority`,而 priority 决定 web_search
       先打哪一家,这件事不能只靠肉眼看图。
  */
  await clickSub('MCP')
  await sleep(500)
  await cdp.eval(`
    (() => {
      const b = [...document.querySelectorAll('button')].find(
        (x) => x.textContent.trim() === '添加'
      )
      if (!b) throw new Error('MCP 页上找不到「添加」按钮')
      b.click()
      return true
    })()
  `)
  await until('MCP 弹窗', () =>
    cdp.eval(`document.querySelector('[role="dialog"][aria-label="添加 MCP 服务器"]') !== null`)
  )
  await sleep(300)
  await cdp.shoot('10-mcp-dialog')

  // 弹窗里有草稿态输入框,按 Esc 关(这条路径本身也是约束 3 要保的)
  await cdp.eval(`
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
  `)
  await sleep(300)

  await clickSub('搜索服务')
  /*
    ★ 这里等得比别处久,不是保守:排序是**乐观更新**(见 stores/websearch.ts),
    而首次进页面那趟 `websearch:list` 的响应要是晚于放手才回来,它会拿库里
    那份旧顺序把乐观更新盖回去 —— 表现是这一场偶发地「拖了等于没拖」。
    先让那趟往返落定再动手。
  */
  await sleep(1400)

  /** 每行右边那个开关的 aria-label 是「启用 <名字>」—— 拿它当行的名字最稳 */
  const providerOrder = () =>
    cdp.eval(`
      [...document.querySelectorAll('li[data-drag-item]')].map((li) =>
        li.querySelector('[role="switch"]')?.getAttribute('aria-label')?.replace(/^启用 /, '') ?? '?'
      )
    `)

  const before = await providerOrder()
  console.log(`拖动前:${before.join(' → ')}`)

  /**
   * 一行的两个坐标,两个都要:
   * - `grip` 是**按下去**的地方(抓手在行的顶端附近,不在行中心);
   * - `center` 是**判定落点**用的(`useDragReorder` 比的是被拖元素的中心
   *   与各行原始中心)。
   *
   * 混用这两个就会差一格:按住抓手拖到下一行的抓手上,位移只有「行高」,
   * 而被拖元素的中心此刻恰好压在那一行的中心线上 —— 落点在边界上。
   */
  const rowAt = (i) =>
    cdp.eval(`
      (() => {
        const li = document.querySelectorAll('li[data-drag-item]')[${String(i)}]
        if (!li) return null
        const g = li.querySelector('.cursor-grab').getBoundingClientRect()
        const r = li.getBoundingClientRect()
        return {
          grip: { x: Math.round(g.left + g.width / 2), y: Math.round(g.top + g.height / 2) },
          center: Math.round(r.top + r.height / 2)
        }
      })()
    `)

  const mouse = (type, x, y) =>
    cdp.send('Input.dispatchMouseEvent', {
      type,
      x,
      y,
      button: 'left',
      buttons: type === 'mouseReleased' ? 0 : 1,
      clickCount: 1
    })

  // 把第 1 行往下拖过第 2 行。步进着走 —— 一步跳到位的话 4px 起拖阈值那段
  // 逻辑根本不会被走到,而它正是这个 hook 里最容易写错的一段
  const from = await rowAt(0)
  const to = await rowAt(2)
  if (from === null || to === null) throw new Error('搜索服务列表里找不到抓手')
  // 位移按**中心到中心**算,再多走 6px 越过边界 —— 正好压线时该落在哪一格
  // 是个可以两说的约定,截图脚本不该去考它
  const dy = to.center - from.center + 6
  await mouse('mousePressed', from.grip.x, from.grip.y)
  for (let k = 1; k <= 8; k += 1) {
    await mouse('mouseMoved', from.grip.x, Math.round(from.grip.y + (dy * k) / 8))
    await sleep(40)
  }
  await cdp.shoot('11-search-dragging')
  await mouse('mouseReleased', from.grip.x, from.grip.y + dy)
  await sleep(800)

  const after = await providerOrder()
  console.log(`拖动后:${after.join(' → ')}`)
  const expected = [before[1], before[2], before[0], ...before.slice(3)]
  if (after.join('|') !== expected.join('|')) {
    throw new Error(`拖动没落到该落的位置。期望 ${expected.join(' → ')},实际 ${after.join(' → ')}`)
  }
  console.log('✓ 拖动重排落点正确')
  await cdp.shoot('12-search-reordered')

  /*
    13:侧边栏那颗「新建对话」。它以前和 Tab 条上的 `+` 走同一条 `open`,
       连点就攒出一排一模一样的「新对话」。同样只在动起来时才看得见,
       所以这里**读 Tab 条上的对话数并断言**,不只截图:

       第一下开一个 → 再点两下仍是一个(重用) → 往输入框里打一个字
       (草稿让它不再算空)→ 这时候点才应该真的多出一个。
  */
  await cdp.eval(`
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
  `)
  await sleep(400)

  /** 主区 Tab 条上标题是「新对话」的有几个 */
  const newChatTabs = () =>
    cdp.eval(`
      [...document.querySelectorAll('[role="tab"]')].filter(
        (t) => t.textContent.includes('新对话')
      ).length
    `)

  const clickNewChat = async () => {
    await clickLabel('新建对话')
    await sleep(350)
  }

  /*
    ★ 基准取**第一下之后**的数,不是进来时的数 —— 前面 02~04 那几场已经在
    第一个对话里发过一条消息,它是「用过的」,所以第一下**应该**新建一个。
    要量的是第二下、第三下不再新建。
  */
  await clickNewChat()
  const base = await newChatTabs()
  await clickNewChat()
  await clickNewChat()
  const reused = await newChatTabs()
  if (reused !== base) {
    throw new Error(`连点「新建对话」不该攒出多个空对话。第一下之后 ${base} 个,再点两下变成 ${reused} 个`)
  }
  console.log(`✓ 连点「新建对话」停在 ${reused} 个对话上,没有再新增`)
  await cdp.shoot('13-new-chat-reused')

  // 打一个字:有草稿的会话不算空,这时候点才该真的新建
  await cdp.eval(`
    (() => {
      const el = document.querySelector('[data-testid="composer-input"]')
      if (!el) throw new Error('找不到输入框')
      el.focus()
      const set = Object.getOwnPropertyDescriptor(
        window.HTMLTextAreaElement.prototype, 'value'
      ).set
      set.call(el, '写了一半')
      el.dispatchEvent(new Event('input', { bubbles: true }))
      return true
    })()
  `)
  await sleep(300)
  await clickNewChat()
  const created = await newChatTabs()
  if (created !== reused + 1) {
    throw new Error(`草稿没写完的对话不该被重用。期望 ${reused + 1} 个,实际 ${created} 个`)
  }
  console.log('✓ 有草稿时「新建对话」照常新建')
  await cdp.shoot('14-new-chat-created')

  // 用户给出的「设置 › 数据」上下两张参考图是 1513×1095。切到同一
  // viewport 和浅色外观后，分别截滚动区顶部、底部和两个关键确认弹窗。
  // 参考图是浅色；如果继续跟随当前系统深色，布局虽相同也无法做有效视觉比较。
  const light = await cdp.invoke('settings:update', { theme: 'light', locale: 'zh-CN' })
  if (light?.ok !== true) {
    throw new Error(`数据页截图无法切换浅色外观:${light?.error?.message ?? JSON.stringify(light)}`)
  }
  await cdp.send('Emulation.setDeviceMetricsOverride', {
    ...DATA_VIEWPORT,
    deviceScaleFactor: 1,
    mobile: false
  })
  await until('数据页浅色外观', () => cdp.eval(`document.documentElement.dataset.theme === 'light'`))
  await sleep(300)
  await clickLabel('设置')
  await until('设置浮层', () =>
    cdp.eval(`document.querySelector('[role="dialog"][aria-label="设置"]') !== null`)
  )
  await clickNav('数据')
  await until('数据统计加载', () =>
    cdp.eval(`
      (() => {
        const page = document.querySelector('[data-testid="data-page"]')
        if (!page) return false
        const values = [...page.querySelectorAll('strong')].slice(0, 4)
        return values.length === 4 && values.every((value) => value.textContent.trim() !== '…')
      })()
    `)
  )
  await sleep(300)
  await cdp.shoot('15-data-top')

  await cdp.eval(`
    (() => {
      const page = document.querySelector('[data-testid="data-page"]')
      if (!page || !page.parentElement) return false
      page.parentElement.scrollTop = page.parentElement.scrollHeight
      return true
    })()
  `)
  await sleep(300)
  await cdp.shoot('16-data-bottom')

  await cdp.eval(`
    (() => {
      const page = document.querySelector('[data-testid="data-page"]')
      if (!page || !page.parentElement) return false
      page.parentElement.scrollTop = 0
      const button = [...page.querySelectorAll('button')].find(
        (item) => item.textContent.trim() === '导出'
      )
      if (!button) return false
      button.click()
      return true
    })()
  `)
  await until('导出弹窗', () =>
    cdp.eval(`document.querySelector('[role="dialog"][aria-label="导出数据"]') !== null`)
  )
  await sleep(200)
  await cdp.shoot('17-data-export-dialog')
  await cdp.eval(`document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))`)
  await sleep(200)

  await cdp.eval(`
    (() => {
      const page = document.querySelector('[data-testid="data-page"]')
      const button = [...(page?.querySelectorAll('button') ?? [])].find(
        (item) => item.textContent.trim() === '清理'
      )
      if (!button) return false
      button.click()
      return true
    })()
  `)
  await until('附件清理预览', () =>
    cdp.eval(`document.querySelector('[role="dialog"][aria-label="清理附件目录"]') !== null`)
  )
  await sleep(200)
  await cdp.shoot('18-data-cleanup-dialog')

  // Electron 的真实最小窗口是 900×600。最后再钉住中英文在这个边界下
  // 不重叠、不横向溢出；长文案可以自然换行，内容区继续纵向滚动。
  await cdp.eval(`document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))`)
  await cdp.send('Emulation.setDeviceMetricsOverride', {
    ...MIN_DATA_VIEWPORT,
    deviceScaleFactor: 1,
    mobile: false
  })
  await cdp.eval(`
    (() => {
      const page = document.querySelector('[data-testid="data-page"]')
      if (!page || !page.parentElement) return false
      page.parentElement.scrollTop = 0
      return true
    })()
  `)
  await sleep(300)
  await cdp.shoot('19-data-min-window-zh')

  const english = await cdp.invoke('settings:update', { locale: 'en-US' })
  if (english?.ok !== true) {
    throw new Error(`数据页截图无法切换英文:${english?.error?.message ?? JSON.stringify(english)}`)
  }
  await until('数据页英文文案', () =>
    cdp.eval(`document.querySelector('[data-testid="data-page"]')?.textContent.includes('Cloud sync')`)
  )
  await sleep(300)
  await cdp.shoot('20-data-min-window-en')

  // 使用统计：前面的演示请求已经通过真实路由产生了用量记录。回到中文，
  // 验证统计 IPC 的详细字段，并在最小窗口截一张与参考图同密度的界面。
  const usageLocale = await cdp.invoke('settings:update', { locale: 'zh-CN' })
  if (usageLocale?.ok !== true) {
    throw new Error(`使用统计截图无法切回中文:${usageLocale?.error?.message ?? JSON.stringify(usageLocale)}`)
  }
  await until('使用统计中文文案', () =>
    cdp.eval(`document.querySelector('[role="dialog"]')?.textContent.includes('数据')`)
  )
  await cdp.send('Emulation.setDeviceMetricsOverride', {
    ...DATA_VIEWPORT,
    deviceScaleFactor: 1,
    mobile: false
  })
  // 「使用统计」是侧栏里的独立一页,不再是「模型」下的子 Tab
  await clickNav('使用统计')
  await until('使用统计加载', () =>
    cdp.eval(`document.querySelector('[data-testid="usage-page"]')?.textContent.includes('请求日志')`)
  )
  const usageLogs = await cdp.invoke('usage:getRequestLogs', {
    to: Date.now() + 1,
    status: 'all',
    limit: 10,
    offset: 0
  })
  if (usageLogs?.ok !== true || usageLogs.data.total < 1) {
    throw new Error(`真实演示请求没有生成用量记录:${JSON.stringify(usageLogs)}`)
  }
  const usageRow = usageLogs.data.items[0]
  for (const field of [
    'inputTokens',
    'cacheReadTokens',
    'cacheWriteTokens',
    'cacheWrite1hTokens',
    'outputTokens',
    'thinkingTokens',
    'thinkingTokensEstimated',
    'latencyMs',
    'timeToFirstTokenMs',
    'costMicros',
    'currency',
    'toolCalls',
    'toolErrors',
    'httpStatus',
    'stopReason',
    'errorKind',
    'errorMessage',
    'endpoint',
    'attempt'
  ]) {
    if (!(field in usageRow)) throw new Error(`用量记录缺少字段 ${field}`)
  }
  await sleep(300)
  await cdp.shoot('21-usage-statistics')
  const expandedUsage = await cdp.eval(`
    (() => {
      const button = document.querySelector('[data-testid="usage-request-row"] button[aria-label="展开请求详情"]')
      if (!button) return false
      button.click()
      return true
    })()
  `)
  if (!expandedUsage) throw new Error('使用统计第一条请求无法展开详情')
  await until('使用统计详情展开', () =>
    cdp.eval(`document.querySelector('[data-testid="usage-page"]')?.textContent.includes('输入 Token')`)
  )
  await sleep(200)
  await cdp.shoot('22-usage-details')

  // Expanded diagnostics are the densest state on this screen. Verify that
  // both locales still wrap and scroll at Electron's true minimum viewport.
  await cdp.send('Emulation.setDeviceMetricsOverride', {
    ...MIN_DATA_VIEWPORT,
    deviceScaleFactor: 1,
    mobile: false
  })
  await sleep(300)
  await cdp.shoot('23-usage-min-window-zh')

  const usageEnglish = await cdp.invoke('settings:update', { locale: 'en-US' })
  if (usageEnglish?.ok !== true) {
    throw new Error(`使用统计截图无法切换英文:${usageEnglish?.error?.message ?? JSON.stringify(usageEnglish)}`)
  }
  await until('使用统计英文文案', () =>
    cdp.eval(`document.querySelector('[data-testid="usage-page"]')?.textContent.includes('Request logs')`)
  )
  await sleep(300)
  await cdp.shoot('24-usage-min-window-en')

  console.log(`\n✅ 截完了,共 24 张,在 ${OUT}/`)
} catch (err) {
  failed = true
  console.error(`\n❌ 截图失败:${err.message}`)
} finally {
  child?.kill('SIGTERM')
  await sleep(500)
  if (child !== null && child.exitCode === null) child.kill('SIGKILL')
  if (testProject !== null) await rm(testProject, { recursive: true, force: true })
}

process.exit(failed ? 1 : 0)
