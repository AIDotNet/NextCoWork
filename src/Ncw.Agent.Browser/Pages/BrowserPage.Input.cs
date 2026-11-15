using System.Text.Json;

namespace Ncw.Agent.Browser.Pages;

public enum MouseButton
{
    Left,
    Right,
    Middle,
}

public readonly record struct Point(double X, double Y);

public sealed partial class BrowserPage
{
    private PageInput? _input;

    /// <summary>输入控制器(快照 / 截图的「新鲜度」状态在这里)。</summary>
    internal PageInput Input => _input ??= new PageInput(this);

    private void InvalidateObservation() => _input?.Invalidate();

    public Task ClickRefAsync(string reference, MouseButton button, int clickCount, CancellationToken ct) => Input.ClickRefAsync(reference, button, clickCount, ct);

    public Task TypeRefAsync(string reference, string text, CancellationToken ct) => Input.TypeRefAsync(reference, text, ct);

    public Task PressAsync(IReadOnlyList<string> keys, string? reference, CancellationToken ct) => Input.PressAsync(keys, reference, ct);

    public Task SelectAsync(string reference, IReadOnlyList<string> values, CancellationToken ct) => Input.SelectAsync(reference, values, ct);

    public Task ScrollAsync(double scrollX, double scrollY, Point? at, CancellationToken ct) => Input.ScrollAsync(scrollX, scrollY, at, ct);

    public Task CuaClickAsync(Point point, MouseButton button, CancellationToken ct) => Input.CuaClickAsync(point, button, ct);

    public Task CuaDragAsync(IReadOnlyList<Point> path, IReadOnlyList<string> modifiers, CancellationToken ct) => Input.CuaDragAsync(path, modifiers, ct);

    /// <summary>
    /// CDP 输入语义(移植自 2.x <c>input.ts</c>)。★ ref 动作消费最近一次快照:一次观察只允许一次写,第二次点击会用上「第一次点击可能已经替换掉的页面」的假设;失败信息让模型重新观察。
    /// </summary>
    internal sealed class PageInput(BrowserPage page)
    {
        private string? _snapshotId;
        private bool _screenshotReady;

        public void SetSnapshot(string id)
        {
            _snapshotId = id;
            _screenshotReady = false;
        }

        public void SetScreenshotReady() => _screenshotReady = true;

        public void Invalidate()
        {
            _snapshotId = null;
            _screenshotReady = false;
        }

        private void Consume()
        {
            _snapshotId = null;
            _screenshotReady = false;
        }

        private string RequireSnapshot() =>
            _snapshotId ?? throw new BrowserException(BrowserErrorKeys.StaleObservation, "Take a fresh browser_snapshot before this action.");

        private void RequireScreenshot()
        {
            if (!_screenshotReady)
            {
                throw new BrowserException(BrowserErrorKeys.StaleObservation, "Take a fresh browser_screenshot before using coordinate actions.");
            }
        }

        public async Task ClickRefAsync(string reference, MouseButton button, int clickCount, CancellationToken ct)
        {
            var point = await PointForAsync(reference, Focus.None, ct).ConfigureAwait(false);
            await ClickAtAsync(point, button, clickCount, ct).ConfigureAwait(false);
            Consume();
        }

        public async Task TypeRefAsync(string reference, string text, CancellationToken ct)
        {
            var snapshotId = RequireSnapshot();
            var point = await PointForAsync(reference, Focus.Focus, ct).ConfigureAwait(false);
            await ClickAtAsync(point, MouseButton.Left, 1, ct).ConfigureAwait(false);
            // 全选现有内容,insertText 替换之(「replace its value with text」)
            await page.EvaluateAsync(PointExpression(snapshotId, reference, Focus.Select), ct).ConfigureAwait(false);
            await page.Send("Input.insertText", w => w.WriteString("text", text), ct).ConfigureAwait(false);
            Consume();
        }

        public async Task PressAsync(IReadOnlyList<string> keys, string? reference, CancellationToken ct)
        {
            RequireSnapshot();
            if (reference is not null)
            {
                await PointForAsync(reference, Focus.Focus, ct).ConfigureAwait(false);
            }

            var modifiers = keys.Where(static k => ModifierBit(k) != 0).ToList();
            var primary = keys.Where(static k => ModifierBit(k) == 0).ToList();
            if (primary.Count != 1)
            {
                throw new BrowserException(BrowserErrorKeys.StaleObservation, "Browser keypress requires exactly one non-modifier key.");
            }

            var mask = 0;
            foreach (var modifier in modifiers)
            {
                mask |= ModifierBit(modifier);
            }

            foreach (var modifier in modifiers)
            {
                var info = ModifierKey(modifier);
                await KeyEventAsync("rawKeyDown", info.Key, info.Code, info.KeyCode, mask, null, ct).ConfigureAwait(false);
            }

            var key = KeyInfo(primary[0], (mask & 8) != 0);
            // Ctrl / Alt / Meta 组合是命令;Shift 仍产生可打印文本(如 "A")
            var text = (mask & 7) == 0 ? key.Text : null;
            await KeyEventAsync(text is null ? "rawKeyDown" : "keyDown", key.Key, key.Code, key.KeyCode, mask, text, ct).ConfigureAwait(false);
            await KeyEventAsync("keyUp", key.Key, key.Code, key.KeyCode, mask, null, ct).ConfigureAwait(false);
            for (var i = modifiers.Count - 1; i >= 0; i--)
            {
                var info = ModifierKey(modifiers[i]);
                await KeyEventAsync("keyUp", info.Key, info.Code, info.KeyCode, mask, null, ct).ConfigureAwait(false);
            }

            Consume();
        }

        public async Task SelectAsync(string reference, IReadOnlyList<string> values, CancellationToken ct)
        {
            var snapshotId = RequireSnapshot();
            var result = await page.EvaluateAsync(SelectExpression(snapshotId, reference, values), ct).ConfigureAwait(false);
            if (!(result.ValueKind == JsonValueKind.Object && result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
            {
                var error = result.ValueKind == JsonValueKind.Object ? StringOf(result, "error") : null;
                throw new BrowserException(
                    BrowserErrorKeys.StaleObservation,
                    error switch
                    {
                        "not_select" => "The ref is not a select element.",
                        "missing_option" => "None of the requested select options exist.",
                        _ => "The browser snapshot is stale. Take a fresh browser_snapshot before another action.",
                    });
            }

            Consume();
        }

        public async Task ScrollAsync(double scrollX, double scrollY, Point? at, CancellationToken ct)
        {
            if (_snapshotId is null && !_screenshotReady)
            {
                throw new BrowserException(BrowserErrorKeys.StaleObservation, "Observe the page with browser_snapshot or browser_screenshot before scrolling.");
            }

            var viewport = await page.ViewportAsync(ct).ConfigureAwait(false);
            var point = at ?? new Point(viewport.Width / 2.0, viewport.Height / 2.0);
            AssertPoint(point, viewport);
            await page.Send("Input.dispatchMouseEvent", w =>
            {
                w.WriteString("type", "mouseWheel");
                w.WriteNumber("x", point.X);
                w.WriteNumber("y", point.Y);
                w.WriteNumber("deltaX", scrollX);
                w.WriteNumber("deltaY", scrollY);
                w.WriteString("pointerType", "mouse");
            }, ct).ConfigureAwait(false);
            Consume();
        }

        public async Task CuaClickAsync(Point point, MouseButton button, CancellationToken ct)
        {
            RequireScreenshot();
            await ClickAtAsync(point, button, 1, ct).ConfigureAwait(false);
            Consume();
        }

        public async Task CuaDragAsync(IReadOnlyList<Point> path, IReadOnlyList<string> modifierKeys, CancellationToken ct)
        {
            RequireScreenshot();
            if (path.Count < 2)
            {
                throw new BrowserException(BrowserErrorKeys.StaleObservation, "Browser drag requires at least two path points.");
            }

            var viewport = await page.ViewportAsync(ct).ConfigureAwait(false);
            foreach (var point in path)
            {
                AssertPoint(point, viewport);
            }

            var modifiers = 0;
            foreach (var key in modifierKeys)
            {
                var bit = ModifierBit(key);
                if (bit == 0)
                {
                    throw new BrowserException(BrowserErrorKeys.StaleObservation, $"Unsupported drag modifier: {key}");
                }

                modifiers |= bit;
            }

            var first = path[0];
            await MouseAsync("mouseMoved", first, null, 0, 0, modifiers, ct).ConfigureAwait(false);
            await MouseAsync("mousePressed", first, "left", 1, 1, modifiers, ct).ConfigureAwait(false);
            foreach (var point in path.Skip(1))
            {
                await MouseAsync("mouseMoved", point, "left", 1, 0, modifiers, ct).ConfigureAwait(false);
            }

            var last = path[^1];
            await MouseAsync("mouseReleased", last, "left", 0, 1, modifiers, ct).ConfigureAwait(false);
            Consume();
        }

        // ── 内部 ──

        private enum Focus
        {
            None,
            Focus,
            Select,
        }

        private async Task<Point> PointForAsync(string reference, Focus focus, CancellationToken ct)
        {
            var snapshotId = RequireSnapshot();
            var raw = await page.EvaluateAsync(PointExpression(snapshotId, reference, focus), ct).ConfigureAwait(false);
            if (!(raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
            {
                var error = raw.ValueKind == JsonValueKind.Object ? StringOf(raw, "error") : null;
                throw new BrowserException(
                    BrowserErrorKeys.StaleObservation,
                    error switch
                    {
                        "missing_ref" => "The ref is no longer present. Take a fresh browser_snapshot and rebuild the target.",
                        "hidden_ref" => "The ref is no longer visible. Take a fresh browser_snapshot and rebuild the target.",
                        _ => "The browser snapshot is stale. Take a fresh browser_snapshot before another action.",
                    });
            }

            if (!(raw.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out var px) && raw.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.Number && y.TryGetDouble(out var py) && double.IsFinite(px) && double.IsFinite(py)))
            {
                throw new BrowserException(BrowserErrorKeys.StaleObservation, "The ref is no longer visible. Take a fresh browser_snapshot and rebuild the target.");
            }

            var point = new Point(px, py);
            AssertPoint(point, await page.ViewportAsync(ct).ConfigureAwait(false));
            return point;
        }

        private async Task ClickAtAsync(Point point, MouseButton button, int clickCount, CancellationToken ct)
        {
            AssertPoint(point, await page.ViewportAsync(ct).ConfigureAwait(false));
            var (name, buttons) = button switch
            {
                MouseButton.Right => ("right", 2),
                MouseButton.Middle => ("middle", 4),
                _ => ("left", 1),
            };
            await MouseAsync("mouseMoved", point, null, 0, 0, 0, ct).ConfigureAwait(false);
            await MouseAsync("mousePressed", point, name, buttons, clickCount, 0, ct).ConfigureAwait(false);
            await MouseAsync("mouseReleased", point, name, 0, clickCount, 0, ct).ConfigureAwait(false);
        }

        private Task MouseAsync(string type, Point point, string? button, int buttons, int clickCount, int modifiers, CancellationToken ct) =>
            page.Send("Input.dispatchMouseEvent", w =>
            {
                w.WriteString("type", type);
                w.WriteNumber("x", point.X);
                w.WriteNumber("y", point.Y);
                if (button is not null)
                {
                    w.WriteString("button", button);
                }

                if (type != "mouseMoved" || buttons != 0)
                {
                    w.WriteNumber("buttons", buttons);
                }

                if (clickCount > 0)
                {
                    w.WriteNumber("clickCount", clickCount);
                }

                if (modifiers != 0)
                {
                    w.WriteNumber("modifiers", modifiers);
                }

                w.WriteString("pointerType", "mouse");
            }, ct);

        private Task KeyEventAsync(string type, string key, string code, int keyCode, int modifiers, string? text, CancellationToken ct) =>
            page.Send("Input.dispatchKeyEvent", w =>
            {
                w.WriteString("type", type);
                w.WriteString("key", key);
                w.WriteString("code", code);
                w.WriteNumber("windowsVirtualKeyCode", keyCode);
                w.WriteNumber("nativeVirtualKeyCode", keyCode);
                w.WriteNumber("modifiers", modifiers);
                if (text is not null)
                {
                    w.WriteString("text", text);
                }
            }, ct);

        private static void AssertPoint(Point point, Viewport viewport)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0 || point.X > viewport.Width || point.Y > viewport.Height)
            {
                throw new BrowserException(
                    BrowserErrorKeys.StaleObservation,
                    $"Browser coordinates ({point.X}, {point.Y}) are outside the {viewport.Width}x{viewport.Height} viewport.");
            }
        }

        private static string? StringOf(JsonElement element, string name) => BrowserPage.StringOf(element, name);

        private static string PointExpression(string snapshotId, string reference, Focus focus)
        {
            var focusCode = focus == Focus.None ? "" : "if (typeof element.focus === 'function') element.focus({ preventScroll: true });";
            var selectCode = focus == Focus.Select
                ? """
                  if (element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement) element.select();
                  else if (element.getAttribute('contenteditable') === 'true') {
                    const selection = globalThis.getSelection();
                    const range = document.createRange();
                    range.selectNodeContents(element);
                    selection.removeAllRanges();
                    selection.addRange(range);
                  }
                  """
                : "";
            return $$"""
                (() => {
                  const state = globalThis.__ncwBrowserState;
                  if (!state) return { ok: false, error: 'missing_snapshot' };
                  if (state.snapshotId !== {{SnapshotScript.QuoteJson(snapshotId)}}) return { ok: false, error: 'stale_snapshot' };
                  const element = state.refs.get({{SnapshotScript.QuoteJson(reference)}});
                  if (!element || !document.contains(element)) return { ok: false, error: 'missing_ref' };
                  element.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
                  const rect = element.getBoundingClientRect();
                  if (rect.width <= 0 || rect.height <= 0) return { ok: false, error: 'hidden_ref' };
                  {{focusCode}}
                  {{selectCode}}
                  return { ok: true, x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
                })()
                """;
        }

        private static string SelectExpression(string snapshotId, string reference, IReadOnlyList<string> values)
        {
            var list = "[" + string.Join(',', values.Select(SnapshotScript.QuoteJson)) + "]";
            return $$"""
                (() => {
                  const state = globalThis.__ncwBrowserState;
                  if (!state || state.snapshotId !== {{SnapshotScript.QuoteJson(snapshotId)}}) return { ok: false, error: 'stale_snapshot' };
                  const element = state.refs.get({{SnapshotScript.QuoteJson(reference)}});
                  if (!(element instanceof HTMLSelectElement) || !document.contains(element)) return { ok: false, error: 'not_select' };
                  const requested = new Set({{list}});
                  let selected = 0;
                  for (const option of element.options) {
                    option.selected = requested.has(option.value);
                    if (option.selected) selected++;
                  }
                  if (selected === 0 && requested.size > 0) return { ok: false, error: 'missing_option' };
                  element.dispatchEvent(new Event('input', { bubbles: true }));
                  element.dispatchEvent(new Event('change', { bubbles: true }));
                  return { ok: true };
                })()
                """;
        }

        internal static int ModifierBit(string name) => name.Trim().ToLowerInvariant() switch
        {
            "alt" => 1,
            "ctrl" or "control" => 2,
            "meta" or "cmd" or "command" => 4,
            "shift" => 8,
            _ => 0,
        };

        private static (string Key, string Code, int KeyCode) ModifierKey(string name) => name.Trim().ToLowerInvariant() switch
        {
            "shift" => ("Shift", "ShiftLeft", 16),
            "alt" => ("Alt", "AltLeft", 18),
            "meta" or "cmd" or "command" => ("Meta", "MetaLeft", 91),
            _ => ("Control", "ControlLeft", 17),
        };

        internal static (string Key, string Code, int KeyCode, string? Text) KeyInfo(string raw, bool shifted)
        {
            var clean = raw.Trim();
            switch (clean.ToLowerInvariant())
            {
                case "enter": return ("Enter", "Enter", 13, null);
                case "tab": return ("Tab", "Tab", 9, null);
                case "escape" or "esc": return ("Escape", "Escape", 27, null);
                case "backspace": return ("Backspace", "Backspace", 8, null);
                case "delete": return ("Delete", "Delete", 46, null);
                case "arrowup": return ("ArrowUp", "ArrowUp", 38, null);
                case "arrowdown": return ("ArrowDown", "ArrowDown", 40, null);
                case "arrowleft": return ("ArrowLeft", "ArrowLeft", 37, null);
                case "arrowright": return ("ArrowRight", "ArrowRight", 39, null);
                case "home": return ("Home", "Home", 36, null);
                case "end": return ("End", "End", 35, null);
                case "pageup": return ("PageUp", "PageUp", 33, null);
                case "pagedown": return ("PageDown", "PageDown", 34, null);
                case "space": return (" ", "Space", 32, null);
            }

            if (clean.Length != 1)
            {
                throw new BrowserException(BrowserErrorKeys.StaleObservation, $"Unsupported browser key: {raw}");
            }

            var upper = char.ToUpperInvariant(clean[0]);
            var isLetter = upper is >= 'A' and <= 'Z';
            var key = shifted && isLetter ? upper.ToString() : clean;
            var code = isLetter ? $"Key{upper}" : char.IsAsciiDigit(clean[0]) ? $"Digit{clean}" : clean;
            return (key, code, upper, key);
        }
    }
}
