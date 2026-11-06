/**
 * 需求:「+」菜单一键启动 Codex CLI —— 在当前工作区根目录起一个终端,
 * 把用户配置的接入信息带进 codex。与 claude-code 插件同构,差异只有两点:
 * baseURL / key 走 OPENAI_* 环境变量;模型经 `-m` 参数传入(Codex CLI 不读
 * 模型环境变量)。不写 ~/.codex 配置文件。
 */
import * as ncw from 'nextcowork'

function str(value: unknown): string {
  return typeof value === 'string' ? value.trim() : ''
}

export function activate(context: ncw.ExtensionContext): void {
  context.subscriptions.push(
    ncw.commands.registerCommand('acme.codex.launch', async (args: unknown) => {
      const raw = args !== null && typeof args === 'object' ? (args as Record<string, unknown>) : {}
      const workspaceId = str(raw.workspaceId)
      if (workspaceId === '') throw new Error('terminal launch requires a workspace context')

      const cfg = (await ncw.configuration.get()) as Record<string, unknown>
      const env: Record<string, string> = {}
      const baseUrl = str(cfg.baseUrl)
      const apiKey = str(cfg.apiKey)
      const model = str(cfg.model)
      if (baseUrl !== '') env.OPENAI_BASE_URL = baseUrl
      if (apiKey !== '') env.OPENAI_API_KEY = apiKey

      const result = await ncw.tabs.openTerminal({
        workspaceId,
        command: 'codex',
        ...(model !== '' ? { args: ['-m', model] } : {}),
        ...(Object.keys(env).length > 0 ? { env } : {}),
        title: '%cmd.launch%'
      })
      if (result.opened !== true) {
        // 'remote-unsupported' 是宿主渲染层认的标记,会换成一句专门的人话提示。
        throw new Error(result.reason === 'remote' ? 'remote-unsupported' : 'terminal launch was declined')
      }
    })
  )
}
