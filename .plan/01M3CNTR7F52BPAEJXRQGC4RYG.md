# 图片生成改造：生图模型必选设置 + 改图 URL 入参 + 对话模型选择器只显示文本模型

## 背景与已确认的决定

上一轮已经落地了对话内生图（`generate_image` 工具 + `kernel/image-gen.ts` 桥 + 图片模型自动识别）。
本次四项决定（与用户逐条确认过）：

1. **「图片插件」= 内置功能，不走插件系统**。插件工具的回传通道只有文本 + 卡片
   （`src/main/plugin/tools.ts` 的 `normalizeToolResult` 不支持 `output.images`），
   真插件做的生图进不了模型上下文；所以扩展现有 `generate_image` 工具，不新建插件包。
2. **URL 由模型工具入参传入**：`image` 参数除了 `"latest"` 和工作区路径，还接受
   **http(s) 图片 URL**。URL 可下载且是图 → 走 edit（改图）；**下载失败 / 404 / 不是图片
   → 工具报错并说清原因，绝不静默回落成文生图**。
3. **「对话生图使用的模型」设置放在 设置 › 模型 › 图片生成**，选择器只显示图片模型。
   **必选、不允许自动**：生成和改图共用这一个设置，只用点名的那个模型，失败即失败
   （不再按 priority 跨家兜底）。未选择 = `generate_image` 整体不下发。
4. **所有对话模型选择器只显示文本模型**（输入框、通用页默认模型/默认子代理/AI审核模型/
   目标判定模型/压缩模型、工作区默认模型、Hooks 面板）。存量配置里已选中图片模型的，
   **打开该选择器时**就地校正为第一个文本模型并 toast 提示（不做启动时全局改写）。

## 现状要点（已在代码里核实）

- `Composer.tsx:2372` `ModelPicker`、`GeneralPage.tsx`（`RoleModelPicker`×3 + `Select`×2）、
  `EditWorkspaceDialog.tsx:203`、`HooksPanel.tsx:177` 都不过滤模态。
  `modelOptions`（`settings/pages/model/enabled-models.ts:72`）是其中多数的共同构建器。
- 辅助模型设置（`compactModel` / `permissionReviewerModel` / `goalEvaluatorModel` /
  `defaultModel`）都是「别名 + providerId 成对」的形状（`shared/domain/settings.ts`），
  `mergeSettings` 有成对写入规则，新设置照抄这条形状。
- `ImageModelPage` 是 `ModelPage.tsx:75` 挂的子页，**当前不收 `SettingsPageProps`**。
- `model-selection.ts` 的 `selectModelBinding` 已有「钉住 providerId 就不做任何回退」的语义，
  生图模型的解析直接复用它。
- `image-gen.ts` 现按 provider/alias priority 逐家兜底（`candidatesOf`），要改成单模型点名。

## 改动清单

### 一、shared 纯逻辑

1. **`src/shared/domain/provider.ts`**：新增
   - `isImageModelAlias(alias: ModelAlias): boolean` —— 判据 `modality === 'image' || capabilities.imageOutput === true`
     （从 `ImageModelPage.tsx:36` 的本地 `isImageModel` **下沉**，渲染层各处换用它，不写第二份）。
   - `isChatModelAlias(alias: ModelAlias): boolean` —— `!isImageModelAlias(alias) && alias.capabilities.textOutput !== false`。
2. **`src/shared/domain/model-selection.ts`**：新增纯函数
   `firstChatModelAlias(models, providers): ModelAlias | null`（校正兜底：第一个启用的文本模型，
   provider 也须 enabled）。
3. **`src/shared/domain/settings.ts`**：
   - `AppSettings` 增 `imageModel: string` + `imageModelProviderId?: string`（照 `compactModel`
     那对的注释与形状；默认 `''` = 未选择）。
   - `mergeSettings` 的**成对写入规则**补上这对（改 `imageModel` 的 patch 必须同时给
     `imageModelProviderId`，否则留下「新别名 + 旧供应商」脏配对）。
   - 加载校验照既有各字段的做法（类型不对 → 落默认值）。
4. **测试**：`src/shared/domain/__tests__/settings.test.ts` 增成对写入用例；
   新增 `isImageModelAlias` / `isChatModelAlias` / `firstChatModelAlias` 的用例
   （放 `shared/domain/__tests__/`，跟邻居同目录）。

### 二、运行时（生图桥改单模型点名）

5. **`src/main/kernel/image-gen.ts`**：
   - `ImageGenDeps` 增 `preferredModel(): { alias: string; providerId: string | undefined } | null`。
   - `candidatesOf` 改为**单模型解析**：用 `selectModelBinding`（钉住 providerId 不回退）
     解析 `preferredModel()`，再验 `isImageModelAlias` + 双方 `enabled`。解析不到 → 空候选：
     `available()` false，`generate`/`edit` 抛
     `No image model is selected. Choose one in Settings > Models > Image generation.`（可行动指引）。
   - 失败**不再换下一家**：单模型失败直接带 `provider.name: reason` 抛出。
   - 导出 `downloadImage(fetchFn, rawUrl, signal): Promise<ImageSource>`，URL 下载的**唯一实现**：
     - 每一跳都过 `ssrfRisk`（含首跳）；`redirect: 'manual'` 跟随，≤5 跳；
       跨域重定向允许（CDN 签名跳转常见）但**每一跳重新过 ssrfRisk**——环回/内网/file 一律拒。
       注释里写清与 `web.ts`（拒跨域）取舍不同的理由。
     - `AbortSignal.timeout(30s)` + 用户 signal 合并（同 `firstImage` 现有写法）。
     - 大小上限 `MAX_ATTACHMENT_BYTES`（32MB）：content-length 预检 + 读取时硬截。
     - mime 用 **shared 的 `imageMimeOfBytes`**，`null` → 报 `Unrecognized image format`，**不再假定 png**。
   - `firstImage` 的 url 分支改调 `downloadImage`（顺带修掉「重定向绕过 ssrfRisk」的缺口）；
     `b64_json` 分支加同一大小上限。
   - 删掉本地 `sniffMime`，`inlineImage` 接受 `imageMimeOfBytes` 的结果（mime 联合类型天然收窄）。
   - 文件头注释同步：需求锚点加「生图模型由设置点名，必选；失败不跨家兜底」。
6. **`src/main/runtime.ts`**：装配处增
   `preferredModel: () => { const s = store.getSettings(); return s.imageModel === '' ? null : { alias: s.imageModel, providerId: s.imageModelProviderId } }`
   （主进程是设置唯一权威，桥不缓存）。

### 三、工具：`image` 入参支持 URL

7. **`src/main/kernel/tool/builtin/image.ts`**：
   - 入参解析三分支（显式比较，注释写清为什么能靠字符串形状区分）：
     - `image === 'latest'` → 现行为不变；
     - `image` 以 `http://` / `https://` 开头 → `downloadImage(ctx.host.fetch, …)`；
       **失败一律 `toolFail` 带原因**（404/超时/非图片/内网地址），且**绝不回落生成**——
       需求注释写明：模型传了 URL 就是要改这张图，默默画一张新图会得到答非所问的结果；
     - 其余 → 工作区路径，现行为不变。
   - schema 的 `image` 描述与工具 description 更新（模型可见的英文，说明三种取值）。
8. **测试**（`tool/builtin/__tests__/image.test.ts`）：
   - URL 成功 → `edit` 收到解析好的 data URL source；
   - URL 404 / 非图片字节 → `toolFail` 且 `generate` **未被调用**（守「不回落」）；
   - URL 指向环回地址 → 拒绝；
   - `latest` / 路径 / 无图报错的既有用例不变。
9. **测试**（`kernel/__tests__/image-gen.test.ts` 改写）：
   - 未选择模型 → `available()` false 且错误信息指向设置页；
   - 点名的模型解析不到 / 被停用 → 同上；
   - 点名生效 → 只发一次请求到那家，失败不换家；
   - `downloadImage`：重定向逐跳校验、超限拒绝、非图片 mime 拒绝。

### 四、设置 UI：图片生成页

10. **`src/renderer/src/settings/pages/model/ModelPage.tsx:75`**：把 `settings`/`patch`
    传给 `ImageModelPage`（签名改成收 `Pick<SettingsPageProps, 'settings' | 'patch'>`）。
11. **`src/renderer/src/settings/pages/model/ImageModelPage.tsx`**：
    - 新增一行「对话生图使用的模型」：控件用 `ProviderModelMenu`（两级：供应商→模型），
      行数据**只列图片模型**（`isImageModelAlias`）；未选择显示空态 +
      `imageGen.modelUnset` 提示「不选择则对话里不能生图」；解析不到绑定（被删/停用）显示
      `imageGen.modelMissing`。
    - 写入 `patch({ imageModel: alias, imageModelProviderId: providerId })`（成对无条件写）。
    - 本地 `isImageModel` 换成 shared 的 `isImageModelAlias`。
    - 文件局部风格是双引号 + 分号，**跟随**。

### 五、对话模型选择器：只显示文本模型 + 打开时校正

过滤**收口在两处**，判据都是 shared 的 `isChatModelAlias`（不 copy-paste 判断）：
`modelOptions()`（`enabled-models.ts`，它服务的四个调用方全是对话语境）与
`Composer.tsx` 的 `rows` 构建。校正逻辑抽一个共用小函数（放 `model-selection.ts`
旁边的渲染层 hook 或同文件纯函数 + 调用点两行），行为统一为：

> 打开选择器时，若当前选中解析为非文本模型 → `onChange(firstChatModelAlias)` +
> toast（`stores/toast.ts`，文案走 i18n）。

12. **`src/renderer/src/views/chat/Composer.tsx`**（双引号+分号局部风格）：
    `ModelPicker` 的 `rows` 只列文本模型（供应商过滤也基于过滤后的模型，避免空供应商组）；
    菜单打开时校正。
13. **`src/renderer/src/settings/pages/GeneralPage.tsx`**：`RoleModelPicker`×3
    （默认模型/默认子代理/压缩模型）与 `Select`×2（AI审核模型/目标判定模型）同样过滤；
    控件渲染/展开时校正（这两类是「页面打开」粒度，符合决定 4）。
14. **`src/renderer/src/shell/EditWorkspaceDialog.tsx`**、
    **`src/renderer/src/views/extensions/hooks/HooksPanel.tsx`**：同上。
15. **`src/renderer/src/components/ProviderModelMenu.tsx`**：若现有 API 没有打开回调，
    加一个可选 `onOpenChange?: (open: boolean) => void`（不动现有调用方行为）。

### 六、i18n

16. **新文件 `src/renderer/src/i18n/image-gen.ts`**：`imageGenZh` / `imageGenEn`
    （带参数的用 `type Params = Record<string, string | number>`），`index.tsx` spread 一行。
    键：
    - `imageGen.model`「对话生图使用的模型」/ `imageGen.modelHint`
    - `imageGen.modelUnset`「未选择——对话里将无法生成图片」
    - `imageGen.modelMissing`「所选模型已不可用，请重新选择」
    - `imageGen.notChatModel`（toast，带 `{model}` 插值）「图片模型不能用于对话，已切换为 {model}」
    zh-CN 与 en-US 同步补（`i18n/index.test.ts` 校验键一致）。

## 边界情况

| 情况 | 行为 |
|---|---|
| 从未选过生图模型（升级后首次） | `generate_image` 不下发；图片页空态引导选择 |
| 点名的生图模型被删/停用 | `available()` false，工具不下发；设置页显示 `imageGen.modelMissing` |
| 一家都没有图片模型 | 图片页选择器无可选项（空态），工具不下发 |
| URL 404 / 超时 / 非图片 / 环回地址 | 工具报错带原因，**不生成新图** |
| URL 重定向（含跨域） | ≤5 跳、每跳重过 `ssrfRisk` |
| 图 > 32MB | 拒绝（下载与 b64 两个分支同上限） |
| 存量对话模型选中了图片模型 | 不动配置；打开选择器时校正 + toast |
| 审核/目标判定/压缩模型「空=跟随」的空值 | 空值选项保留，校正只针对已选中的非文本模型 |

## 明确不做（上轮 review 遗留，另行决定）

- `image-gen.ts` 凭证形状问题（keypair 凭证的 SSH 私钥可能被当 Bearer 头）——本次不动，只在 review 里记着；
- `syncClientModels` 与 `setAliases` 对图片能力位的形状差异——不动；
- 多图编辑、把图存到工作区——不在本次范围。

## 验证

```bash
npm run typecheck:web   # 渲染层 + shared
npm run typecheck       # 全仓
npm test                # vitest
npm run lint
```

手测清单：
1. 设置 › 模型 › 图片生成：选择生图模型 → 对话里「画一张」成功；不选 → 工具不出现；
2. 对话里让模型改一张远程 URL 的图：有效 URL 改图成功；坏 URL 报错且**不**画新图；
3. 输入框模型选择器、通用页五个选择器、工作区默认模型、Hooks 面板均不出现图片模型；
   人为把默认模型改成图片模型后打开选择器 → 自动切回第一个文本模型 + toast；
4. 切换语言（zh/en）检查新增文案两份齐全。
