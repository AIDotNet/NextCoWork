namespace Ncw.Client.ViewModels;

/// <summary>设置搜索的一个结果:哪个分区,命中的那一句(界面上作为摘要显示)。</summary>
public sealed record SettingsHit(SettingsSection Section, string Snippet);

/// <summary>
/// 设置搜索(docs/design/07 §3「可以搜索」、03 §11.6):在每个分区的全部文字(标题、每一项的名字、说明)里找。
/// 文字由界面按当前语言收集好交进来(Client 不产出界面文字);这里只管匹配和排序,纯函数。
/// <list type="bullet">
/// <item>查询按空白切成几个词,每个词都要在同一个分区里出现(不区分大小写);</item>
/// <item>分区名本身命中的排在前面,其余按分区在导航里的顺序;</item>
/// <item>摘要取第一句包含第一个词的文字(分区名命中时就是分区名)。</item>
/// </list>
/// </summary>
public static class SettingsSearch
{
    public static IReadOnlyList<SettingsHit> Find(string query, IEnumerable<(SettingsSection Section, string Title, IReadOnlyList<string> Texts)> index, int limit = 8)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return [];
        }

        var hits = new List<(SettingsHit Hit, bool TitleHit, int Order)>();
        var order = 0;
        foreach (var (section, title, texts) in index)
        {
            order++;
            var all = texts.Prepend(title).ToList();
            if (!terms.All(term => all.Any(text => text.Contains(term, StringComparison.CurrentCultureIgnoreCase))))
            {
                continue;
            }

            var titleHit = terms.All(term => title.Contains(term, StringComparison.CurrentCultureIgnoreCase));
            var snippet = titleHit ? title : all.First(text => text.Contains(terms[0], StringComparison.CurrentCultureIgnoreCase));
            hits.Add((new SettingsHit(section, snippet.Trim()), titleHit, order));
        }

        return [.. hits.OrderByDescending(h => h.TitleHit).ThenBy(h => h.Order).Take(limit).Select(h => h.Hit)];
    }
}
