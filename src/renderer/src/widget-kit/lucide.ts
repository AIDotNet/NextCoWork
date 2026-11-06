/**
 * `lucide-react` —— TSX widget 里的图标,和宿主界面同一套。
 *
 * 全量转发:widget 是模型写的,用哪个图标事先不知道,摇树无从谈起。产物几百 KB,
 * 但只在用到图标的 widget 里加载(外壳按编译产物里的 `require` 决定加载哪些模块)。
 */
export * from 'lucide-react'
