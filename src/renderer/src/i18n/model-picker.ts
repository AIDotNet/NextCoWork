/** 模型选择弹层(`components/ModelPickerMenu.tsx`)的文案。 */
type Params = Record<string, string | number>

export const modelPickerZh = {
  'modelPicker.favorites': '收藏',
  'modelPicker.search': '搜索模型…',
  'modelPicker.noMatch': '没有匹配的模型',
  'modelPicker.noFavorites': '还没有收藏的模型,点模型右侧的 ☆ 加入',
  'modelPicker.favorite': '收藏',
  'modelPicker.unfavorite': '取消收藏',
  'modelPicker.providerUnavailable': '该供应商已停用或没有可用模型',
  'modelPicker.thinking': '推理强度',
  'modelPicker.context': '上下文窗口',
  'modelPicker.default': '默认',
  'modelPicker.autoHint': ({ level }: Params) => `自动(${String(level)})`,
  'modelPicker.contextUnavailable': ({ window }: Params) => `该模型最大窗口为 ${String(window)},不支持扩展`
}

export const modelPickerEn = {
  'modelPicker.favorites': 'Favorites',
  'modelPicker.search': 'Search models…',
  'modelPicker.noMatch': 'No matching models',
  'modelPicker.noFavorites': 'No favorites yet — click ☆ next to a model to add it',
  'modelPicker.favorite': 'Add to favorites',
  'modelPicker.unfavorite': 'Remove from favorites',
  'modelPicker.providerUnavailable': 'This provider is disabled or has no available models',
  'modelPicker.thinking': 'Reasoning effort',
  'modelPicker.context': 'Context window',
  'modelPicker.default': 'Default',
  'modelPicker.autoHint': ({ level }: Params) => `Auto (${String(level)})`,
  'modelPicker.contextUnavailable': ({ window }: Params) => `This model tops out at ${String(window)}`
}
