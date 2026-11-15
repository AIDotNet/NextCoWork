// 按 esbuild 的输出风格:单文件 ESM,`nextcowork` 是 external。逻辑移植自 2.x 的 create-nextcowork-plugin 模板,后面几条命令演示受权限门管辖的宿主 API。
import * as ncw from 'nextcowork'

async function remember(key, value) {
  await ncw.storage.global.set(key, typeof value === 'string' ? value : JSON.stringify(value))
}

// 失败也留痕:插件看到的是稳定的 Error.code(permission_denied / rejected / invalid_argument ...),不是一句泛泛的「宿主函数失败」
function guarded(name, work) {
  return async (args) => {
    try {
      await remember('last.' + name, await work(args ?? {}))
      await remember('last.' + name + '.error', '')
    } catch (error) {
      await remember('last.' + name + '.error', String(error.code) + ': ' + error.message)
    }
  }
}

export function activate(context) {
  let count = 0

  context.subscriptions.push(
    ncw.commands.registerCommand('acme.hello.hello', async (args) => {
      count += 1
      const config = await ncw.configuration.get()
      await remember('greetings', String(count))
      await remember('workspaceId', (args && args.workspaceId) || '')
      await remember('greeting', config.loud ? String(config.greeting).toUpperCase() : config.greeting)
      try {
        await ncw.window.showMessage('info', 'plugin.acme.hello.greeted', { count })
        await remember('notified', 'yes')
      } catch (error) {
        await remember('notified', error.code)
      }
    }),
    ncw.commands.registerCommand('acme.hello.read', guarded('read', async ({ path }) => (await ncw.workspace.fs.readFile(path)).data)),
    ncw.commands.registerCommand('acme.hello.write', guarded('write', async ({ path, data }) => (await ncw.workspace.fs.writeFile(path, data)).revision > 0)),
    ncw.commands.registerCommand('acme.hello.exec', guarded('exec', async ({ command, args }) => {
      const result = await ncw.process.exec(command, args ?? [])
      return { code: result.code, stdout: result.stdout.trim() }
    })),
    ncw.commands.registerCommand('acme.hello.fetch', guarded('fetch', async ({ url }) => {
      const response = await ncw.net.fetch(url)
      return { status: response.status, body: response.body }
    })),
    ncw.commands.registerCommand('acme.hello.ask', guarded('ask', async () => ncw.window.showConfirm({ titleKey: 'plugin.acme.hello.cmd.ask', danger: false }))),
    ncw.commands.registerCommand('acme.hello.perm', guarded('perm', async ({ permissions }) => ncw.permissions.request(permissions, 'plugin.acme.hello.cmd.perm'))),
    ncw.commands.registerCommand('acme.hello.config', guarded('config', async () => ncw.configuration.get()))
  )
}

export function deactivate() {
  // context.subscriptions 里的东西宿主会统一 dispose
}
