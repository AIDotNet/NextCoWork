/**
 * 办公编辑器端到端探针 —— 在真的 Electron 里走一遍:
 * 文字「打开 .docx → 画布出像素 → 点击、打字、输入法 → 功能区加粗 → Ctrl/Cmd+S 存回工作区」,
 * 表格「打开 .xlsx → 行列头 → 点选单元格 → 名称框跟随 → 编辑栏写公式 → 存回工作区」,
 * 演示「打开 .pptx → 缩略图 → 新建幻灯片 → 换空白版式 → 插文本框打字 → 存回工作区 → 放映与退出」。
 *
 * ## 为什么要它
 *
 * 画布通道的每一段都有单测(主进程通道、主窗口转发、视图客户端、tile 账目、输入合批),宿主集成
 * 测试也用真引擎走过会话层。但**把它们连起来的那几处**只有在 Electron 里才存在:preload 白名单、
 * iframe 的 postMessage 来源核对、ArrayBuffer 转移、隐藏输入框的焦点与输入法、插件运行时的
 * import map。任何一处断了,表现都是「画布一片空白 / 打字没反应」且零报错。
 *
 * ## 跑法
 *
 *   npm run build
 *   node plugins/ncw.writer/build.mjs && node plugins/ncw.sheets/build.mjs && node plugins/ncw.slides/build.mjs
 *   NCW_OFFICE_RUNTIME=<解开的引擎插件目录> NCW_OFFICE_FIXTURES=<含 blank.docx / blank.xlsx / blank.pptx 的目录> node scripts/office-editors-probe.mjs
 *
 * 引擎插件目录默认取 `../ncw-office-runtime/build/ncw.office-runtime`(引擎仓库 `package-plugin.mjs` 的产物)。
 *
 * ## 不往生产代码里塞调试出口
 *
 * 原生 helper 的执行确认是一个系统对话框,探针够不着。这里用 playwright 的 Electron 驱动在
 * **测试进程里**把主进程的 `dialog.showMessageBox` 换成「允许」—— 生产代码不带任何开关。
 */
// page.evaluate / frame.evaluate 的回调跑在页面里,这两个是页面的全局
/* global window, document */
import { copyFile, mkdir, mkdtemp, readFile, realpath, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { setTimeout as sleep } from 'node:timers/promises'
import { inflateRawSync } from 'node:zlib'
import electron from 'electron'
import { _electron } from 'playwright-core'

const projectRoot = process.cwd()
const runtimeDir = resolve(process.env.NCW_OFFICE_RUNTIME ?? join(projectRoot, '..', 'ncw-office-runtime', 'build', 'ncw.office-runtime'))
const fixtures = resolve(process.env.NCW_OFFICE_FIXTURES ?? join(projectRoot, '..', 'ncw-office-runtime', 'test-fixtures'))
const writerDir = join(projectRoot, 'plugins', 'ncw.writer')
const sheetsDir = join(projectRoot, 'plugins', 'ncw.sheets')
const slidesDir = join(projectRoot, 'plugins', 'ncw.slides')
const OUT = '/tmp/nextcowork-office-writer'
const MOD = process.platform === 'darwin' ? 'Meta' : 'Control'

let failed = false
const check = (label, ok, detail = '') => {
  console.log(`${ok ? '✅' : '❌'} ${label}${detail === '' ? '' : ` — ${detail}`}`)
  if (!ok) failed = true
}

async function until(label, fn, timeoutMs = 90_000) {
  const deadline = Date.now() + timeoutMs
  for (;;) {
    const value = await fn().catch(() => null)
    if (value !== null && value !== undefined && value !== false) return value
    if (Date.now() > deadline) throw new Error(`超时等待「${label}」`)
    await sleep(250)
  }
}

/** 读 zip(docx / xlsx)里的一个条目(只支持 stored / deflate,够探针用) */
async function zipEntry(path, name) {
  const buf = await readFile(path)
  let eocd = -1
  for (let i = buf.length - 22; i >= 0; i--) if (buf.readUInt32LE(i) === 0x06054b50) { eocd = i; break }
  let offset = buf.readUInt32LE(eocd + 16)
  for (let n = 0; n < buf.readUInt16LE(eocd + 10); n++) {
    const method = buf.readUInt16LE(offset + 10)
    const size = buf.readUInt32LE(offset + 20)
    const nameLength = buf.readUInt16LE(offset + 28)
    const skip = nameLength + buf.readUInt16LE(offset + 30) + buf.readUInt16LE(offset + 32)
    const local = buf.readUInt32LE(offset + 42)
    if (buf.subarray(offset + 46, offset + 46 + nameLength).toString() === name) {
      const start = local + 30 + buf.readUInt16LE(local + 26) + buf.readUInt16LE(local + 28)
      const data = buf.subarray(start, start + size)
      return (method === 8 ? inflateRawSync(data) : data).toString('utf8')
    }
    offset += 46 + skip
  }
  return ''
}

const docxXml = (path) => zipEntry(path, 'word/document.xml')

/** docx 正文的全部文字 */
async function docxText(path) {
  const xml = await docxXml(path)
  return [...xml.matchAll(/<w:t(?: [^>]*)?>([^<]*)<\/w:t>/g)].map((m) => m[1]).join('')
}

let app = null
let project = null

/**
 * 截图只是留证据,不是断言。★ 窗口被遮住 / 不在前台时 Chromium 可能不出帧,截图会一直等到超时 ——
 *   以前这会让整个探针挂掉。这里超时就记一笔(渲染进程还答不答、窗口可见 / 聚焦与否)然后继续,
 *   用来区分「窗口没出帧」与「渲染进程卡死」。
 */
async function shot(name) {
  const page = app?.windows()[0]
  if (page === undefined) return
  try {
    await page.screenshot({ path: join(OUT, name), timeout: 10_000 })
  } catch (error) {
    const answer = (promise) => Promise.race([promise, sleep(5000).then(() => '5 s 内没回应')]).catch((e) => String(e))
    const renderer = await answer(page.evaluate(() => `visibility=${document.visibilityState} focus=${String(document.hasFocus())}`))
    const window = await answer(app.evaluate(({ BrowserWindow }) => JSON.stringify(BrowserWindow.getAllWindows().map((w) => ({ visible: w.isVisible(), minimized: w.isMinimized(), focused: w.isFocused() })))))
    console.log(`⚠️ 截图 ${name} 没截到(${String(error.message).split('\n')[0]})— 渲染进程 ${renderer};窗口 ${window}`)
  }
}
try {
  await mkdir(OUT, { recursive: true })
  project = await realpath(await mkdtemp(join(tmpdir(), 'nextcowork-writer-')))

  const env = { ...process.env }
  delete env.ELECTRON_RUN_AS_NODE
  delete env.ELECTRON_NO_ATTACH_CONSOLE
  delete env.ELECTRON_RENDERER_URL
  app = await _electron.launch({ executablePath: electron, args: [projectRoot, `--user-data-dir=${join(project, '.ud')}`], cwd: project, env })
  // 见文件头「不往生产代码里塞调试出口」
  await app.evaluate(({ dialog }) => { dialog.showMessageBox = async () => ({ response: 0, checkboxChecked: false }) })
  const page = await app.firstWindow()
  // 诊断:iframe 里的报错与控制台只有在这里看得到(失败时一并打印)
  const logs = []
  page.on('console', (message) => { logs.push(`[console:${message.type()}] ${message.text()}`) })
  page.on('pageerror', (error) => { logs.push(`[pageerror] ${error.message}`) })
  page.on('response', (response) => { if (response.status() >= 400) logs.push(`[http ${response.status()}] ${response.url()}`) })
  page.on('requestfailed', (request) => { logs.push(`[requestfailed] ${request.url()} ${request.failure()?.errorText ?? ''}`) })
  app.process().stderr?.on('data', (chunk) => { const s = String(chunk); if (/documents|error|Error/.test(s) && !s.includes('ncw-office-helper')) logs.push(`[main] ${s.trim()}`) })
  globalThis.__probeLogs = logs
  await page.setViewportSize({ width: 1400, height: 900 })
  const invoke = (channel, req) => page.evaluate(async ([c, r]) => {
    const result = await window.nextcowork.invoke(c, r)
    return result.ok ? { ok: true, data: result.data } : { ok: false, error: result.error.message }
  }, [channel, req])

  await until('preload', () => page.evaluate(() => typeof window.nextcowork === 'object'))
  // 登录页是异步出现的:等到「登录页」或「工作区外壳」其中之一出现再决定
  const offline = page.getByRole('button', { name: '免登录使用' })
  const shell = page.locator('button[aria-label="工作区文件"]')
  await until('登录页或工作区外壳', async () => (await offline.count()) > 0 || (await shell.count()) > 0)
  if (await offline.count() > 0) await offline.first().click()
  await invoke('settings:update', { locale: 'zh-CN' })

  for (const dir of [runtimeDir, writerDir, sheetsDir, slidesDir]) {
    const installed = await invoke('plugins:installPackage', { path: dir })
    check(`装上 ${dir.split('/').pop()}`, installed.ok, installed.ok ? '' : installed.error)
  }
  const catalog = await invoke('plugins:list', undefined)
  for (const plugin of catalog.data.plugins) {
    await invoke('plugins:grantPermissions', { pluginId: plugin.id, permissions: plugin.manifest.permissions })
    const enabled = await invoke('plugins:setEnabled', { pluginId: plugin.id, enabled: true })
    const status = enabled.data?.plugins.find((p) => p.id === plugin.id)?.status
    check(`启用 ${plugin.id}`, enabled.ok && status !== 'error', String(status))
  }

  // 样本放进默认工作区(它的根由应用决定,不一定是 cwd),再从文件树打开 → 自定义编辑器(文字)
  const bootstrap = await invoke('app:getBootstrap', undefined)
  const docx = join(bootstrap.data.workspaces[0].rootPath, 'report.docx')
  await copyFile(join(fixtures, 'blank.docx'), docx)
  const before = await readFile(docx)
  await until('工作区文件按钮', () => page.locator('button[aria-label="工作区文件"]').count())
  const row = page.locator('[role="treeitem"][title="report.docx"]')
  await until('文件树里出现 report.docx', async () => {
    if (await row.count() > 0) return true
    const refresh = page.locator('button[aria-label="刷新"]')
    if (await refresh.count() > 0) await refresh.first().click()
    else await page.locator('button[aria-label="工作区文件"]').first().click()
    return false
  }, 30_000)
  await row.click()

  const frame = await until('文字编辑器 iframe', async () => page.frames().find((f) => f.url().startsWith('ncw-plugin://ncw.writer/')) ?? null)
  await until('画布出像素', () => frame.evaluate(() => {
    const canvases = [...document.querySelectorAll('canvas')]
    if (canvases.length === 0) return false
    const ctx = canvases[0].getContext('2d')
    const px = ctx.getImageData(Math.floor(canvases[0].width / 2), Math.floor(canvases[0].height / 2), 1, 1).data
    return px[3] === 255
  }), 120_000)
  check('画布画出了引擎渲染的页面', true)
  // 功能区的初始状态:用户还没动手,字体框就该有值(引擎空闲时才发第一批状态,画布要补拉)
  const fontName = await until('字体框有初始值', async () => {
    const value = (await frame.locator('button[aria-label="字体"]').innerText()).trim()
    return value === '' ? null : value
  }, 15_000).catch(() => '')
  check('打开后不动手,字体框已有当前字体', fontName !== '', JSON.stringify(fontName))
  await shot('01-opened.png')

  // 点进正文,打字
  const doc = frame.locator('[role="document"]')
  const box = await doc.boundingBox()
  await page.mouse.click((box?.x ?? 0) + 200, (box?.y ?? 0) + 120)
  await page.keyboard.type('Hello')
  // 输入法:组字 → 提交(CDP 的 imeSetComposition 走的就是真实的 composition 事件)
  const cdp = await page.context().newCDPSession(page)
  await cdp.send('Input.imeSetComposition', { text: 'zhong', selectionStart: 5, selectionEnd: 5 })
  await sleep(300)
  await cdp.send('Input.insertText', { text: '中' })
  await until('状态变成未保存', () => frame.evaluate(() => document.body.innerText.includes('未保存')), 20_000)
  check('打字后文档变脏', true)
  const caret = await frame.evaluate(() => [...document.querySelectorAll('[role="document"] div')].some((d) => d.className.includes('bg-page-ink')))
  check('画出了文字光标', caret)
  await shot('02-typed.png')
  check('保存之前磁盘上的文件没变', (await readFile(docx)).equals(before))

  // 功能区:全选(经画布交给引擎)→ 点「加粗」→ 按钮进入按下状态 → 存盘后文件里有 <w:b/>
  // 正文所在那块 tile 的像素:加粗之后字形变了,这块必须被重画(证明画布真的重画了,而不只是按钮变了)
  const textTile = () => frame.evaluate(() => {
    const canvas = document.querySelector('[role="document"] canvas')
    return [...canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data]
  })
  const tileBefore = await textTile()
  await page.keyboard.press(`${MOD}+KeyA`)
  const boldButton = frame.locator('button[aria-label="加粗"]')
  // 按下态 = IconButton 的 active 样式(text-accent);只比「class 变没变」会被 hover / 焦点样式骗过
  await boldButton.click()
  await until('加粗按钮按下', async () => (await boldButton.getAttribute('class'))?.includes('text-accent'), 20_000)
  check('功能区加粗后按钮进入按下状态', true)
  check('加粗按钮给读屏报已按下', (await boldButton.getAttribute('aria-pressed')) === 'true')
  check('撤销不是开关,不报按下状态', (await frame.locator('button[aria-label="撤销"]').getAttribute('aria-pressed')) === null)
  const changed = await until('画布重画出粗体', async () => {
    const now = await textTile()
    let diff = 0
    for (let i = 0; i < now.length; i += 4) if (Math.abs(now[i] - tileBefore[i]) + Math.abs(now[i + 3] - tileBefore[i + 3]) > 40) diff++
    return diff > 50 ? diff : null
  }, 15_000).catch(() => null)
  check('画布重画出了粗体字形', changed !== null, `变化的像素 ${String(changed)}`)
  await shot('02b-bold.png')
  await sleep(1500)
  const tile = await frame.evaluate(() => document.querySelector('[role="document"] canvas')?.toDataURL('image/png') ?? '')
  if (tile !== '') await writeFile(join(OUT, '02c-tile.png'), Buffer.from(tile.split(',')[1], 'base64'))


  // Cmd/Ctrl+S:由插件截走、经宿主存回工作区(不能送进引擎)
  await page.keyboard.press(`${MOD}+KeyS`)
  await until('状态变成已保存', () => frame.evaluate(() => document.body.innerText.includes('已保存')), 30_000)
  const saved = await docxText(docx)
  check('保存后文件里有键入的文字与输入法提交的字', saved.includes('Hello中'), JSON.stringify(saved))
  check('拼音没有进文件', !saved.includes('zhong'), JSON.stringify(saved))
  check('功能区的加粗进了文件', /<w:b\/>/.test(await docxXml(docx)))
  await shot('03-saved.png')

  // ─── 表格 ───
  const xlsx = join(bootstrap.data.workspaces[0].rootPath, 'budget.xlsx')
  await copyFile(join(fixtures, 'blank.xlsx'), xlsx)
  const sheetRow = page.locator('[role="treeitem"][title="budget.xlsx"]')
  await until('文件树里出现 budget.xlsx', async () => {
    if (await sheetRow.count() > 0 && await sheetRow.isVisible()) return true
    // 文字编辑器的 Tab 此刻盖在文件树上面:先切回「工作区文件」再刷新
    const filesTab = page.getByText('工作区文件', { exact: true })
    if (await filesTab.count() > 0) await filesTab.first().click()
    const refresh = page.locator('button[aria-label="刷新"]')
    if (await refresh.count() > 0 && await refresh.first().isVisible()) await refresh.first().click()
    return false
  }, 30_000)
  await sheetRow.click()
  const sheetFrame = await until('表格编辑器 iframe', async () => page.frames().find((f) => f.url().startsWith('ncw-plugin://ncw.sheets/')) ?? null)
  await until('表格画布出像素', () => sheetFrame.evaluate(() => {
    const canvas = document.querySelector('[role="document"] canvas')
    return canvas !== null && canvas.getContext('2d').getImageData(10, 10, 1, 1).data[3] === 255
  }), 120_000)
  const columnLabels = await until('列标', () => sheetFrame.evaluate(() => {
    const labels = [...document.querySelectorAll('[aria-label="列标"] div')].map((d) => d.textContent)
    return labels.length > 3 ? labels : null
  }), 20_000)
  check('列标来自引擎且从 A 开始', columnLabels[0] === 'A' && columnLabels[1] === 'B', JSON.stringify(columnLabels.slice(0, 4)))
  // B3 的中心:A 列 1275 twips、行高 255 twips,100% 下 1 CSS px = 15 twips
  const sheetDoc = sheetFrame.locator('[role="document"]')
  const sheetBox = await sheetDoc.boundingBox()
  await page.mouse.click((sheetBox?.x ?? 0) + (1275 + 637) / 15, (sheetBox?.y ?? 0) + (510 + 127) / 15)
  const nameBox = sheetFrame.locator('input[aria-label="名称框"]')
  await until('名称框跟随到 B3', async () => (await nameBox.inputValue()) === 'B3', 20_000)
  check('点选单元格后名称框显示 B3', true)
  const formulaBar = sheetFrame.locator('input[aria-label="编辑栏"]')
  await formulaBar.click()
  await formulaBar.fill('=1+2')
  await formulaBar.press('Enter')
  await until('编辑栏显示公式原文', async () => (await formulaBar.inputValue()) === '=1+2', 20_000)
  check('编辑栏写入公式后显示公式原文(不是结果)', true)
  const sheetBold = sheetFrame.locator('button[aria-label="加粗"]')
  await sheetBold.click()
  await until('表格加粗按下', async () => (await sheetBold.getAttribute('aria-pressed')) === 'true', 20_000)
  await shot('04-sheet.png')
  // 点到别的(非粗体)单元格:按钮要复位。引擎约 0.7 s 后才报 false,画布要补拉。
  // ★ 先等加粗那一轮补拉走完(最后一次在 2 s):否则是它顺手拉到了 false,测不出「点选之后有没有补拉」
  await sleep(2500)
  await sheetDoc.click({ position: { x: 300, y: 200 } })
  const released = await until('离开粗体单元格后加粗复位', async () => (await sheetBold.getAttribute('aria-pressed')) === 'false', 10_000).catch(() => false)
  check('离开粗体单元格后加粗按钮复位', released === true, String(await sheetBold.getAttribute('aria-pressed')))
  await page.keyboard.press(`${MOD}+KeyS`)
  await until('表格已保存', () => sheetFrame.evaluate(() => document.body.innerText.includes('已保存')), 30_000)
  const sheetXml = await zipEntry(xlsx, 'xl/worksheets/sheet1.xml')
  check('公式与结果都进了 xlsx', /<c r="B3"[^>]*>\s*<f[^>]*>1\+2<\/f>\s*<v>3<\/v>/.test(sheetXml), sheetXml.match(/<c r="B3"[\s\S]{0,120}/)?.[0] ?? '(没有 B3)')
  await shot('05-sheet-saved.png')

  // 往下滚过引擎报的已用区域(空表约 A1:R51):画布自己留余量,行号、格子、点选都要跟过去
  const sheetBox2 = await sheetDoc.boundingBox()
  await page.mouse.move((sheetBox2?.x ?? 0) + 200, (sheetBox2?.y ?? 0) + 200)
  const farRow = await until('行号滚过第 120 行', async () => {
    await page.mouse.wheel(0, 1200)
    const rows = await sheetFrame.evaluate(() => [...document.querySelectorAll('[aria-label="行号"] div')].map((d) => Number(d.textContent)).filter((n) => n > 0))
    const top = Math.min(...rows)
    return top > 120 ? top : null
  }, 30_000).catch(() => null)
  check('滚轮能滚过已用区域,行号跟着走', farRow !== null, `可见首行 ${String(farRow)}`)
  const farTile = await until('余量里画出了格子', () => sheetFrame.evaluate(() => {
    // 引擎最初报的高度约 13005 twips = 867 CSS px;在它下面的 tile 有像素才算画出来了
    return [...document.querySelectorAll('[role="document"] canvas')].some((c) => parseFloat(c.style.top) > 2000 && c.getContext('2d').getImageData(1, 1, 1, 1).data[3] === 255)
  }), 20_000).catch(() => false)
  check('滚到已用区域之外仍画出了格子', farTile === true)
  await page.mouse.click((sheetBox2?.x ?? 0) + 60, (sheetBox2?.y ?? 0) + 60)
  const farAddress = await until('名称框跟到远处的格子', async () => {
    const value = await nameBox.inputValue()
    return Number(/[0-9]+$/.exec(value)?.[0] ?? 0) > 120 ? value : null
  }, 15_000).catch(() => null)
  check('点选余量里的格子,名称框跟过去', farAddress !== null, String(farAddress ?? await nameBox.inputValue()))
  await shot('05b-sheet-scrolled.png')

  // ─── 演示 ───
  const pptx = join(bootstrap.data.workspaces[0].rootPath, 'deck.pptx')
  await copyFile(join(fixtures, 'blank.pptx'), pptx)
  const deckRow = page.locator('[role="treeitem"][title="deck.pptx"]')
  await until('文件树里出现 deck.pptx', async () => {
    if (await deckRow.count() > 0 && await deckRow.isVisible()) return true
    const filesTab = page.getByText('工作区文件', { exact: true })
    if (await filesTab.count() > 0) await filesTab.first().click()
    const refresh = page.locator('button[aria-label="刷新"]')
    if (await refresh.count() > 0 && await refresh.first().isVisible()) await refresh.first().click()
    return false
  }, 30_000)
  await deckRow.click()
  const slideFrame = await until('演示编辑器 iframe', async () => page.frames().find((f) => f.url().startsWith('ncw-plugin://ncw.slides/')) ?? null)
  // 画布的中心像素不透明(幻灯片居中放在留白里,左上角是留白)
  const opaqueCenter = (selector) => slideFrame.evaluate((sel) => {
    const canvas = document.querySelector(sel)
    if (canvas === null || canvas.width === 0) return false
    return canvas.getContext('2d').getImageData(Math.floor(canvas.width / 2), Math.floor(canvas.height / 2), 1, 1).data[3] === 255
  }, selector)
  await until('幻灯片画布出像素', () => opaqueCenter('[role="document"] canvas'), 120_000)
  check('幻灯片画布画出了引擎渲染的页面', true)
  await until('缩略图出像素', () => opaqueCenter('[role="listbox"] canvas'), 30_000)
  const slideOptions = slideFrame.locator('[role="listbox"] [role="option"]')
  check('缩略图栏有 1 张', (await slideOptions.count()) === 1, String(await slideOptions.count()))
  await shot('06-slides.png')

  await slideFrame.locator('button[aria-label="新建幻灯片"]').first().click()
  await until('缩略图变成 2 张', async () => (await slideOptions.count()) === 2, 20_000)
  await until('状态栏跟到第 2 张', () => slideFrame.evaluate(() => document.body.innerText.includes('第 2 / 2 张')), 20_000)
  check('新建幻灯片后缩略图 2 张、当前是第 2 张', true)
  await slideFrame.locator('button[aria-label="版式"]').click()
  await slideFrame.getByRole('menuitem', { name: '空白' }).click()
  await slideFrame.getByRole('radio', { name: '插入' }).click()
  await slideFrame.locator('button', { hasText: '文本框' }).click()
  // 新文本框直接进入文字编辑:画出光标。★ 再等一会儿再看 —— 修订号变化引发的查询曾经把编辑状态打断
  //   (带别的 part 的版面查询会切走用户视图),光标先出现、随即消失
  const slideCaret = () => slideFrame.evaluate(() => [...document.querySelectorAll('[role="document"] div')].some((d) => d.className.includes('bg-page-ink')))
  await until('文本框光标', slideCaret, 10_000).catch(() => null)
  await sleep(1200)
  check('插入文本框后进入文字编辑(画出光标且保持)', await slideCaret())
  await page.keyboard.type('Probe slide')
  await until('演示变脏', () => slideFrame.evaluate(() => document.body.innerText.includes('未保存')), 20_000)
  await page.keyboard.press('Escape')
  await sleep(300)
  await shot('07-slide-typed.png')
  await page.keyboard.press(`${MOD}+KeyS`)
  await until('演示已保存', () => slideFrame.evaluate(() => document.body.innerText.includes('已保存')), 30_000)
  const slide2 = await zipEntry(pptx, 'ppt/slides/slide2.xml')
  check('第 2 张幻灯片进了 pptx', slide2 !== '')
  check('空白版式:第 2 张没有占位符', slide2 !== '' && !slide2.includes('<p:ph'), slide2.match(/<p:ph[^>]*>/)?.[0] ?? '')
  check('文本框里键入的文字进了 pptx', slide2.includes('Probe slide'), [...slide2.matchAll(/<a:t>([^<]*)<\/a:t>/g)].map((m) => m[1]).join('|'))

  await slideFrame.locator('button', { hasText: '从当前页放映' }).click()
  await until('放映画出像素', () => opaqueCenter('[role="dialog"][aria-label="放映"] canvas'), 30_000)
  check('放映画出了当前页', true)
  await shot('08-slideshow.png')
  await page.keyboard.press('Escape')
  await until('放映退出', async () => (await slideFrame.locator('[role="dialog"][aria-label="放映"]').count()) === 0, 10_000)
  check('Esc 退出放映', true)
  console.log(`\n${failed ? '有断言失败' : '全部通过'} — 截图在 ${OUT}/`)
} catch (error) {
  failed = true
  console.error(`探针挂了:${error.stack ?? error}`)
  try { await app?.windows()[0]?.screenshot({ path: join(OUT, 'failure.png'), timeout: 10_000 }) } catch { /* 截不到就算了 */ }
  // ★ 窗口被隐藏 / 最小化时不出帧:画布靠 requestAnimationFrame 量可见区域,一格都画不出来,
  //   点选、截图随之超时。实测探针跑的时候有人把窗口收起来就会这样 —— 这种失败不说明代码有问题。
  try {
    const windows = await app?.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows().map((w) => ({ visible: w.isVisible(), minimized: w.isMinimized() })))
    console.error(`窗口状态 ${JSON.stringify(windows)}${windows?.some((w) => w.visible && !w.minimized) ? '' : ' —— 没有可见窗口,这次失败多半是窗口被收起,重跑'}`)
  } catch { /* 取不到就算了 */ }
  try {
    const frame = app?.windows()[0]?.frames().find((f) => f.url().startsWith('ncw-plugin://'))
    if (frame !== undefined) console.error(`iframe ${frame.url()}\n${(await frame.evaluate(() => document.documentElement.outerHTML)).slice(0, 1500)}`)
  } catch { /* 取不到就算了 */ }
  for (const line of (globalThis.__probeLogs ?? []).slice(-40)) console.error(line)
} finally {
  await app?.close().catch(() => undefined)
  if (project !== null) await rm(project, { recursive: true, force: true }).catch(() => undefined)
  await writeFile(join(OUT, 'result.txt'), failed ? 'failed\n' : 'passed\n')
  process.exit(failed ? 1 : 0)
}
