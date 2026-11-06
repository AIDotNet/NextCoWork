# NextCoWork 内存优化实施计划

## 1. 目标与已确认范围

目标：Agent 的执行、排队续跑、后台子代理回传和目标检查不依赖聊天视图；无人查看时不向 Renderer 推送正文，也不保留完整转录和图片展示资源。用户查看时再读取必要数据，后台任务持续执行，原始历史与模型上下文不丢失。

已与用户确认：

| 项目 | 本轮决定 |
| --- | --- |
| 优化范围 | 完整分阶段方案：后台控制层、可见性订阅、会话释放、数据分页、折叠正文懒加载、图片引用化、缓存与运行记录回收。 |
| 无人查看 | 切走或关闭会话、离开可见分屏、窗口隐藏或最小化。失焦不算；多窗口各自判断。 |
| 待审批或提问 | 应用内角标/提醒，不强制切换、不自动授权、不新增系统通知。所有窗口隐藏时保存待处理状态，Agent 正常等待。 |
| 图片 | 新图片使用受管附件引用；旧内联图在访问时按需、分批转换，保留原图，失败保持原样。 |
| 历史展示 | 全文查找、复制全文、导出走数据层，不要求挂载全量 DOM。 |
| 折叠与依赖 | 暂缓虚拟滚动，不新增依赖。保留现有默认展开/折叠规则，只优化已经收起的内容；不自动收起更多历史最终回复。 |

不在本轮范围：新后台守护进程、退出应用后继续运行、重启后自动恢复执行、浏览器自动休眠/切换后端、改变模型上下文或并发上限、修改权限策略、禁用 GPU/沙箱、定时强制 GC、删历史以换取内存、无关重构或依赖升级。

保留现有行为：退出应用仍中止本地运行；冷启动恢复草稿与队列但不自动发起新请求；手动停止或错误结束不自动消费剩余队列。UI 重载、切会话、隐藏窗口则不影响同一主进程内正在执行的任务。

## 2. 已验证事实与证据边界

- 历史现场采样中 Renderer footprint 约 15–16.4 GB，峰值 17.1 GB；后来主进程约 227 MB。32 GB 机器处于内存压力警告级。没有 JS 堆快照，不能把全部 footprint 判定为 JS 泄漏，也不能承诺某项优化必然省出多少 GB。
- 核心执行已在主进程。`src/main/ipc/agent.ts:206` 创建 RunHandle 并启动 driver；`:91` 已支持无人订阅时停止推送而不停止 run。
- 队列续跑、后台结果回传和目标唤醒仍依赖 Renderer：`src/renderer/src/stores/session.ts:637`、`:845`、`:1787`；`src/main/goal/runtime.ts:305` 的空闲目标唤醒最终交给拥有窗口调用发送 API。
- 订阅目前按 webContents 维护，而不是按可见聊天视图维护。`src/main/window/registry.ts:145`；`src/main/ipc/agent.ts:238` 还会把父订阅复制给子 run。
- 多个控制操作把正文订阅当作授权条件，包括交互回答、插话和权限切换。`src/main/ipc/agent.ts:294`、`:322`、`:343`。直接退订会影响正确性，必须先分离控制与正文通道。
- 会话 store 是全局 Map；关闭聊天 Tab 不释放 store，工作区清理只遍历当前 Tab。`src/renderer/src/stores/session.ts:1160`、`:1231`；`src/renderer/src/stores/tabs.ts:1041`、`:1296`。
- 普通内层 Tab 切换已卸载视图；浏览器 webview 为保持 CDP target 而留存。`src/renderer/src/shell/Dock.tsx:295–312`。本轮不重新改造所有视图生命周期，也不销毁 Agent 正在使用的浏览器宿主。
- 工具/思考过程通过 SurfaceReveal 在收起动画结束后卸载，已有机制应保留。`src/renderer/src/views/chat/RunProcessBlock.tsx:97`、`src/renderer/src/components/ui/Surface.tsx:138`。
- 长用户消息只是 220px 高度裁切，计划卡也在裁切盒内渲染完整 Markdown。`src/renderer/src/views/chat/Thread.tsx:943`、`:591`。隐藏样式不等于释放正文、DOM 或 store。
- 会话详情一次读取完整历史。`src/main/db/repo.ts:653`、`:1127`。当前编辑使用 replaceHistory；该操作删除未出现在提交列表中的旧消息。`src/renderer/src/stores/session.ts:399`、`src/main/db/repo.ts:1181`。不能把分页结果当完整历史提交。
- 截图当前是 data URL；已有 SessionImageStore 可复用，上传、校验、去重、会话归属和生命周期已有实现。`src/main/kernel/tool/builtin/browser.ts:97`、`src/main/session-images.ts:41`。
- 图片已有 lazy loading，但 compact 只缩小 CSS 尺寸。`src/renderer/src/views/chat/MessageImage.tsx:88–104`。本轮同时减少内联字符串与原尺寸预览解码。
- RunHandle 已在 message_commit 时裁剪冗余 delta，run_end 时清空监听器；这些不是需要重写的泄漏点。`src/main/kernel/run-registry.ts:139–183`、`:150`。reap() 的生产接入、总字节预算和消费者安全回收仍需补齐。

实施前重新测量真实基线。系统 footprint、JS heap、DOM、解码图片和浏览器 guest 分别观察，避免把内存从 Renderer 搬到主进程就宣称优化成功。

## 3. 目标架构与不变量

### 3.1 三类状态分离

1. **主进程运行控制**：RunRegistry、会话互斥、队列、内部插话、子代理结果、目标唤醒、待决交互、运行参数快照。没有 Renderer 也能完成调度。
2. **持久化事实与展示索引**：SQLite 保存规范消息、运行/用量/改动记录，附件保存原图；派生 UI 索引只保存小型摘要与正文引用，可重建。
3. **Renderer 展示**：轻量状态/角标、草稿编辑态、当前查看的元数据页，以及真正展开的正文和图片。不是第二套调度器，也不是整段历史的永久副本。

不允许为了生成 UI 快照，在主进程另建一份完整 messages/tools 转录。实时展示快照只覆盖未提交内容和必要状态；已提交正文回到数据库按需读取。

### 3.2 查看状态

- `visible`：视图在可见分屏，所在窗口已显示且未最小化；持有正文查看租约。
- `hidden`：会话切走、面板不可见或窗口隐藏/最小化；退正文租约，释放无其他视图引用的正文缓存，控制状态保留。
- `disposed`：Tab/工作区关闭或窗口销毁；清理租约、在途请求、展示 store 和监听器，不停止 run。
- 恢复查看：读轻量控制状态与历史页，取得当前实时快照水位，再接增量。不能全量重放长期隐藏期间的所有原始事件。

使用主进程窗口 show/hide/minimize/restore 状态和 Renderer 分屏可见性；不把 blur/focus 当作暂停条件，不仅依赖可能受窗口遮挡影响的 document.visibilityState。

### 3.3 安全与数据不变量

- 正文查看不是控制授权。新的控制登记必须维持现有会话、工作区/配置作用域与窗口来源限制，不能仅凭 runId 授权，也不能因切走界面扩大权限。
- 元数据通道不夹带全文、base64、完整工具参数/输出或密钥。审批详情由用户打开待处理项后读取，审批仍由既有 InteractionGate 判定。
- 普通排队输入、内部插话、目标消息和历史修改都经过同一会话互斥与幂等规则；一条会话不能双重启动。
- 未查看时主进程照常提交消息、记录用量、运行工具。退订仅停止正文分发，不丢原始历史，不伪造完成状态。
- 多窗口/同会话多分屏用引用计数与租约区分。一个窗口隐藏、旧 effect 清理或迟到的退订不能影响其他查看者。

## 4. 实施顺序

### 阶段 0：基线、诊断计数与回归样本

**主要文件**：`src/main/ipc/agent.ts`、`src/main/window/registry.ts`、`src/renderer/src/stores/session.ts`、现有 IPC/Renderer 测试；必要的诊断采样与测试辅助仅服务本次优化。

- 记录可见/隐藏视图数、正文租约数、正文事件数与估计字节、控制事件数、store 数、正文缓存引用/估计字节、在途请求数、RunRegistry 数及保留日志估计字节。
- 计数增量维护；不要每个 token JSON.stringify 整段转录。诊断只保留有界样本，不记录聊天正文、图片或凭证，不建设新的持久化日志系统或设置页面。
- 建立短会话、长工具会话、长用户消息、计划卡、旧内联图、多窗口、后台子代理和目标延迟检查的固定样本。
- 实施前记录并保留已有未提交修改以及测试基线；不重置工作区、不改无关代码。

**完成条件**：能区分 UI 正文开销、控制开销、JS/DOM/图片和主进程开销；后续每阶段使用相同样本比较。

### 阶段 1：把续跑与结果协调移到主进程

**主要文件**：

- 新增必要的 `src/main/session-runtime.ts`：可测试、依赖注入的会话控制器。
- `src/main/ipc/agent.ts`、`src/main/runtime.ts`、`src/main/goal/runtime.ts`。
- `src/main/state/store.ts`、`src/shared/domain/queued-input.ts`、`src/shared/agent/run-request.ts`。
- `src/shared/ipc/contract.ts`、`src/main/ipc/index.ts`、`src/renderer/src/services/agent.ts`、`src/renderer/src/services/app.ts`、`src/renderer/src/stores/session.ts`。

**行为**：

1. 主进程成为队列和协调输入的唯一写入者。Renderer 发送入队、编辑、删除、引入/取消引入、继续队列、权限/模式更新等意图，接收轻量确认；不再自行决定下一 run 何时启动。
2. 复用 queued-input.ts 的逐条 SendOptions 快照、promoted 优先、FIFO、附件引用、20 条和单条 32000 字限制；保留权限/计划模式的既有 retag 例外，不擅自更换供应商、模型或配置作用域。
3. 当前 run 的内部消息复用 RunHandle.enqueueInternal，普通输入复用现有信箱规则；不另造覆盖用户输入的信箱。
4. 子 run 完成后，主进程按 childRunId/callId 幂等取得最后助手正文并回传父会话。不能读取完整子历史只为找末条回答，也不能用 240 字摘要代替完整结果。父 run 活跃时内部插话，空闲时按受控参数启动协调 run。
5. 队列续跑必须等 driver 的 finally 完成并释放 retainSessionForRun 互斥，再启动下一 run。run_end 不是持久化收尾完成的保证；处理正常结束、错误、停止及并发操作竞态。
6. Goal 的空闲 check-in 不再让 Renderer 调 send。复用目标 ID、失效检查、退避和空闲次数限制，由控制器唤醒；UI 只更新状态。
7. 对每次输入/回传保留稳定 intentId、inputMessageId 和 runId，记录 pending/launching/consumed/paused 状态；重复回调、多窗口和重连不重复计费或提交消息。
8. 冷启动不自动执行遗留队列或回传。窗口重载与隐藏不暂停同一主进程中的调度；真正退出仍按现有 shutdown 流程停止任务。

**持久化**：

- 保留 `session.input.<sessionId>` 的现有 v1 草稿/队列载荷，不简单升版本而丢弃旧草稿。
- 新的控制账本使用独立、带版本的 KV 键，保存参数引用、意图/投递状态，不保存密钥或另一份完整转录。
- 草稿更新与队列命令分开。旧的 Renderer 整份 persistInput 不能覆盖主进程已经消费/编辑后的队列；更新 IPC 版本与调用方，持久化兼容不等于允许旧新 Renderer 混跑。
- 队列变更立即确认落盘；草稿保留既有防抖，视图释放前 flush。复用 30 天输入存档规则，不新增启动时全库扫描。

**完成条件**：没有聊天视图/正文订阅时，正常 run、队列续跑、后台结果回传和目标延迟检查均正确；单会话互斥、停止、错误暂停和重启安全行为不变。确认后关闭 Renderer 原来的调度路径，不能双控制器并行。

### 阶段 2：可见性正文订阅与展示 store 释放

**主要文件**：

- `src/main/window/registry.ts`、`src/main/ipc/agent.ts`、`src/main/ipc/index.ts`。
- `src/shared/ipc/contract.ts`、`src/shared/agent/event.ts`，以及必要的新 UI DTO 文件 `src/shared/domain/session-view.ts`。
- `src/renderer/src/services/agent.ts`、`src/renderer/src/stores/session.ts`、`src/renderer/src/stores/tabs.ts`。
- `src/renderer/src/views/chat/ChatView.tsx`、`src/renderer/src/shell/Dock.tsx`、`src/renderer/src/shell/AppShell.tsx`、窗口生命周期接线处 `src/main/index.ts`。
- 必要的新 `src/main/session-view.ts` 管理轻量实时展示快照与恢复，不保存完整历史。

**接口与行为**：

- 增加明确的正文 watch/unwatch 租约和 control snapshot/changed 接口。契约、注册表和版本表一起更新。
- 租约以窗口、视图实例、会话/run、代次标识；重复 watch/unwatch 幂等，迟到的旧响应/退订无权删除新租约。
- 初次可见发送保持“登记合法查看者在先、启动在后”的原子顺序；后台协调启动不自动订阅正文。新草稿会话不能因尚无数据库行而丢首批事件。
- 取消父订阅无条件继承全部子正文的行为。父卡只接收子任务小型状态；打开子代理只读面板才登记该子转录的正文租约。
- 控制/待处理状态独立于正文订阅：显示运行角标、后台任务状态、待审批数量；点击待处理项可读取详情并回答，不需要先加载聊天全文。
- 最小化/hide 由主进程立即停正文分发；Renderer 同时取消在途正文请求、卸载聊天重体、释放无人引用的正文缓存。失焦不触发这一过程。
- 重新显示时从 SQLite 页和实时展示快照恢复。水位/代次区分历史快照与新增量，避免重复提交、断序 attach 风暴或整段历史重克隆。
- 不直接过滤原始 AgentEventEnvelope 的部分事件再沿用“末 seq 减事件数”逻辑。若使用正文引用化的视图事件，给它独立明确的序号/范围与快照语义；核心 AgentEvent、模型和后台监听器仍保留真实事件。
- 实时展示仅保留未提交块和必要状态；message_commit 后换成持久化正文引用。不要使用完整 TranscriptState 在主进程复制全部历史。日志已裁剪时也能从当前 live 状态和数据库恢复。
- 无本地查看者时释放展示 store，不再因为 activeRunId 非空就强行留完整转录；运行事实由控制器保留。草稿、队列和未保存输入必须先确认持久化。
- 工作区清理按 store 的真实归属收集，包含此前已经关闭 Tab 的会话；同一 Renderer 中还有其他查看者时不释放共享正文。

**浏览器例外**：保留现有 webview/CDP 宿主和自动化连接；仅暂停聊天正文，不通过销毁宿主制造内存下降。窗口/视图通知也不能切断 Agent 的浏览器桥接。

**完成条件**：隐藏握手生效后正文 IPC 为零，控制提醒仍正确；重新打开会话没有重复、缺失、永远运行或丢草稿；一窗口隐藏不影响另一窗口。

### 阶段 3：数据层分页与已折叠正文的按需读取

**主要文件**：

- `src/main/db/schema.ts`、`src/main/db/repo.ts`、`src/main/state/store.ts`、`src/main/ipc/sessions.ts`。
- `src/shared/domain/session-view.ts`、`src/shared/ipc/contract.ts`、`src/main/ipc/index.ts`、`src/renderer/src/services/sessions.ts`。
- `src/renderer/src/stores/session.ts`、`src/renderer/src/views/chat/Thread.tsx`、`RunProcessBlock.tsx`、`ToolTimeline.tsx`、`ToolDetail.tsx`、`ChatView.tsx`、`todo-history.tsx`、`src/shared/agent/todo.ts`。

**先保证历史修改安全**：

- 将 UI 编辑消息、从某消息截断重跑、删除回合和删除回复改成后端按消息 ID/边界执行的事务操作。
- 复用会话互斥；验证 ID 属于该会话、角色/边界合法、历史版本匹配。编辑保留附件和非文本结构部分。
- 后端同步维护消息顺序、全文索引、附件归属、run 映射与改动审查语义。UI 分页窗口绝不能调用 replaceHistory 作为完整历史。
- 通用 getHistory 和模型 ContextAssembler 保持完整行为；replaceHistory 仍可供经过校验的完整导入/替换使用，不拿 LIMIT 改它的语义。

**读取与索引**：

- 增加 UI 元数据页、按消息/part/callId 读取正文、历史定位以及会话摘要接口，与现有 SessionDetail 分离。
- 追加一个只包含派生数据的 UI 索引迁移，不修改已发布迁移。当前 schema 最后版本为 29，实施时使用下一可用版本。
- 索引保存 messageId、sessionId、ordinal、run/回合定位、角色/内部标记、parts 类型、短预览、正文版本及引用等必要小字段，不复制全文/base64；工具详情也只保留引用和小型状态。
- 新写入在持久化边界更新索引；旧会话访问时按小批补建，处理一批后让出事件循环。不能为一页展示调用 getHistory 解析全会话；索引可丢弃重建。
- 页面使用稳定 ordinal/id 与完整回合边界，处理工具回执、内部协调消息、压缩分隔线、导入排序。初始以最近约 50 个回合作为测试起点；向前读取时不切断工具调用/回执配对。
- 正文版本与历史破坏性变更代次分别管理：追加新消息不让所有旧页失效，编辑/删除/重排则使相关页和引用失效。迟到响应不得重新填充已经隐藏或关闭的会话。
- Todo 最新状态及某次更新的上一份清单、用量、模型、目标、改动审查等使用独立摘要/索引查询，不能从“当前这一页”推断全会话事实。

**只优化现有折叠，不做虚拟滚动**：

- 保留既有过程块折叠判据、0.22s 退出卸载及滚动锚点；不再让已收起块的 props/闭包持有完整正文树和大工具输出。
- 在真正挂载的展开内容组件内取得正文引用、发起读取并持有缓存。收起动画实际卸载后释放引用；再次展开重新按需读取。复用现有 SurfaceReveal，不无关重写通用动效组件。
- 长用户消息保留现有 220px 默认预览与展开操作，预览使用有界文本而不是完整隐藏 DOM 测高度；展开才加载完整正文。键盘进入收起内容中的引用需先展开/加载，不能焦点落在不存在内容上。
- 计划卡保持 220px 外观和“打开完整计划”入口，改为读取并渲染有界 Markdown 摘录，不先读取全文再 CSS 裁切。
- 工具输入、输出、思考、生图详情、汇报正文和 diff 在已经收起时只保留状态/摘要/引用；显示中的最终回答及用户主动展开内容不被强行收起。
- 元数据、正文与流式末轮分开订阅，避免每 token 重扫历史 Todo 或生成折叠正文子树；已有 memo 和工具折叠卸载机制保留。
- 全文查找使用既有 FTS/查询路径，命中可按消息 ID 定位并加载对应页/正文；复制全文和导出在主进程基于规范数据完成，不先把全历史塞回 Renderer。

**缓存策略与边界**：

- 无引用的折叠正文、隐藏/关闭会话正文立即释放；仍有实际展开组件引用的正文保持。
- 元数据与可复用细节按字节估算限额，不只按条目数；初始正文缓存软预算 128 MiB/Renderer、元数据缓存软预算 4 MiB，基于固定样本调整。用增量计数，不反复序列化整个 store。
- 可见且已展开的正文可暂时超过软预算，不能静默截断或强制收起；不承诺本轮对整个可见长会话 DOM 有硬上限。真正的视口虚拟化是明确暂缓项，不写成本轮必交付功能。

**完成条件**：已收起过程和长内容没有隐藏的全量正文 DOM，也不持有无用全文；展开仍正确，默认展开规则不变；分页后编辑、重跑、Todo、用量、定位、复制、导出均不丢数据。

### 阶段 4：新图引用化、旧图按需转换与真实缩略图

**主要文件**：`src/main/kernel/tool/builtin/browser.ts`、`src/main/session-images.ts`、`src/main/kernel/session-images.ts`、`src/main/db/repo.ts`、`src/main/ipc/attachment.ts`、`src/main/net/attachment-protocol.ts`、`src/shared/domain/attachment.ts`、`src/renderer/src/views/chat/MessageImage.tsx`、相关图片详情/灯箱渲染处，以及 `src/main/ipc/storage.ts` 的缓存统计/清理接线。

- 新截图在发出 tool_end/提交消息之前，复用 ctx.sessionImages 与现有上传、魔数/MIME、大小、去重、owner、realpath 围栏处理，返回 ncw:// 原图引用。保存失败保留既有内联结果并给出可诊断状态，不丢图片或破坏工具调用。
- 最终生图等已有附件路径继续复用；不能把工具图片误塞到用户草稿附件栏。按现有 draft -> committed 生命周期，在对应消息提交时登记正确 message owner。
- 访问旧会话的相应页或正文时，按批转换 image part 和 tool_result.output.images 中合法内联图；只在文件原子落盘、字节校验及附件登记成功后，条件更新原消息中的引用。
- 使用 sessionId/messageId/part 定位和原正文版本做 CAS，转换中发生编辑、删除或导入更新时放弃旧更新并重读。按归属/内容哈希幂等去重，避免每次打开又存一份。
- 不修改消息 ID、角色、ordinal、原始时间、附件以外的文本/结构或语义；更新派生索引并定向失效该页，不广播要求所有会话全量 hydrate。
- 不启动全库迁移任务。限制同时转换为 1，批间让出主循环；磁盘满、超限、损坏图片、跨会话引用、缺失文件或取消时保留原样并可重试，原图不做有损压缩。
- UI compact 预览使用真实小尺寸文件；原图仍用于模型、灯箱、另存和图像工具，截图坐标说明仍使用原尺寸/CSS viewport，不能把缩略图喂给模型。
- 无新依赖：使用现有 Electron nativeImage 等可用能力，限制解码并发、输入像素/字节规模和请求排队；大图/异常图不能产生无界解码或阻塞循环批处理。
- 可采用原附件 URL 的严格枚举预览参数。先完整验证原附件路径/真实根，再读取或生成派生缩略缓存；缓存键包含原文件身份/校验和、尺寸和格式版本，不接受任意路径/尺寸。保留原资源 Range/HEAD、视频和缓存行为。
- 缩略缓存放独立受管缓存区域，不伪装成用户 draft/原始附件；独立限额与清理，原始附件随规范消息生命周期保留。首次建议固定小尺寸档，缓存总磁盘预算 256 MiB；原图丢失时回到占位而不是越界回退。
- 过程 partialImages 是易失预览，不进入永久历史；仅需要查看时保留最新必要预览，替换/结束后释放旧展示引用。Run 日志不能永久积累大体积 tool_progress 图像，恢复依赖当前展示快照而非旧预览帧。
- 验证现有备份/恢复包含原附件，JSON/全文导出不只导出本机路径。当前云配置同步主要是 providers/usage，本轮不扩展成聊天图片同步系统；不让迁移破坏既有导入来源身份和续聊脱离规则。

**完成条件**：新截图/最终图不在 Renderer 历史和事件中永久持有 base64；旧图转换可重复、可取消、不丢原图；缩略与原图用途分离；备份恢复及现有图像工具能读取原图。

### 阶段 5：运行日志与缓存安全回收

**主要文件**：`src/main/kernel/run-registry.ts`、`src/main/session-runtime.ts`、`src/main/session-view.ts`、`src/main/runtime.ts`、`src/main/ipc/agent.ts`、`src/renderer/src/stores/session.ts`。

- 保留现有 commit 后 delta 裁剪和结束后清监听器，不重复实现已经存在的回收。
- 增加日志字节与全局保留预算；初始实验值为 16 MiB/run、64 MiB 已结束 run 缓存，完成后短 TTL 约 2 分钟。这是回放/缓存预算，不是模型上下文截断。
- trim 必须与新的数据库页/实时展示快照恢复协同；显式标记历史日志已裁剪，不能返回一个看似完整但无法恢复的空快照。
- 主进程定期及完成后触发安全 reap：已结束、driver finally 已释放、无待决交互、无正文查看租约、无正在消费的子结果/父任务或必要目标上下文引用时才回收。
- 完成记录的 status、时间、错误摘要、用量、必要工具时长等在持久化/轻量索引中可恢复；已 reap 的 run 重新查看走数据库终态，不留永久 loading。
- 清理 completed controller、派生 live 快照、取消句柄、索引加载 Promise、订阅和诊断样本中的引用；已投递报告去重依据可持久化，但不把完整报告长期留在内存。
- 原始 Agent 输出、当前执行上下文和权限交互不能被缓存淘汰器截断；回收减少的是展示/回放冗余及已结束句柄。

**完成条件**：后台任务数量随时间增加时，已结束的 run、日志和 UI 缓存不再线性常驻；结果回传、目标唤醒和重连不因回收失效。

## 5. 迁移、发布与回退

1. 实施数据变更前，使用现有一致性备份机制保护 SQLite 和原始附件，不复制正在写入的单个数据库文件冒充完整备份。
2. 规范 messages、模型协议及原图字节保持兼容；UI 索引是新派生表，按访问补建，可重建，不能启动时对所有消息做全量回填。输入存档保持 v1，控制账本另行版本化。
3. 图片仅按需更新成功部分。文件写入与数据库提交之间使用原子文件、事务和幂等身份；失败保留原内联内容，未引用的临时文件由既有/派生缓存清理路径处理。
4. 对导入、replaceHistory、会话删除/归档、账户或数据目录切换、整库恢复，主动暂停迁移/读取并清理租约及缓存；重建索引后恢复，不沿用前一作用域的 Promise 或正文。
5. 分阶段启用，后台控制层通过验收之后才能启用隐藏退订与正文淘汰。主进程与 Renderer 通过 bootstrap/IPC 能力版本协同，不允许新 UI 连接不支持这些语义的旧主进程。
6. 不在运行中切换新旧调度器。回退先恢复可工作的 UI 数据/订阅路径，再撤销控制接管，杜绝双重续跑/回传；保留规范历史、附件和输入存档，不用删除数据回滚性能功能。
7. 每阶段独立小补丁与回归结果，不覆盖既有用户改动，不进行未经请求的提交、推送或发布。

## 6. 验证计划

### 6.1 单元与集成覆盖

扩展现有：

- `src/main/ipc/__tests__/agent-pump.test.ts`、`agent-run.test.ts`、`subagent-wiring.test.ts`、`subagent-queue-wiring.test.ts`。
- `src/main/kernel/__tests__/run-registry.test.ts`。
- `src/main/ipc/__tests__/attachment.test.ts`、`attachment-commit.test.ts`、`attachment-cleanup.test.ts`。
- `src/main/net/__tests__/attachment-protocol.test.ts`。
- `src/renderer/src/stores/__tests__` 与聊天现有测试。

为新控制器、租约/快照、UI 索引/ID 修改和折叠正文缓存增加必要测试；复用现有 Vitest、React DOM、jsdom 与 fake-emitter，不增加测试依赖。

必须覆盖：

- 没有 Renderer/正文订阅的正常运行、续队、子代理回传和目标 check-in。
- 正常结束但 finally 尚未完成时不提前开下一 run；错误/用户停止不自动续队。
- promoted/FIFO、各条参数快照、权限/模式 retag、附件和重复意图幂等；冷启动不自动执行。
- hide/minimize/切 Tab/关闭/重载/HMR/StrictMode、旧请求迟到、两个窗口和同会话多个可见视图。
- 正文零推送时待审批仍可发现、读取和响应；错误窗口、跨作用域 ID、已解决交互与未授权租约被拒绝。
- 正在运行的子浏览器任务在聊天隐藏后仍可自动化；不销毁 CDP guest。
- 超过旧日志 2000 条上限的长运行恢复、提交与快照并发、不连续日志、子 run 新开/关闭以及已 reap 终态。
- 折叠时不读取正文，不创建隐藏全量 Markdown/图片节点；退出动画完成后释放正文，展开重新读取，滚动锚点/键盘操作和默认展开规则不变。
- 很早的消息编辑/删除/重跑不误删页外历史；工具调用/回执、压缩线、内部消息、Todo 前一版本、用量、改动审查和全文定位均正确。
- 新/旧图片、转换中编辑/删除、磁盘满/权限错误、缺失与损坏图片、同图幂等、MIME/路径/符号链接逃逸、缩略参数边界；原图工具、导出和备份恢复。

### 6.2 仓库命令

以下是实施后执行的验证，不代表本计划阶段已运行或通过：

```sh
npm run typecheck
npm run test -- src/main/ipc/__tests__/agent-pump.test.ts src/main/ipc/__tests__/agent-run.test.ts src/main/ipc/__tests__/subagent-wiring.test.ts src/main/kernel/__tests__/run-registry.test.ts
npm run test
npm run lint
npm run e2e
```

命令均来自当前 package.json。e2e 会构建工程，执行前说明耗时；既有未提交变更造成的失败与本次新增失败分别记录，不擅自清理无关代码。

### 6.3 内存与功能验收

- 固定数据与 fake driver 分别跑可见、未查看、最小化、多窗口场景 30–60 分钟；比较 Renderer/主进程 footprint、JS heap、DOM 与解码图片，不只看 RSS 或启动时数据。
- 隐藏生效后正文消息/图片字节为零；后台继续产出持久化消息，应用内待处理状态仍更新，UI 不持续创建正文对象或重体 DOM。
- 反复开关同一长会话、子代理面板及折叠块 50 次：租约、无引用正文、DOM/图片和未完成请求回到相同场景基线，不随次数单调增长。
- 100 次截图或同等固定图片样本：已收起/未查看会话不累积 base64 和原图解码展示；新旧图原始字节与读取结果一致。
- 至少两个窗口查看同一 run，关闭/最小化一个后另一窗口继续；待审批只解决一次；恢复查看无丢消息/重复、草稿/队列不丢。
- 已完成 run/cache 数量受回收条件和预算约束；不能仅把原来的 16 GB 转移成主进程全量 UI 投影。
- 本轮不承诺固定总内存数或可见且大量展开历史的 DOM 硬上限。成功判据是无人查看/已收起时不再按执行时长和已访问历史持续累积，数据完整、控制行为正确；若残余主要来自默认展开的历史 DOM，虚拟滚动作为以后单独确认的优化，不偷偷加入本轮。

## 7. 阶段依赖与交付边界

执行顺序：基线 -> 主进程控制接管 -> 可见性正文订阅/释放 -> 安全的历史数据接口与折叠正文懒加载 -> 图片引用/按需迁移/缩略 -> 运行与缓存安全回收 -> 完整压力与功能验收。

每阶段先完成对应回归再启用下一阶段。不能跳过控制迁移而直接停 UI 事件泵，不能跳过按 ID 的历史修改而直接分页，不能只隐藏 DOM 就宣称已释放 store，也不能把默认折叠扩展为自动收起所有旧回复。

本文件是这项优化的唯一实施计划；当前只形成计划，不修改应用代码，不执行迁移、构建或性能压测。
