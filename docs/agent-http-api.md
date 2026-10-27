# Agent HTTP API v1

在「设置 → 连接 → 开放网关」启用，复制实际监听地址和 API 密钥。应用必须保持运行。
默认仅监听 `127.0.0.1:19836`；开启「允许远程连接」后绑定 `0.0.0.0`，界面列出可访问的局域网 IPv4 地址。
端口被占用时使用动态端口，以界面实际地址为准。远程服务使用 `http://<电脑局域网IP>:<实际端口>`，并确保该端口可达。
跨公网访问时由 HTTPS 反向代理提供域名和 TLS，关闭代理的 SSE 缓冲；本服务本身提供 HTTP。
所有业务请求都要求 `Authorization: Bearer <key>`，JSON 请求要求 `Content-Type: application/json`。
浏览器只接受与网关 Host 同源的 Origin；其他网站和 `null` Origin 被拒绝。密钥代表操作当前应用工作区的权限，请只交给可信客户端。

机器可读契约为 [agent-http-api.openapi.json](agent-http-api.openapi.json)，可导入 Postman。接口变更时同时更新该契约、本文和 HTTP 回归测试；不维护第二份手工集合。

本接口执行完整 Agent（包括工具、Skills、MCP、权限审批和持久化），不是 OpenAI 协议代理。

## Swagger UI 页面测试

打开 `http://<实际监听地址>:<实际端口>/docs`，本机例如 `http://127.0.0.1:19836/docs`。
页面和 `/docs/openapi.json` 无需鉴权即可加载，不包含账户数据；静态资源随应用打包，无需 CDN。
点击 **Authorize**，只粘贴 API 密钥（不加 `Bearer ` 前缀），然后用各接口的 **Try it out → Execute**：

1. `GET /v1/workspaces` 获取工作区 id。
2. `POST /v1/sessions` 填写 workspaceId，保存返回的会话 id。
3. `POST /v1/runs` 填写 sessionId 和 text，保存返回的 runId。
4. `GET /v1/runs/{runId}` 查询运行快照；`GET /v1/sessions/{sessionId}` 查询持久化回复。
5. `POST /v1/runs/{runId}/abort` 取消执行；如果在等待审批，使用 interactions 和 response 接口回答。

密钥仅保留在当前页面内存，刷新后需重新填写。API 地址自动采用页面所在地址，适应动态端口和局域网访问。
Swagger UI 不提供实时聊天气泡；SSE 接口可能等到运行结束才显示内容，实时状态请使用快照查询或专用 SSE 客户端。
使用 HTTPS 反向代理时保留原始 Host，同时代理 `/docs` 资源与 `/v1` 路由；不依赖 X-Forwarded-* 头。

| 方法 | 路径 | 行为 |
|---|---|---|
| GET | `/v1/status` | 查询监听和上游健康状态 |
| GET | `/v1/workspaces` | 列出当前账户可见工作区 |
| GET | `/v1/models` | 列出可调用模型别名及供应商 ID |
| GET | `/v1/sessions?workspaceId=...` | 列出工作区会话 |
| POST | `/v1/sessions` | 创建会话，body 为 `{ "workspaceId": "...", "title": "可选标题" }` |
| GET | `/v1/sessions/{sessionId}` | 获取持久化会话及完整消息 |
| POST | `/v1/runs` | 发送消息，返回 202 和 `{ "runId", "sessionId", "eventsUrl" }` |
| GET | `/v1/runs/{runId}` | 获取当前 run 快照 |
| GET | `/v1/runs/{runId}/events?sinceSeq=0` | SSE：先发 snapshot，再发增量 event，run_end 后关闭 |
| POST | `/v1/runs/{runId}/abort` | 停止运行及其子代理，返回 204 |
| GET | `/v1/runs/{runId}/interactions` | 查询该运行及子运行的待决交互 |
| POST | `/v1/interactions/{interactionId}/response` | 回答交互，返回 204 |

普通列表响应为 `{ "data": [...] }`。错误为 `{ "error": { "code", "message" } }`。
HTTP 400 表示输入错误，401 密钥错误，403 Origin/Host 不允许，404 资源不存在，409 会话忙或交互已失效，413 请求超过 1 MiB，415 类型错误，503 网关/客户端不可用。

## 发消息

```json
{
  "sessionId": "创建会话返回的 id",
  "text": "请说明这个工程的结构",
  "model": "模型别名（可选）",
  "modelProviderId": "供应商 ID（可选）",
  "mode": "code",
  "thinking": "auto",
  "permissionMode": "ask",
  "webSearch": true
}
```

只有 sessionId、text 必填。模型优先使用请求、会话、工作区默认值；其余选项继承会话/工作区。
permissionMode 可为 ask、auto、full，不会因 HTTP 调用自动放宽。相同会话同一时间只能运行一次，忙时返回 409，不自动重试或排队。
新一次 POST /v1/runs 有副作用；请求超时时请先查询会话，不要盲目重发。

## SSE 与断线恢复

事件名 `snapshot` 的 data 是共享的 RunSnapshot，含 seq、events、status、pendingInteractions 和 children。
后续事件名 `event` 的 data 是 `{ "seq": 123, "event": AgentEvent }`；SSE id 同为 seq。
重连传 sinceSeq 或 Last-Event-ID。快照中的历史 delta 已可能被 message_commit 合并，序号不要求连续；应用后将本地序号设为 snapshot.seq。
运行结束后仍会发送一次 snapshot，再关闭连接。run_end 的 status 是 done、error 或 aborted；HTTP 202 只表示启动成功。
心跳是 SSE 注释。断开 SSE 不取消运行，显式调用 abort 才取消；慢客户端超过缓冲上限会断开，可重新获取快照。
run 快照仅在当前应用进程保留，应用重启后从 GET /v1/sessions/{id} 读取持久化消息。
父 run 的 SSE 包含子代理起止事件；用其中的 childRunId 订阅子 run 事件或查询其快照。

## 回答权限审批和提问

遇到 interaction_request，从事件或 interactions 接口取得 id 和 kind，向 response 接口发送对应 body：

```json
{ "kind": "tool_permission", "decision": { "kind": "allow_once" } }
```

拒绝可用 `{ "kind": "deny", "reason": "原因" }`；持久批准用 `{ "kind": "allow_always", "scope": "workspace" }`。
Agent 提问用 `{ "kind": "ask_user", "answers": [["第一题答案"], ["第二题答案"]] }`，关闭提问用 answers: null。
计划审批用 `{ "kind": "plan_approval", "action": "approve_current" }`，或 approve_new_session / request_revision / reject；request_revision 必须带 feedback。
目标提议用 `{ "kind": "goal_proposal", "approved": true }`。交互仍按内核的题数、选项和状态校验。

## Python 示例

```python
import json
import requests

base = "http://127.0.0.1:19836"  # 使用界面显示的实际地址
client = requests.Session()
client.headers["Authorization"] = "Bearer YOUR_KEY"

def checked(response):
    response.raise_for_status()
    return response.json()

workspace = checked(client.get(base + "/v1/workspaces"))["data"][0]
session = checked(client.post(base + "/v1/sessions", json={"workspaceId": workspace["id"]}))
run = checked(client.post(base + "/v1/runs", json={"sessionId": session["id"], "text": "你好"}))
with client.get(base + run["eventsUrl"], stream=True) as response:
    response.raise_for_status()
    response.encoding = "utf-8"
    for line in response.iter_lines(decode_unicode=True):
        if line.startswith("data: "):
            print(json.loads(line[6:]))  # 收到 interaction_request 时需通过另一请求应答
```

关闭网关会关闭 HTTP 连接，已启动的 Agent 仍由应用管理，可从桌面查看/停止。
