/** 需求：Agent HTTP 网关的真实状态、远程监听和密钥操作统一提供中英文文案。 */
type Params = Record<string, string | number>

export const gatewayZh = {
  'gateway.enableHint': '通过 Agent 专用 HTTP 接口调用会话、工具和权限审批。默认仅本机，开启远程连接后允许局域网客户端访问。',
  'gateway.remote': '允许远程连接',
  'gateway.remoteHint': '监听所有 IPv4 网卡。客户端使用下方局域网地址和 Bearer 密钥；公网访问应通过 HTTPS 反向代理。',
  'gateway.status': '监听状态',
  'gateway.listening': '正在监听',
  'gateway.disabled': '已关闭',
  'gateway.starting': '正在应用设置…',
  'gateway.addressHint': '显示实际地址；远程客户端请使用局域网地址或反向代理地址。',
  'gateway.copyKey': '复制 API 密钥',
  'gateway.copied': 'API 密钥已复制',
  'gateway.keyHint': '所有请求均需 Authorization: Bearer <key>，密钥保存在应用凭证存储中。',
  'gateway.interfacesHint': '创建会话、发送消息、SSE 事件、取消执行和交互应答。',
  'gateway.healthEmpty': '尚无上游请求记录',
  'gateway.resetHealth': '重置健康状态',
  'gateway.healthy': '正常',
  'gateway.unhealthy': '异常',
  'gateway.error': ({ error }: Params) => `网关操作失败：${error}`
}

export const gatewayEn = {
  'gateway.enableHint': 'Call conversations, tools and approvals through the Agent HTTP API. Local access is the default; enable remote connections for LAN clients.',
  'gateway.remote': 'Allow remote connections',
  'gateway.remoteHint': 'Listen on all IPv4 interfaces. Clients use a LAN address below and a Bearer key; use an HTTPS reverse proxy for public access.',
  'gateway.status': 'Listener status',
  'gateway.listening': 'Listening',
  'gateway.disabled': 'Disabled',
  'gateway.starting': 'Applying settings…',
  'gateway.addressHint': 'Actual listening addresses. Remote clients use a LAN address or reverse proxy URL.',
  'gateway.copyKey': 'Copy API key',
  'gateway.copied': 'API key copied',
  'gateway.keyHint': 'Every request requires Authorization: Bearer <key>. The key is kept in the application credential store.',
  'gateway.interfacesHint': 'Create sessions, send messages, stream SSE events, abort runs and answer interactions.',
  'gateway.healthEmpty': 'No upstream requests recorded yet',
  'gateway.resetHealth': 'Reset health',
  'gateway.healthy': 'Healthy',
  'gateway.unhealthy': 'Unhealthy',
  'gateway.error': ({ error }: Params) => `Gateway operation failed: ${error}`
}
