/**
 * 插件**请求宿主界面做一件事**的那几种报文 —— 主进程与渲染层共用。
 *
 * ## 为什么单独一个文件
 *
 * `state.ts` 描述的是「一个已装插件此刻是什么样子」(状态、诊断、授权),
 * 是一份**投影**;这里描述的是「插件刚刚要求界面做什么」,是一次**请求**。
 * 两者的生命周期完全不同:前者随 catalog 整份替换,后者一次性、带回执。
 * 混在一起会让「这个字段在哪种场景下才有值」变成每个调用点都要重判的事。
 *
 * ## 这里没有任何句柄
 *
 * 全是可结构化克隆的纯数据(同 `state.ts`)。主进程**不画 UI**:它只把请求
 * 广播出去,由渲染层用既有的 `components/ui/**` 画,再把回执送回来。
 * 放主进程画的话就只能用 `dialog.showMessageBox` 拼裸文本 —— 既不跟随主题,
 * 也不跟随语言,而插件给的每一句话都是 l10n key。
 */

/**
 * 插件请求打开的东西。
 *
 * ★ 判别联合而不是「一个 url 加几个可选字段」:三种目标在主进程侧要校验的
 * 东西完全不同(webapp 查清单、browser 查 `hostPermissions`、view 查视图声明),
 * 合成一个形状的结果是每个调用点都要重新判断「这次到底是哪一种」。
 *
 * ★ 渲染层收到时**不再做安全判断**:能走到广播这一步,说明主进程已经核过了
 * (同 `plugins:openCustomEditor` 的分工)。渲染层只决定「放在哪一格」。
 */
export type PluginTabTarget =
  | {
      kind: 'webapp'
      webAppId: string
      url: string
      /** `%key%` 去掉百分号之后的 l10n key 片段,渲染层拼 `plugin.<id>.<key>` */
      title: string
      icon?: string
      open: PluginTabOpen
    }
  | { kind: 'browser'; url: string; open: PluginTabOpen }
  | {
      kind: 'terminal'
      /** 主进程已经备好的启动 spec 的 id —— 渲染层 Tab 必须原样引用它,见 `tabs.ts` 的 `case 'terminal'` */
      terminalId: string
      /** 菜单是在哪个工作区点的 —— Tab 开在那个工作区,不开在「当前活动」的 */
      workspaceId: string
      /** `%key%` 去掉百分号之后的 l10n key 片段,渲染层拼 `plugin.<id>.<key>`;缺省回落「终端」 */
      title?: string
    }
/*
  ★ **没有 `kind: 'view'`。** `contributes.views` 里 location 为 sidebar/panel 的
  视图这一版还打不开:它需要一种「挂一块插件自有 HTML、但**不绑定文件**」的
  Tab,而现有的 `custom` Tab 从定义上就是「某个文件的编辑器」(路径由 Tab 提供,
  见 `PluginViewFrame` 的文档通道)。把它塞进 `custom` 会开出一个读不到任何文档的
  空编辑器 —— 那比打不开更糟。声明了这类视图的插件会在详情页看到一条诊断。
*/

/** 开在哪:当前工作区的内层 Tab / 一个独立的外层功能 Tab / 右侧工作台。 */
export type PluginTabOpen = 'tab' | 'feature' | 'right'

/**
 * 一次等用户回答的交互。`*Key` 全是 l10n key,不是文案。
 *
 * 取消(直接关掉、超时)一律回**取消值**而不是错误:用户没理会一个弹窗
 * 不是故障,而一个异常会在插件那边变成它无从处理的东西。
 */
export type PluginInteractionRequest =
  | { kind: 'quickPick'; items: { id: string; labelKey: string }[]; placeholderKey?: string }
  | { kind: 'input'; titleKey: string; placeholderKey?: string; initial?: string; password?: boolean }
  | { kind: 'confirm'; titleKey: string; detailKey?: string; danger?: boolean }

/**
 * 插件挂着的一条长任务进度。`done: true` = 撤掉它。
 *
 * ★ 撤销必须由**宿主**兜底:插件被禁用 / 休眠 / 应用退出时,它挂着的每一条
 * 都要被撤掉。漏了的症状是状态栏上留着一条永远转下去、而且没有主人的进度,
 * 用户唯一的出路是重启。
 */
export interface PluginProgressUpdate {
  id: string
  titleKey?: string
  fraction?: number
  messageKey?: string
  done?: boolean
}
