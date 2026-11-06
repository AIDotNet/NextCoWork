/**
 * 扩展面板 —— 技能 / 命令 / 子代理 / 钩子四类资源的统一入口。
 *
 * ★ 为什么这四个在一起、而不是散进设置页:它们在磁盘上是**同构**的 ——
 * 同样的 `<appData>/xxx/` + `<workspaceRoot>/.next-cowork/xxx/` 两层扫描、
 * 同样的 frontmatter、同样的「项目覆盖全局」。而设置页装的是开关型配置
 * (选一个值、拨一下就生效),塞不下一个带列表和编辑器的资源管理器。
 *
 * ★ 这一层只负责**外壳**:52px 的 header(返回 + 标题 + 四路 Tab)和分发。
 * header 那一条的类名、拖动区、红绿灯让位由 `shell/FeatureFrame` 统一管
 * (四个 feature 页原本各写了一份);每个 Tab 的工具条归各自的 Panel 自己画 ——
 * 四类资源的操作差别很大(Skill 要装包、命令要新建文件、钩子要试运行),
 * 提到这里只会变成一堆互相不相干的条件渲染。
 */
import { ArrowLeft } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { IconButton } from '../../components/ui/IconButton'
import SegmentedControl from '../../components/arc/segmented-control/segmented-control'
import { useI18n } from '../../i18n'
import { cn } from '../../lib/cn'
import { FeatureFrame } from '../../shell/FeatureFrame'
import { SkillsFeature } from '../skills/SkillsFeature'
import { AgentsPanel } from './agents/AgentsPanel'
import { CommandsPanel } from './commands/CommandsPanel'
import { HooksPanel } from './hooks/HooksPanel'
import { PluginsPanel } from './plugins/PluginsPanel'
import { ModesPanel } from './modes/ModesPanel'

type ExtensionTab = 'plugins' | 'skills' | 'commands' | 'agents' | 'modes' | 'hooks'

export function ExtensionsFeature({ onClose }: { onClose?: () => void }): ReactNode {
  const { t } = useI18n()
  const [tab, setTab] = useState<ExtensionTab>(() => {
    const requested = sessionStorage.getItem('next-cowork:extensions-tab')
    sessionStorage.removeItem('next-cowork:extensions-tab')
    // ★ 默认落在**第一个** Tab 上。这条规律原来就在(那时第一个是技能),
    //   插件挪到首位之后跟着走 —— 让「第一个」和「进来看到的」保持是同一个,
    //   否则 Tab 栏的顺序就变成了一条没有含义的排列。
    return requested === 'modes' ? 'modes' : 'plugins'
  })

  return (
    <FeatureFrame
      header={
        <>
          <IconButton
            label={t('ext.back')}
            size={28}
            width={40}
            onClick={onClose}
            className="rounded-pill bg-tint"
          >
            <ArrowLeft size={15} />
          </IconButton>
          <h1 className="text-[14px] font-medium text-fg">{t('ext.title')}</h1>
          <SegmentedControl
            className="ml-3 app-no-drag"
            value={tab}
            onValueChange={(value) => setTab(value as ExtensionTab)}
            label={t('ext.title')}
            options={[
              /*
                ★ 插件排第一。它是这一页里**唯一带代码**的一类扩展,也是用户
                最常回来看的那一类(装 / 更新 / 授权都在这儿,而技能、命令那几类
                装完基本不再动)。放末位等于让最常走的那条路每次都多扫一遍整条
                Tab 栏。
              */
              { value: 'plugins', label: t('plugins.title') },
              { value: 'skills', label: t('ext.tab.skills') },
              { value: 'commands', label: t('ext.tab.commands') },
              { value: 'agents', label: t('ext.tab.agents') },
              { value: 'modes', label: t('ext.tab.modes') },
              { value: 'hooks', label: t('ext.tab.hooks') }
            ]}
          />
        </>
      }
    >
      {/*
        ★ Skills 走 `chromeless`:它自带一个和上面这条一模一样的 header(返回 + 标题),
        嵌进来会变成两条。右侧那三颗按钮(刷新 / 作用域 / 安装)是 Skill 专属的,
        留在它自己那条工具条里。
      */}
      <div
        className={cn('flex min-h-0 flex-1 flex-col', tab !== 'skills' && 'hidden')}
        aria-hidden={tab !== 'skills'}
      >
        {/*
          保持技能面板挂载，切换到其它扩展类型时只隐藏它。技能市场来自远端，
          卸载再挂载会丢掉本地筛选状态，并重复请求市场列表和分类。
        */}
        <SkillsFeature chromeless />
      </div>

      {tab === 'commands' ? (
        <CommandsPanel />
      ) : tab === 'agents' ? (
        <AgentsPanel />
      ) : tab === 'modes' ? (
        <ModesPanel />
      ) : tab === 'hooks' ? (
        <HooksPanel />
      ) : tab === 'plugins' ? (
        <PluginsPanel />
      ) : null}
    </FeatureFrame>
  )
}
