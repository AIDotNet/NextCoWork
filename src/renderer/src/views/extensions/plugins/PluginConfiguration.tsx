/**
 * 插件设置项的**通用渲染器** —— 按清单里的 JSON Schema 描述符画控件。
 *
 * ## 为什么必须是通用的
 *
 * 这个仓库里的设置项今天**不是数据**:每一项都是设置页里手写的一段 JSX。
 * 那对内置设置是合理的(它们有各自的排版与联动),但插件的设置项在编译期
 * 根本不存在 —— 没有一段 JSX 能提前为它们写好。
 *
 * 所以这里换一个方向:清单里给**描述符**(type / title / default / enum),
 * 这一层按描述符查表画控件。代价是插件的设置长得比内置的朴素,
 * 换来的是「插件加一个开关不需要改宿主一行代码」。
 *
 * ## 四种类型,不多不少
 *
 * `boolean` / `string` / `number` / `enum`。够覆盖开关、路径、阈值、单选四类,
 * 而每多一种类型就多一种「宿主画错了」的可能。要更复杂的配置界面,
 * 插件可以自己贡献一个视图 —— 那条路是开着的。
 */
import { useEffect, useState, type ReactNode } from 'react'
import type { PluginConfigurationProperty } from '../../../../../shared/plugin/manifest'
import type { InstalledPlugin } from '../../../../../shared/plugin/state'
import { Select } from '../../../components/ui/Select'
import { TextInput } from '../../../components/ui/TextInput'
import { Switch } from '../../../components/arc/switch/switch'
import { useI18n, type TranslationKey } from '../../../i18n'
import { invoke } from '../../../services/ipc'

export function PluginConfiguration({ plugin }: { plugin: InstalledPlugin }): ReactNode {
  const { t } = useI18n()
  const [values, setValues] = useState<Record<string, boolean | string | number>>({})
  const properties = plugin.manifest.contributes.configuration?.properties ?? {}
  const entries = Object.entries(properties)

  useEffect(() => {
    let alive = true
    void invoke('plugins:getConfiguration', { pluginId: plugin.id }).then((next) => {
      if (alive) setValues(next)
    })
    return () => { alive = false }
  }, [plugin.id])

  if (entries.length === 0) return null

  const update = (key: string, value: boolean | string | number | null): void => {
    void invoke('plugins:setConfiguration', { pluginId: plugin.id, key, value }).then(setValues)
  }

  return (
    <>
      <h4 className="mt-4 text-[12px] font-medium text-fg">
        {/* ★ 标题也是 key(清单校验强制 `%key%`),不是插件写死的一句话 */}
        {t(keyOf(plugin.id, plugin.manifest.contributes.configuration?.title ?? ''))}
      </h4>
      <div className="mt-2 space-y-2">
        {entries.map(([key, property]) => (
          <Row
            key={key}
            pluginId={plugin.id}
            name={key}
            property={property}
            value={values[key]}
            onChange={(value) => update(key, value)}
          />
        ))}
      </div>
    </>
  )
}

function Row({
  pluginId,
  name,
  property,
  value,
  onChange
}: {
  pluginId: string
  name: string
  property: PluginConfigurationProperty
  value: boolean | string | number | undefined
  onChange: (value: boolean | string | number | null) => void
}): ReactNode {
  const { t } = useI18n()
  const label = t(keyOf(pluginId, property.title))

  return (
    <div className="flex items-center gap-3 rounded-[8px] bg-tint px-2.5 py-2">
      <div className="min-w-0 flex-1">
        <div className="truncate text-[12.5px] text-fg">{label}</div>
        {/* ★ 设置项的 id 是**协议标识符**,不翻译 —— 同工具名、模型名 */}
        <div className="truncate font-mono text-[10.5px] text-fg-faint">{name}</div>
      </div>
      {property.type === 'boolean' ? (
        <Switch checked={value === true} onCheckedChange={() => onChange(value !== true)} aria-label={label} />
      ) : property.type === 'enum' ? (
        <Select
          className="w-[160px]"
          ariaLabel={label}
          value={typeof value === 'string' ? value : ''}
          options={(property.enum ?? []).map((option) => ({ value: option, label: option }))}
          onValueChange={(next) => onChange(next)}
        />
      ) : (
        <TextInput
          className="w-[160px]"
          ariaLabel={label}
          value={value === undefined ? '' : String(value)}
          onChange={(next) => {
            if (property.type !== 'number') { onChange(next); return }
            /*
              ★ 数字框里清空 = 恢复默认值,不是写一个 0。
              写 0 的话,用户想「把它清掉」的动作会变成「把它设成 0」——
              而对一个阈值来说那两件事差得很远。
            */
            if (next.trim() === '') { onChange(null); return }
            const parsed = Number(next)
            if (Number.isFinite(parsed)) onChange(parsed)
          }}
        />
      )}
    </div>
  )
}

/** `%config.gridMode%` → `plugin.<id>.config.gridMode` */
function keyOf(pluginId: string, ref: string): TranslationKey {
  return `plugin.${pluginId}.${ref.replace(/^%|%$/g, '')}` as TranslationKey
}
