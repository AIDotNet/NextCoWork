/**
 * 文件树右键「添加到聊天」—— 把一条文件引用追加进**这个工作区主区正在看的那个对话**的草稿。
 *
 * 需求:用户在右侧文件树上挑中一个文件,要的是「把它带进我正在写的那句话」。
 * 草稿里落下的是 `[name](path)`,和输入框 `@` 选中、拖文件进来是同一种写法
 * (`shared/domain/file-mention.ts` 的文件头解释了为什么这样就够了)。
 *
 * 落点规则照 `views/skills/use-skill.ts`(Skill 页「在对话中使用」):主区激活的是对话
 * 就用它;不是(主区正开着一个文档之类)就走 `newChat` —— 它会先复用一张没写过字的白纸,
 * 没有才新建。区别只在于这里**不切工作区**:文件树永远属于当前工作区。
 *
 * 故意不做:聚焦输入框。输入框没有「从外面请求聚焦」的口子,为这一处开一个不值得;
 * 草稿变化本身会让输入框里立刻出现那枚 chip,这就是反馈。
 */
import { appendMention } from '../../../../shared/domain/file-mention'
import { chatKey } from '../../../../shared/domain/tab'
import { sessionStore } from '../../stores/session'
import { useTabsStore } from '../../stores/tabs'

/** 返回 false = 没找到也建不出一个对话 Tab(调用方据此提示,而不是静默吞掉这次点击)。 */
export function addFileToChat(workspaceId: string, file: { name: string; path: string }): boolean {
  const tabs = useTabsStore.getState()
  let state = tabs.stateOf(workspaceId)
  let active = state.tabs.find((tab) => tab.id === state.activeTabId)
  if (active?.kind !== 'chat') {
    tabs.newChat(workspaceId)
    state = useTabsStore.getState().stateOf(workspaceId)
    active = state.tabs.find((tab) => tab.id === state.activeTabId)
  }
  if (active?.kind !== 'chat') return false
  const session = sessionStore(chatKey(active)).getState()
  session.setDraft(appendMention(session.draft, file))
  return true
}
