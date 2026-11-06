/** Issue #179: verify tool approval layout in real, isolated Electron windows. Run after npm run build. */
import assert from 'node:assert/strict'
import { mkdtemp, readFile, realpath, stat, writeFile } from 'node:fs/promises'
import { createServer } from 'node:http'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import electron from 'electron'
import { _electron } from 'playwright-core'

const project = resolve(import.meta.dirname, '..')
const directory = await realpath(await mkdtemp(join(tmpdir(), 'nextcowork-tool-approval-qa-')))
const content = ['verified', ...Array.from({ length: 300 }, (_, index) => `// Preview line ${index}: ${'parameter content '.repeat(6)}`)].join('\n')
const model = 'approval-layout-qa'
const screenshots = []
let requests = 0
let serverError
let application
let page

const server = createServer(async (request, response) => {
  try {
    let raw = ''
    for await (const bytes of request) raw += bytes
    const body = JSON.parse(raw)
    assert.equal(request.headers.authorization, 'Bearer qa-local-only')
    assert.equal(body.stream, true)
    const title = JSON.stringify(body.messages).includes('Generate a short conversation title from the first user message.')
    const agent = body.tools?.some(tool => tool.function?.name === 'Write') === true
    const step = agent ? requests++ : -1
    if (step > 0) assert(body.messages.some(message => message.role === 'tool' && message.tool_call_id === 'qa-write'))
    response.writeHead(200, { 'content-type': 'text/event-stream' })
    const send = (data) => response.write(`data: ${JSON.stringify(data)}\n\n`)
    send({ model, choices: [{ index: 0, delta: step === 0
      ? { tool_calls: [{ index: 0, id: 'qa-write', type: 'function', function: {
        name: 'Write', arguments: JSON.stringify({ file_path: 'approval-qa.txt', content })
      } }] }
      : { content: title ? 'Approval layout QA' : agent ? 'APPROVAL QA COMPLETE' : 'QA helper response' }, finish_reason: step === 0 ? 'tool_calls' : 'stop' }] })
    send({ choices: [], usage: { prompt_tokens: 100, completion_tokens: 30 } })
    response.end('data: [DONE]\n\n')
  } catch (error) {
    serverError = error
    response.destroy(error)
  }
})
await new Promise((done) => server.listen(0, '127.0.0.1', done))

async function invoke(channel, request) {
  const result = await page.evaluate(`window.nextcowork.invoke(${JSON.stringify(channel)}, ${JSON.stringify(request ?? null)})`)
  assert(result.ok, `${channel}: ${result.error?.message}`)
  return result.data
}

async function frames() {
  await page.evaluate('new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))')
}

async function screenshot(name) {
  const path = join(directory, name)
  await page.screenshot({ path })
  screenshots.push(path)
}

async function assertPinnedApproval(label) {
  await frames()
  const card = page.locator('[data-interaction-kind=tool_permission]')
  const body = card.getByTestId('interaction-body')
  const before = await card.evaluate((element) => {
    const viewport = element.closest('[data-testid=thread]').getBoundingClientRect()
    const detail = element.querySelector('[data-testid=interaction-body]')
    const buttons = [...element.querySelectorAll('button')].map(button => {
      const rect = button.getBoundingClientRect()
      return { top: rect.top, visible: rect.top >= viewport.top && rect.bottom <= viewport.bottom }
    })
    return { overflow: detail.scrollHeight - detail.clientHeight, scrollTop: detail.scrollTop, buttons }
  })
  assert(before.overflow > 0, `${label}: long parameters must overflow the preview`)
  assert.equal(before.scrollTop, 0, `${label}: actions must be visible before scrolling parameters`)
  assert(before.buttons.length === 4 && before.buttons.every(button => button.visible), `${label}: all approval actions must be visible`)
  await body.evaluate(element => { element.scrollTop = element.scrollHeight })
  await frames()
  assert(await body.evaluate(element => element.scrollTop > 0), `${label}: parameters must remain scrollable`)
  const tops = await card.locator('button').evaluateAll(buttons => buttons.map(button => button.getBoundingClientRect().top))
  assert(tops.every((top, index) => Math.abs(top - before.buttons[index].top) < 1), `${label}: scrolling parameters must not move approval actions`)
  await body.evaluate(element => { element.scrollTop = 0 })
}

try {
  const env = { ...process.env }
  delete env.ELECTRON_RUN_AS_NODE
  delete env.ELECTRON_NO_ATTACH_CONSOLE
  delete env.ELECTRON_RENDERER_URL
  application = await _electron.launch({ executablePath: electron, args: [project, `--user-data-dir=${join(directory, 'profile')}`], cwd: directory, env })
  page = await application.firstWindow()
  page.setDefaultTimeout(25000)
  await page.waitForFunction('typeof window.nextcowork === "object"')
  const offline = page.getByRole('button', { name: '免登录使用', exact: true })
  await page.getByTestId('composer-input').or(offline).first().waitFor()
  if (await offline.isVisible()) await offline.click()
  await page.getByTestId('composer-input').waitFor()
  const bootstrap = await invoke('app:getBootstrap')
  const workspace = bootstrap.workspaces[0]
  assert(workspace && resolve(workspace.rootPath).startsWith(`${directory}/`), 'QA workspace must be isolated')
  await invoke('provider:upsert', { id: model, name: 'Approval QA', protocol: 'openai-chat',
    baseUrl: `http://127.0.0.1:${server.address().port}/v1`, credentialRef: '', priority: 0, enabled: true })
  await invoke('provider:setCredential', { providerId: model, apiKey: 'qa-local-only' })
  await invoke('provider:setAliases', { providerId: model, models: [model] })
  const [alias] = await invoke('provider:listModels', { providerId: model })
  await invoke('model:update', { ...alias, contextWindow: 200000, maxOutputTokens: 8192,
    capabilities: { ...alias.capabilities, tools: true } })
  await invoke('workspace:update', { id: workspace.id, settings: { permissionMode: 'ask', defaultModel: model } })
  await invoke('settings:update', { defaultModel: model, locale: 'zh-CN', theme: 'dark' })
  await page.waitForFunction(`document.querySelector('[data-testid=composer-send]')?.title === ${JSON.stringify(model)}`)
  await page.getByTestId('composer-input').fill('验证长参数授权选项固定显示。')
  await page.getByTestId('composer-send').click()
  const card = page.locator('[data-interaction-kind=tool_permission]')
  await card.waitFor()
  assert.equal(await stat(join(workspace.rootPath, 'approval-qa.txt')).then(() => true).catch(() => false), false)
  await assertPinnedApproval('default window')
  await screenshot('approval-default.png')

  const bounds = await application.evaluate(({ BrowserWindow }) => {
    const window = BrowserWindow.getAllWindows()[0]
    const previous = window.getBounds()
    window.setSize(900, 600)
    return previous
  })
  await page.waitForFunction('innerWidth <= 900 && innerHeight <= 600')
  await assertPinnedApproval('900x600 window')
  await screenshot('approval-small.png')
  await card.locator('[data-row-value=allow_edited]').click()
  const editor = card.locator('textarea')
  await editor.fill('{ invalid')
  await editor.press(process.platform === 'darwin' ? 'Meta+Enter' : 'Control+Enter')
  await card.getByRole('alert').waitFor()
  await frames()
  const editGeometry = await card.locator('button, [role=alert]').evaluateAll(elements => {
    const viewport = elements[0].closest('[data-testid=thread]').getBoundingClientRect()
    return { top: viewport.top, bottom: viewport.bottom, controls: elements.map(element => {
      const rect = element.getBoundingClientRect()
      return { text: element.textContent, top: rect.top, bottom: rect.bottom }
    }) }
  })
  assert(editGeometry.controls.every(control => control.top >= editGeometry.top && control.bottom <= editGeometry.bottom),
    `edit actions and validation error must remain visible in the small window: ${JSON.stringify(editGeometry)}`)
  await screenshot('approval-edit-error.png')
  await editor.press('Escape')
  await card.locator('[data-row-value=allow_always]').waitFor()
  await assertPinnedApproval('after cancelling edits')

  await application.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setSize(size.width, size.height), bounds)
  await page.reload()
  await card.waitFor()
  await assertPinnedApproval('after renderer reload')
  await card.locator('[data-row-value=allow_once]').click()
  await page.getByText('APPROVAL QA COMPLETE', { exact: true }).waitFor()
  assert.equal(await readFile(join(workspace.rootPath, 'approval-qa.txt'), 'utf8'), content)
  assert(requests >= 2, 'the Agent must continue after the approved tool completes')
  assert.equal(serverError, undefined)
  console.log('PASS: long parameters scroll independently; actions stay visible in normal and 900x600 windows; edit validation, Escape, reload recovery, and approved execution succeed')
  console.log(JSON.stringify({ screenshots, isolatedData: directory }, null, 2))
} catch (error) {
  console.error(error)
  console.error(`QA artifacts: ${directory}`)
  if (page) {
    await page.screenshot({ path: join(directory, 'failure.png') }).catch(() => {})
    const text = await page.locator('body').innerText().catch(() => 'Renderer unavailable')
    await writeFile(join(directory, 'failure.txt'), text)
  }
  process.exitCode = 1
} finally {
  application?.process().kill('SIGTERM')
  server.closeAllConnections()
  await new Promise((done) => server.close(done))
}
