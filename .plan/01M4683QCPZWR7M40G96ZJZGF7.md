# 视频生成模块实施计划

## 1. 已确认的目标和范围

本计划只用于实施评审，当前尚未修改应用代码，也未执行构建或测试。

用户已确认以下决策：

1. 参考图片生成的设置、供应商管理、模型选择、工具桥和结果展示，但视频页必须展示全部已收录的视频供应商和视频模型，未配置的也能浏览。
2. 本轮实际接入 14 家：Google、xAI、火山方舟、阿里百炼、可灵、MiniMax、智谱、硅基流动、Runway、Luma、fal.ai、Replicate、腾讯混元原生、AWS Nova Reel。另支持自定义已实现的视频协议。
3. 支持文生视频、图生视频、首尾帧、视频编辑和延长；按具体供应商、模型、API 版本的真实能力开放，不能宣称每个模型都有所有能力。
4. 视频模型只在设置页选择，保存“模型别名 + 供应商”配对。工具不能覆盖选择，失败不跨供应商或模型回退。
5. 视频生成是可恢复的后台任务：提交后立即返回任务回执，卡片持续更新；重启后恢复查询和下载，不重新提交，不自动启动新的 Agent 对话。
6. 聊天“停止”只停止 Agent；视频生成开关只禁止新建付费任务。已提交任务继续收取成品，卡片提供独立取消操作。
7. 成品自动保存为会话附件，提供稳定的 `ncw://` 地址、播放器和下载操作。
8. 生成、查询和取消共用 `generate_video`；另外提供受文件写入权限管控的 `SaveVideo`。
9. 付费调用沿用图片方式：选好模型并开启功能后可直接调用，不增加逐次付费确认、价格估算或预算系统。
10. 编辑/延长的视频输入仅接受公网 HTTP(S) 视频 URL，不接受本地路径、`ncw://`、内联 data URL，不实现用户本地视频上传功能或共享临时图床。适配层可按官方协议，将已验证的公网 URL 转为 URL 引用或受上限约束的临时字节；不能借此读取本机视频文件。
11. 图片输入仍按各家官方能力处理：支持会话图片、工作区图片和公网图片 URL；只接受 CDN URL 且没有官方图片上传方式的接口须明确要求用户提供公网图片 URL，不能偷偷上传到第三方图床。
12. 不新增聊天输入框的视频模型快捷选择，不实现视频剪辑时间线、转码、音频合成、批量视频生成或视频理解模块。

“全部”指本轮登记的供应商目录、其经官方资料核对的视频型号、用户自定义目录和已有视频绑定的完整并集，不承诺已穷尽全球市场或自动适配任意商城模型的未知输入 schema。

## 2. 已核实的代码事实

- `src/renderer/src/settings/pages/model/ModelPage.tsx:72-78`：图片页已有独立实现；视频仍进入 `StubModalityPage`。
- `ImageModelPage.tsx:45-109`：供应商列表、配置面板、独立开关和全局模型选择可以作为视频页交互参照。目前图片左列主要是已配置供应商，不能直接复制其过滤规则后声称显示全部视频供应商。
- `src/shared/domain/provider.ts:192-215`：已有 `videoOutput` 和 `video` 模态，但没有统一的视频模型判据。现有 `isChatModelAlias` 不能可靠排除只设置了视频模态、未声明 `textOutput: false` 的自定义视频记录。
- 内置目录已收录 Sora、Seedance、混元视频和 Nova Reel，但不能把目录收录等同于客户端已有调用能力。
- `src/main/kernel/image-gen.ts`：生图桥同步判断开关和所选模型，实际执行时读取凭据；只调用选中的绑定，不跨家回退。图片网络调用和进度不能直接作为视频后台任务实现。
- `src/main/runtime.ts:2785-2825`：工具桥必须使用 `listResolvedModels()`，不能使用未解析目录覆盖的原始别名。视频装配沿用这一口径。
- `ProviderPanel.tsx:482-504、574-599`：拖拽和手工添加会整表替换供应商别名；仅依赖视频页的过滤列表会删除同供应商下的文本/图片模型。导入弹窗已有 `preserveAliases`，但 `ProviderPanel.tsx:1161` 仅区分图片和文本。
- `src/main/ipc/provider.ts:556-646`：当前整表导入实际已取消 20 条上限，不能根据过时注释重新限制完整视频目录。
- `src/shared/agent/message.ts:185-208`：工具结果只有图片媒体字段，没有视频结果和后台视频任务引用。
- `src/main/db/repo.ts:975-1064`：消息附件提交只收集图片引用；重放消息时还会删除没有进入保留集合的附件。视频产物必须参与该生命周期，不能只写一个文件路径。
- `src/shared/domain/attachment.ts:141、251-277`：通用上传上限 32 MiB，缺少视频 MIME/扩展名映射。生成视频须走独立流式存储，不扩大用户图片上传上限。
- `src/main/net/attachment-protocol.ts:192`：虽然已有 `stream` scheme 权限，但当前 `net.fetch(fileURL)` 没有转发请求 Range 头，不能据注释认定拖动视频进度条已经可用。
- `src/renderer/index.html:27`：现有 CSP 已允许 `media-src 'self' ncw:`，不需要放宽为任意网络媒体源。
- `src/main/environment/ssh/sftp.ts:9、136-153`：现有二进制写入限制 32 MiB，没有跨本地/SSH 的流式写入口；直接照搬 `SaveImage` 无法保存较大视频。
- 当前数据库迁移到版本 27；新结构追加下一条迁移，不修改任何已发布迁移。
- `package.json` 已有 React、Zod、Vitest、Node/Electron 和现有 UI 组件；没有各视频厂商 SDK、AWS SDK 或 FFmpeg。默认使用注入的 fetch、Node 内置 crypto/stream，不假定这些额外依赖存在。

## 3. 供应商事实与接口核对要求

官方文档按 2026-10-05 本次调查核对；这里只记录实际读到的事实，实施时仍需核对每个型号的完整请求/响应 schema。

| 提供商 | 本轮适配入口和关键差异 |
| --- | --- |
| Google | Veo 使用 `v1beta/models/{model}:predictLongRunning`、operation 查询和受鉴权保护的下载 URI，鉴权头为 `x-goog-api-key`。Veo 3.1 有首尾帧及受限延长；延长只接受符合原生生成来源和时效要求的视频。Gemini Omni Flash 使用另一条原生 API 路径，须单独登记 profile，不能发到现有 OpenAI 兼容聊天端点。 |
| xAI | `/v1/videos/generations`、`/edits`、`/extensions`，使用 `request_id` 查询 `/v1/videos/{id}`。已核实 `grok-imagine-video-1.5` 与经典型号的首尾帧/编辑/延长能力不同；编辑不接受自定义时长、比例和分辨率。 |
| 火山方舟 | `/api/v3/contents/generations/tasks`，文本、首尾帧和参考视频通过 content 的 type/role 表达。Seedance 2.5 有 `omni_reference_task_type`，编辑要求适配比例和保持原视频时长；旧版本不能照搬该参数。 |
| 阿里百炼 | 原生 `/api/v1/services/aigc/video-generation/video-synthesis` 和 `/api/v1/tasks/{id}`，创建必须带 `X-DashScope-Async: enable`。Wan 2.7 与旧版参数不同，模型/Key/地域必须一致，不能使用聊天 compatible-mode 地址。 |
| 可灵 | 当前官方 Quick Start 已核实使用 Bearer API Key 和 `https://api-singapore.klingai.com`，目录含 3.0、3.0 Omni、3.0 Turbo。不要按旧 AK/SK JWT 教程作为新接入默认；具体视频资源端点、型号 ID 和能力表须从当前 API Reference 核对。 |
| MiniMax | H3/H3-Max 使用 `/v2/video_generation` 和新版任务接口；Hailuo 系列是独立 v1 链路。新版返回 task.content.url，旧版成功后还需按 file_id 获取下载地址。参考视频输入不自动等价于专门的视频编辑或延长。 |
| 智谱 | `/api/paas/v4/videos/generations` 和异步结果查询。已核实 CogVideoX-3、CogVideoX-2/Flash、托管 Vidu 型号的输入与首尾帧参数不同。使用按量 API，不把 Coding Plan 的权限和 Key 当作视频额度。 |
| 硅基流动 | 须核对当前官方视频提交/查询协议及在售模型。旧视频文档入口、本次尝试的新版路径和 llms 入口均返回 404，不能据旧路径猜测并标成已接通。该核对是此适配器启用前的技术验证项。 |
| Runway | `/v1/text_to_video`、`image_to_video`、`video_to_video`，查询/取消使用 `/v1/tasks/{id}`；所有请求带 `X-Runway-Version: 2024-11-06`。官方请求体按模型区分，`gen4.5`、`gemini_omni_flash`、`seedance2` 等的时长、比例和字段不能混用；已退役型号不得作为默认种子。 |
| Luma | 当前 Ray 3.2 使用 `https://agents.lumalabs.ai/v1/generations`，type 区分 video/video_edit，帧和输出参数放在 video 中。Ray 2/Flash 仍有旧 Dream Machine API，须独立 version/profile；旧延长仅接受已完成的原生生成资源，DELETE 不直接视为取消。 |
| fal.ai | 使用 `queue.fal.run/{endpoint}`，鉴权为 `Authorization: Key ...`；状态、结果、取消资源由队列协议给出。取消为 PUT，202 仅代表请求已接收。COMPLETED 仍可能携带 error；模型 endpoint、输入/输出 schema 须逐条映射。 |
| Replicate | 创建 prediction、查询 `/v1/predictions/{id}`、POST cancel。官方模型名和版本锁定模型走不同创建形态。`openapi_schema` 决定输入映射；输出可能是字符串、数组或对象，不能一律取某个 url 字段。API 结果默认一小时后清理。 |
| 腾讯混元 | 官方 Node SDK 已核实 `vclm.tencentcloudapi.com`、版本 `2024-05-23`、`SubmitHunyuanToVideoJob` / `DescribeHunyuanToVideoJob`。这是 TC3 签名 Action API，不是混元文本 TokenHub 地址；请求/输出字段和公开售卖型号继续依据官方 SDK/API 参考核对。 |
| AWS Nova Reel | 已核实 Nova Reel 1.1 为 `amazon.nova-reel-v1:1`，使用 Bedrock 异步调用和 S3 输出，支持文本与首帧图片，720p、6 秒增量。地区和访问策略有约束，需 AWS SigV4、region、输出 bucket/prefix 及下载权限，不能把它当成普通 Bearer API。 |

补充事实：OpenAI 官方明确宣布 Sora 2 和 Videos API 于 2026-09-24 下线，无一对一替代 API。保留历史供应商/型号展示，标注“官方已停用”，不作为原生可调用默认选项；用户显式配置的第三方兼容接口独立判断，不能因此声称 OpenAI 官方仍可用。

实施前对 14 家建立版本化 profile：精确型号/endpoint ID、请求映射、响应映射、支持动作、输入媒介、参数组合、鉴权、取消语义、结果有效期、官方来源和核对日期。通过文档和协议 fixture 核对后才显示“可接入”。缺失资料、404 或需要额外客户权限的条目明确标注，不能用可安装但不工作的空适配器交差；确实受阻时交付其余已验证部分并列明阻塞，不虚报 14 家全部完成。

## 4. 领域模型、目录和配置

### 4.1 新增视频领域词汇

新增 `src/shared/domain/video-generation.ts`，集中定义：

- 视频动作、供应商适配器 ID、版本化模型 profile、按动作区分的参数约束。
- 生成能力与客户端可执行能力分别表达：文生、图生、首尾帧、编辑、延长、原生声音、来源限制、图片格式/大小/尺寸、视频来源/大小/时效，以及合法时长/比例/分辨率组合。
- `VideoJob`、对渲染层安全的 `VideoJobView`、取消结果和本地存储状态。
- 视频 MIME、扩展名/容器签名检查及独立 `MAX_GENERATED_VIDEO_BYTES`，初始 512 MiB；超限明确报错，不静默截断。二进制只用于流式 IO，不进入消息、IPC 结果或 JSON job body。

### 4.2 模型和供应商关系

新增共享 `video-provider-presets.ts` 和必要的 profile 数据模块，供应商连接数据与模型目录分开：

- 模型品牌不等于提供商：同一个 Veo、Kling、Wan 型号可由原厂和聚合商提供，均展示并保留具体绑定。
- 目录由内置视频条目、用户目录、已配置视频绑定以及经验证的提供商模型发现结果合并。没有连接或密钥时仍能浏览内置目录。
- fal/Replicate 等可展示发现的全部视频条目，但只有存在已核对 input mapping/profile 的条目可作为调用选择；未知 schema 明确显示“尚未接入/需配置映射”，不凭字段名猜测。
- 在现有 vendor 文件补充已核对的视频模型；新增厂商文件仅用于已有目录没有的实际模型品牌。更新 `model-catalog-inventory/index.ts`、`manufacturers.ts` 和相应测试，不更新无关文本/图片型号。
- 生命周期和连接可用性按“供应商 × 模型 × API 版本”判断，不能把 Sora 原生停用状态强加给所有同名自定义绑定。

在 `provider.ts` 新增 `isVideoModelAlias`，判据为 `modality === 'video' || capabilities.videoOutput === true`；聊天过滤同时排除视频生成记录，仍允许只有 videoInput 的正常文本模型。

在目录入口新增共享 `isVideoModelId`。优先查目录，再保守识别视频 ID；修正未知 Seedance/Grok video 被当前图片正则误判的重叠，不扩散无关启发式。`ipc/provider.ts`、`ipc/client-auth.ts`、模型导入弹窗使用同一判据；新建未知视频绑定可由明确的视频导入入口标记模态，不覆盖已有用户显式覆盖。

### 4.3 连接与模型配置

- `UpstreamProvider` 增加可选 `videoGeneration` 配置：adapter、video base URL、必要的地域和 AWS S3 输出设置等；聊天 `protocol` 不扩充为一串视频厂商协议。
- 同一供应商的聊天/图片地址和现有密钥不被视频配置覆盖。支持在已有连接上增加独立视频端点；腾讯签名连接等与已有文本 API Key 不同的通道使用独立 provider 记录。
- `ModelAlias` 增加可选视频绑定配置，引用具体 profile/版本，必要时保存经验证的商城字段映射。参数能力以绑定对应 profile 为准，不把原厂参数自动复制给托管版本。
- 对配置新增字段进行 IPC/导入校验；旧渲染层没有发送新字段时保留现值。用户无法经自定义 credentialRef 引用其他供应商的密钥。
- NextCoWork 托管连接继续锁定凭据发送地址；不得允许 video base URL 绕过其原有主机限制。其视频模型可常驻展示/拉取，但调用能力须有明确兼容协议契约，不能自动假定平台实现了 `/videos`。

### 4.4 全局设置和凭据

修改 `settings.ts`、`data.ts`、`config-sync-registry.ts`：

- `videoModel: ''`、`videoModelProviderId?: string`、`videoGenerationEnabled: true`。
- 模型与供应商成对合并；开关只认布尔，坏值保留当前有效设置；关闭不清空模型。
- 三项归 providers 同步分类，遵循现有导入/备份兼容规则。视频任务和本地视频文件不作为跨设备配置同步内容。
- 旧库通过默认设置合并补齐；不自动选视频模型或启用任何实际付费调用。

扩展 `credential.ts` 的类型/解析/序列化以支持腾讯和 AWS 的签名凭据：保存在现有 provider 槽的加密 JSON 中，包含明确 scheme、ID/secret 和必要的临时 token，不把密钥放进 provider JSON。

增加独立的签名凭据写入 IPC 与安全摘要；更新显式 reveal、加密导入导出、配置同步及所有需要区分 Bearer/OAuth 的消费者。签名凭据不能落进“非 API Key 就是 OAuth”的分支，也不能把整个 JSON 或私钥拼入 Bearer。已有裸 API Key 的存储和降级兼容行为保持不变；新原生签名提供商在旧版本不可调用这一限制明确说明。

可灵新接入按本次核实的 Bearer Key 实现，不擅自增加旧版 JWT 通道。

## 5. 视频设置页

新增 `src/renderer/src/settings/pages/model/VideoModelPage.tsx`，由 `ModelPage.tsx` 的 video 分支挂载。布局沿用图片页的左列供应商、右列面板、页脚开关/模型选择。

- 左列显示全部本轮目录供应商、历史停用条目、已配置自定义连接和托管 NextCoWork 连接；未配置、空模型、暂不可调用的供应商不因过滤消失。
- 右侧展示所选供应商的完整视频模型并集，包含搜索、型号 ID、功能徽章、参数限制、目录来源和配置/停用/未接入状态。可以浏览未配置供应商并显式添加连接、选择要绑定的模型。
- 模型表列出该供应商实际提供的型号，而不是按 manufacturerId 猜测其售卖列表；不画视频 Token 上下文或虚假 Token 价格。
- 页脚提供“对话视频生成”和“对话视频生成使用的模型”。说明关闭只限制新增任务，已提交任务继续收取。
- 使用现有 `ProviderModelMenu` 按供应商分组，只允许选择已启用、已配置且存在可执行 profile 的视频绑定。当前失效选择保留标签并提示重选，不回退到另一家。
- 模型选择只写设置，不发付费测试请求。改设置影响下一次提交，已经提交的任务保持创建时的模型、端点和凭据身份。
- 在视频面板中隐藏聊天 Responses、思考预算、Token 限制等不适用控件，显示真实视频协议、凭据和地域/S3 配置。沿用现有组件、头像 fallback 和中英文 i18n，不下载额外品牌资产。

模型变更新增主进程模态级写入口（例如 `provider:setModalityAliases`），保留原整表接口兼容：

- 主进程基于最新别名合并视频部分，保留其他模态的全部记录、顺序和覆盖值，避免多窗口旧快照覆盖非视频配置。
- 视频拖拽、添加、删除和导入均走该入口，不能只把过滤后的列表传给整表 setAliases。
- 更新 `ProviderPanel.tsx`、`ImportModelsDialog.tsx`、`import-models.ts` 与 renderer provider service/IPC contract，明确传 video 模态。
- 改名时同步更新被点名的视频选择；删除/停用时不解锁为其他供应商，显示失效状态，查询已提交任务仍可用。

## 6. 供应商适配层与工具桥

在 `src/main/kernel/upstream/video/` 新增纯 Node 适配模块：google、xai、ark、dashscope、kling、minimax、bigmodel、siliconflow、runway、luma、fal、replicate、tencent、aws，以及有明确契约的 openai-compatible；共享 types、鉴权签名和有限响应解析。

统一端口包括创建、查询、取结果和可选取消，返回规范化任务 ticket/status/assets。每个适配器按 profile 明确：URL 和 body 形状、状态映射、业务错误、下载鉴权和取消能力；不能使用一个猜测式 `/videos/generations` 覆盖所有供应商。

- 所有出网使用注入的 `getHost().fetch`，继续受应用代理设置影响。
- AWS SigV4、腾讯 TC3 使用 Node crypto 实现最小已验证 HTTP 请求和签名，并配官方签名向量测试；不默认引入 SDK。AWS 只实现本轮必要的异步调用/查询和 S3 成品下载，不新增素材共享 bucket 上传功能。
- Google、MiniMax、Luma 等新旧版本保持不同 profile ID，创建时冻结版本，升级后不能拿新版参数去查询旧任务。
- 参数按动作和模型组合校验。缺省优先采用短视频、较低合法分辨率的明确 profile 默认值；显式不合法的时长/比例/分辨率/帧组合立即失败，不能自动升档、裁剪、增加付费调用或忽略尾帧。
- 公网 URL 输入逐跳校验；视频源下载转发仅在官方协议需要字节时执行，限制请求总大小和 Base64 膨胀，不开放本地视频输入。仅接受原生视频的操作验证来源/时效，不能伪装为任意视频编辑。
- 下载失败、不支持的功能、业务失败和用户取消分别处理。HTTP 200、COMPLETED 或成功创建 ID 都不等于已有可播放视频。
- 取消只调用官方明确的取消操作，不借用“删除已生成媒体”冒充取消，不保证停止计费或退款。

新增 `src/main/kernel/video-gen.ts`，定义窄 `VideoGenBridge`；主进程注入选择解析、任务管理和安全素材处理。更新 `agent-session.ts`、`tool/registry.ts`、`runtime.ts` 装配：

- 选择解析复用 `selectModelBinding(listResolvedModels(), providers, videoModel, providerId)`。
- 新建任务执行前再次检查开关和所选绑定，读取凭据，不因同步工具可用性判断而隐藏“缺 Key”的可行动错误。
- 原有任务的查询/取消不依赖当前默认模型，也不受关闭生成开关影响；有本会话历史任务时保留工具管理入口。
- 选中的模型名称、支持动作和紧凑参数约束作为可信应用状态提供给模型，避免 Agent 猜时长/分辨率；不提供密钥或原始第三方说明书文本。

## 7. 可恢复的后台任务

新增 `src/main/video-generation/manager.ts` 和 `src/main/db/video-jobs.ts`；在 `db/schema.ts` 追加版本 28（实施时若已有更新则使用新的下一版本）。

数据库结构：

- `video_generation_jobs`：本地 job ID、config profile、workspace/session、origin run/call、来源消息关联、provider/model/profile/endpoint 快照、凭据槽及非明文身份标识、规范化请求元数据、上游 ID、云端状态、本地取回状态、取消状态、结果引用/有效期、错误和时间/revision。
- 唯一约束保护同一会话同一 tool call 的重复提交；索引覆盖待恢复任务、会话列表和更新时间。
- `video_generation_job_assets`：job 与会话附件的输出/封面关系。外键保证会话和消息删除后没有可继续写入的悬空任务。不得持久化视频 Base64、原始密钥或大量图片请求体。

状态分成云端结果与本地取回两条轨道：云端 submitting/unknown/queued/running/succeeded/failed/canceled；本地 waiting/downloading/ready/retryable_error/expired/paused。特别保留“云端成功，但下载失败”。

执行规则：

1. 持久化本地任务身份后才发付费创建；只有收到并存下上游 ID 才返回已提交回执。
2. 创建的超时、断线或进程退出若不能确认提交结果，标记 submission_unknown，不能自动重发。仅在供应商明确提供幂等保证时使用稳定 client token；request_id 字段本身不当作幂等证明。
3. 后台轮询按供应商建议间隔执行，处理 Retry-After、429 和临时网络错误；每个 job 最多一个 worker，限制查询/下载并发，不紧密循环。网络退避和查询恢复不得变成新建付费任务。
4. 单次 HTTP 请求和下载有可取消 deadline；不使用图片 120 秒上限判断整个视频生成失败。长时间查询问题显示暂停/可重试，保留上游身份。
5. 进度只有供应商真的返回才显示百分比；否则只显示排队/生成/下载阶段和耗时。
6. 一旦上游接受任务，worker 生命周期独立于 originating Agent 的 signal。提交前的聊天停止可阻止发请求；提交后或提交结果不明时保留任务和状态，防止云端仍运行却在 UI 消失。
7. 启动完成数据库/凭据/配置初始化后恢复同 config profile 的查询和下载；退出时停止本机 worker、关闭文件并保留记录，不默认取消云端任务。
8. 切换账户、配置 profile、凭据身份或删除连接时暂停不能安全续查的任务，不使用另一账户/供应商的 Key 查询旧任务；不在模型改选时重定向已有 job。
9. 独立取消返回请求中/已取消/已完成/不支持/失败的真实状态。202 不立即标 canceled；取消与完成竞争时仍保存已产生的视频。不支持取消时继续收取，并提示可能仍计费。
10. 删除会话/轮次时停掉对应本机 worker，按可用取消能力尽力处理孤儿云任务，完成回调不得重建已删除会话或附件；不能宣称删除等于退款。
11. 将待完成视频纳入 `runtime.ts` 现有 backgroundWork 事实，避免目标判定把“已提交”当“已经生成”；不自动唤醒新的付费 Agent run。

任务状态是唯一真源；渲染层按 job ID + revision 合并更新，不在工具易失 progress 或两个独立状态表中维护另一份后台生命周期。

## 8. 视频附件、播放、下载与工作区保存

新增 `src/main/kernel/session-videos.ts`、`src/main/session-videos.ts`，提供本会话视频读取/写入端口；必要时拆出安全流式下载模块。

- 成品在主进程流式写入附件根临时文件，边读边限制大小和计算校验和，验证容器签名后原子提交为 ULID 文件及会话附件。不经过 renderer ArrayBuffer 或 Base64 IPC。
- 配置 video/mp4、video/webm 和需要的 QuickTime MIME/扩展名；不把 HTML/JSON/空文件当视频，也不把 `.mp4` 文件名当格式证据。
- 云端结果 URL 有时效时尽快下载；保存失败保留云端成功事实、上游 ID、可恢复取回信息，重试只查询/下载，绝不重新生成。
- 结果只包含稳定 `ncw://` 引用和小型元数据。媒体字段不进文本聊天上游的二进制输入；不能向用户宣称 Agent 已看懂视频。
- 工具和 IPC 读取验证会话归属、scope、路径和 realpath；跨会话、主题附件、符号链接越界及格式不匹配拒绝。
- 下载鉴权只发给该适配器明确的 API/资源主机，跳转到不相关主机时移除秘密；匿名 CDN 下载逐跳复用 SSRF 防线。AWS 下载只访问配置的输出 bucket/prefix 下对应任务对象。

附件生命周期修改：

- 在 `db/repo.ts` 的消息附件保留集合纳入视频引用和该消息关联的 job assets；后台完成不能变成用户输入框的待发送草稿，也不能在消息重放时被删掉。
- `getHistory`/会话加载投影可按本会话 job ID 补齐最新安全媒体快照，UI 同时订阅后台更新；不覆盖仍在运行的 Agent 内存消息或错误地制造新的 tool result。
- 更新 `data.ts`、`ipc/storage.ts` 及会话复制/分支逻辑，保存/校验/迁移视频附件引用。视频复制使用流式文件 IO，不套用图片读取上限；旧图片逻辑不重命名、不重构。
- 复制/导出已完成任务时保留媒体快照，导入或新分支不自动接管原会话的未完成云端 job；显示未接管说明，不重复付费提交。完整本机备份恢复按相同账户身份恢复查询。
- 维持现有备份整体大小限制，超过限制明确反馈，不通过扩大通用文件上限暗中改变其他功能。

播放协议修改 `main/net/attachment-protocol.ts`：针对视频用受边界校验的文件流实现单 Range 响应，处理开放区间、suffix、206/416、Content-Range、Content-Length、Accept-Ranges 和 HEAD；多 range 不支持时明确拒绝。保留图片现有行为、缓存规则和路径防线，不放宽 CSP。用真实 Electron 验证播放器拖动行为，不能只测试注释或假 fetch。

跨工作区流式保存：

- 在 `kernel/host.ts` 增加可选、有 signal/maxBytes/exclusive 约束的 `writeStream` 端口；为 `kernel/node-fs.ts`、`environment/local.ts` 和 `environment/ssh/sftp.ts` 实现有背压、临时文件提交和失败清理的路径。
- 不修改现有普通 readBytes/writeBytes 的 32 MiB 默认；新视频端口显式使用视频上限。本地/SSH 保存都不整包分配大视频。
- 对覆盖写在源视频完整验证且临时文件写完后再提交，网络/磁盘错误不能留下被截断的旧目标；默认原子排他创建，`overwrite: true` 才允许替换。

## 9. 两个 Agent 工具

### `generate_video`

新增 `src/main/kernel/tool/builtin/video.ts`，Zod schema 按 action 明确区分：

- generate：prompt，可选 image（含 latest、会话图片、工作区图片或 HTTP(S) 图片）、last_frame、duration、aspect_ratio、resolution、seed。两个帧齐全才进入首尾帧模式，不支持时立即返回原因。
- edit / extend：prompt、video_url（仅公网 HTTP(S)），按具体 profile 接受合法的附加参数，不能静默变为文生视频。
- status / cancel：本会话的 job_id，不要求当前默认模型保持原选择。
- 不接受 model/provider 覆盖，不新增 n 或自动多段拼接。

按已确认的图片方式设置 `readOnly: true`、`destructive: false`、`needsNetwork: true`；其只读语义是“不写工作区”，会话产物及已提交云任务由视频设置管理。不加入 Web 搜索开关名单，也不扩大现有 Plan/子代理工具白名单。

提交成功回执明确为“已提交、尚未完成”，含实际模型、提供商、job ID 和查询方式；`ToolOutput` 增加小型视频任务引用/快照。status 成功后返回 `videos` 和稳定地址；失败/取消/取回失败都不得说成成品已生成并已保存。提示模型不要紧密轮询，不因 toolOk 的提交回执宣布已完成。

### `SaveVideo`

新增 `src/main/kernel/tool/builtin/save-video.ts`：

- 来源为本会话 ncw 视频、拥有的已完成 job 或公网视频 URL；目标为 file_path，overwrite 默认 false。
- `readOnly: false`、`destructive: true`，和 SaveImage 同等写权限；使用现有 `resolvePath` / `restrictedWrite`，Plan 文件围栏和工作区/SSH 路径语义照常生效。
- 宿主缺少视频仓或流式写入能力时不下发，不伪造可保存的承诺。
- 不偷偷转格式，不参与文本 diff/撤销日志；扩展名不匹配时明确提示实际容器。

在 builtin/index.ts 注册/导出两工具，提供 presenter、快照可用性、权限和输入验证测试。

## 10. IPC 和聊天结果卡

新增 `main/ipc/video.ts`、renderer `services/video.ts` 和 `stores/video-jobs.ts`；修改 `shared/ipc/contract.ts`、`main/ipc/index.ts` 的频道注册。

所需频道为 list/get owned jobs、cancel、retry retrieval 和通过系统保存对话框下载成品，另有 `video:jobChanged` revision 推送。状态更新不塞进已经结束的 agent:event 流。所有操作校验 session、job 和当前配置身份；列表/广播不返回凭据、绝对本机附件路径或大型输入。

新增 `VideoGenDetail.tsx` 和可单测的 `video-gen-view.ts`：

- 提交、排队、生成、下载、暂停、取消请求中、完成及失败分别呈现；显示真正的模型/供应商、参数和提示词。
- 即使 generate_video 的工具调用已结束，卡片仍根据 job 状态持续更新；工具调用耗时不冒充视频生成耗时。
- 完成后 `<video controls playsInline preload="metadata">` 播放 ncw 资源，不自动播放，不直接加载任意云端 URL。提供下载、复制地址/提示词和键盘可操作的独立取消按钮。
- 本地无法解码 HEVC 等输出时提示播放器不支持该编码，仍允许下载，不把已生成的视频错误标成生成失败，也不新增自动转码。
- “生成成功但下载失败”显示可重试取回，不提供会悄悄重新生成的重试按钮。
- 在提交回执未落盘或聊天停止竞争窗口，根据本会话 origin call/job 关系仍能找到已接受的后台任务。

`tool-presenter.ts` 增加 video shape 和两工具映射，更新 `ToolDetail.tsx`、`ToolIcon.tsx`、`ToolTimeline.tsx`、必要的 parts 展示分派和 pinned shape 规则；不修改插件公开形态协议。增加 `i18n/video-gen.ts` 并接入中英词汇，生成结果不被过程折叠隐藏。

## 11. 实施顺序

1. 先核对 14 家当前接口/型号/鉴权/能力和版本，补齐明确的 profile 与协议 fixture；特别完成硅基文档定位、可灵当前 API Reference、Google Omni 和腾讯/AWS 精确请求字段。
2. 完成共享领域类型、目录分类、连接配置、凭据和设置兼容；实现模态级绑定更新和视频设置页，不发付费测试请求。
3. 完成任务迁移/repository、后台 manager、生命周期和账户身份边界，以 fake adapter 验证幂等、暂停、恢复与取消竞争。
4. 完成 14 家适配器及版本化商城模型映射，分别接入并验证；再接入自定义有明确协议的兼容入口，不用动态猜 body 或跨家回退。
5. 完成流式视频附件、Range 播放、跨本地/SSH 的 SaveVideo、消息附件保留与复制/导出。
6. 注册两个工具，装配桥与 IPC/job store，接入结果卡和中英文文案，完成端到端非付费模拟及回归。

仅新建本模块确实需要的源文件和测试，不改无关格式/命名，不补写其他 Markdown 文档、不升级依赖、不提交或发布版本。

## 12. 验证与验收

### 自动测试

测试文件继续使用当前 Vitest 配置的 `.test.ts`，测试内核/状态纯逻辑及既有方式的 renderer SSR/DOM，不新增另一套测试运行器。

- 目录/配置：video 判据、视频输入文本模型不误伤、Seedance/Grok 不误归图片、同名多供应商、所有目录可见、不可执行 profile 不可选、空/失效/停用选择、成对合并、开关保留选择、旧库和非法字段。
- 模态更新：视频添加、拖拽、删除、导入和多窗口变化不能删除/重置/重排其他模态资产；改名更新视频选择，删除不跨家回退。
- 14 家适配器：按官方 schema 断言 URL/headers/body、型号和动作参数；创建/轮询/下载/业务错误；当前可灵 Bearer、Google 特殊 header、Runway version、fal Key 和 PUT cancel、腾讯/AWS 签名向量、MiniMax 新旧版本、商城 schema/输出形态。
- 状态安全：提交前/后停止、创建应答丢失、重复 call、重启多次恢复只查询、关闭开关、改模型、换账户/Key、provider 删除、cancel 与 completed 竞争、404/401/429、unknown 状态、结果过期、session 删除后晚回调。
- 成品存储：32 MiB 以上流式视频、512 MiB 超限、空/HTML/伪 MIME 响应、磁盘满、下载失败后只取回、跳转 SSRF、敏感头跨域泄漏、附件事务失败、草稿不混入生成视频、消息重放不删视频。
- 保存/迁移：本地与 SSH 流式写、覆盖 race、保留旧目标、中断清理、断连结果未知、Plan 围栏、跨会话拒绝、复制/分支/备份恢复和导入不重复云端任务。
- UI：初始/运行/完成/失败/暂停/不支持取消/下载失败/不可解码媒体，中英切换、播放器无 autoplay、保存对话框取消不报成功、工具结束后仍更新、产物不被折叠。
- 保持图片生成、SaveImage、聊天模型筛选、网络开关、provider import/export、附件协议与会话分支既有测试通过。

### 仓库命令

实施完成后执行已在 package.json 核实的命令，先定向测试，再整体检查：

```sh
npm run test -- src/shared/domain/__tests__ src/main/kernel/__tests__ src/main/kernel/tool/builtin/__tests__ src/main/net/__tests__ src/main/environment/__tests__ src/renderer/src/settings/pages/model/__tests__ src/renderer/src/views/chat/__tests__
npm run test
npm run typecheck
npm run lint
npm run build
```

新增 video-generation manager、video-jobs 和 adapter 测试纳入定向命令对应的实际路径；不把未来命令写成已经执行或通过。记录现有无关失败，不顺手修复超出本任务范围的代码。

### 手动验收

1. 新安装/旧库进入视频页，可以浏览所有登记提供商与型号；没有所选模型时不会新增付费生成调用。
2. 选择某一供应商的视频模型，Agent 只能使用该绑定；生成/图生/首尾帧/编辑/延长按真实能力成功或给出明确不可用原因，不静默换模型。
3. 模拟后台任务延迟完成，聊天停止、切换会话、关闭开关后仍收取；重启应用只恢复查询和下载，POST 计数不增加。
4. 卡片可播放和拖动进度条、下载/保存；历史视频不依赖已过期的云端链接；SaveVideo 在本地/SSH 正确保存大于 32 MiB 的成品，未授权覆盖被拒绝。
5. 独立取消显示真实云端结论；不支持取消时不伪报取消/停止计费。云端成功但下载失败可在不再次生成的前提下恢复。
6. 有新版本/不同协议或凭据的提供商不能覆盖既有文本/图片配置；目录展示、调用能力和在线验证状态清晰区分。
7. 真实付费 smoke test 仅使用用户提供的测试凭据，并在用户明确同意测试开销后执行；计划批准本身不等同于授权对 14 家发付费生成请求。没有真实测试的接口只报告文档/fixture 验证结果，不能称已在线验证。

## 13. 官方参考入口

- OpenAI 停用公告所在指南：https://developers.openai.com/api/docs/guides/video-generation
- Google 视频概览与 Veo：https://ai.google.dev/gemini-api/docs/video?hl=en ，https://ai.google.dev/gemini-api/docs/veo?hl=en
- xAI：https://docs.x.ai/developers/model-capabilities/video/generation ，https://docs.x.ai/developers/model-capabilities/video/extension
- 火山方舟：https://docs.volcengine.com/docs/ark/create-video-generation-task-api?lang=zh
- 百炼：https://help.aliyun.com/zh/model-studio/text-to-video-api-reference
- 可灵：https://kling.ai/document-api/guides/get-started/overview ，https://kling.ai/document-api/guides/get-started/quick-start
- MiniMax：https://platform.minimax.io/docs/llms.txt ，https://platform.minimax.io/docs/api-reference/video-generation-v2-create.md
- 智谱：https://docs.bigmodel.cn/llms.txt ，https://docs.bigmodel.cn/api-reference/模型-api/视频生成异步.md
- Runway：https://docs.dev.runwayml.com/ai-context.md ，https://docs.dev.runwayml.com/api.md
- Luma 当前：https://docs.agents.lumalabs.ai/ ；旧版：https://docs.lumalabs.ai/docs/video-generation.md
- fal：https://fal.ai/docs/documentation/model-apis/inference/queue.md ，https://fal.ai/docs/platform-apis/v1/models.md
- Replicate：https://replicate.com/docs/reference/http
- 腾讯官方 SDK：https://raw.githubusercontent.com/TencentCloud/tencentcloud-sdk-nodejs/master/src/services/vclm/v20240523/vclm_client.ts
- AWS：https://docs.aws.amazon.com/nova/latest/userguide/video-generation.html
- 硅基流动：本次旧路径及 llms 入口未能定位有效视频文档，实施前从官方控制台/当前文档目录重新确认，不能将这些 404 地址作为已验证接口依据。

---

# 实施结果(2026-10-05)

## 已完成并验证

- **共享领域**:`shared/domain/video-generation.ts`(动作/档案/两轨状态/`ToolOutputVideo` 相关约束)、`video-profiles.ts`(23 条版本化档案)、`video-provider-presets.ts`(14 家连接预设)。
- **判据与配置**:`isVideoModelAlias`、`isVideoModelId`;`UpstreamProvider.videoGeneration`、`ModelAlias.video`;`AppSettings.videoModel / videoModelProviderId / videoGenerationEnabled`;同步登记项;签名凭证(TC3 / SigV4)第三种 kind 与全部消费点。
- **目录**:新增 runway / luma / kling / fal / replicate / siliconflow 六个厂商文件;google / xai / qwen / minimax / zhipu 补视频型号;修正 `isImageModelId` 把 `seedance` / `grok-imagine-video` 误判为图片。
- **适配器**:google-veo、xai、ark、dashscope、minimax(v1+v2)、bigmodel、runway、luma(新旧两条)、fal(队列 + `route` 身份)、replicate、aws-bedrock。腾讯 TC3 签名与鉴权骨架已写完并有官方向量测试,**字段形状未核对**,故其 profile `actions: []`。
- **后台任务**:schema 第 28 条、`db/video-jobs.ts`、`video-generation/manager.ts`(`submission_unknown` 不重发、取回与生成两轨、凭据指纹、取消的三种真实结论)、启动恢复只查询不提交。
- **存储与播放**:`video-generation/download.ts`(流式 + 上限 + 容器签名 + 跨源去鉴权头)、`kernel/session-videos.ts` + `session-videos.ts`、`kernel/stream-write.ts`(本地/SSH 两条流式写)、`attachment-protocol.ts` 的**单区间 Range**(原先 Range 头根本没被转发,视频拖不动进度条)、附件与分支/复制的视频生命周期(视频走磁盘到磁盘的流式拷贝,不进内存)。
- **工具与界面**:`generate_video`(提交/查询/取消)、`SaveVideo`;`video` 形态 + `VideoGenDetail` 卡片(播放器、独立取消、重试取回、不可解码仍可保存)、`VideoModelPage` 设置页、IPC 五条 + `video:jobChanged`、中英文文案。

## 验证

- `npm run test` — 6837 passed / 29 skipped,0 failed。
- `npm run typecheck` — 三份 tsconfig 全过。
- `npm run build` — 通过。
- 变更文件 `eslint` — 0 findings(仓库其余部分是既有的生成物噪声)。
- `scripts/e2e-probe.mjs` — **本机环境本就不通过**(登录页挡住首屏),已在干净的 HEAD worktree 上复现同样失败,确认与本次改动无关。

## 未完成 / 阻塞

- **腾讯混元生视频、硅基流动**两家**可见但不可调用**(设置页标「接口待核对」):前者只核到 Action 名、endpoint 与版本,请求字段未核到正文;后者本次没能定位到当前官方视频文档(旧路径与 llms 入口均 404)。两者的连接、签名与型号目录都已就位,补齐字段形状即可启用。
- 未做真实付费联调:没有用户提供的测试凭据,按约定不代为发起付费生成请求。
