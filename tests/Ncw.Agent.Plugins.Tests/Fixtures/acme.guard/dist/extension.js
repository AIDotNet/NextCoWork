import * as ncw from 'nextcowork'

export function activate(context) {
  context.subscriptions.push(
    // 拦截器只能收紧:返回 allow 不会跳过宿主自己的权限链
    ncw.agent.registerToolInterceptor(async ({ tool, input }) => {
      if (tool === 'Bash' && typeof input.command === 'string' && /\brm\s+-rf\b/.test(input.command)) {
        return { decision: 'deny', reasonKey: 'plugin.acme.guard.denied' }
      }
      if (tool === 'Write') return { decision: 'ask' }
      if (tool === 'Read') return { decision: 'allow' }
      return undefined
    }),
    ncw.agent.registerContextProvider(async ({ sessionId }) => 'Guard is active for session ' + sessionId + '. Follow the guard-style skill.')
  )
}
