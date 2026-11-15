using System.Text.Json.Nodes;
using Ncw.Agent.Storage;

namespace Ncw.Agent.Services.Widgets;

public enum WidgetFailure
{
    Invalid,
    TooLarge,
}

public sealed class WidgetException(WidgetFailure kind, string key, string message) : Exception(message)
{
    public WidgetFailure Kind { get; } = kind;

    public string Key { get; } = key;
}

/// <summary>
/// 内联可视化小组件的持久化状态(2.x 的 <c>widget-host.ts</c> 的 <c>getWidgetState</c> / <c>setWidgetState</c>):每张卡(<c>sessionId</c> + <c>callId</c>)一份 JSON,存在 kv 表的
/// <c>session.widget.&lt;sessionId&gt;</c>(2.x 同一个键,删会话 / 清空历史时已经按它清理)。状态是<b>不可信输入</b>(组件是模型写的):大小有上限,不是合法 JSON 的拒绝。
/// 小组件的状态不进模型上下文。手机端降级为静态快照:读状态 viewer 起,写 member 起。
/// </summary>
public sealed class WidgetStateStore(KvStore kv)
{
    /// <summary>单张卡的状态上限(UTF-8 字节)。</summary>
    public const int MaxStateBytes = 64 * 1024;

    /// <summary>一个会话里最多记多少张卡的状态(超过的最早的丢掉)。</summary>
    public const int MaxCardsPerSession = 200;

    private readonly Lock _gate = new();

    private static string Key(string sessionId) => $"session.widget.{sessionId}";

    /// <summary>某张卡的状态(JSON 文本);没有 = null。</summary>
    public string? Get(string sessionId, string callId)
    {
        lock (_gate)
        {
            return Load(sessionId)[callId]?.ToJsonString();
        }
    }

    /// <summary>写某张卡的状态;<paramref name="stateJson"/> 为 null = 清掉这张卡的状态。</summary>
    public void Set(string sessionId, string callId, string? stateJson)
    {
        if (callId.Length is 0 or > 256)
        {
            throw new WidgetException(WidgetFailure.Invalid, "widgets.invalid.callId", "A widget call id is 1-256 characters.");
        }

        JsonNode? state = null;
        if (stateJson is not null)
        {
            if (System.Text.Encoding.UTF8.GetByteCount(stateJson) > MaxStateBytes)
            {
                throw new WidgetException(WidgetFailure.TooLarge, "widgets.error.stateTooLarge", "The widget state is too large.");
            }

            try
            {
                state = JsonNode.Parse(stateJson);
            }
            catch (System.Text.Json.JsonException)
            {
                throw new WidgetException(WidgetFailure.Invalid, "widgets.invalid.stateJson", "The widget state is not valid JSON.");
            }
        }

        lock (_gate)
        {
            var map = Load(sessionId);
            if (state is null)
            {
                map.Remove(callId);
            }
            else
            {
                map.Remove(callId); // 重写时挪到最新的位置(JsonObject 保序:最早的在前)
                map[callId] = state;
                while (map.Count > MaxCardsPerSession)
                {
                    map.Remove(map.First().Key);
                }
            }

            kv.Set(Key(sessionId), map.ToJsonString());
        }
    }

    private JsonObject Load(string sessionId)
    {
        try
        {
            return kv.Get(Key(sessionId)) is { } json && JsonNode.Parse(json) is JsonObject map ? map : new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return new JsonObject(); // 坏值当作没有:下一次写会重写
        }
    }
}
