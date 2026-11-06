/**
 * `components` 模块 —— TSX widget 的写法与宿主组件清单。
 *
 * 这一段是**我们自己的**(不是从参考实现抽出来的那几段):它描述的是
 * `nextcowork/widget` / `nextcowork/charts` 的真实 API(`renderer/src/widget-kit/*`)。
 * ★ 改了 widget-kit 的签名就要同步改这里 —— 模型照这份写代码,签名对不上的症状是
 *   widget 渲染出一行 "X is not a function",而模型以为自己写对了。
 */
export const TSX_COMPONENTS = `## TSX widgets (format: "tsx")

Prefer TSX for anything interactive or data-heavy: dashboards, calculators, configurators, forms, tables, charts. It renders with the host app's own UI kit, follows the user's theme (light/dark, accent color) automatically, and sits directly in the conversation with no frame around it.

### Module shape
\`\`\`tsx
import { useState } from 'react'
import { Stack, Row, Stat, StatGrid, Slider, Segmented } from 'nextcowork/widget'
import { LineChart } from 'nextcowork/charts'
import { TrendingUp } from 'lucide-react'

export default function Widget() {
  const [rate, setRate] = useState(5)
  return <Stack gap={4}>…</Stack>
}
\`\`\`
- Must \`export default\` a component. It takes no props.
- Only these imports work: \`react\`, \`nextcowork/widget\`, \`nextcowork/charts\`, \`lucide-react\`. No other packages, no CDN, no relative imports, no CSS imports.
- TypeScript is fine (types are stripped, not checked).
- Put data inline as constants at the top of the file.

### Layout rules (important)
- **Never wrap the widget in a card, border, shadow or background.** The outermost element is a plain \`Stack\`. The conversation is the background.
- Lay out with props, not Tailwind classes: \`Stack gap\`, \`Row gap/align/justify/wrap\`, \`Grid cols | minWidth\`. Gap units are 4px (\`gap={3}\` = 12px). Arbitrary Tailwind classes are NOT available — they silently do nothing.
- Use \`style={{…}}\` for one-off sizing. For colors use CSS variables: \`var(--color-fg)\`, \`--color-fg-muted\`, \`--color-fg-faint\`, \`--color-accent\`, \`--color-success\`, \`--color-warning\`, \`--color-danger\`, \`--color-tint\` (subtle fill), \`--color-hairline\` (divider), \`--color-chart-1\` … \`--color-chart-8\`. Never hard-code hex colors.
- Prefer \`Grid minWidth={…}\` over fixed \`cols\` so it reflows in a narrow window. Width is the chat column (~700px max, can be ~360px).
- \`Stack\` stretches its children to full width (good for charts and tables). Put buttons and badges inside a \`Row\` so they keep their natural width.
- Use \`Panel\` (a borderless tinted block) only when grouping is genuinely needed.

### nextcowork/widget — components
Layout: \`Stack\`, \`Row\`, \`Grid\`, \`Panel\`, \`Divider\`
Text: \`Heading level={1|2|3}\`, \`Text tone="default|muted|faint|info|success|warning|danger" size="sm|md|lg" mono\`, \`Code\` (block), \`Kbd\`
Status: \`Badge tone\`, \`Callout title tone\` (tones: neutral | info | success | warning | danger)
Data:
- \`Stat label value delta? hint? invert?\` — delta like "+12%" is colored automatically (\`invert\` for cost-like metrics). Wrap several in \`StatGrid\`.
- \`Table columns={[{ key, label, align?: 'left'|'right'|'center', render?: (row, i) => node, width? }]} rows={[…]} maxHeight? onRowClick? empty?\` — right-align numeric columns.
- \`Tabs items={[{ id, label, content }]} value? defaultValue? onChange?\`
- \`List\` + \`ListItem title description? trailing? onClick?\`
Controls (the host's own):
- \`Button variant="accent|ghost|danger" size="sm|md" icon? onClick disabled?\`
- \`IconButton label onClick size?\` (children = icon)
- \`Slider value min max step? onPreview={(v) => …} onCommit={(v) => …} ariaLabel\` — use \`onPreview\` for live updates while dragging.
- \`Segmented value options={[{ value, label }]} onChange label?\`
- \`Select value options={[{ value, label }]} onValueChange ariaLabel\`
- \`Toggle checked onChange label?\`
- \`TextInput value onChange placeholder? size?\`, \`TextArea\`, \`NumberInput value onCommit min? max?\`
- \`RadioCards\` / \`CheckboxCards\` (option cards), \`ProgressBar value={0..1} label?\`, \`Spinner\`, \`Tooltip content\`, \`EmptyState title hint?\`

### nextcowork/charts
Themed recharts presets — no axis lines clutter, theme colors, host tooltip.
- \`LineChart data x="month" series={['revenue', { key: 'cost', label: 'Cost' }]} height? format? curve? dots?\`
- \`AreaChart … stacked?\`, \`BarChart … stacked? horizontal?\`
- \`PieChart data name="segment" value="share" donut? format?\`
- \`Sparkline data={[…numbers]} width? height? color?\`
- \`format\` formats y values and tooltips, e.g. \`(v) => '$' + v.toLocaleString()\`.
- \`Recharts\` re-exports raw recharts for anything else.

### Host capabilities (nextcowork/widget)
- \`useWidgetState(key, initial)\` — like useState, but persisted with this widget: sliders, tabs and form values survive reopening the conversation. Use it for any user-adjustable value.
- \`useWidgetData(initial)\` — the latest data pushed by \`visualize_update_widget\` (falls back to \`initial\`). Use for widgets you plan to refresh.
- \`const { interactive, submitted, submit, submitting } = useSubmit()\` — when you called visualize_show_widget with \`interactive: true\`, call \`submit(value)\` from a confirm button; the value comes back to you as the tool result. Disable the button once \`submitted\`.
- \`const action = useAction('run_tests')\` → \`action.run(params?)\`, \`action.status\` ('idle'|'running'|'done'|'error'), \`action.output\` — runs a tool call you declared in \`actions\`. The user approves it in a host dialog; output returns to the widget only.
- \`const { complete, text, status } = useCompletion()\` — \`complete(prompt, { system?, maxTokens? })\` asks the model once (no tools). The user is asked to allow it the first time. Use sparingly.
- \`host.copy(text)\`, \`host.download(name, data, { mime?, encoding? })\`, \`host.openFile(path)\` (workspace files), \`host.openUrl(url)\` (https, user confirms), \`host.toast(message, tone?)\`
- \`host.draft(text)\` — puts a follow-up into the user's message box without sending. Use this for drill-down buttons ("Explain this step").
- \`host.setContext(summary)\` — attaches a short summary of the widget's current state to the user's next message (shown to the user, removable). Call it when the state changes, e.g. "Rate 7%, term 20y, monthly payment $1,240".
- \`useTheme()\` → \`{ appearance: 'light'|'dark', motion }\`.

### Declaring actions
\`\`\`json
"actions": [
  { "id": "run_tests", "label": "Run unit tests", "tool": "Bash", "input": { "command": "npm test -- {{suite}}" },
    "params": { "suite": { "type": "enum", "values": ["api", "ui"] } } },
  { "id": "reload_metrics", "label": "Reload metrics", "tool": "Read", "input": { "file_path": "/abs/path/metrics.json" } }
]
\`\`\`
Params are only enums or bounded numbers — the widget cannot inject free text into a command. Keep actions narrow and read-only where possible.

### Example: loan calculator
\`\`\`tsx
import { Stack, Grid, Stat, StatGrid, Slider, Text, useWidgetState, host } from 'nextcowork/widget'
import { AreaChart } from 'nextcowork/charts'
import { useEffect, useMemo } from 'react'

const money = (v: number) => '$' + Math.round(v).toLocaleString()

export default function Widget() {
  const [amount, setAmount] = useWidgetState('amount', 300000)
  const [rate, setRate] = useWidgetState('rate', 6.5)
  const [years, setYears] = useWidgetState('years', 30)
  const monthly = useMemo(() => {
    const r = rate / 100 / 12, n = years * 12
    return r === 0 ? amount / n : (amount * r) / (1 - Math.pow(1 + r, -n))
  }, [amount, rate, years])
  const curve = useMemo(() => Array.from({ length: years + 1 }, (_, y) => {
    const r = rate / 100 / 12, k = y * 12
    const balance = r === 0 ? amount - monthly * k : amount * Math.pow(1 + r, k) - monthly * ((Math.pow(1 + r, k) - 1) / r)
    return { year: y, balance: Math.max(0, balance) }
  }), [amount, rate, years, monthly])
  useEffect(() => { void host.setContext(\`Loan \${money(amount)} at \${rate}% for \${years}y → \${money(monthly)}/month\`) }, [amount, rate, years, monthly])
  return (
    <Stack gap={5}>
      <StatGrid>
        <Stat label="Monthly payment" value={money(monthly)} />
        <Stat label="Total interest" value={money(monthly * years * 12 - amount)} invert />
      </StatGrid>
      <Grid minWidth={180} gap={4}>
        <Stack gap={1}><Text tone="muted" size="sm">Amount {money(amount)}</Text>
          <Slider value={amount} min={50000} max={1000000} step={10000} onPreview={setAmount} onCommit={setAmount} ariaLabel="Amount" /></Stack>
        <Stack gap={1}><Text tone="muted" size="sm">Rate {rate}%</Text>
          <Slider value={rate} min={1} max={12} step={0.1} onPreview={setRate} onCommit={setRate} ariaLabel="Rate" /></Stack>
        <Stack gap={1}><Text tone="muted" size="sm">Term {years}y</Text>
          <Slider value={years} min={5} max={40} onPreview={setYears} onCommit={setYears} ariaLabel="Term" /></Stack>
      </Grid>
      <AreaChart data={curve} x="year" series={[{ key: 'balance', label: 'Balance' }]} format={money} height={200} />
    </Stack>
  )
}
\`\`\`

### Example: let the user choose (interactive: true)
\`\`\`tsx
import { Stack, RadioCards, Button, Row, Text, useSubmit, useWidgetState } from 'nextcowork/widget'

const OPTIONS = [
  { value: 'postgres', label: 'PostgreSQL', description: 'Relational, strong consistency' },
  { value: 'dynamo', label: 'DynamoDB', description: 'Managed key-value, scales automatically' }
]

export default function Widget() {
  const [choice, setChoice] = useWidgetState('choice', 'postgres')
  const { submit, submitted, submitting } = useSubmit()
  return (
    <Stack gap={3}>
      <RadioCards value={choice} options={OPTIONS} onValueChange={setChoice} ariaLabel="Database" />
      <Row justify="end">
        {submitted ? <Text tone="muted" size="sm">Submitted</Text>
          : <Button variant="accent" disabled={submitting} onClick={() => submit({ database: choice })}>Use this</Button>}
      </Row>
    </Stack>
  )
}
\`\`\`

### Editing and refreshing
- Small change to an existing widget → \`visualize_edit_widget\` with exact old/new string pairs, not a full rewrite.
- New numbers for a widget that reads \`useWidgetData\` → \`visualize_update_widget\` with the data only.

### Checklist before calling
1. Default export, only allowed imports.
2. No outer card/border/background; layout via props.
3. Colors from CSS variables or tones; charts via nextcowork/charts.
4. User-adjustable values in \`useWidgetState\`.
5. Drill-down buttons use \`host.draft\`, not fake links.`
