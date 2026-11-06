/**
 * 插件市场条目 —— 主进程与渲染层共用的只读投影。
 *
 * ★ 单独一个文件而不是放进 `state.ts`:那边描述的是**装在本机上的**插件
 * (带状态、诊断、授权),这边描述的是**市场上的**条目(带下载量、作者)。
 * 两者只有名字像,字段几乎不重叠 —— 合成一个类型会让「这个字段在哪种场景
 * 下才有值」变成每个调用点都要重新判断的事。
 */

export interface PluginMarketItem {
  /** `publisher.name` —— 和本机已装的那份比对靠它,不是 slug */
  pluginId: string
  slug: string
  publisher: string
  displayName: string
  description: string
  category: string
  iconUrl: string | null
  downloadCount: number
  version: string | null
  /** `^0.2.0` 这类。列表已按它过滤过,留着是为了在详情页说清「要求什么版本」 */
  engines: string | null
  /**
   * 这一版声明的**必选能力**。
   *
   * ★ 列表里就要显示,不能等到点了安装才说。装插件这个决定的全部信息量
   * 就在这一行里 —— 把它藏到下一屏,等于让用户先决定再了解。
   */
  permissions: string[]
  /**
   * 这一版自带的 Skill 名字。
   *
   * ★ 和 `permissions` 同样**列表里就要显示**,理由却不同:能力回答「它能对我
   * 做什么」,skill 回答「它会往模型的目录里塞什么」。后者是插件里唯一一类
   * 装上之后**每一轮对话都在场**的贡献 —— 藏到详情页,用户就是在不知情的
   * 情况下改变了每次提问的上下文。
   *
   * 名字来自服务端解包时读到的 `SKILL.md` frontmatter,不是清单里的路径。
   * 老服务端不返回这个字段,所以客户端按**空数组**处理(不是「未知」)——
   * 少显示一行,比对着一个没有内容的标题栏强。
   */
  skills: string[]
  author: string
}

export interface PluginMarketPage {
  items: PluginMarketItem[]
  hasNext: boolean
  page: number
}

/**
 * 「这个已装的插件有新版」—— 主进程算完之后推给渲染层的一条。
 *
 * ★ 整个判定在主进程完成,渲染层不做版本比较、也不碰 slug。理由是
 * `escalatedPermissions` 那一条必须拿 `approvedRequired` 算,而它**不在**
 * `InstalledPlugin` 的投影里(见下面的字段注释)。判定一半在这边一半在那边,
 * 就会出现「横幅说要重新授权、装完其实不用」这种对不上的情况。
 */
export interface PluginUpdate {
  /** `publisher.name`,和 `InstalledPlugin.id` 同一个值 */
  pluginId: string
  slug: string
  displayName: string
  currentVersion: string
  latestVersion: string
  /**
   * 新版本要、而用户没批准过的**必选**能力。
   *
   * ★ **数组不是布尔**:界面上要逐条列出来。「权限有变化」这句话回答不了
   * 用户唯一想知道的事 —— 变成了什么。
   *
   * ★ 拿 `approvedRequired` 算,不是 `granted`。后者是超集(可选能力批过的
   * 也在里面),用它算会漏掉一种真实情况:某能力这一版是可选、用户批过,
   * 下一版变必选 —— 算出来「没扩权」,而主进程装完会置成 `pending-approval`,
   * 插件停了,界面却提前说过不会停。
   */
  escalatedPermissions: string[]
  /**
   * 是从市场装的吗。本地 ZIP / 目录装的为 `false`。
   *
   * ★ `false` 的那些**不计入「N 个插件可更新」也不进「全部更新」**,但照常
   * 打徽标、详情页照常能点更新(点它 = 用户明确表态)。用户手上那份可能是
   * 他自己改过的构建,一次批量更新把它换成市场版,他从没说过要跟市场走。
   */
  fromMarket: boolean
}

/** 「全部更新」跑完之后的汇总。失败不中断,所以两边都有值是正常的 */
export interface PluginUpdateResult {
  updated: string[]
  /** `messageKey` 是 key 不是句子 —— 渲染层 `t()` 之后才是人话 */
  failed: { pluginId: string; messageKey: string }[]
}
