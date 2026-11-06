/**
 * 内置工具清单 —— 「注册了哪些内置工具」的唯一出处。
 *
 * 名字全部照搬 Claude Code(`Read` / `Write` / `Edit` / `LS` / `Glob` / `Grep` …),
 * 理由见 `fs.ts` 的文件头。
 *
 * 它由两部分组成:**代码里写死的那一批**(`coreTools()`)+ **运行期注册进来的
 * provider**(`registerToolProvider`)。第二部分存在的理由是贡献方在编译期
 * 还不存在(插件),而硬编码数组只能在编译期改。
 *
 * ★ **`echoTool` 必须留在第一位。**
 *
 * `src/main/kernel/upstream/demo.ts:318` 拿 `tools[0]` 按名字调用,并按 `echo`
 * 的入参构造调用。顺序一变,演示上游就改去调 `Read`,而它构造的入参对不上 ——
 * `agent-run.test.ts` 里那条 `'演示值:text'` 会以**完全看不出根因**的方式挂掉:
 * 报错停在断言上,而真正的原因在另一个文件的数组顺序里。
 * `index.test.ts` 有一条用例专门钉住这一点,失败信息里写明了它保护谁。
 * provider 贡献的工具**一律排在写死的那批之后**,所以这条不受它们影响。
 */
import type { ToolRegistration } from '../registry'
import { bashTool } from './bash'
import { backgroundShellTools } from './bash-background'
import { echoTool } from './echo'
import { editTool, lsTool, readTool, writeTool } from './fs'
import { globTool, grepTool } from './search'
import { skillTool } from './skill'
import { taskTool } from './task'
import { workflowTool } from './workflow'
import { todoWriteTool } from './todo'
import { webFetchTool } from './web'
import { webSearchTool } from './web-search'
import { browserTools } from './browser'
import { askUserTool } from './interaction'
import { proposeGoalTool } from './goal'
import { scheduledTaskTools } from './scheduled'
import { enterPlanModeTool, exitPlanModeTool } from './plan-file'
import { visualizeEditWidgetTool, visualizeReadMeTool, visualizeShowWidgetTool, visualizeUpdateWidgetTool } from './visualize'
import { generateImageTool } from './image'
import { saveImageTool } from './save-image'
import { generateVideoTool } from './video'
import { saveVideoTool } from './save-video'

/**
 * 代码里写死的那一批。**只有这张表保证顺序**(见文件头 echo 那条),
 * provider 贡献的一律排在它之后。
 */
function coreTools(): ToolRegistration[] {
  return [
    // ★ 第一位是 echo,别动 —— 见上面的说明
    echoTool,
    readTool,
    writeTool,
    editTool,
    lsTool,
    globTool,
    grepTool,
    bashTool,
    // `Bash({ run_in_background })` 的另外两半 —— 三个一起才构成「后台命令」这件事
    ...backgroundShellTools,
    todoWriteTool,
    webFetchTool,
    skillTool,
    webSearchTool,
    askUserTool,
    proposeGoalTool,
    enterPlanModeTool,
    exitPlanModeTool,
    ...browserTools,
    ...scheduledTaskTools,
    // 可视化那一对。顺序上**只有 echo 那条约束**(见文件头),放这里是因为
    // 它们和 `EnterPlanMode` 一样属于"改变这一轮怎么表达"的工具,不是文件操作。
    visualizeReadMeTool,
    visualizeShowWidgetTool,
    visualizeEditWidgetTool,
    visualizeUpdateWidgetTool,
    /*
      对话内生图。跟在可视化那对后面:同属「这一轮产出一张给人看的东西」的工具,
      不是文件操作。顺序上仍然只有 echo 那条约束(见文件头)。
      ★ 它有自己的 `isEnabled`(没配生图模型就整体不下发),见 `image.ts` 文件头。
    */
    generateImageTool,
    /*
      把对话里的图(用户附件 / 生成图的 `ncw://` 地址)写进工作区。紧跟生图:
      它是「画完之后存下来」那一步。顺序上仍然只有 echo 那条约束(见文件头)。
      ★ 它也有自己的 `isEnabled`(宿主写不了二进制就不下发),见 `save-image.ts` 文件头。
    */
    saveImageTool,
    /*
      对话内生成视频。紧跟生图那一对:同属「这一轮产出一个给人看的东西」。
      ★ 与生图不同的是它**异步** —— 提交返回的是任务回执,成品由后台 worker
      落成本会话附件(见 kernel/video-gen.ts 与 video-generation/manager.ts)。
      ★ 它有**自己**的 `isEnabled`(没配视频模型就不下发)。
    */
    generateVideoTool,
    /*
      把对话里的视频写进工作区。紧跟生成视频 —— 它是「生成完之后存下来」那一步。
      ★ 它写的是**流**(几十到几百兆),所以要求宿主有 `writeStream`;
      宿主没有时整体不下发。见 save-video.ts 文件头。
    */
    saveVideoTool,
    /*
      ★ `Task` 是唯一一个**每次现造**的内置工具:它的 description 里逐字带着
      当前可用的子代理清单(照搬 CC),而那份清单会随目录重扫而变。
      `runtime.ts` 在每次 run 之前会用新的清单再 `register()` 一次 ——
      注册表按 internalId 幂等替换且保住 externalName,所以历史转录不会失配。
    */
    taskTool(),
    /*
      把多个子代理编排成依赖图并行执行。紧跟 `Task`:它就是「一次派好几个、还管先后」的那一个。
      ★ 实验功能,有自己的 `isEnabled`(设置 › 实验 › Workflow,出厂关闭)。
    */
    workflowTool
  ]
}

/** 一个 provider 就是「这一批工具现在长什么样」,每次装配现问一遍。 */
export type BuiltinToolProvider = () => readonly ToolRegistration[]

/**
 * provider 注册表 —— 让「有哪些工具」变成运行期可增删的事实。
 *
 * ★ **为什么不是让调用方往数组里 push。** 工具清单会被重复装配(每次
 * `getTools()` 重建、每次 run 的 `snapshotRunTools`),而贡献方的内容
 * 会变(插件启用/禁用、子代理清单重扫)。存函数而不是存结果,
 * 意味着这张表永远不会滞留一份过期快照。
 *
 * ★ **按 id 幂等**。同一个 id 注册两次是替换,不是叠加 —— 插件重载走的
 * 正是这条路,不幂等的话重载一次工具就多一份。
 */
const providers = new Map<string, BuiltinToolProvider>()

/** 返回一个注销函数(同 `Disposable` 的惯例),插件禁用时调它。 */
export function registerToolProvider(id: string, provider: BuiltinToolProvider): () => void {
  providers.set(id, provider)
  return () => {
    // 只注销自己这一次注册的那个 —— 中间被同 id 替换过的话,替换者说了算。
    if (providers.get(id) === provider) providers.delete(id)
  }
}

/** 主要给测试用:把进程内那张表清空。 */
export function clearToolProviders(): void {
  providers.clear()
}

/**
 * 这一刻的内置工具全表 = 代码里写死的那批 + 各 provider 现报的那批。
 *
 * ★ **内置名字不可被顶替。** 后来者与已有的 internalId 撞名时**丢掉后来者**,
 * 而不是覆盖:注册表本身是按 internalId 幂等替换的(见 `ToolRegistry.register`),
 * 于是一个贡献 `Bash` 的 provider 会把真的 Bash 换掉,而模型完全看不出来 ——
 * 那是一条没有任何症状的提权路径。先来先得也让这张表的内容不依赖注册顺序之外的东西。
 */
export function builtinTools(): ToolRegistration[] {
  const out = coreTools()
  const seen = new Set(out.map((t) => t.internalId))
  for (const provider of providers.values()) {
    for (const tool of provider()) {
      if (seen.has(tool.internalId)) continue
      seen.add(tool.internalId)
      out.push(tool)
    }
  }
  return out
}

export { bashTool } from './bash'
export { backgroundShellTools, bashOutputTool, killShellTool } from './bash-background'
export { echoTool } from './echo'
export { editTool, lsTool, readTool, writeTool } from './fs'
export { globTool, grepTool } from './search'
export { skillTool } from './skill'
export { taskTool } from './task'
export { todoWriteTool } from './todo'
export { webFetchTool } from './web'
export { webSearchTool } from './web-search'
export { browserTools } from './browser'
export { visualizeEditWidgetTool, visualizeReadMeTool, visualizeShowWidgetTool, visualizeUpdateWidgetTool } from './visualize'
export { generateImageTool } from './image'
export { saveImageTool } from './save-image'
export { generateVideoTool } from './video'
export { saveVideoTool } from './save-video'
export {
  AVAILABLE_MODULES as VISUALIZE_MODULES,
  getGuidelines as visualizeGuidelines,
  type GuidelineModule
} from './visualize-guidelines'
export {
  createScheduledTaskTool,
  deleteScheduledTaskTool,
  listScheduledTasksTool,
  scheduledTaskTools,
  updateScheduledTaskTool
} from './scheduled'
