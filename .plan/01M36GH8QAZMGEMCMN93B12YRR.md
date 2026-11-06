# 聊天图片预览与用户消息附件布局

## 已核实的现状与确定的行为

- `src/renderer/src/views/chat/AttachmentTray.tsx` 将已上传图片显示为不可点击的 `ncw://` 缩略图；`Composer.tsx` 已把草稿附件传给托盘。`MessageImage.tsx` / `ImageLightbox.tsx` 已实现转录图片的键盘可达灯箱、关闭后焦点归还和多图切换，复用它们，不另建弹窗或 IPC。
- `Thread.tsx` 的 `UserBubble` 目前把文字、`file_ref` 和图片放在同一个气泡中；`MentionText.tsx` 用 `parseMentions` 将正文里的本地路径 Markdown 链接渲染成可点 chip；`MessageFileRef.tsx` 与 `openFileReference` 已支持核验文件后在右侧打开。
- 用户已确定：正文保留可点击的 `@` 引用，下方也显示对应文件卡片；草稿图仅上传完成后可预览；图片约 96px 高、文件卡片约 32px 高，多项换行、不限制总列表高度；同一文件路径在下方去重。消息发给模型的内容不改变。

## 实施步骤

1. 修改 `src/renderer/src/views/chat/AttachmentTray.tsx`：仅对 `status === 'done'` 且附件 MIME 为图片的条目，把现有缩略图变成独立的 `<button type="button">`，点击挂载已有 `ImageLightbox`；灯箱传单张 `{ mime, dataRef: attachment.url }`，关闭时卸载，保留现有删除、重试、上传中/失败的状态与交互。预览按钮用已有 `chat.zoomImage` 文案作无障碍标签，确保点击缩略图不会触发删除/重试；不尝试为未上传或失败的本地文件创建 object URL。需要时给预览按钮加 `app-no-drag`。
2. 修改 `src/renderer/src/views/chat/Thread.tsx` 的 `UserBubble`：保留现有正文 `MentionText`（含可点 `@` chip）、编辑按钮和图片/文件引用的空消息判定；将 `file_ref` 文件卡片和图片从 `bg-tint` 文字气泡移出，渲染为同一条用户消息右对齐、气泡正下方的独立紧凑附件区域。仅有图片或文件时不画空文字气泡，但仍显示附件。保留图片 `siblings` / `index` 分组和可选 `workspaceId` 行为；编辑态沿用既有编辑器，不改编辑/发送语义。
3. 在 `src/renderer/src/views/chat/` 同目录新增一个纯逻辑 `user-message-attachments.ts`（按仓库规则写需求/不变式文件头注释）：基于已有 `parseMentions(text)` 取出 `kind === 'mention'` 的文件路径及显示名，合并消息 `file_ref`，按路径首次出现顺序去重；图片保持原有 part 顺序且不按 URL 去重。同一路径来自两种来源时只显示一张文件卡片；原始正文和消息 parts 不改写、不做磁盘查找。`UserBubble` 使用该结果渲染下方文件卡片，点击仍走 `openFileReference`；无工作区上下文时保留只读卡片而非无效按钮。正文中的普通 URL 不生成卡片，技能标记不生成文件卡片。
4. 调整 `MessageImage.tsx` 的缩略图尺寸入口（例如可选紧凑样式 prop，仅用户附件区域传入），使用户消息下方图片最大高度约 96px、宽度有界、`object-contain` 不裁切，助手/其他位置仍保持原有 320px 上限；如需要同步减小图片失败/外部绝对路径占位尺寸，仅对紧凑入口生效。`MessageFileRef.tsx` 已是 `h-8`，沿用其尺寸与文件名截断；附件区使用有限宽度、自动换行、右对齐和小间距，不新增颜色 token、不截断整个列表。同步更新受行为变化影响的既有注释，保留其关于纯图片消息不能消失、文件路径不可显示为裸文本和焦点行为的原始理由。

## 测试与验证

- 在 `src/renderer/src/views/chat/__tests__/` 新增或扩展 `.test.ts`：纯函数覆盖 `@`/手写本地文件链接、URL 与技能排除、相同路径重复及跨 `file_ref` 去重、首现顺序、原消息不变；DOM 测试覆盖仅图片消息不出现空文字气泡、混合消息附件区位于气泡之外、`@` chip 保持正文可点且下方只有一张卡片、文件点击右侧打开/失效时不打开、无工作区时只读、多个图片仍能灯箱翻页。
- 草稿托盘 DOM 测试覆盖上传完成图片点击打开/关闭灯箱、图片与附件的控制按钮互不干扰、上传中/失败及非图片不出现预览按钮；沿用现有 JSDOM + `createElement` 测试惯例，不用 `.test.tsx` 或引入新依赖。
- 执行仓库已有的 `npm run typecheck:web`、`npm test`、`npm run lint`；必要时在应用里人工核对图片高度、折行、右侧文件面板与 Esc/焦点恢复。并发工作区已有未提交改动，实现前重新读取待改文件，仅做精确局部修改，不覆盖其他改动。

## 边界

- 不改 `ContentPart` 持久化结构、发送编码、附件协议、上传流程或文件存在性检查；`ncw://` 失效图片沿用已有占位行为，磁盘绝对路径图片沿用不可在渲染层直接预览的限制。
- `parseMentions` 将正文里的本地文件 Markdown 链接统一视作文件引用，历史消息也会按这一既有解析规则显示下方卡片；无法从目前的消息格式中可靠区分真正通过 `@` 菜单插入的引用与用户手写的同形链接，不虚构来源信息。
