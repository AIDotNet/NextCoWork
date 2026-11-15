namespace Ncw.Agent.Widgets.Guidelines;

// 取自 2.x `src/main/kernel/tool/builtin/visualize-guidelines/ui-components.ts`:这是发给模型的设计规范,正文逐字不改(2.x 的原则:改动任何一个数值都会让模型稳定地画坏图)。
// 来源与版权说明见 2.x 原文件头(Anthropic `visualize:read_me` 响应,经 pi-generative-ui(MIT)抽取)。常量字符串:NativeAOT 下不读资源、不用反射。
internal static partial class GuidelineText
{
    public const string UiComponents = """
## UI components

### Aesthetic
Flat, clean, white surfaces. Minimal 0.5px borders. Generous whitespace. No gradients, no shadows (except functional focus rings). Everything should feel native to claude.ai — like it belongs on the page, not embedded from somewhere else.

### Tokens
- Borders: always `0.5px solid var(--color-border-tertiary)` (or `-secondary` for emphasis)
- Corner radius: `var(--border-radius-md)` for most elements, `var(--border-radius-lg)` for cards
- Cards: white bg (`var(--color-background-primary)`), 0.5px border, radius-lg, padding 1rem 1.25rem
- Form elements (input, select, textarea, button, range slider) are pre-styled — write bare tags. Text inputs are 36px with hover/focus built in; range sliders have 4px track + 18px thumb; buttons have outline style with hover/active. Only add inline styles to override (e.g., different width).
- Buttons: pre-styled with transparent bg, 0.5px border-secondary, hover bg-secondary, active scale(0.98). If it triggers sendPrompt, append a ↗ arrow.
- **Round every displayed number.** JS float math leaks artifacts — `0.1 + 0.2` gives `0.30000000000000004`, `7 * 1.1` gives `7.700000000000001`. Any number that reaches the screen (slider readouts, stat card values, axis labels, data-point labels, tooltips, computed totals) must go through `Math.round()`, `.toFixed(n)`, or `Intl.NumberFormat`. Pick the precision that makes sense for the context — integers for counts, 1–2 decimals for percentages, `toLocaleString()` for currency. For range sliders, also set `step="1"` (or step="0.1" etc.) so the input itself emits round values.
- Spacing: use rem for vertical rhythm (1rem, 1.5rem, 2rem), px for component-internal gaps (8px, 12px, 16px)
- Box-shadows: none, except `box-shadow: 0 0 0 Npx` focus rings on inputs

### Metric cards
For summary numbers (revenue, count, percentage) — surface card with muted 13px label above, 24px/500 number below. `background: var(--color-background-secondary)`, no border, `border-radius: var(--border-radius-md)`, padding 1rem. Use in grids of 2-4 with `gap: 12px`. Distinct from raised cards (which have white bg + border).

### Layout
- Editorial (explanatory content): no card wrapper, prose flows naturally
- Card (bounded objects like a contact record, receipt): single raised card wraps the whole thing
- Don't put tables here — output them as markdown in your response text

**Grid overflow:** `grid-template-columns: 1fr` has `min-width: auto` by default — children with large min-content push the column past the container. Use `minmax(0, 1fr)` to clamp.

**Table overflow:** Tables with many columns auto-expand past `width: 100%` if cell contents exceed it. In constrained layouts (≤700px), use `table-layout: fixed` and set explicit column widths, or reduce columns, or allow horizontal scroll on a wrapper.

### Mockup presentation
Contained mockups — mobile screens, chat threads, single cards, modals, small UI components — should sit on a background surface (`var(--color-background-secondary)` container with `border-radius: var(--border-radius-lg)` and padding, or a device frame) so they don't float naked on the widget canvas. Full-width mockups like dashboards, settings pages, or data tables that naturally fill the viewport do not need an extra wrapper.

### 1. Interactive explainer — learn how something works
*"Explain how compound interest works" / "Teach me about sorting algorithms"*

Use `imagine_html` for the interactive controls — sliders, buttons, live state displays, charts. Keep prose explanations in your normal response text (outside the tool call), not embedded in the HTML. No card wrapper. Whitespace is the container.

```html
<div style="display: flex; align-items: center; gap: 12px; margin: 0 0 1.5rem;">
  <label style="font-size: 14px; color: var(--color-text-secondary);">Years</label>
  <input type="range" min="1" max="40" value="20" id="years" style="flex: 1;" />
  <span style="font-size: 14px; font-weight: 500; min-width: 24px;" id="years-out">20</span>
</div>

<div style="display: flex; align-items: baseline; gap: 8px; margin: 0 0 1.5rem;">
  <span style="font-size: 14px; color: var(--color-text-secondary);">£1,000 →</span>
  <span style="font-size: 24px; font-weight: 500;" id="result">£3,870</span>
</div>

<div style="margin: 2rem 0; position: relative; height: 240px;">
  <canvas id="chart"></canvas>
</div>
```

Use `sendPrompt()` to let users ask follow-ups: `sendPrompt('What if I increase the rate to 10%?')`

### 2. Compare options — decision making
*"Compare pricing and features of these products" / "Help me choose between React and Vue"*

Use `imagine_html`. Side-by-side card grid for options. Highlight differences with semantic colors. Interactive elements for filtering or weighting.

- Use `repeat(auto-fit, minmax(160px, 1fr))` for responsive columns
- Each option in a card. Use badges for key differentiators.
- Add `sendPrompt()` buttons: `sendPrompt('Tell me more about the Pro plan')`
- Don't put comparison tables inside this tool — output them as regular markdown tables in your response text instead. The tool is for the visual card grid only.
- When one option is recommended or "most popular", accent its card with `border: 2px solid var(--color-border-info)` only (2px is deliberate — the only exception to the 0.5px rule, used to accent featured items) — keep the same background and border as the other cards. Add a small badge (e.g. "Most popular") above or inside the card header using `background: var(--color-background-info); color: var(--color-text-info); font-size: 12px; padding: 4px 12px; border-radius: var(--border-radius-md)`.

### 3. Data record — bounded UI object
*"Show me a Salesforce contact card" / "Create a receipt for this order"*

Use `imagine_html`. Wrap the entire thing in a single raised card. All content is sans-serif since it's pure UI. Use an avatar/initials circle for people (see example below).

```html
<div style="background: var(--color-background-primary); border-radius: var(--border-radius-lg); border: 0.5px solid var(--color-border-tertiary); padding: 1rem 1.25rem;">
  <div style="display: flex; align-items: center; gap: 12px; margin-bottom: 16px;">
    <div style="width: 44px; height: 44px; border-radius: 50%; background: var(--color-background-info); display: flex; align-items: center; justify-content: center; font-weight: 500; font-size: 14px; color: var(--color-text-info);">MR</div>
    <div>
      <p style="font-weight: 500; font-size: 15px; margin: 0;">Maya Rodriguez</p>
      <p style="font-size: 13px; color: var(--color-text-secondary); margin: 0;">VP of Engineering</p>
    </div>
  </div>
  <div style="border-top: 0.5px solid var(--color-border-tertiary); padding-top: 12px;">
    <table style="width: 100%; font-size: 13px;">
      <tr><td style="color: var(--color-text-secondary); padding: 4px 0;">Email</td><td style="text-align: right; padding: 4px 0; color: var(--color-text-info);">m.rodriguez@acme.com</td></tr>
      <tr><td style="color: var(--color-text-secondary); padding: 4px 0;">Phone</td><td style="text-align: right; padding: 4px 0;">+1 (415) 555-0172</td></tr>
    </table>
  </div>
</div>
```
""";
}
