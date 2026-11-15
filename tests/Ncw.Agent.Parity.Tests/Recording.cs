using System.Text.Json.Nodes;

namespace Ncw.Agent.Parity.Tests;

/// <summary>
/// 从 2.x 录制的一个场景(<c>Recordings/*.json</c>,纯数据)。<see cref="Input"/> 是回放的全部输入(上游 SSE 原文、工具结果、用户操作),
/// <see cref="Expected"/> 是 2.x 在同一份输入上产出的规范化结果(事件序列、落库行、发给上游的请求体)。
/// 录制脚本在仓库之外(见 docs/parity.md「回放对照的录制方法」)。
/// </summary>
public sealed class Recording
{
    private Recording(string file, JsonObject root)
    {
        File = file;
        Root = root;
    }

    public string File { get; }

    public JsonObject Root { get; }

    public string Id => Root["id"]!.GetValue<string>();

    public string Description => Root["description"]!.GetValue<string>();

    public JsonObject Source => Root["source"]!.AsObject();

    public JsonObject Input => Root["input"]!.AsObject();

    public JsonObject Expected => Root["expected"]!.AsObject();

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Recordings");

    public static IReadOnlyList<Recording> LoadAll() =>
        [.. System.IO.Directory.EnumerateFiles(Directory, "*.json").OrderBy(f => f, StringComparer.Ordinal).Select(Load)];

    public static Recording Load(string file) => new(file, JsonNode.Parse(System.IO.File.ReadAllText(file))!.AsObject());

    public static IEnumerable<object[]> Ids() => LoadAll().Select(r => new object[] { r.Id });

    public static Recording ById(string id) => LoadAll().Single(r => r.Id == id);
}
