namespace Ncw.Agent.Browser.Pages;

/// <summary>
/// 页面侧的固定程序(移植自 2.x <c>snapshot-script.ts</c>),在隔离世界里执行:每次观察都建一张新的 ref 表(<c>e1</c>、<c>e2</c>…),
/// 导航会销毁隔离世界、第二次状态变更会消耗宿主侧的快照 id——两道边界合在一起,让过期的 ref 失败,而不是点到旧坐标上现在碰巧占着的元素。
/// 页面自己的脚本碰不到这张表(隔离世界),所以页面不能把后续的点击重定向到别处。
/// </summary>
internal static class SnapshotScript
{
    public static string Expression(string snapshotId) => "(" + Program + ")(" + QuoteJson(snapshotId) + ")";

    /// <summary>JSON 字符串字面量(快照 id 是我们自己生成的十六进制串,这里仍做完整转义,不拼接未转义的文本)。</summary>
    internal static string QuoteJson(string value)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\u2028': sb.Append("\\u2028"); break;
                case '\u2029': sb.Append("\\u2029"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    private const string Program = """
function (snapshotId) {
  const refs = new Map();
  globalThis.__ncwBrowserState = { snapshotId: snapshotId, refs: refs };

  const roleOf = (element) => {
    const explicit = (element.getAttribute('role') || '').trim().split(/\s+/)[0];
    if (explicit) return explicit;
    const tag = element.tagName.toLowerCase();
    if (/^h[1-6]$/.test(tag)) return 'heading';
    if (tag === 'a' && element.hasAttribute('href')) return 'link';
    if (tag === 'button' || tag === 'summary') return 'button';
    if (tag === 'textarea') return 'textbox';
    if (tag === 'select') return element.hasAttribute('multiple') ? 'listbox' : 'combobox';
    if (tag === 'option') return 'option';
    if (tag === 'img') return 'img';
    if (tag === 'nav') return 'navigation';
    if (tag === 'main') return 'main';
    if (tag === 'aside') return 'complementary';
    if (tag === 'header') return 'banner';
    if (tag === 'footer') return 'contentinfo';
    if (tag === 'form') return 'form';
    if (tag === 'table') return 'table';
    if (tag === 'tr') return 'row';
    if (tag === 'th') return element.getAttribute('scope') === 'row' ? 'rowheader' : 'columnheader';
    if (tag === 'ul' || tag === 'ol') return 'list';
    if (tag === 'li') return 'listitem';
    if (tag === 'input') {
      const type = (element.getAttribute('type') || 'text').toLowerCase();
      if (type === 'hidden') return null;
      if (type === 'checkbox') return 'checkbox';
      if (type === 'radio') return 'radio';
      if (['button', 'submit', 'reset', 'image'].includes(type)) return 'button';
      if (type === 'range') return 'slider';
      return 'textbox';
    }
    if (element.getAttribute('contenteditable') === 'true') return 'textbox';
    return null;
  };

  const directText = (element) => Array.from(element.childNodes)
    .filter((node) => node.nodeType === Node.TEXT_NODE)
    .map((node) => node.textContent || '')
    .join(' ')
    .replace(/\s+/g, ' ')
    .trim();

  const visible = (element) => {
    const style = getComputedStyle(element);
    if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') return false;
    if (Number(style.opacity) === 0) return false;
    const rect = element.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0;
  };

  const labelledBy = (element) => {
    const ids = (element.getAttribute('aria-labelledby') || '').trim().split(/\s+/).filter(Boolean);
    return ids.map((id) => (document.getElementById(id) || {}).textContent || '').join(' ').replace(/\s+/g, ' ').trim();
  };

  const accessibleName = (element, role) => {
    const ariaLabel = (element.getAttribute('aria-label') || '').trim();
    if (ariaLabel !== '') return ariaLabel;
    const by = labelledBy(element);
    if (by !== '') return by;
    if (element instanceof HTMLInputElement && element.labels !== null && element.labels.length > 0) {
      const label = Array.from(element.labels).map((item) => item.textContent || '').join(' ').replace(/\s+/g, ' ').trim();
      if (label !== '') return label;
    }
    const alt = (element.getAttribute('alt') || '').trim();
    if (alt !== '') return alt;
    if (role === 'button' && element instanceof HTMLInputElement && element.value !== '') return element.value;
    const text = (element.textContent || '').replace(/\s+/g, ' ').trim();
    if (text !== '') return text.slice(0, 500);
    const title = (element.getAttribute('title') || '').trim();
    if (title !== '') return title;
    return element.getAttribute('placeholder') || '';
  };

  const candidates = [];
  const elements = Array.from(document.body ? document.body.querySelectorAll('*') : []);
  for (const element of elements) {
    if (candidates.length >= 5000) break;
    if (!visible(element)) continue;
    let role = roleOf(element);
    const ownText = directText(element);
    const focusable = element instanceof HTMLElement && element.tabIndex >= 0;
    if (role === null && (ownText !== '' || focusable)) role = focusable ? 'generic' : 'text';
    if (role === null) continue;
    const name = accessibleName(element, role);
    if (role === 'text' && ownText === '') continue;
    candidates.push({ element: element, role: role, name: role === 'text' ? ownText : name });
  }

  const included = new Set(candidates.map((candidate) => candidate.element));
  const lines = [];
  candidates.forEach((candidate, index) => {
    const ref = 'e' + String(index + 1);
    refs.set(ref, candidate.element);
    let depth = 0;
    let parent = candidate.element.parentElement;
    while (parent !== null) {
      if (included.has(parent)) depth++;
      parent = parent.parentElement;
    }
    const el = candidate.element;
    const attrs = ['ref=' + ref];
    if (candidate.role === 'heading') attrs.push('level=' + el.tagName.slice(1));
    if (el.getAttribute('aria-expanded') !== null) attrs.push('expanded=' + el.getAttribute('aria-expanded'));
    if (el.getAttribute('aria-pressed') !== null) attrs.push('pressed=' + el.getAttribute('aria-pressed'));
    if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement || el instanceof HTMLSelectElement) {
      if (!(el instanceof HTMLInputElement && el.type === 'password')) {
        attrs.push('value=' + JSON.stringify(el.value.slice(0, 500)));
      }
      if (el.disabled) attrs.push('disabled=true');
      if (el instanceof HTMLInputElement && (el.type === 'checkbox' || el.type === 'radio')) {
        attrs.push('checked=' + String(el.checked));
      }
      const placeholder = el.getAttribute('placeholder');
      if (placeholder !== null && placeholder !== '') attrs.push('placeholder=' + JSON.stringify(placeholder));
    }
    if (el instanceof HTMLOptionElement && el.selected) attrs.push('selected=true');
    const name = candidate.name === '' ? '' : ' ' + JSON.stringify(candidate.name);
    lines.push('  '.repeat(Math.min(depth, 12)) + '- ' + candidate.role + name + ' [' + attrs.join(' ') + ']');
  });

  return {
    tree: lines.join('\n'),
    title: document.title,
    url: location.href,
    snapshotId: snapshotId,
    viewport: {
      width: Math.max(0, Math.round(globalThis.innerWidth)),
      height: Math.max(0, Math.round(globalThis.innerHeight))
    }
  };
}
""";
}
