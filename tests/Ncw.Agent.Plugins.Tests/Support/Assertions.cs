namespace Ncw.Agent.Plugins.Tests.Support;

public static class Wait
{
    /// <summary>轮询到条件成立(最多 <paramref name="timeout"/>);没成立就失败。</summary>
    public static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for: {what}");
            }

            await Task.Delay(10);
        }
    }
}

public static class Js
{
    /// <summary>造一个 JSON 对象文本(命令的 args)。值只认字符串 / 整数 / 布尔 / null / 字符串数组(不走反射序列化)。</summary>
    public static string Args(params (string Key, object? Value)[] properties)
    {
        var o = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, value) in properties)
        {
            o[key] = value switch
            {
                null => null,
                string s => s,
                int i => i,
                bool b => b,
                string[] a => new System.Text.Json.Nodes.JsonArray([.. a.Select(x => (System.Text.Json.Nodes.JsonNode?)x)]),
                _ => throw new ArgumentException($"unsupported value {value.GetType()}"),
            };
        }

        return o.ToJsonString();
    }
}
