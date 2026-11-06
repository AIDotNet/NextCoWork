# 原生 Office / PDF 引擎包与完整编辑器实施计划

## 1. 本次确认的交付目标

本文件是本轮后续实施的唯一计划；旧计划或先前聊天中的建议若与本节冲突，以本节为准。

- **正式交付同时包含 Word、Excel、PowerPoint、PDF 的基础和高级功能，以及受控宏执行。** 基础闭环只是内部研发里程碑，不单独宣称正式交付完成，不用隐藏未实现按钮来降低验收范围。
- 首批正式验收平台：**macOS arm64、macOS x64、Windows x64**。Linux 保留协议与目录适配能力，但不作为本轮发行目标；Windows arm64 不在本轮目标中。
- UI 采用 WPS 风格的信息架构：Ribbon、文档画布、导航/属性侧栏、底部状态栏、常用快捷键。使用自有品牌与合法素材，不复制 WPS 商标或专有资源；不承诺任意文件、所有功能逐像素等价。
- 核心提供通用文档平台；引擎是**独立可选插件**。插件发行包内携带对应平台运行库，不塞入 NextCoWork 主安装包，不要求用户另装 LibreOffice，也不在插件首次运行时临时下载未知二进制。
- 完整规划市场一键安装、平台包选择、依赖安装、升级和回滚；**市场服务端作为外部交付依赖**。客户端接口模拟通过不等于市场闭环已完成。
- 只采用**开源免费**依赖，允许评估 GPL/AGPL 引擎。允许评估不等于允许擅自改变主工程 Apache-2.0 许可证；实际分发义务必须审查，商业 SDK、购买或签约不在本计划内。
- **保持现有原生执行确认**：继续 `process`、`allowedCommands`、实际执行审批。正常开文档需要新启动 helper 时可能出现确认框；不新增 `native.execute`，不把安装同意替代执行批准。
- 宏由 UI 或 Agent 显式请求，经受控入口和现有权限链执行。禁止文件打开、预览、恢复、导出时自动运行宏；不承诺任意 VBA、COM、ActiveX 跨平台兼容。
- 本轮不扩大到旧二进制 Office 格式 `.doc/.xls/.ppt`、ODF、SSH 文件直接编辑、云端协同或 OCR。现有 OOXML/宏变体/PDF 格式白名单保持；以后扩大范围需要另行确认。

### 必须交付的功能矩阵

| 产品 | 基础功能 | 本轮必须验收的高级功能 |
|---|---|---|
| Writer | DOCX 打开/预览/编辑/保存、搜索、文字/段落/样式、表格图片、缩放、撤销重做 | 修订/批注、目录/脚注、页眉页脚、分节分页、复杂表格与图片定位 |
| Sheets | XLSX 工作表、单元格范围、值/公式/格式、选择编辑、复制粘贴、保存 | 公式重算、图表、筛选排序、冻结、条件格式、数据透视、打印区域 |
| Slides | PPTX 页导航、文字/图片/对象、布局、编辑保存 | 母版/主题、分组排列、备注、动画/切换、实际放映 |
| PDF | 分页/缩放、文本搜索选择复制、保存与导出 | 原有文字/图片编辑、页面重排/合并拆分、可编辑批注、表单、密码学数字签名 |
| Macros | DOCM/XLSM/PPTM 打开与宏项目往返保留 | 枚举受支持入口、UI/Agent 显式执行、结果/取消/超时/副作用说明 |

每一项必须落到可执行命令与真实文件测试；“接口返回成功”“按钮存在”“fake engine 测试通过”都不能替代格式能力验收。

## 2. 本轮只读调查确认的事实

下表描述当前代码，不把历史实现报告当作现状，也不把代码注释中的量测当作本轮实测。

| 已确认事实 | 代码依据 | 计划中的处理 |
|---|---|---|
| 已有会话管理、渲染、私有产物保存/导出、修订与作用域基础 | `src/main/document-engine/{manager,file-store,native-host,provider-registry}.ts` | 复用并扩展，不另建一套模型/保存状态 |
| helper v1 附件无 id，要求 binary 先于且紧邻回执；握手/故障路径已有测试 | `src/main/document-engine/native-host.ts:12-39` | 保留 v1；新交互引擎使用显式版本化 v2，不偷偷改变旧解析 |
| 文档操作仅有有限基础联合，query 仅 outline/text/cells | `src/shared/document-engine/protocol.ts:137`、`:371` | 增加真实交互/查询/高级命令 schema，禁止任意 UNO/脚本调用 |
| 插件 RPC 目前只接 open/apply/save/export/getState/getOperation/close | `src/main/plugin/document-rpc.ts:175-181` | 补 query、能力、订阅、恢复、撤销与宏等独立受控方法 |
| 没有真实引擎插件目录与原生构建配置 | `plugins/**`、CMake/solution 文件搜索未找到对应实现 | 新建实际发行插件与原生构建入口 |
| renderer 未接文档引擎 IPC；custom editor 固定取 `views[0]` | `src/renderer/src/views/plugins/CustomEditorView.tsx:85`、`src/shared/ipc/contract.ts` | 落实已声明的 viewId/documentEngine，新增独立 session frame |
| 旧 `ncw:doc` 对非文本/非图片只发送空串 | `src/renderer/src/shell/PluginViewFrame.tsx:143-161` | Office/PDF 不走整文件文本/图片通道，不能把空串当新文档 |
| 原生包规格、入口摘要、payload 索引、链接恢复已有实现 | `src/shared/plugin/native-component.ts`、`src/main/plugin/native-installer.ts` | 在既有规格上打包并补发行验证，不放宽普通包限制 |
| 原生包当前硬限额是压缩 2 GiB、展开 6 GiB、100000 条、48 层 | `src/shared/plugin/native-component.ts:53-58` | 用真实三个目标的发行包重新测量；超限阻断，不让包自报提高预算 |
| 运行前目前主要复核入口摘要，不能据此断言全部动态库运行期未被修改 | `src/main/plugin/native-installer.ts:272-297` | 增加 payload/平台签名与装载路径完整性验证策略 |
| helper 已有 process 授权与实际执行确认 | `src/main/ipc/plugin-engines.ts`、`src/main/plugin/manager.ts:413-418` | 保持既有行为，拒绝或撤权后不能用缓存 provider 绕过 |
| 主进程已有退出/账户切换检查，尚无自动崩溃快照恢复 | `src/main/document-engine/manager.ts:20-22`、`src/main/document-engine/quit-guard.ts` | 接恢复索引与逐文档处理，不把 reloadFromDisk 叫恢复 |
| 市场仍按一个版本下载单包、20 MiB 上限，整包进内存 | `src/main/ipc/plugin-market.ts:34`、`:219-262`、`:336-364` | 平台 artifact 协议与有界流式下载必须一并改 |
| 市场共享类型无平台 artifact / 原生包 / 依赖闭包 | `src/shared/plugin/market.ts` | 新增可选 DTO，保持普通插件兼容 |
| CLI 不收集 native/licenses，依赖 POSIX rm/sh/cp/zip，整包读入内存 | `packages/plugin-cli/index.mjs:161-208` | 增加原生包路径、跨平台流式打包上传与源代码发行物 |
| CLI 当前只声明 esbuild peer dependency | `packages/plugin-cli/package.json` | 若引入流式 ZIP 库，必须显式新增依赖、锁版本、审核许可证 |
| 当前插件 API 为 0.3.2 | `src/shared/plugin/api-version.ts:40` | 保持旧 API；新增能力完成后递增 patch，并同步 SDK/脚手架 |
| Vitest 只收 `src/**/*.test.ts` | `vitest.config.ts:15` | 不能把插件目录中的测试或 `.test.tsx` 当作已被默认测试覆盖 |

### 上游资料已核实的边界

- LibreOfficeKit README 描述 tiled rendering/editing、twips 坐标、游标/选区/tile invalidation 回调；相关 API 需 `LOK_USE_UNSTABLE_API`。参考：<https://raw.githubusercontent.com/LibreOffice/core/master/libreofficekit/README.md>。
- 当前 LOK C 头文件有 `paintTile/paintPartTile`、多 view、键鼠输入、剪贴板、`startURP`、放映接口等；**存在接口不证明所选发行构建可用**。没有据此验证过可直接返回完整活动文档树的 API。参考：<https://raw.githubusercontent.com/LibreOffice/core/master/include/LibreOfficeKit/LibreOfficeKit.h>。
- LibreOffice 官方明确 Office/VBA 对象模型差异，仅部分脚本兼容。参考：<https://help.libreoffice.org/latest/en-US/text/shared/guide/ms_user.html>。
- MuPDF 官方文档提供 AGPL 与商业授权两条路，AGPL 有对应源码等义务。此处仅选开源评估路线，不保证独立进程可使其他代码免于义务。参考：<https://mupdf.readthedocs.io/en/latest/license.html>。

## 3. 唯一架构与状态归属

```text
文件树 / 聊天文件链接 / Agent 工具
                 |
         既有 custom Tab 路由
                 |
  Writer / Sheets / Slides / PDF 前端插件
  Ribbon、画布、选区、导航、属性、工具入口
       |                         |
  文件绑定 view 通道      nextcowork.documents
       +-------------+-----------+
                     |
       核心 Document Session Runtime
       身份 / 权限 / 串行事务 / 修订 / 保存 / 恢复
                     |
             受控 framed stdio
          +----------+----------+
          |                     |
 ncw.office-runtime      ncw.pdf-runtime
 helper + LibreOffice    helper + MuPDF 开源构建
          |                     |
          +-- 每文件唯一活动模型 -+
```

- **Office 路线固定为 LibreOfficeKit + 同活动模型的语义桥**，负责 Writer/Calc/Impress。不将现成桌面 LibreOffice 窗口嵌进 Electron。
- **PDF 路线单独评估 MuPDF 开源构建**，不经 Draw 导入/导出冒充原生 PDF 编辑；许可证或必需能力未通过时是发布阻断，不自动改用商业版或降级。
- 核心只负责平台事实，不实现 Word 排版、Excel 计算或 PDF 内容语义；引擎只操作私有副本，不自行选择原文件/导出落点。
- 主进程拥有 session/generation/modelRevision/savedRevision/diskRevision/seq；引擎拥有活动文档内容；renderer 只缓存控制状态与有界视口数据。
- 用户输入、Agent、宏、撤销/重做进入同一 session 的串行修改队列。每个 view 有自己的选区/光标/part/缩放，不能靠全局 active document 或全局 currentCall 定位。
- 同账户同底层文件不得产生两份独立可写模型。延续 canonical path 去重；检测硬链等别名并拒绝第二个独立写会话，避免原子替换后使用过期 inode 作为永久身份。
- 不将正文、图像 tile、大块 base64 写进全局 zustand、活动日志或模型工具输出；会话变更事件只推给有授权的订阅者。

### 规划目录（下列新增均为实施阶段工作，本轮不创建）

```text
plugins/ncw.office-runtime/
  package.json
  src/extension.ts
  native-src/CMakeLists.txt
  native-src/{main,protocol,office-engine,model-bridge,writer,sheets,slides,macros}.*
  native/<platform-arch>/           helper + 运行库 + payload 索引
  licenses/                        LICENSE/NOTICE/对应源码说明
  l10n/

plugins/ncw.pdf-runtime/
  package.json
  src/extension.ts
  native-src/CMakeLists.txt
  native-src/{main,pdf-engine,pdf-edit,pdf-forms,pdf-sign}.*
  native/<platform-arch>/
  licenses/
  l10n/

plugins/ncw.writer/
plugins/ncw.sheets/
plugins/ncw.slides/
plugins/ncw.pdf/
  package.json
  src/{extension,tools}.ts
  view/                            各产品视图和领域命令表
  l10n/

packages/office-plugin-common/
  src/                             Ribbon、视口调度、会话客户端适配、能力驱动控件

scripts/office/
  versions.lock.json               上游精确版本/commit/源码摘要/构建配置/工具链
  build-native.mjs
  assemble-runtime.mjs
  package-runtime.mjs
  verify-runtime.mjs
  smoke.mjs
  fidelity.mjs
```

共享原生协议代码不链接引擎语义。包含/链接 AGPL 代码的 helper 及相应构建材料按许可证发行；最终哪些代码构成需公开的系统以合规审查为准，不凭目录边界代判。

## 4. P0：先完成可行性证明与发布基线

这是最优先、失败成本最低的阶段；不先铺满四套界面。P0 的最小 helper、输入/渲染探针和语义桥直接放进最终插件模块，后续扩展复用，不建立一套会被丢弃的独立 POC 产品。

### 4.1 构建与版本基线

1. 在 `versions.lock.json` 锁定 LibreOffice、MuPDF、所有附属库/字体、源码 commit/摘要、构建选项与工具链版本。可以评估当前稳定版本，但构建禁止依赖浮动 latest/master。
2. helper 采用 C++/C 原生可执行程序；CMake 管 helper 的构建，LibreOffice/MuPDF 本体沿用锁定版本官方支持的构建流程，不假设整个 LibreOffice 能用 CMake 构建。
3. macOS arm64 先做开发闭环；随后立刻验证 macOS x64、Windows x64 的真实打包构建。编译器、SDK、CMake、LO 构建所需工具必须在 CI 中显式装配，不能假设本机已有。
4. 系统下限不能高于主应用已声明并验证的支持范围而不通知用户；P0 固化三个目标的最低系统/工具链与测试机器。若引擎要求提高下限，作为兼容性决策回到用户确认，不能悄悄提高。

### 4.2 必须用真实引擎证明的链路

- 一个 DOCX、一个 XLSX、一个 PPTX、一个 PDF：**打开 → 渲染 → UI 输入 → Agent 语义修改 → 导出/保存 → 关闭 → 重开**。
- 证明查询/语义写入与渲染同一活动模型：先输入未保存文本，再让 Agent 查询和修改；画布立即变化，原文件直到保存前逐字节不变。
- 语义桥优先验证 LOK `startURP`/UNO 连接到当前组件的方式；若公开 API 无法稳定拿到当前模型，在锁定 LO 构建中增加最小专用扩展/桥。不得另开第二个 headless 文档，更不得假设存在 `getDocumentTree`。
- 验证中文 IME composition、选区、多 view、一次修改一组撤销、PPT 动画实际执行、PDF 内容编辑/表单/签名路径。
- 验证宏自动执行关闭、宏显式执行、超时/取消与文件/网络副作用边界。只发现 `runMacro` 函数不算宏能力实现。
- 确认所选 LO 构建的 tile mode、像素顺序和 alpha 语义：统一输出 straight RGBA；必要时 BGRA 转换/去预乘。twips 与 CSS px/DPR 的换算写成可测纯函数。

### 4.3 基准与许可证硬门

- 建立可再分发的合成测试文档与测试字体，并收集用户愿意提供的代表业务样本；不自动上传用户文档到云端。
- 固定 WPS/Office 参考版本、字体、语言、DPI、缩放和基准导出条件。用户样本/授权参考环境尚未提供的项标记 `unknown`，不能按通过处理。
- 对全部依赖产出 SBOM、LICENSE/NOTICE、修改记录及准确对应的源码和构建脚本。MuPDF 的 AGPL 影响范围、LibreOffice/UNO 依赖、字体再分发、macOS 签名及 Windows 发行均需审查。
- **P0 通过条件**：三目标真实引擎都可跑；同模型桥接、必需高级能力的代表样本、宏控制与许可路径都有证据。任一必需项失败，停在阻断处报告证据；不能转成“先发布基础版”。

## 5. P1：扩展协议、调度与恢复，不破坏旧插件

### 5.1 宿主与 helper 协议

涉及 `src/shared/document-engine/{protocol,session}.ts`、`src/main/document-engine/{manager,native-host,native-frame}.ts`、`src/shared/plugin/native-component.ts`。

- 将新增协议类型/校验器按职责放入同目录 `interaction.ts`、`events.ts`、`commands.ts`，既有大文件只接线。
- 新 helper 使用 **protocol v2**：握手声明真实引擎版本、schema 版本、能力；render/query/input/command/undo/redo/macro 的请求/回执、事件均带明确关联身份。
- v2 二进制附件带 request/attachment 关联头，与 JSON 元数据的长度/尺寸/格式严格一致；保持有界帧与有限在途请求。v1 继续原有紧邻配对规则，不能让旧无 id 附件混入 v2。
- `NativeComponent.protocol` 与 host 支持范围明确校验；provider 启动选择对应版本适配器。更高未知版本装载/握手明确拒绝；不是简单把全局常量改成 2 导致所有 v1 helper 被拒。
- 异步回调只在 helper 单一事件/输出队列发送：脏区、游标、选区、页/表/幻灯片列表、命令状态、布局与进度。传输附件期间不能被其他事件写入打断。
- 增加明确的 page/part/sheet/view、坐标空间与 DPR 字段；像素尺寸继续使用宿主预算，不能把文档单位当像素上限。
- 格式/操作/UI 命令用有限注册表；每条有输入 schema、读写属性、适用种类、能力条件和处理器。`.uno:` 命令名只在引擎内部受审表中映射，不接受插件/模型传任意命令串。

### 5.2 修改记账与取消

- 用户键鼠输入、IME 提交、Agent 批次、宏、undo/redo 都成为有 `operationId` 的有序事务；明确完成回执才推进 modelRevision。
- 将 helper 的回调标明来源/事务，不把一次修改的 ACK 与 dirty 回调各计一次 revision。布局、游标、滚动等只读状态不推进文档修订。
- 过期 generation/revision 必须拒绝；查询返回的语义引用绑定 generation。批次的原子性/可撤销性以引擎实际支持回答，不在部分失败时空成功。
- `result_unknown` 不自动重放。UI 与 Agent 都可查询操作结果；超时、取消和进程退出撤销在途授权并丢弃迟到响应。
- 用分层调度取代把所有插件文档永久串到一条全局队列：生命周期门控制 open/close/安装/切账户；实际工作按 session 排队。不同文件不互相卡死；同文件顺序与清理不变式必须保留并补竞态测试。
- 长宏/导出/分析用受控 operation handle、进度与取消，不冒用工具的 `interactive:true` 获得更长执行时间。

### 5.3 恢复与数据完整性

新增 `src/main/document-engine/recovery.ts`，复用 `file-store.ts` 的摘要、私有产物与发布逻辑。

- 周期性写**活动模型恢复快照**到账户私有目录，携带引擎/格式/schema/generation/modelRevision/原文件摘要；先临时写入校验，再原子更新恢复索引。
- 恢复快照不是自动覆盖源文件；不把原始 working copy 当成未保存内容的快照。只恢复最后已成功持久化的版本，明确说明其后的输入可能丢失。
- 崩溃后提供：恢复快照、明确从磁盘重载、另存副本、显式丢弃。读取失败/快照版本不兼容保留恢复物，不静默删除或默认采用磁盘。
- 保存仍执行磁盘冲突检查、非空/尺寸校验、同目录原子替换；导出默认不覆盖，禁止覆盖源文件或其他活动文档。
- 正常关闭、工作区关闭、退出、切账户、禁用、卸载、引擎更新共用收尾；失败不清 dirty、不丢会话。保留现有退出否决与明确丢弃语义，新增逐文档恢复出口。
- 文件数据不做批量迁移、不首次打开就保存、不转换原格式；没有引擎时只报不可用，绝不写空文件。

## 6. P2：实现两个真实引擎提供者

### 6.1 `ncw.office-runtime`

- 每份文档独立 helper/profile，加载包内锁定运行库与私有工作副本。动态库搜索路径由包根决定，不依赖用户 PATH 或已安装的 soffice。
- `office-engine` 管初始化、文档装载、LO 事件循环、多 view、tile、输入/剪贴板、保存/导出；`model-bridge` 管当前模型的受控 UNO 访问与稳定对象引用。
- Writer/Sheets/Slides 适配器分别实现领域查询、基础操作和高级命令。查询必须有限结果、可分页/按区域读取，不能把整本书或整张工作簿塞进模型上下文。
- 命令完成、重算/重排与保存完成是不同状态。等待相应引擎确认后才能报告结果，不能仅以投递 UNO 命令成功作为操作成功。
- 打开时禁用自动宏、未批准外部链接更新、DDE/OLE/ActiveX 执行；加密文档走宿主密码交互，不把密码留在参数日志或工具结果。
- 宏项目保留与宏执行分别测试；不可保真的保存必须返回具体警告/拒绝，并把必需样本列入阻断，不盲拷原 OOXML 部件掩盖关系损坏。
- Windows 跨 DLL 内存按 LOK 对应释放接口处理；macOS 运行库路径与签名结构按真实构建验证，不复制开发机 `.app` 后就宣布可发行。

### 6.2 `ncw.pdf-runtime`

- 独立 MuPDF C/C++ helper，使用相同宿主会话协议与安全文件提交机制。MuPDF 只是底层库，不是现成的 PDF 编辑界面。
- 基础：页树、页尺寸/旋转、tile、文本提取搜索、选区、对象命中；不同坐标空间统一由适配器转换。
- 高级：修改实际文本/图像对象及内容流、保留未修改资源；页面插入/删除/重排/合并拆分；真实 annotation/widget 更新。不得用盖白块加新文字、整页栅格化或截图覆盖冒充原文编辑。
- 表单按可支持的字段类型校验与保存；PDF JavaScript/自动动作默认不执行。XFA、缺字体、加密权限、损坏对象、扫描件无文本层等返回准确状态，不暗中 OCR 或扁平化。
- 数字签名与可视签名分开：证书/私钥由宿主管理，模型与插件只拿短期签名操作句柄。私钥不进入 JSON RPC、活动日志或模型上下文。
- 签名绑定待提交产物的准确字节与摘要，验证 ByteRange/增量保存/证书校验结果。已有签名的文件编辑可能破坏签名，必须如实提示，不承诺任意修改后签名仍有效。
- AGPL 构建与源码发行成对、版本可追溯。许可审查要求扩大源码义务或变更核心许可时，暂停发行并取得用户明确决定。

## 7. P3：文件绑定通道、真实画布与四类 UI

### 7.1 主窗口到当前文件专用视图通道

新增：

- `src/main/ipc/document-engine.ts`：受信窗口请求的有限接口与定向订阅。
- `src/renderer/src/services/document-engine.ts`：IPC 信封拆解，不引 store。
- `src/renderer/src/stores/document-engine.ts`：按 session/view 订阅的控制状态。
- `src/renderer/src/views/plugins/DocumentEngineFrame.tsx` 与同目录 `document-engine-channel.ts`：iframe 装配与可测握手/请求调度。
- `src/renderer/src/plugin-ui/document-session.ts`：发布给插件视图的会话 API。

接线文件：`src/shared/ipc/contract.ts`、`src/preload/index.ts`、`src/main/ipc/index.ts`、`src/main/window/registry.ts`、`CustomEditorView.tsx`、`packages/plugin-api/nextcowork-view.d.ts`、`src/renderer/src/plugin-ui/view.tsx`、`scripts/build-plugin-runtime.mjs`。

补 `src/main/document-engine/runtime.ts` 作为账户级装配对象，由 `src/main/ipc/plugins.ts` 在启动时创建一次并注入插件桥和主窗口 IPC。两条入口必须引用同一 SessionManager，不能各自 new 一个。插件身份/授权查询通过注入提供，document-engine 内部不反向 import IPC；`src/main/account-switch.ts`、`src/main/index.ts` 与 `quit-guard.ts` 只调用该运行时的生命周期门。

关闭交互同时接入 `src/renderer/src/stores/documents.ts`、`src/renderer/src/views/files/DocumentDialogs.tsx` 和现有 Tab 关闭路径：dirty 来源仍是主进程快照，不再维护一份可独立写入的布尔；新增事件泵只在 `App.tsx` 启动一次，退出/切账户失败时保持原窗口可保存。

约束：

1. `CustomEditorView` 先按 `viewId` 选视图，省略时保留 `views[0]` 旧行为；绑定 `documentEngine` 才走新通道，旧文本/图片插件完全不变。
2. 主进程从窗口/Tab 绑定派生 workspace/path/plugin/viewType，只授予当前文档的 view token。iframe 不得任意指定其他路径、账户或 session。
3. source、精确 origin、实例 nonce、view token 均校验；dev/打包后的 origin 分别测试，不能用宽泛 `*` 或伪同源假设代替授权。
4. 控制 IPC 在 contract 登记，renderer 只有 services 写频道；文档事件按窗口/会话定向推送，不 `emitToAll` 内容。
5. seq/generation 缺口获取快照；旧实例、切 Tab、刷新后的迟到消息丢弃。所有监听与请求在 cleanup 释放；`key` 绑定 workspace/path/view 的语义身份。
6. 高频 tile 使用有界请求和 transferable buffer，不穿全局 store。保留插件 CSP，不开放任意 localhost/HTTP/WebSocket，更不让 iframe 获得 Node。

### 7.2 画布与输入

- 视口 tile 调度、LRU 内存预算、失效矩形合并、可见优先、离屏取消；缓存 key 至少含 session/generation/modelRevision/part/view/zoom/DPR。
- 真键鼠选区、光标、拖动、滚轮、剪贴板多 MIME、中文 IME composition；IME 尚未提交的文本不能被重复输入或当成最终修改记账。
- 保留 Word 页布局、Sheets 行列头/公式栏/工作表标签、Slides 缩略图/画布/放映、PDF 页导航与工具面板的领域差异。
- 性能预算先在 P0 机器上量测，再在 CI 固化基线与允许回归范围；不把猜测的毫秒数宣称为实测 SLA。
- canvas 配语义 overlay/键盘路径/焦点管理与屏幕阅读信息；仅画像素不算可访问性完成。

### 7.3 功能区与文案

- `packages/office-plugin-common` 提供 Ribbon 框架、保存/错误/状态、能力驱动命令控件；每个前端插件用自己的命令注册表，不复制四份生命周期逻辑。
- 注册项包括 id、l10n key、快捷键、图标、分组、能力/选区条件与真实 handler。没有后端能力不画可操作按钮；必需功能未实现仍算未完成，不因隐藏按钮通过验收。
- 宿主文案扩充既有 `src/renderer/src/i18n/documents.ts`；复杂新增引擎文案另建 `i18n/document-engine.ts`，同时注册中英。插件 manifest/工具标题用各自 `l10n`，view 使用自己的中英表。
- 使用宿主已有 UI 原子组件、主题 token、`cn`、焦点陷阱与动效档位；不引入裸颜色/新风格格式化工具。每个新主组件控制在 400 行以内，纯逻辑抽同目录 `.ts`。

## 8. P4：Agent 工具、受控宏与同模型操作

### 8.1 API 与工具

扩展 `src/shared/plugin/protocol.ts`、`src/main/plugin/document-rpc.ts`、`src/main/plugin/protocol.ts`、`packages/plugin-api/nextcowork.d.ts`：

- 读：`query/getCapabilities/search/listMacros/getOperation/subscribe`。
- 写：`create/apply/undo/redo/save/export`，恢复相关 API 显式区分恢复快照、磁盘重载、丢弃。
- 宏：独立 `runMacro/cancelOperation`，不藏在泛化 execute 或任意脚本字段里。
- 读写能力逐条映射；为文档宏新增最小能力 `documents.macros`，执行同时要求当前文档的写权限。修改 `src/shared/plugin/permission.ts`、方法权限表、SDK Permission 联合及授权文案/测试，完整落实清单上界、用户授予与升级扩权。旧插件不自动获得该权限；它不替代原生 helper 的现有 process 执行审批，也不自动授权网络/外部进程。
- 继续显式传每次 callId，并从内核 ToolContext 派生账户/工作区/run/permissionMode/signal。租约队列真正开始执行时重核权限与调用存活，取消/结束的工具不能靠先排队后执行继续写。
- 文件绑定 UI token 与 Agent callId 分开，不把前台 workspace 作为后台工具兜底。

在四个前端插件的 `src/tools.ts` 注册按读/写/宏划分的工具，校验 inputSchema 对应实际输入；Agent 工具拿语义引用而非屏幕像素坐标。宏等副作用工具如实标记 readOnly/destructive/needsNetwork。

结果的 content 必须含文件、操作 id、applied revision、dirty/saved 状态、保真警告、失败/未知结果及是否可撤销；卡片仅作 UI，不承载模型必须知道的事实。

### 8.2 宏的权限与实际保障

- 只接受当前文档内已枚举的入口 id 与有界类型参数，不接受任意脚本 URL、JavaScript、shell 或未审 UNO 属性调用。
- UI 与 Agent 都可发起，执行仍经过现有权限模式/审批；不偷偷变成“只能用户手动点 UI”，也不把 process 批准当成任意宏自动获批。
- 打开、预览、恢复、保存和导出不运行事件宏；外部链接更新、PDF JS、嵌入对象执行均独立处理。
- P0 必须验证原生宏的文件/网络/子进程隔离或受控代理。清洗环境变量与分进程不等于 OS 沙箱；若某类宏的副作用无法约束到已批准范围，拒绝执行并作为该必需能力的发布阻断。
- 只承诺经矩阵验证的宏能力；平台专有 COM/ActiveX 不兼容时明确拒绝，不在 macOS 模拟空成功。用户新增业务宏样本后同步更新兼容矩阵，不能自动改写宏代码。
- 超时/崩溃返回 result_unknown，先查状态，不自动重跑。文件/网络副作用不能靠撤销文档模型回滚，要在结果中如实说明。

## 9. P5：真实原生发行包、安装事务与平台安全

### 9.1 打包与完整性

- 每个 engine plugin 同一版本有 `darwin-arm64/darwin-x64/win32-x64` 三份 artifact，ZIP 顶层仍为插件 id；每包只带本平台 payload，逻辑插件可共用无平台包。
- payload 必须包括实际加载的库、LO 资源/profile 初始配置、MuPDF 附属资源、合法字体、LICENSE/NOTICE、文件索引；不借开发机路径或系统安装补依赖。
- 最终修改二进制路径/签名完成后再生成摘要和 payload 索引；所有发布摘要对应最终字节。签名、归档索引、ZIP 摘要三者不能互相覆盖或形成未定义自引用。
- macOS：校验完整嵌套 bundle/运行库签名与可重定位路径，在下载带 quarantine 的场景验证启动/公证分发；不使用关 Gatekeeper、清除隔离属性作为用户安装步骤。
- Windows：验证 MSVC/UNO 所需运行库、DLL 查找顺序、长路径/Unicode 路径、无开发工具环境。不得从当前文档目录装载同名 DLL。
- 对应源码构建产物与许可证材料可下载、可追溯；源码发行不是把二进制 ZIP 改个名字。签名凭据只由 CI secret 注入，不读取/复制用户私钥，不自动发布。
- 运行前除入口摘要外，增加 payload 一致性与平台签名检查；只对已验证不可变版本目录缓存验证结果，文件变动即失效。不把纯 hash 当作可信发布者身份。

### 9.2 安装/升级事务

扩展 `src/main/plugin/installer.ts`、`native-installer.ts`、`provider-registry.ts`；新增 `src/main/plugin/install-transaction.ts`。

1. 解析编辑器与引擎依赖闭包，检查版本范围、平台、API floor、循环依赖、包总预算与磁盘空间；展示全部安装项、原生执行风险、大小和许可。
2. 包下载到隔离 staging，逐项校验权威摘要/平台/schema/路径/展开预算；普通插件继续原有限额，不能因为包自称 native 就扩大授权。
3. 原生包的合法相对链接按既有索引恢复，拒越界、循环、绝对链接、硬链/设备/FIFO 等不支持类型；不能沿已有链接写出 staging。
4. 依赖全部验证成功再激活事务；持久化事务日志，异常退出后能恢复旧版本或完成安装。任何失败不留下编辑器启用但引擎缺失的半状态。
5. 引擎共享给多个前端：卸载消费方只释放自己的租约，不递归关闭共享引擎；被依赖的引擎不能无提示移除。
6. 有脏会话时不替换引擎；活动干净会话也须释放后再切换运行库，或将升级 staged 等会话结束。不能热替换正在加载的 DLL/dylib。
7. 依赖缺失/权限拒绝/当前平台无构建时保留 Tab 和用户文件，提供明确安装/授权说明，不能悄悄退到系统 soffice 或外部服务。

### 9.3 CLI 与 CI

- `packages/plugin-cli/index.mjs`：Node 文件 API 替代本轮原生打包路径的 rm/sh/cp；原生/许可证/schema 目录进入发行白名单；安全收集已声明入口与 payload。
- 增加经过版本锁定与许可证审查的流式 ZIP 实现（例如 `yazl`），显式登记到 `packages/plugin-cli/package.json`；不假设现有依赖里已经有。使用流式 hashing/upload，避免 GB 级 readFile/Blob/Buffer.concat。
- 新增 `.github/workflows/office-runtime.yml`，与主应用 release workflow 分离：三目标原生构建 → helper smoke → 真实 ZIP 安装 → 文档往返 → 签名/源码/SBOM/预算校验 → artifact。
- 原有 `.github/workflows/ci.yml` 保持宿主测试；原生构建/真实引擎测试必须有显式 job 与可复用 script，不因为默认 Vitest 未收集而伪装全绿。
- 新的构建脚本先核验工具链与版本再运行。计划命令需实施后才存在，例如 `npm run office:build`、`office:package`、`test:office-native`、`test:office-fidelity`；未添加脚本前不得声称它们可以执行。
- CI 默认只产出验证 artifact；发布到 GitHub/市场、提交和推送仍需用户明确要求。

## 10. 市场服务端外部依赖与客户端迁移

当前未定位到市场服务端源码，不能填写虚构的服务端文件路径。由服务端负责人交付以下契约，客户端用合同测试验证；生产联调前禁止宣布一键安装已完成。

### 必需数据契约

- artifact 身份：pluginId、version、platform、arch、最低系统版本、包规格版本、插件 API range、helper protocol、包大小、权威 sha256、下载标识、许可/对应源码信息。
- 平台包必须以 `(pluginId, version, platform, arch)` 区分；不能用现有 `(pluginId, version)` 的单包模型覆盖不同架构。
- 安装授权一次返回完整且精确版本锁定的依赖计划/摘要，不让客户端绕过授权随便下载另一个依赖包。
- 服务端 InspectPackage、存储/CDN/网关上限、上传流式处理、平台过滤、审核权限扩张与 GPL/AGPL 源码材料校验同步升级。
- 老客户端不应看到它无法安装的原生 artifact；缺平台信息不默认取别的架构。普通 JS 插件保持原响应兼容。

### 桌面侧具体文件

- `src/shared/plugin/{market,state,manifest}.ts`：artifact、依赖安装计划、状态与可选字段；解析层严格校验。
- `src/main/ipc/plugin-market.ts`：带 appVersion/apiVersion/platform/arch 请求，按授权计划流式落临时文件、限量/hash/取消/清理；不全局提高 20 MiB 普通包上限。
- `src/main/plugin/install-transaction.ts`：依赖事务、失败回滚、共享引擎引用与版本切换。
- `src/renderer/src/services/plugins.ts` 与扩展插件安装/详情视图：只显示后端可执行的状态与操作；中英文文案用插件领域 i18n。
- 下载显示真实字节/总量与分阶段进度，未知大小显示不确定进度，不虚构百分比。

## 11. 兼容、迁移和故障行为

- 保留 `InnerTab.kind: 'custom'` 及已有持久化 pluginId/viewType/path；sessionId/view token 不持久化为跨进程身份。
- `viewId/documentEngine` 省略的旧插件继续旧 `ncw:doc` 通道；Markdown/图片/Excalidraw 不改保存语义。
- 新 API 默认在当前 0.3.2 基线上递增 patch（实施时若版本已并发变化则重新核实）；同步 SDK、runtime shim、脚手架及兼容测试。旧公开字段语义不变，helper v2 单独版本化。
- 恢复索引与安装事务日志各自带 schemaVersion，采用惰性迁移；不可识别的数据保留并给出处理入口，不猜着清除。
- 错误在宿主转稳定领域码，renderer/view 按 locale 翻译。可重试失败、明确未生效、结果未知、用户取消分开，不能三层 catch 后统一显示成功。
- 引擎断开时保留会话状态、恢复项与原文件；重连必须递增 generation 并使旧引用失效，不能让旧 callback 操作新模型。
- 隐藏 Tab 可释放像素缓存，不释放未保存的活动模型；视图关闭与插件普通休眠不能等同于丢弃文档。
- 多代理/多人并发工作：修改前重读文件，精确 Edit；不覆盖无关未提交改动，不重排大文件，不修顺路问题。新增非平凡分支写需求与失败症状注释，修改 `★` 边界同步保留理由和回归测试。

## 12. 实施顺序与每阶段完成条件

| 阶段 | 工作 | 出口条件 |
|---|---|---|
| P0 | 版本/许可/构建基线，同模型与四类关键能力原生验证 | 三目标真实闭环与必需能力代表样本可行；所有未知/失败明确登记，不先宣布可发布 |
| P1 | helper v2、session 调度、操作/事件/恢复协议、宿主 fake 测试 | v1 兼容；作用域、revision、取消、恢复、保存冲突自动化通过 |
| P2 | Office/PDF 原生适配与基础/高级命令 | 每项命令绑定真实模型并通过格式级测试，宏自动执行禁用有效 |
| P3 | 文件绑定通道、真实输入画布、四类 WPS 风格 UI | UI 与 Agent 同模型；三平台中文 IME、键鼠、主题/快捷键、撤销完整 |
| P4 | Agent 工具/受控宏、恢复与关闭完整交互 | 后台工作区/并发/取消/宏副作用/崩溃流程通过，不需要强杀才能脱困 |
| P5 | 可重定位发行包、安装事务、CLI/CI/签名源码产物 | 干净机器从最终 ZIP 安装，不依赖开发机文件；升级失败能回滚 |
| P6 | 市场外部联调、全部功能矩阵与样本验收 | 服务端/客户端全链路和全部高级功能在三目标通过，才满足正式交付 |

P3/P4/P5 可以在 P0/P1 稳定后按文件所有权并行；不能跳过 P0 把真实引擎可用性当作假定。PDF 编辑/签名、PPT 动画、宏兼容、同模型语义桥、许可、签名及市场服务端均可能阻断正式交付。

## 13. 验证方案

### 13.1 宿主与协议自动化

- 在既有 `src/main/document-engine/__tests__`、`src/main/plugin/__tests__`、`src/shared/document-engine/__tests__`、`src/shared/plugin/__tests__` 扩展 `.test.ts`。
- 版本握手、v1/v2 隔离、粘包/拆包、错 id/缺帧/重复帧、非法长度、事件交错、stdin 错误、超时/迟到回执、取消后拒新写入。
- 唯一模型、多 view/多工作区、多插件、跨账户隐藏、目录/软链/硬链/外部替换、权限撤销、审批期间换包/摘要变化。
- 修改 revision 只推进一次；查询读到前序修改；失败批次与未知结果不重放；undo 不覆盖别人的后续输入；恢复快照 seq/generation 正确。
- 保存/导出零字节、超大产物、原子发布、目标并发创建/变更/删除；缺盘/只读/空间不足；签名产物字节绑定。
- 关闭/禁用/更新/切账户/退出排空与否决，未保存内容不丢；确认丢弃才销毁，超时不把批准状态残留到下次操作。
- 市场 mock DTO、非法平台/版本/授权摘要、依赖循环/冲突、断点失败、事务进程中断与恢复。

### 13.2 Renderer 与插件包测试

- DOM 测试继续 `.test.ts` + createElement/JSDOM，不引入不存在的 testing-library，不新建静默跳过的 `.test.tsx`。
- origin/source/nonce/token、两个 iframe 串话、切 Tab 迟到事件、listener cleanup、seq 补快照、可见区域调度/缓存淘汰、IME 重复提交和快捷键焦点。
- 旧文本/图片 custom editor、插件激活/主题/语言/关闭确认不回归。
- 每个前端/引擎包经真实 installer 安装最终 ZIP，验证 selector/viewId/引擎依赖/l10n/native 索引/许可文件及 engines floor。

### 13.3 三目标真实文档与发行验收

- Writer：页数、分页、中文换行、目录/脚注、页眉页脚、表格/图片锚点、修订批注往返。
- Sheets：公式和值、重算、命名范围、图表、条件格式、透视、冻结/筛选/打印设置。
- Slides：对象/母版/主题/备注、对象分组与布局、动画及切换时序；静态截图不能证明放映通过。
- PDF：真实内容流/字体/图像、页树、annotation/widget、签名前后 ByteRange 与验证状态；不把“能渲染”当作“结构完整”。
- 宏：受支持样本、禁自动执行、宏项目往返、UI/Agent 显式执行与审批、被拒副作用、取消/结果未知不重跑。
- 未修改文件原字节和 mtime 不变；修改后 Office/WPS 不出现修复提示；所有必需样本的高级部件保留。
- 带密码、缺字体、损坏/超大/恶意压缩、无网、Unicode/长路径、多显示器/DPI、跨平台剪贴板、helper 崩溃/恢复都覆盖。
- 三个目标各用干净环境与最终已校验包，尤其验证 macOS 下载隔离/签名、Windows DLL 与运行库；开发机直接启动不算发行通过。
- 视觉比较与结构/数值断言并用。字体/版本/DPI 固定，抗锯齿容差有依据；不靠放大阈值掩盖重排、分页变化或对象丢失。

### 13.4 已存在的仓库验证命令

```sh
npm run typecheck
npm test
npm run lint
npm run test:release
npm run build
```

本轮已读取 `package.json` 核实上述命令；计划阶段未执行。原生构建/签名/文档兼容测试需添加专用 scripts 与 CI 后才可执行，不能将既有 Node fake helper 测试描述为真实 Office 引擎验收。

## 14. 最终完成定义与明确阻断

全部满足才算正式完成：

1. macOS arm64/x64、Windows x64 从真实发行包可安装运行，不依赖外部 LibreOffice。
2. 四类文档全部基础/高级功能与受控宏通过冻结的功能/兼容样本矩阵；未通过不能以只读或隐藏按钮替代。
3. UI、Agent、宏共用唯一活动模型，未保存改动、撤销、恢复、冲突和关闭行为均可验证。
4. 保持现有 process/allowedCommands/执行确认；许可声明、权限授予、参数范围与实际副作用边界一致，不宣传并不存在的原生沙箱。
5. 完成本地包与市场依赖安装/升级/回滚，服务端接口联调通过；外部服务端未交付则整条一键安装仍未完成。
6. 许可证/SBOM/对应源码与构建、平台签名、可再分发字体材料齐全且经审查；没有未经批准的商业依赖或核心许可变更。
7. 原有插件、文本/图片编辑器和持久化 Tab 不回归，仓库测试/类型检查/lint/build 与新原生矩阵均通过。

明确非承诺：所有 WPS 功能逐像素克隆、任意 VBA/COM/ActiveX 跨平台等价、任意 PDF 编辑后既有签名仍有效、所有字体可合法随包、Linux 本轮发行。若用户代表样本包含当前引擎无法满足的必需项，交付保持阻断并提出证据，不擅自改变本计划目标。

计划制定时的状态：仅只读调查、确认需求与保存此计划。实施进展见下一节。

## 15. 实施记录

### 15.1 P0 第一轮（2026-09-29）

**对计划的偏离（需知悉）**：Office 引擎插件不在本仓库新建 `plugins/ncw.office-runtime/`，而是沿用已存在的独立仓库 `AIDotNet/ncw-office-runtime`（`~/Desktop/code/ncw-office-runtime`）。它已锁定 LibreOffice 26.8.0 官方发行版（四平台 SHA-256）、有真实 helper、四平台 CI 与“删掉系统 LibreOffice 后对包重跑”的自足验证。重复建设会产生两份分叉的 helper。

**已取得的证据**

| 项 | 结果 |
|---|---|
| helper 本机重编译 | 摘要与已登记值一致（可重复构建，macOS arm64） |
| helper 对真实 LibreOffice 26.8.0.3 一致性测试 | 12/12 通过：DOCX/XLSX/PPTX 修改→保存→新进程重开读回、导出 PDF、真实像素渲染、整批拒绝不改文档 |
| 宿主 ↔ helper 协议对齐 | 补齐 Word 按文字定位的三种操作、`layout` 查询、渲染 `part`、批次逐条结果、插件 RPC `documents.query`；此前宿主会过滤掉 helper 的 Word 写操作 |
| 宿主集成测试 `npm run test:office-native` | 4/4：生产路径（registry 摘要核对 → session manager → native provider → 真 helper + **随包** LibreOffice）下打开/修改/读回未保存内容/渲染/保存/新进程重开/批次拒绝/Excel 公式/分表渲染/导出 |
| 发行 ZIP 经真实安装器安装后运行 | 同一组 4/4 通过（macOS arm64，未签名） |
| 引擎 CI（45b097e） | darwin-arm64、darwin-x64、linux-x64 全绿（含随包自足验证）；**win32-x64 打包失败**：CI 的 bash 步骤里 `tar` 解析为 Git Bash 的 GNU tar。已在引擎仓库改为 System32 bsdtar，待 CI 验证 |
| 引擎 CI（a032b79，run 36593483736） | 四平台全绿：darwin-arm64、darwin-x64、win32-x64、linux-x64 的「对系统 LibreOffice 一致性」「打包」「删掉系统 LibreOffice 后对随包插件一致性」三步均通过，all-platforms 组装通过。win32-x64 打包问题已修。证据粒度为 GitHub API 的步骤结论；逐条用例日志需登录才能下载，本机未取到 |

**同模型证据**：渲染结果的 `modelRevision` 与刚完成的修改一致，未保存修改可被查询读回而磁盘不变——修改、查询、渲染作用于同一个 LibreOfficeKit 活动文档。语义引用（稳定对象 ref）尚未实现，Word 写操作按字面文字定位。

**仍为阻断 / 未开始（按计划 §14 不得宣称完成）**

1. 交互编辑：~~helper 未接键鼠/IME/选区回调与 tile 失效事件~~ 引擎侧与宿主会话层已接通（见 15.2）；视图通道与画布 UI 仍未开始（P3）。
2. 宏：helper 如实报 `macros.run=false`；受控执行与副作用约束未验证（P0 阻断项）。→ 「不自动运行」已有证据（见 15.5）；显式执行与副作用隔离仍阻断，需要用户选路线。
3. PDF：当前经 LibreOffice Draw 只读打开、仅导出；MuPDF provider 未开始，AGPL 分发审查未做（P0 阻断项）。
4. PPT 母版/动画/放映、Excel 图表/透视、Word 修订/批注等高级功能均未验证。→ 演示编辑器的基础编辑、幻灯片管理、版式、插入文本框 / 形状 / 表格与静态放映已有证据（见 15.10）；母版、动画 / 切换、备注、图片媒体仍未开始。
5. ~~Windows x64 随包 ZIP 尚无通过 CI 的产物~~ a032b79 的 CI 已产出并对随包插件跑通一致性测试；宿主侧在 Windows 上经真实安装器安装并跑 `test:office-native` 仍未验证。
6. 许可审查（`licenses/NOTICE.txt` 标注的 RELEASE BLOCKER）、代码签名/公证未做。
7. 视图会话通道、四个前端插件、Agent 工具、恢复快照、市场平台 artifact 与依赖事务均未开始。→ 视图会话通道已接通（见 15.6）；文字 / 表格 / 演示三个编辑器已接通（见 15.7–15.10），PDF 编辑器、Agent 工具、恢复快照、市场仍未开始。
8. ~~本地 `build/` 中的 ZIP 早于清单权限修改；正式包需经 CI 重新打包。~~ a032b79 的 CI 已按带 `process` / `allowedCommands` 的清单重新打包（artifact，未发布）。

**工作区环境注意**：仓库位于 iCloud 同步的 `~/Desktop`。本轮出现编辑工具报告成功但磁盘内容丢失修改、尾部残留旧字节的情况；已从 HEAD 恢复并用“临时文件 + fsync + rename”重放，之后逐次回读核对。建议把仓库移出 iCloud 同步范围。

### 15.2 P0 第二轮：交互输入（2026-09-29）

**已取得的证据**

| 项 | 结果 |
|---|---|
| helper `document.input`（引擎仓库） | 键盘（`postKeyEvent`）、鼠标（`postMouseEvent`，twips）、输入法组字/提交（`postWindowExtTextInputEvent`）、切换工作表/幻灯片；回执带失效矩形、光标、选区、单元格光标、`modified` |
| helper 一致性测试（真实 LibreOffice 26.8.0.3） | 15/15（新增 3 个）：键入 → 读回；方向键不算修改；组字期间不算修改，提交后是最终文字而不是拼音；Backspace + Ctrl+Z 撤销读回；Calc 鼠标点到 B3 再键入回车落在 B3；非法批次整批拒绝；PDF 拒绝输入 |
| 宿主 `interaction.ts` + `DocumentSessionManager.input` | 与 Agent 修改共用一条队列与修订号；只有 `modified` 才推进 modelRevision；generation 过期拒绝；结果不明标崩溃（6 个单测 + 收窄单测） |
| 宿主集成（生产路径，目录与 ZIP 经真实安装器两种方式） | 各 5/5：画布键入/IME 进入同一活动模型，打字前规划的 Agent 批次被 `stale_revision` 挡下，保存后新进程重开读回 |
| 仓库验证 | typecheck 通过；全量 424 files / 6654 tests 通过；lint 0 error（4 条既有 warning） |

**实测得出、已写进代码注释的引擎行为**

- 不开 `LOK_FEATURE_PART_IN_INVALIDATION_CALLBACK` 就**收不到任何**失效矩形（已画区域按 part 0 登记，失效却带 `INT_MIN`，被全部裁掉）。视图必须先 render 过一块区域，那块区域才有失效通知。
- 按键是异步投递的：helper 等回调静默后再回执；本机每批约 100–400 ms（Calc 首次进入单元格编辑最慢）。**这是本机单次观测，不是性能基线**。
- 「是否修改」按撤销栈前后比较判定。Calc 单元格编辑中（未回车）也被判为修改——偏保守（多推进修订号，只会让 Agent 多重读一次，不会漏记）。→ 15.4 复测更正：只有进入编辑后的**第一批**按键报修改，之后到结束编辑前都报 false，结束编辑那一批报 true；Impress 文本框相同。

**新发现的问题（未修，需后续处理）**

1. ~~`query { kind: 'text' }` 会移动用户的光标/清掉选区~~ 已修（见 15.3：Agent 独立视图）。原问题：SelectAll + resetSelection 在用户视图上做，Agent 读一次正文用户插入点就跳走；实测不止查询，Agent 的写操作也会挪走用户光标与当前工作表。
2. ~~组字期间查询正文会读到未提交的拼音（引擎把组字串临时放进模型）。~~ 已修（见 15.4）；实测问题比这更重：Agent 此时追加的文字会被下一次组字更新吞掉。
3. 用户输入尚未进入操作回执表（无 operationId、不可按 id 查询），属于计划 §5.2。
4. 键码换算（DOM `KeyboardEvent` → `awt::Key`）、twips ↔ CSS px/DPR 换算、失效矩形 → tile 调度都属于视图侧，尚未实现。→ 前两项与「失效矩形 → 哪几块 tile」的网格计算已作为纯函数实现（见 15.4）；可见优先、LRU、离屏取消等调度仍未做。
5. ~~以上只在 macOS arm64 验证；Windows 线程模型下（请求在 LO 主线程处理）的输入等待逻辑未经 CI 验证。~~ helper 一致性测试（含输入用例）已在四平台 CI 通过（a032b79）；宿主集成测试仍只在 macOS arm64 跑过。

### 15.3 P0 第三轮：Agent 独立视图（2026-09-29）

**问题复现**：先写一致性测试再改。旧 helper 上，用户键入 `abc` 并左移一格后，Agent 查询正文并在文末追加，用户再打 `X`，结果是 `['abc', 'Bot lineX']`（应为 `['abXc', 'Bot line']`）；表格里用户点 B3 后 Agent 插表写单元格，用户输入的 `7` 没有落在 B3。

**做法**：helper 打开文字/表格/演示文档后用 `createView` 建一个 Agent 视图（独立回调，只收命令回执与查找结果，不记画布状态）。`apply` 与 `text`/`cells` 查询在 Agent 视图上做；`input`、`render`、`layout`、`saveAs` 在用户视图上做，由 RAII 切回，异常路径也切回。两个视图共享同一个文档模型，各自有光标、选区和当前工作表。

**证据**

| 项 | 结果 |
|---|---|
| helper 一致性测试 | 17/17（新增 2 个）：Agent 查询/写入之后，用户的插入点和当前单元格保持不变；Agent 修改会给用户画布发失效矩形 |
| 顺带发现并修复 | Agent 修改触发的 `ModifiedStatus=true` 会漏进下一次空批次拉取的回执（实测 `modified:true`），宿主会为同一次修改推进两次修订号。现在只认本批输入期间的信号 |
| 宿主集成（目录 / ZIP 经真实安装器） | 各 5/5：Agent 修改后拉取不重复计修订号，并返回失效区域；用户随后键入落在自己的插入点；保存后重开读回 `H中Xi!` |

**已知限制 / 风险**

- 撤销栈是文档级的：用户按 Ctrl+Z 可能撤掉 Agent 的修改（LibreOffice 语义，未做按视图撤销）。
- 注释中记载：macOS CI 上曾因「建临时视图再切换」使 PDF 导出崩溃（本机未复现）。Agent 视图同样涉及建视图和切换视图，本机 17/17 通过，但 macOS/Windows CI 未验证。→ 已在四平台 CI 通过（a032b79），含 PDF 导出与两个 Agent 视图用例；若 Agent 视图没建出来而退回单视图，那两个用例会失败，所以步骤通过即说明四平台都建出了 Agent 视图。

### 15.4 P0 第四轮：输入法组字与 Agent 并发、PPTX 输入、换算纯函数（2026-09-30）

**实测复现（本机 LibreOffice 26.8.0.3，修复前的 helper）**

- 组字 `zhong` 期间，Agent 读正文读到 `abzhong`；Agent 在文末追加的 `AG`，被用户下一次组字更新连同拼音一起替换，**无声丢失**。
- 组字期间投递普通按键 `x`：`x` 被并进组字区，下一次组字更新把它一起替换掉。点击不结束组字，下一次组字仍替换原处。
- 空串组字只清空组字区，不结束组字会话。
- Calc / Impress 的「是否修改」：进入编辑后第一批按键报 true，之后到结束编辑前都报 false，结束编辑那一批报 true（实测了 Impress 的 Esc、Calc 的方向键离开单元格）。原因是文字留在编辑引擎里，结束编辑时才写回模型。

**做法**

- helper 记录用户视图的组字状态（引擎没有可查询的接口）。组字期间，Agent 的 `apply`、正文/单元格查询、保存/导出都报新错误码 `busy`，表示什么都没做、可以原样重试。版面查询与渲染照常。
- 组字进行中只收 text 事件；键鼠和切换部分整批拒绝（`invalid_operation`）。空串组字改为「清空并结束」。输入回执新增 `composing` 字段。
- 宿主：`DocumentErrorCode` 新增 `busy`，native-host 认得它，插件层映射为可重试的 `rejected`（消息带 `[busy]`）。manager 把「引擎明确未生效」的判定收成一个 `isEngineRefusal`（原先五处复制），`busy` 不标崩溃；apply 不记账，同一个 operationId 可以原样重试。
- 新增纯函数：`src/shared/document-engine/viewport.ts`（twips ↔ CSS px、按 DPR 的 tile 网格、相邻 tile 共用边界、失效矩形/视口覆盖哪些 tile）和 `keys.ts`（DOM 键盘事件 → `awt::Key` 与修饰位；快捷键字母先按布局字符认，认不出再按键位；组字中/单独修饰键不送）。

**证据**

| 项 | 结果 |
|---|---|
| helper 一致性测试 | 19/19（新增 2 个）：组字期间 Agent 读/写/存报 busy，中途按键整批拒绝，提交后正文正确，取消后 Agent 追加的文字不被吞；PPTX 点进标题占位符键入 + IME，Esc 结束编辑报 modified，保存后幻灯片 XML 读回 `Deck标题` |
| 宿主单测 | busy 透传不崩溃、同一 operationId 可重试；回执 `composing` 收窄；`[busy]` → `rejected`；换算与键码 14 个用例 |
| 宿主集成（目录 / ZIP 经真实安装器） | 各 5/5：生产路径下组字期间插件 `documents.query` 得到 `rejected` + `[busy]`，会话仍可用 |
| 仓库验证 | typecheck 通过；全量 425 files / 6670 tests 通过（另有一次运行中 `settings/__tests__/release-notes.test.ts` 偶发失败，与本轮改动无关，复跑通过）；lint 0 error（4 条既有 warning） |

**仍未解决**

1. 组字状态只能由视图结束：视图组字到一半消失（关页、崩溃），Agent 会一直收到 busy。宿主收回视图时要补发一次空串组字，属于 P3 视图通道的工作。
2. Calc / Impress 编辑中（未结束编辑）Agent 读到的是旧值，写同一格或同一形状会被用户随后的提交覆盖（后写者赢）。这一轮没有加锁或提示。
3. `keys.ts` 的键码值只有 Ctrl+Z 一例在真实引擎上验证过（与一致性测试同值）；其余按 LibreOffice `Key.idl` 与 `vcl/keycodes.hxx` 的定义编写，未逐键实测。macOS 的 Cmd 是否对应 MOD1，要等画布接上后实测。
4. 本轮引擎改动尚未推送，四平台 CI 未跑。

### 15.5 P0 第五轮：宏的安全基线（2026-09-30）

**样本**：引擎仓库新增 `test/macro-fixtures.mjs`，测试运行时现场生成 `.odt` / `.ods`。宏源码写在测试代码里，可以审阅：Basic 宏绑定 11 个文档事件（OnLoad、OnViewCreated、OnSave、OnSaveAs、OnCopyTo、OnPrepareUnload、OnUnload 等），每个事件往 `$HOME` 写一个标记文件。用 ODF 是因为 Basic 库与事件绑定是纯 XML；OOXML 的 VBA 工程是二进制 `vbaProject.bin`，没法手写审阅。两者在 LibreOffice 里经过同一道闸门，也就是加载时的 `MacroExecutionMode`。

**证据（本机 LibreOffice 26.8.0.3）**

| 项 | 结果 |
|---|---|
| 正向对照 1：LibreOffice 桌面版，宏安全级别设为「低」，headless 打开样本 | 出现 OnLoad / OnLoadFinished / OnViewCreated 标记，说明样本里的宏能跑、事件确实绑上了 |
| 正向对照 2：临时改一份 helper，加载参数加 `EnableMacrosExecution=true,MacroSecurityLevel=0` | 打开时出现 OnLoad / OnViewCreated，saveAs 时出现 OnCopyTo（odt、ods 相同），说明测试能察觉宏执行 |
| 生产 helper（不传 EnableMacrosExecution → LibreOfficeKit 以 `NEVER_EXECUTE` 加载，依据 LO `desktop/source/lib/init.cxx`） | odt、ods 经过打开、渲染、键入、保存、导出 PDF、关闭，**零标记文件**；保存出的文件里宏工程原样保留 |
| 引擎一致性测试 | 21/21（新增 2 个） |

helper 的 `documentLoad` 处已加 ★ 注释：不传 `EnableMacrosExecution` 是故意的，由上面的用例钉住。helper 没有任何运行宏的入口：不调 `runMacro`，也不接受任意 UNO 命令。

**仍然阻断（需要用户决定方向）**

1. **显式执行**：LibreOfficeKit 的宏开关只能在加载时设定。以可执行模式加载时，打开那一刻 OnLoad、VBA 的 `Workbook_Open` / `Document_Open` / `Auto_Open` 就会运行，与「打开时不运行事件宏」冲突。官方二进制上没找到加载后单独开放某个入口的公开接口。可选路线：(a) 另起一个隔离的「宏执行会话」，把当前模型存成临时副本后以可执行模式打开，只允许运行后替换模型（generation+1），打开时触发的事件宏视为本次执行的一部分，要如实告知；(b) 自建 LibreOffice 构建，加一个最小补丁或扩展，让加载后可以只放行指定入口；(c) 宏执行保持阻断，不在本轮交付。
2. **副作用隔离**：Basic / VBA 可以写任意文件、起子进程、访问网络。子进程隔离需要平台沙箱：macOS 用 `sandbox-exec` 配置（已标记弃用但可用），Windows 用 AppContainer / 受限令牌 + Job 对象。两者都没有验证。
3. OOXML VBA 样本（docm / xlsm / pptm）还没有，要么需要可再分发的样本文件，要么写一个 `vbaProject.bin` 生成器。
4. 宏枚举（`listMacros`）可以不执行宏、直接从文件解析出来（ODF 是 XML，OOXML 是 MS-OVBA），但要等执行路线定下来才有意义，尚未实现。

### 15.6 P3 第一步：编辑器画布 ↔ 文档会话通道（2026-09-30）

用户要求先做宏以外的部分。按 §7.1 接通「主窗口画布 → 主进程会话」这条链路。编辑器 UI（Ribbon、画布组件）还没做。

**链路**：插件视图 iframe 发 `ncw:engine:*` 报文 →（postMessage）→ 主窗口 `DocumentEngineFrame` / `document-engine-channel.ts` →（`documentEngine:*` IPC）→ `ipc/document-engine.ts` → `plugin/document-view.ts`（`DocumentViewChannel`）→ `PluginDocuments.openEditorView/saveEditorView/closeEditorView` 与 `DocumentSessionManager`。与插件 RPC 和 Agent 工具共用同一份会话表，操作的是同一个活动模型。

**新增 / 改动**

- shared：`view.ts`（主窗口 ↔ 主进程的请求与事件）、`view-frame.ts`（iframe ↔ 主窗口报文及收窄、`[code]` 错误解析）。
- main：`PluginDocuments` 新增画布入口，复用路径门、依赖版本门和格式过滤。画布不建租约（租约 5 分钟空闲会被收掉），但会记入归属表，所以画布里的脏会话同样会挡住插件禁用 / 卸载。`save` 的写回前路径重验抽成 `saveResolved`，租约和画布共用。
- main：`DocumentViewChannel`。token 绑定发起窗口，切账户后作废；关 Tab 时如果没有别的 Tab 共用这个会话，先取消未提交的组字再释放；窗口销毁或重载（`did-navigate`）时释放全部视图；会话变更按会话 id 定向推给打开它的窗口（`documentEngine:changed` 列入 `TargetedEventChannel`，编译期禁止广播）。
- IPC：契约新增 7 个 invoke 频道和 1 个定向事件。错误统一翻成 `[code] message`。
- renderer：`services/document-engine.ts`；`document-engine-channel.ts`（token 不出主窗口、iframe 重载先关旧视图、dispose 后迟到的打开回执立即关掉、像素用独立 ArrayBuffer 转移、旧状态不覆盖新状态）；`DocumentEngineFrame`；`PluginViewFrame` 增加 `channel` 扩展点；`CustomEditorView` 优先按 `viewId` 选视图，绑定了 `documentEngine` 的编辑器走会话通道，不再走整文件文本通道。
- 视图 SDK：`nextcowork/view` 新增 `openEngineDocument()`（请求按 id 配对、错误带码、dispose 后不留挂起的 Promise、只收来自父窗口的报文），并导出宿主同一份换算函数（`lokKeyOf`、`tileRequest`、`tilesInView` 等），d.ts 同步更新。插件运行时构建的指纹加入 `src/shared/document-engine`，否则改了换算函数运行时不会重建。

**证据**

| 项 | 结果 |
|---|---|
| 单测 | `DocumentViewChannel` 5 个、iframe 报文通道 7 个、视图客户端 5 个 |
| 插件运行时 | 重新构建后 `view.js` 导出新 API，下发测试 12/12（裸 import 全在 import map 里） |
| 宿主集成（真引擎，目录 / ZIP 经真实安装器） | 各 6/6，新增的画布用例：画布打开 → 版面 → 渲染 → 键入 `Hi` → Agent 经插件 RPC 看到同一修订号并追加 `!` → 通道算出要推给画布 → 画布空批次拉取得到失效区、修订号不重复计 → 画布保存 → 重开读回 `Hi!` |
| 仓库验证 | typecheck 通过；全量 428 files / 6687 tests 通过；lint 0 error（4 条既有 warning） |

**未做 / 已知限制**

1. 没有在 Electron 里端到端跑过：IPC 接线、`did-navigate` 回收、iframe 实际收发只由类型检查和各段单测覆盖。要等第一个真实的办公前端插件（ncw.writer）来验证。
2. 同一文件开在两个 Tab 时，两个画布共用 helper 唯一的用户视图（光标、组字状态共享）。引擎侧要支持多个用户视图才能分开。
3. 关 Tab 前的挽留沿用自定义编辑器的 `setDirty` → `customEditor.save` 契约，需要办公插件的逻辑侧实现保存；§7.1 要求的「由主进程快照驱动的关闭交互（stores/documents.ts、DocumentDialogs）」还没做。
4. 画布通道没有速率 / 在途上限：视图一次发很多 render 时全部进会话队列。tile 调度器（可见优先、离屏取消）属于视图侧，还没做。

### 15.7 P3 第二步：宿主画布与第一个编辑器 ncw.writer，Electron 端到端跑通（2026-09-30）

**做法与对计划的偏离**：画布（tile 调度、光标 / 选区覆盖层、键鼠与输入法）不放在 `packages/office-plugin-common`，改由宿主运行时 `nextcowork/view` 提供 `DocumentCanvas`。理由：vitest 只收 `src/**`，放 packages 下的逻辑没有测试覆盖；由宿主下发，所有办公插件只有一份实现。插件只画产品界面（工具栏、侧栏）。

**新增**

- `shared/document-engine/tile-cache.ts`：tile 账目。失效只把块标为旧、不擦除；请求在途时被失效的结果照样收下但仍要重画；同一块同时只有一个请求在途；换配置全部作废；按字节预算淘汰最久没用的块，可见块不淘汰。6 个单测。
- `plugin-ui/canvas-input.ts`：鼠标键位换算（DOM 右 2 / 中 4 ↔ LO 中 2 / 右 4）、点击修饰位、发送队列（同一时刻一批在途；相邻的组字更新和鼠标移动只保留最新；组字不跨提交合并；失败不卡队列）。9 个单测。
- `plugin-ui/DocumentCanvas.tsx`：每块 tile 一个 canvas；隐藏输入框跟着光标走，输入法候选窗因此出现在光标旁；`onShortcut` 让插件截走保存键（送进引擎的话 LibreOffice 会存进它自己的私有副本）；Agent 修改后拉取失效区重画；遇到 `stale_generation` 重取版面。
- 主题新增 `--color-page-ink`（纸面光标用纯黑，推的，不进 THEME_TOKENS）。
- 插件 API 升到 0.3.3（纯新增）。
- `plugins/ncw.writer`：清单（`*.docx` / `*.docm`，`viewId`，`documentEngine: ncw.office-runtime/office`，依赖引擎插件）；视图包含保存按钮、状态、缩放（75–150%）和画布，Ctrl/Cmd+S 经宿主保存。中英文案在视图内自带。
- `scripts/office-writer-probe.mjs` + `npm run e2e:office`：用 playwright-core 驱动真实的 Electron，在测试进程里把原生执行确认换成「允许」，生产代码没有调试开关。

**端到端证据（Electron 44，本机 macOS arm64）**：安装引擎插件目录和 writer → 启用 → 从文件树打开 `report.docx` → 画布画出引擎渲染的白页 → 点击正文、键入 `Hello`、通过 CDP `imeSetComposition` 组字 `zhong` 并提交「中」 → 状态变为「未保存」、画出黑色文字光标、磁盘文件未变 → Cmd+S → 状态变为「已保存」，磁盘上的 docx 正文是 `Hello中`，不含拼音。截图在 `/tmp/nextcowork-office-writer/`。

**端到端过程中发现并修复的既有问题**（都会让所有用 React / `nextcowork/ui` 写的插件视图白屏且零报错，单测都没覆盖到）

1. 插件运行时里 React 门面文件引的是 `./react-core.js`，但协议层只提供带 `__` 前缀的 `/__react-core.js` → 404。构建脚本改为引 `./__react-core.js`，同步改了下发测试的断言。
2. Vite lib 模式不替换 `process.env.NODE_ENV`，`ui.js` 里 framer-motion 的那一句执行时报 `process is not defined`。构建里加了 `define`。

**发现但未修的既有问题**

- 宿主关 Tab 前挽留会向插件发 `customEditor.save`，但插件运行时 `__bootstrap` 没有这个分支（落到 unsupported invocation）→ 任何报过脏的自定义编辑器，只要还脏着，Tab 就关不掉。因此画布**不**把脏状态报给这张表（`DocumentEngineFrame` 头注释写明了理由）。
- `plugin-ui/view.tsx` 的 `onDocument` / `saveDocument` 和视图主题垫片都用 `event.origin === location.origin` 过滤宿主消息；宿主窗口与插件 iframe 不同源，这个判断按理永远不成立。没有在 Electron 里验证，画布通道改用 `event.source === parent`，不受影响。

**仍未做**：完整 Ribbon（字体 / 段落 / 样式 / 插入）要等引擎侧的受审命令注册表（§5.1）；表格和演示的前端插件；右键菜单、剪贴板多 MIME、拖动选择的自动滚动；画布键盘路径的无障碍（读屏只能读到 aria-label）；关 Tab 时由主进程快照驱动的保存对话框。

### 15.8 P3 第三步：功能区命令（受审命令表）与 Writer 的「开始 / 插入」（2026-09-30）

**做法**（§5.1「格式/操作/UI 命令用有限注册表」）

- 引擎仓库新增 `native-src/command-registry.hpp`：命令按产品概念命名（`format.bold`、`style.paragraph`、`insert.table`……），映射到 UNO 命令，参数按类型重建。表里没有保存、宏和文件类命令。同一个 id 可以按文档种类映射到不同的 UNO 命令（Calc 的对齐是 `AlignLeft`）。
- helper 新增 `document.command`，在用户视图上执行，回执与 `document.input` 同形。组字进行中拒绝执行命令。状态回调按命令 id 回报（`states`），空批次拉取时补发全量状态。`capabilities.commands` 声明这份文档可用的命令。新增查询 `fonts` / `styles`。
- 宿主新增 `shared/document-engine/commands.ts`：封闭表 + 参数收窄；认不出的 id 不送进引擎，引擎声明而宿主不认识的 id 不暴露。`manager.command` 与画布输入共用账目（抽出 `interactive`）。画布通道、IPC（`documentEngine:command` / `documentEngine:list`）、iframe 报文、视图客户端 `doc.command` / `doc.list` / `doc.onResult` 都已接通。
- 画布：所有回执（输入和命令）都经 `doc.onResult` 应用；版面到手后先拉一次取光标与状态；改了模型之后 250ms 再拉一次迟到的失效区；新增 `controller.focus()`。
- `ncw.writer`：功能区「开始」页有撤销 / 重做、样式、字体、字号、加粗 / 倾斜 / 下划线 / 删除线 / 上下标、字体颜色、突出显示、清除格式、四种对齐、项目符号 / 编号、缩进；「插入」页有表格（行列对话框）和分页符。只画引擎声明了的命令，按钮的按下状态来自 `states`。文案中英两套放在 `view/messages.ts`，图标用仓库里已有的 lucide-react。

**过程中发现并修复的问题**

1. **（引擎）没有组字时的空串「取消」会删掉选区。** LibreOffice 会拿空串替换当前选区；宿主关 Tab 时会例行发一次取消，于是用户选中的整段文字被删，而且就发生在刚保存之后。宿主集成测试复现了这个问题：画布全选 → 加粗 → 保存 → 关窗后，重开读到空文本。现在 helper 逐条记录投递前的组字状态，没有组字时跳过这条取消；一致性测试加了断言。
2. **（画布）功能区命令的重绘失效晚于回执到达。** Electron 实测：点「加粗」后文件里是粗体、按钮也按下了，但画布上的像素与加粗前逐一相同。LibreOffice 的重绘失效是空闲时才发的，现在改了模型之后补拉一次。
3. **（SDK 声明）`nextcowork-view.d.ts` 里 `Select` / `NumberInput` / `Menu` 的声明与宿主实现不符**（onChange ↔ onValueChange / onCommit，受控 Menu ↔ trigger + 渲染函数）。照着声明写的插件拿不到回调。已按实现改正。

**证据**

| 项 | 结果 |
|---|---|
| 引擎一致性 | 22/22（新增功能区用例：套样式 / 倾斜 / 字号 / 撤销后 XML 核对 `Heading1`、`<w:i/>`、`<w:sz w:val="40"/>`；表外命令、坏参数、未知样式、组字中执行命令都被拒；fonts / styles 查询） |
| 宿主单测 | 命令表 5 个、manager.command 2 个、画布通道 1 个、iframe 报文 1 个；全量 435 files / 6726 tests 通过；typecheck 通过；lint 0 error |
| 宿主集成（目录 / ZIP） | 各 6/6：画布经通道全选（不计修订）→ 加粗（修订 +1、状态 `format.bold=true`）→ `.uno:Save` 被拒 → 样式列表含 `Heading 1` → 保存 → 重开读回 `Hi!` |
| Electron 端到端 | 连跑两次全部通过：画布出像素 → 键入 + 输入法 → Cmd+A → 点功能区「加粗」→ 按钮按下 → 画布正文那块 tile 的像素确实变了 → Cmd+S → 文件含 `Hello中` 和 `<w:b/>` |

**已知限制**

- 打开时拿不到全量初始状态：LibreOffice 只在属性槽被失效时才发状态回调（实测打字、全选都不会触发），所以字体 / 样式框在第一次格式操作之前是空的。要在打开后找一个不改模型、又能让属性槽刷新的办法。
- 宿主的 `IconButton` 没有 `aria-pressed`，切换类按钮的按下状态读屏读不出来（宿主组件的既有缺口，没改）。
- 撤销 / 重做按钮一直可用：`.uno:Undo` 的状态回调在 LOK 下没有出现过。
- 表格、演示的前端插件与各自的功能区还没做；Writer 的修订 / 批注、目录、页眉页脚、分节要等引擎侧的命令与查询。

### 15.9 P3 第四步：表格编辑器 ncw.sheets（公式栏、行列头、工作表标签）（2026-09-30）

**引擎**
- 新增状态回报：当前单元格公式原文 `cellFormula`、地址 `cellAddress`（LOK 回调 19 / 34，另存一份最新值，空批次拉取时补发）；行列头失效 `headersChanged`（回调 33）。
- 新增查询 `headers {x,y,width,height}`：读 `.uno:ViewRowColumnHeaders`。**实测引擎返回的是 96 DPI 像素**（行高 17、列宽 85），helper 乘 15 换成 twips，与单元格光标对齐（误差不超过 1 像素）。
- 命令表新增 `cells.enter {text}`（编辑栏写当前单元格，以 = 开头即公式）和 `cells.goto {ref}`（名称框跳转，引用格式在宿主和引擎各校验一次）。
- 一致性测试 23/23，新增用例：名称框跟随光标、编辑栏写入后单元格值为 3 且公式原文为 `=1+2`、跳转、坏引用被拒、行列头位置与单元格光标对齐、文字文档查行列头被拒。

**宿主**
- 协议、会话查询、画布通道、IPC（`documentEngine:headers`）、iframe 报文、视图客户端 `doc.headers()` / `doc.list('parts')` 都已接通；主进程收窄行列头的形状。
- 画布新增 `onViewport`，把可见区域的 CSS px 和对应的 twips 一起交给插件，用来对齐行列头与滚动。
- 画布在改了模型之后补拉两次（250ms / 1000ms）。Electron 实测，只拉一次时约四次里有一次重绘落在拉取之后。
- d.ts 同步。单测：命令收窄 1 个、回执收窄 1 个、画布通道（parts 列表 / 行列头收窄）、iframe 报文（headers）。

**插件**
- 新增 `plugins/office-common/ribbon-parts.tsx`：三个办公编辑器共用的按钮、分组、字体 / 字号框、颜色菜单（§7.3 的 office-plugin-common，放在 plugins 下，由各插件的构建经相对路径打包）。`ncw.writer` 的功能区已改为使用它。
- `plugins/ncw.sheets`：功能区（撤销重做、字体字号、加粗 / 倾斜 / 下划线 / 删除线、字体颜色、对齐、合并、自动换行、货币、百分比）、名称框 + 编辑栏（失焦或回车才提交，Esc 放弃）、引擎给出的行号列标（随滚动平移，高亮当前行列）、工作表标签（经引擎切换，Agent 改了修订号就重取标签）、Cmd/Ctrl+S 经宿主保存。

**证据**
- Electron 端到端（`npm run e2e:office`，探针改名为 `scripts/office-editors-probe.mjs`）连跑三次全部通过。文字部分同 15.8。表格部分：列标 A/B/C/D 来自引擎 → 点 B3，名称框显示 B3 → 编辑栏输入 `=1+2` 回车后显示公式原文 → 加粗 → 保存 → `sheet1.xml` 里 `<c r="B3" …><f>1+2</f><v>3</v></c>`。
- 宿主集成（目录 / ZIP）各 6/6；全量 435 files / 6728 tests 通过；typecheck 通过；lint 0 error。

**已知限制**
- 可滚动范围只到引擎报的文档尺寸（空表约 A1:R51）。要向引擎登记客户端可见区域（`setClientVisibleArea`）才能继续往外滚。
- 插入 / 删除 / 重命名工作表、拖动改列宽、冻结窗格、筛选排序、条件格式、数据透视、图表：都还没有引擎命令。
- 按钮按下状态只在状态变化时更新：从粗体单元格移到普通单元格时，引擎不一定回报 `false`（截图里可见）。与 15.8 的「初始状态」是同一个根因。
- 两个插件视图的类型检查只用了临时 tsconfig（包含 `packages/plugin-api` 的 d.ts），没有进 `npm run typecheck`。

### 15.10 P3 第五步：演示编辑器 ncw.slides（缩略图、版式、插入、放映）（2026-09-30）

**引擎**（未提交；本机重新打包 `ncw.office-runtime-0.1.0-darwin-arm64.zip`，sha256 `8a14f6fe…1426`）
- 命令表：`insert.table` 放开到演示；新增 `insert.textBox` / `insert.rectangle` / `insert.ellipse`（`CreateDirectly`，插在当前幻灯片中央，文本框直接进入文字编辑）、`slides.new` / `duplicate` / `delete` / `moveUp` / `moveDown`、`slides.layout {layout}`（只收 0 / 1 / 3 / 19 / 20 / 32）。
- 回执新增 `parts`（幻灯片 / 工作表数变了才报）；`part` 改为**量出来的**用户视图当前部分 —— 「上移幻灯片」时引擎不发 SET_PART。两个值都在拿锁之前量（锁里调引擎可能同步回调、回调也要这把锁）。
- 一致性测试 24/24（新增 `.pptx: slide commands …`）。实测：用户在文本框里编辑时，按 `part` 渲染别的幻灯片（缩略图）不会打断编辑。

**宿主**
- `commands.ts` 新增上述 id 与 `SLIDE_LAYOUTS`；`interaction.ts` 收窄 `parts`（1..100000）；单测补齐。
- `DocumentCanvas` 新增 `centered`：文档居中放在留白里；可见块与 `onViewport` 扣掉文档在滚动框里的偏移。
- `office-common/ribbon-parts.tsx`：`CommandButton` 在引擎报 `"disabled"` 时置灰（顺带修掉「撤销始终可点」）；共享 `InsertTableDialog`，`ncw.writer` 改用它。
- d.ts：`EngineCommandId` 新 id、`EngineInputResult.parts`、`DocumentCanvas.centered`；`EngineTile.pixels` 改为 `Uint8ClampedArray<ArrayBuffer>`（与实现一致；原声明下 `new ImageData(pixels, …)` 类型检查不过，而注释说可以直接这样用）。
- **插件视图纳入类型检查**：新增 `plugins/tsconfig.json` 与 `npm run typecheck:plugins`，并入 `npm run typecheck`（取对外发布的 d.ts，不取宿主源码）。
- `build-plugin-runtime.mjs` 的指纹纳入 `plugins/`（内置插件的 class 也要生成进 `ui.css`）；`e2e:office` 加上 slides 的构建。

**插件 `plugins/ncw.slides`**（viewType `ncw.slides.pptx`，`*.pptx` / `*.pptm`，图标 `image`）
- 顶栏：保存、路径 · 状态、错误、「从当前页放映」、缩放（适应窗口 / 50% / 75% / 100%；适应窗口按画布区域与幻灯片尺寸取两位小数，避免拖窗口时每像素清一次 tile 缓存）。
- 功能区「开始」：撤销重做、新建幻灯片 + 版式菜单（按引擎报的当前版式打勾）、字体字号、BIUS、上下标、颜色、突出显示、对齐、项目符号 / 编号 / 缩进。「插入」：新建幻灯片、版式、文本框、矩形、椭圆、表格。只画引擎声明了的命令。
- 缩略图栏（`thumbnails.tsx`）：整页渲染，同时只有一个请求在途，当前页优先、其次可见页（IntersectionObserver）；失效带 `part` → 那页，不带 / `all` → 当前页，`parts` / `documentSizeChanged` / `onChange` → 全部；只标过期、旧图继续显示，防抖 400ms 重画。上方是新建 / 复制 / 删除 / 上移 / 下移；方向键 / Home / End 切页。
- 放映（`slideshow.tsx`）：盖住视图的黑底层，按设备像素分块（每块 ≤ 2048px）拼成整页，当前页到手后预载下一张；→ ↓ 空格 PageDown Enter / ← ↑ PageUp Backspace / Home End / Esc / 单击；最后一张之后显示「放映结束」；Agent 在放映中改了文档就丢缓存重画。F5 从头、Shift+F5 从当前页。
- 状态栏「第 n / N 张」；Cmd/Ctrl+S 截走经宿主保存。

**踩到的坑（已修，探针守着）**
- 版面查询带**别的** part 时，helper 会把用户视图切过去量完再切回来（`layout()` 的 `setPart`）。编辑器起初在每次 `onChange` 后用 `layout(0)` 取幻灯片尺寸，而用户自己每次改动也会触发 `onChange` —— 于是「插入文本框」之后文字编辑状态立刻被结束，键入的字全部落空、文件里没有文本框。引擎层复现：`layout part:0` 之后按键回执 `cursorVisible:false`、存盘无文字；`layout` 不带 part 则正常。修法：编辑器只量当前页（每张幻灯片一样大）。**引擎侧的隐患仍在**：任何在用户编辑中查询别的 part 版面的调用方都会打断编辑，后续应让 helper 不切换视图就能量尺寸，或在组字 / 文字编辑中拒绝。

**证据**
- Electron 端到端 `npm run e2e:office`：修复后连跑三次全部通过（每次 30 项断言，含文字、表格两段回归）；修复前第一次跑失败在「文本框里键入的文字进了 pptx」。演示部分：从文件树打开 pptx → 幻灯片画布与缩略图出像素 → 缩略图 1 张 → 「新建幻灯片」后 2 张、状态栏「第 2 / 2 张」→ 版式选「空白」→ 插入文本框后画出光标且 1.2s 后仍在 → 键入 → Esc → Cmd+S → `ppt/slides/slide2.xml` 无 `<p:ph`、含 `Probe slide` → 放映画出当前页 → Esc 退出。
- 宿主集成 `test:office-native`：目录方式 6/6、ZIP 方式 6/6。
- 全量 435 files / 6730 tests 通过；typecheck（含新增的 typecheck:plugins）通过；lint 0 error（4 个既有 warning）。

**已知限制**
- 备注、母版编辑、动画 / 切换、插入图片与媒体、缩略图拖动排序：没有引擎命令，不画。
- 放映只铺满标签页（iframe 有意不给 `allow="fullscreen"`，防止插件全屏伪造界面），静态渲染，无动画与媒体。
- 缩略图在文本框编辑中会画出引擎的编辑态外观（选中框），退出编辑、防抖后重画才恢复。
- 同一文件开两个 Tab 仍共用一个用户视图（编辑器共性问题，未修）。
- 引擎本轮改动未提交、CI 未跑（Windows / macOS x64 未验证）。

### 15.11 P3 第六步：编辑器共性问题 —— 功能区初始状态与复位、开关按钮的读屏状态、表格滚动范围、查别的部分不打断编辑（2026-09-30）

**根因（先量再改；探索脚本在 `/tmp/ncw-explore/states2.mjs`、`viewport*.mjs`、`farrender.mjs`）**
- 功能区状态**不是缺失，是晚到**：LibreOffice 在空闲时才发 STATE_CHANGED。实测：打开后第一批约 1.5 s（演示约 0.7 s）；移动光标 / 点到别的单元格后，加粗等状态约 0.7 s 后才变（引擎确实会报 `false`）。画布原先只在版面到手时拉一次、只在「改了模型」之后补拉 —— 于是打开时字体框、样式框是空的，离开粗体单元格后加粗按钮不复位。
- 表格可滚动范围：引擎报的尺寸**只跟着单元格光标长**（每次 PageDown 翻倍）；`setClientVisibleArea` 不让它长。但尺寸之外照样能渲染、点选（点到后尺寸随之变大）、查行列头。不登记可见区域时 PageDown 一次跳 28、76、130 行，越翻越远。
- 15.10 里的隐患：带**别的** part 的版面查询在用户视图里切页去量，会结束用户正在做的文字编辑。

**引擎**（未提交；已重新打包，`ncw.office-runtime-0.1.0-darwin-arm64.zip` sha256 `4b912558…c47`）
- `layout {part}` 量别的部分时在 Agent 视图里切（当前部分每个视图各自的），用户视图不动；没有独立 Agent 视图时保持旧行为。
- 新输入事件 `{type:'viewport', x, y, width, height}` → `setClientVisibleArea`；组字中途也放行（不碰文字；否则组字时滚一下，同一批的组字跟着被拒）。能力里声明 `interaction.visibleArea: true`。
- 一致性测试 26/26，新增：量别的幻灯片之后文本框仍在编辑、键入进了文件；登记 30 行高的可见区域后 PageDown 一次翻 25–32 行、组字中途登记可见区域后组字照常提交进单元格、坏参数被拒。

**宿主**
- `canvas-input.ts` 新增 `LatePulls` / `needsLatePull` / `PULL_AFTER_INPUT_MS=[250,1000,2000]` / `PULL_AFTER_OPEN_MS=[500,1500,3000]`：用户输入、改了模型、光标 / 选区 / 当前部分动了，都排补拉（重排不堆积；补拉自己的回执不续补拉）；版面第一次到手时按打开的时刻表补拉。
- `IconButton` 新增 `pressed`（输出 `aria-pressed`），与 `active` 分开 —— 宿主里的 `active` 多半是「面板开着 / 当前项」，不是开关。`CommandButton` 只在状态是 `true` / `false` 时报按下状态（撤销这类报 `enabled` / `disabled` 的不是开关）。
- `interaction.ts`：`viewport` 事件的收窄；只有引擎声明了 `visibleArea` 才放行 —— **旧版引擎不认识它，会把整批连同按键一起拒掉**。
- `DocumentCanvas`：可见区域防抖 150 ms、变了才登记（直接进队列，不排补拉）；表格的可滚动 / 可渲染范围 = 引擎尺寸与「已到达的最远可见区域再留一屏」取大（`viewport.ts` 的 `grownSheetReach`，只长不缩，上限 2^28 twips ≈ Calc 最后一行），换表时清零。
- d.ts：`IconButton.pressed`、`EngineInputEvent` 的 `viewport`、`interaction.visibleArea`。
- 单测：补拉排程（重排、取消、时刻留余量、补拉不续补拉）、viewport 收窄与能力门控、合批只留最后一个可见区域、`grownSheetReach`。

**证据**
- Electron 端到端新增 7 项断言：打开后不动手字体框就有值；加粗按下后 `aria-pressed="true"`、撤销没有 `aria-pressed`；表格加粗后**等补拉走完**再点到别的格子，加粗复位；滚轮滚过已用区域，行号到 153 行以后、余量里画出了格子、点选后名称框跟到 A159。
- **反证**：把画布临时改回旧的补拉规则（只在改了模型后拉 [250,1000]、打开时不补拉）重跑，「字体框初始值」与「离开粗体单元格复位」两项都失败；复原后通过。复位那一项起初在旧规则下也能过（加粗那一轮的 1 s 补拉顺手拉到了 false），已改为先等 2.5 s 再点。
- 端到端（新引擎包）共 8 次：4 次全部通过（37 项）；4 次在前几步截图 / 点选时超时。其中一次加了诊断，确认渲染进程正常应答，但 Electron 的窗口全部不可见、且有一个被最小化 —— 多半是探针在用户屏幕上反复弹窗、窗口被收起了（推断，未直接观察到）；另外 3 次症状相同，当时没有诊断。探针已改为截图超时只记一笔继续，挂掉时打印窗口可见状态。（换引擎包之前另有 1 次全部通过，即状态补拉改动的首跑）；宿主集成目录 / ZIP 各 6/6；全量 435 files 通过（`release-notes` 偶发失败一次，单独重跑通过）；typecheck（含插件）通过；lint 0 error。

**仍未做 / 需要决定**
- 同一文件开两个 Tab 仍共用一个用户视图。两条路：(a) helper 按视图区分输入 / 渲染 / 状态 / 回调（协议改动大）；(b) 同一文件已开时切到那个 Tab 而不是再开一个（影响所有自定义编辑器的 Tab 行为）。**等用户选**。
- 补拉时刻是本机量的；更慢的机器上首批状态若晚于 3 s，字体框要等用户第一次动手。根治要引擎主动推送（协议目前是请求—应答）。
- 表格：冻结窗格、拖动改列宽、工作表增删改名仍没有引擎命令。
- 15.9「已知限制」里的滚动范围与按钮不复位、15.8 的「初始状态」由本节解决；15.10 里记的引擎隐患（查别的部分打断编辑）已在引擎侧修掉。
