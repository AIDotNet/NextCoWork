import type { ReactNode } from 'react'
import { Switch } from '../../components/arc/switch/switch'
import { useI18n } from '../../i18n'
import type { SettingsPageProps } from '../props'
import { SettingGroup, SettingRow } from '../Row'

/**
 * 设置 › 实验。每一项都出厂关闭,见 `shared/domain/settings.ts` 的 `ExperimentalSettings`。
 *
 * ★ 行标题必须和 `nav.ts` 里 `SETTINGS_INDEX` 那一行逐字相同 —— 搜出来点过去,那一行要真的在。
 */
export function ExperimentalPage({ settings, patch }: SettingsPageProps): ReactNode {
  const { t } = useI18n()
  return (
    <SettingGroup>
      <p className="px-1 pb-1 text-[12px] leading-[1.5] text-fg-muted">{t('settings.experimental.intro')}</p>
      <SettingRow
        title={t('settings.experimental.workflow.title')}
        description={t('settings.experimental.workflow.desc')}
      >
        <Switch
          data-testid="experimental-workflow"
          aria-label={t('settings.experimental.workflow.title')}
          checked={settings.experimental.workflow}
          // 嵌套块只给要改的那一项(见 AppSettingsPatch)
          onCheckedChange={(workflow) => patch({ experimental: { workflow } })}
        />
      </SettingRow>
      <SettingRow
        title={t('settings.experimental.workflowConfirm.title')}
        description={t('settings.experimental.workflowConfirm.desc')}
        last
      >
        <Switch
          data-testid="experimental-workflow-confirm"
          aria-label={t('settings.experimental.workflowConfirm.title')}
          checked={settings.experimental.workflowConfirm}
          // 工作流本身关着时这一项没有意义,但不藏:藏起来的话打开工作流的人不知道还有它
          disabled={!settings.experimental.workflow}
          onCheckedChange={(workflowConfirm) => patch({ experimental: { workflowConfirm } })}
        />
      </SettingRow>
    </SettingGroup>
  )
}
