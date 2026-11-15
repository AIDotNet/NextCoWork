namespace Ncw.Agent.Storage;

/// <summary>
/// 应用设置(2.x 的 <c>settings</c> 表第 1 行,一整份 JSON)。Storage 只原文进出、不解释:字段的含义和默认值归读它的那一层
/// (Host 把它读成 Run 开始时的设置快照;设置页的读写是 <c>settings.*</c> 方法,服务层是 <c>SettingsService</c>)。
/// </summary>
public sealed class SettingsRepository(AgentDatabase database)
{
    /// <summary>设置行的 JSON 文本;没有这一行(全新安装、还没改过任何设置)返回 null。</summary>
    public string? ReadJson() => database.Value("SELECT json FROM settings WHERE id = 1") as string;

    /// <summary>整份替换(没有这一行就插入;表上有 <c>CHECK (id = 1)</c>,只会有一行)。</summary>
    public void WriteJson(string json) => database.Run(
        "INSERT INTO settings (id, json) VALUES (1, $j) ON CONFLICT (id) DO UPDATE SET json = excluded.json", ("$j", json));

    /// <summary>读-改-写在同一个事务里(调用方的 <paramref name="transform"/> 拿到当前原文,返回新原文)。返回写进去的新原文。</summary>
    public string Update(Func<string?, string> transform) => database.InTransaction(() =>
    {
        var next = transform(ReadJson());
        WriteJson(next);
        return next;
    });
}
