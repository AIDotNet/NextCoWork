/**
 * 展开区里所有「产物块」共用的那张卡。
 *
 * ★★ 需求:行本身是无边框的文本(§ `row.tsx`),但**展开之后露出来的东西是产物** ——
 * 一段终端输出、一份代码、一列命中、一张 diff。它们必须看起来是"一块被端出来的
 * 东西",否则和上下的行糊成一片,用户分不清哪里是这次调用的结果、哪里是下一行。
 *
 * ★★ 底色**不能用 `bg-canvas`**。转录区本身就铺在 canvas 上(图片主题下那一层
 * 甚至是透明的,壁纸直接透出来),于是"卡片"和背景同色 —— 表现就是
 * 「展开之后背景是透明的,根本看不出有张卡」。这正是这条常量存在的原因:
 * 往上提一档到 `surface-raised`,再加一圈**装饰性** `stroke` 描边
 * (不是 `border`,后者受可读性护栏改写,见 `theme.css` 里那段)。
 *
 * ★ 圆角/内边距不开放成参数:四种产物块必须长成同一张卡,不然展开区又会变成
 * 「每个工具一套配方」——那正是 `ui/Surface.tsx` 文件头记的那次教训。
 */

/** 卡壳:圆角 + 描边 + 比转录底亮一档的底色。内容自己负责内边距。 */
export const DETAIL_CARD_CLASS =
  'overflow-hidden rounded-[10px] border border-stroke bg-surface-raised'

/** 失败那一档:同一张卡,只把底色和描边换成 danger —— 形状一致才看得出是同一层东西 */
export const DETAIL_CARD_DANGER_CLASS =
  'overflow-hidden rounded-[10px] border border-danger/35 bg-danger/[0.07]'
