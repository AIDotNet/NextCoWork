/**
 * 需求：外部客户端可在网关自己的 /docs 填写密钥并测试 Agent API。
 * 页面、资源、契约公开但不包含账户数据或密钥；业务接口仍独立鉴权。
 * 静态资源以 raw 导入构建产物，避免安装包排除 docs/ 或离线时页面空白。
 */
import type { ServerResponse } from 'node:http'
import bundle from 'swagger-ui-dist/swagger-ui-bundle.js?raw'
import stylesheet from 'swagger-ui-dist/swagger-ui.css?raw'
import specification from '../../../docs/agent-http-api.openapi.json?raw'

const initializer = `window.onload = function () {
  window.ui = SwaggerUIBundle({
    url: '/docs/openapi.json',
    dom_id: '#swagger-ui',
    deepLinking: true,
    persistAuthorization: false,
    validatorUrl: null,
    supportedSubmitMethods: ['get', 'post'],
    presets: [SwaggerUIBundle.presets.apis]
  })
}`

const html = `<!DOCTYPE html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>NextCoWork Agent API</title><link rel="stylesheet" href="/docs/swagger-ui.css"></head>
<body><div id="swagger-ui"></div><script src="/docs/swagger-ui-bundle.js"></script><script src="/docs/init.js"></script></body></html>`

// 需求：只暴露固定资源，不把 URL 当文件路径；避免静态服务越界读取主机文件。
const assets: Record<string, { type: string; body: string }> = {
  '/docs': { type: 'text/html', body: html },
  '/docs/': { type: 'text/html', body: html },
  '/docs/swagger-ui.css': { type: 'text/css', body: stylesheet },
  '/docs/swagger-ui-bundle.js': { type: 'text/javascript', body: bundle },
  '/docs/init.js': { type: 'text/javascript', body: initializer },
  '/docs/openapi.json': { type: 'application/json', body: specification }
}

/** 需求：先加载文档再输入 Bearer；只豁免这些只读资源，不豁免任何业务路由。 */
export function serveDocs(method: string | undefined, pathname: string, response: ServerResponse): boolean {
  const asset = Object.hasOwn(assets, pathname) ? assets[pathname] : undefined
  if ((method !== 'GET' && method !== 'HEAD') || asset === undefined) return false
  response.writeHead(200, {
    'Content-Type': `${asset.type}; charset=utf-8`,
    'X-Content-Type-Options': 'nosniff',
    'Cache-Control': 'no-store',
    'Referrer-Policy': 'no-referrer',
    'Content-Security-Policy': "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'"
  })
  response.end(method === 'HEAD' ? undefined : asset.body)
  return true
}
