/**
 * 需求:「+」菜单一键启动 Claude Code —— 在当前工作区根目录起一个终端,
 * 把用户在插件详情页配置的接入信息(baseURL / API Key / 模型)以**环境变量**
 * 带进 claude CLI,不写任何配置文件。
 *
 * ★ 插件本身不做任何决定以外的事:工作区上下文(workspaceId)由宿主菜单
 * 附带,env 的注入与 pty 的启动全在宿主(`tabs.openTerminal`)。这里只做
 * 「读配置 → 组 env → 发起」。
 * ★ ANTHROPIC_AUTH_TOKEN 是 Claude Code 认中转站 key 的标准变量;
 * ANTHROPIC_MODEL 与 CLI 的 --model 等价,走 env 免去命令行转义。
 */
import * as ncw from 'nextcowork'

function str(value: unknown): string {
  return typeof value === 'string' ? value.trim() : ''
}

export function activate(context: ncw.ExtensionContext): void {
  context.subscriptions.push(
    ncw.commands.registerCommand('acme.claude-code.launch', async (args: unknown) => {
      const raw = args !== null && typeof args === 'object' ? (args as Record<string, unknown>) : {}
      const workspaceId = str(raw.workspaceId)
      if (workspaceId === '') throw new Error('terminal launch requires a workspace context')

      const cfg = (await ncw.configuration.get()) as Record<string, unknown>
      const env: Record<string, string> = {}
      const baseUrl = str(cfg.baseUrl)
      const apiKey = str(cfg.apiKey)
      const model = str(cfg.model)
      // 全部可空:没配就是裸启动,宿主与 CLI 自己的默认值说了算。
      if (baseUrl !== '') env.ANTHROPIC_BASE_URL = baseUrl
      if (apiKey !== '') env.ANTHROPIC_AUTH_TOKEN = apiKey
      if (model !== '') env.ANTHROPIC_MODEL = model

      const result = await ncw.tabs.openTerminal({
        workspaceId,
        command: 'claude',
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
