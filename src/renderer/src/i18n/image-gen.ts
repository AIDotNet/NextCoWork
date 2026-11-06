/**
 * 对话内生图/改图的文案(设置 › 模型 › 图片生成 页脚的「对话生图」开关与
 * 「对话生图使用的模型」两行,以及对话模型选择器把误选的图片模型换回文本模型时的 toast)。
 *
 * 需求:生图模型由用户点名(必选、不设自动档,见 `AppSettings.imageModel`),
 * 于是界面上必须回答三个问题:选的是哪个、没选会怎样、选的那个失效了怎么办。
 * 生图另有自己的开关(`AppSettings.imageGenerationEnabled`),说明行里要讲清它
 * 不受「联网搜索」管 —— 原先两者绑在一起,正是「找不到工具」那次事故的根因。
 * 这个域此前不存在,按 §6.3 新建独立文件,不往 index.tsx 那三千行里堆。
 *
 * `imageGen.card.*` 是对话里那张生图卡片(`views/chat/ImageGenDetail.tsx`)的文案:
 * 加载格的状态字与读屏标签、成品网格的放大入口、部分失败时的那行说明。
 * `imageGen.card.prompt*` 是卡片底部提示词区的标签、展开/收起与复制按钮。
 * `imageGen.card.image*` 是每张图右上角那排动作(复制图 / 下载图)的标签与反馈 ——
 * 和提示词的复制**分开**:两个动作的对象不同(一段文字 vs 一张几 MB 的图),
 * 「已复制」这四个字在同一个卡片里出现两次时,用户要能一眼分出是哪一个。
 */
export const imageGenZh = {
  'imageGen.enabled': '对话生图',
  'imageGen.enabledHint': '关闭后 Agent 不能生成或编辑图片。不受输入框「联网搜索」开关影响。',
  'imageGen.model': '对话生图使用的模型',
  'imageGen.modelHint': '对话里「生成图片 / 编辑图片」都由它完成。',
  'imageGen.modelPick': '选择图片模型',
  'imageGen.modelUnset': '未选择——对话里将无法生成图片。',
  'imageGen.modelMissing': '所选模型已不可用，请重新选择。',
  'imageGen.noImageModels': '还没有图片模型，先在左侧添加一家图片供应商。',
  'imageGen.notChatModel': '图片模型不能用于对话，已切换为 {model}',
  'imageGen.card.region': '生成的图片',
  'imageGen.card.generating': '生成中',
  'imageGen.card.editing': '编辑中',
  'imageGen.card.slot': '第 {index}/{total} 张',
  'imageGen.card.open': '放大查看第 {index} 张图片',
  'imageGen.card.partial': '成功生成 {done}/{total} 张',
  'imageGen.card.prompt': '提示词',
  'imageGen.card.promptExpand': '展开',
  'imageGen.card.promptCollapse': '收起',
  'imageGen.card.promptCopy': '复制提示词',
  'imageGen.card.promptCopied': '已复制',
  'imageGen.card.promptCopyFailed': '复制失败',
  'imageGen.card.imageCopy': '复制图片',
  'imageGen.card.imageCopied': '图片已复制',
  'imageGen.card.imageCopyFailed': '复制图片失败',
  'imageGen.card.imageSave': '下载图片',
  'imageGen.card.imageSaved': '图片已保存',
  'imageGen.card.imageSaveFailed': '保存图片失败'
}

export const imageGenEn: Record<keyof typeof imageGenZh, string> = {
  'imageGen.enabled': 'Image generation in chat',
  'imageGen.enabledHint': 'When off, the agent cannot generate or edit images. Not affected by the Web search switch.',
  'imageGen.model': 'Model for image generation in chat',
  'imageGen.modelHint': 'Used by the generate/edit image tool in chat.',
  'imageGen.modelPick': 'Choose an image model',
  'imageGen.modelUnset': 'Not selected — image generation is unavailable in chat.',
  'imageGen.modelMissing': 'The selected model is unavailable. Pick another one.',
  'imageGen.noImageModels': 'No image models yet — add an image provider on the left first.',
  'imageGen.notChatModel': 'Image models cannot chat; switched to {model}',
  'imageGen.card.region': 'Generated images',
  'imageGen.card.generating': 'Generating',
  'imageGen.card.editing': 'Editing',
  'imageGen.card.slot': 'Image {index} of {total}',
  'imageGen.card.open': 'View image {index}',
  'imageGen.card.partial': '{done} of {total} generated',
  'imageGen.card.prompt': 'Prompt',
  'imageGen.card.promptExpand': 'Show more',
  'imageGen.card.promptCollapse': 'Show less',
  'imageGen.card.promptCopy': 'Copy prompt',
  'imageGen.card.promptCopied': 'Copied',
  'imageGen.card.promptCopyFailed': 'Copy failed',
  'imageGen.card.imageCopy': 'Copy image',
  'imageGen.card.imageCopied': 'Image copied',
  'imageGen.card.imageCopyFailed': 'Copying the image failed',
  'imageGen.card.imageSave': 'Download image',
  'imageGen.card.imageSaved': 'Image saved',
  'imageGen.card.imageSaveFailed': 'Saving the image failed'
}
