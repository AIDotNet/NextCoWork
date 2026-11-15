using System.Text;
using System.Text.Json;
using Ncw.Agent.Llm.Json;

namespace Ncw.Agent.Llm;

/// <summary>
/// 把事件流折成最终的助手消息(2.x <c>BlockAccumulator</c> / <c>ToolCallAccumulator</c>)。
/// 块按下标归位(可以交错);工具调用参数按字符串累积,只在 end 时解析一次,失败变成 <see cref="ToolCallPart.InputError"/> 而不是异常;
/// 同下标的 <see cref="BlockOpaque"/> 合进思考块;出错或取消时最终消息仍保留已收到的内容和用量。
/// </summary>
public sealed class StreamAssembler
{
    private enum Kind
    {
        Text,
        Thinking,
        ToolCall,
    }

    private sealed class Block(Kind kind)
    {
        public Kind Kind { get; } = kind;

        public StringBuilder Text { get; } = new();

        public ProtocolOpaque? Opaque { get; set; }

        public string CallId { get; set; } = "";

        public string Name { get; set; } = "";

        public bool Ended { get; set; }

        public JsonElement Input { get; set; }

        public string? InputError { get; set; }
    }

    private readonly SortedDictionary<int, Block> _blocks = [];
    private readonly Dictionary<string, int> _callIndex = [];

    public string? Model { get; private set; }

    public string? ProviderId { get; private set; }

    public string? Api { get; private set; }

    public string? UpstreamModel { get; private set; }

    public StopReason? Stop { get; private set; }

    public TokenUsage Usage { get; private set; } = new();

    public CostInfo Cost { get; private set; } = CostInfo.NotMetered;

    public int? LatencyMs { get; private set; }

    public AgentError? Error { get; private set; }

    /// <summary>收到过 <see cref="MessageEnd"/>。</summary>
    public bool Completed { get; private set; }

    /// <summary>已经 start 但没有 end 的工具调用(流被截断时用来补孤儿结果)。</summary>
    public IEnumerable<(string CallId, string Name)> PendingToolCalls =>
        _blocks.Values.Where(b => b is { Kind: Kind.ToolCall, Ended: false }).Select(b => (b.CallId, b.Name));

    private Block Get(int index, Kind kind)
    {
        if (!_blocks.TryGetValue(index, out var block))
        {
            block = new Block(kind);
            _blocks[index] = block;
        }

        return block;
    }

    public void Apply(LlmEvent ev)
    {
        switch (ev)
        {
            case MessageStart start:
                Model = start.Model;
                ProviderId = start.ProviderId ?? ProviderId;
                Api = start.Api ?? Api;
                UpstreamModel = start.UpstreamModel ?? UpstreamModel;
                break;
            case TextDelta text:
                Get(text.Index, Kind.Text).Text.Append(text.Text);
                break;
            case ThinkingDelta thinking:
                Get(thinking.Index, Kind.Thinking).Text.Append(thinking.Text);
                break;
            case BlockOpaque opaque:
                // 透传载体和同下标的思考文字合进同一个思考块;没有文字(redacted、空推理条目)时单独成块。
                Get(opaque.Index, Kind.Thinking).Opaque = opaque.Opaque;
                break;
            case ToolCallStart start:
            {
                var block = Get(start.Index, Kind.ToolCall);
                block.CallId = start.CallId;
                block.Name = start.Name;
                _callIndex[start.CallId] = start.Index;
                break;
            }

            case ToolCallDelta delta:
                if (_callIndex.TryGetValue(delta.CallId, out var deltaIndex))
                {
                    _blocks[deltaIndex].Text.Append(delta.ArgsDelta);
                }

                break;
            case ToolCallEnd end:
                if (_callIndex.TryGetValue(end.CallId, out var endIndex))
                {
                    var block = _blocks[endIndex];
                    block.Ended = true;
                    // 空参数是合法的:无参工具的上游可能一个 delta 都不发。
                    var raw = block.Text.ToString();
                    var json = raw.Trim().Length == 0 ? "{}" : raw;
                    try
                    {
                        block.Input = Jsn.Parse(json);
                    }
                    catch (JsonException e)
                    {
                        block.Input = Jsn.EmptyObject;
                        block.InputError = $"Invalid tool arguments JSON ({e.Message}): {raw}";
                    }
                }

                break;
            case MessageEnd end:
                Completed = true;
                Stop = end.Stop;
                Usage = end.Usage;
                Cost = end.Cost;
                LatencyMs = end.LatencyMs;
                break;
            case StreamError error:
                Error = error.Error;
                break;
        }
    }

    /// <summary>
    /// 最终的块序列。没有 end 的工具调用(流被截断)的参数一律判失败。
    /// <c>stopReason=MaxTokens</c> 时 pi 规则是所有工具调用判失败并提示重发,这是 Loop 的事;这里只如实标出解析失败的调用。
    /// </summary>
    public IReadOnlyList<Part> Parts()
    {
        var parts = new List<Part>();
        foreach (var block in _blocks.Values)
        {
            switch (block.Kind)
            {
                case Kind.Text:
                    if (block.Text.Length > 0)
                    {
                        parts.Add(new TextPart(block.Text.ToString()));
                    }

                    break;
                case Kind.Thinking:
                    if (block.Text.Length > 0 || block.Opaque is not null)
                    {
                        parts.Add(new ThinkingPart(block.Text.ToString(), block.Opaque));
                    }

                    break;
                case Kind.ToolCall:
                    parts.Add(block.Ended
                        ? new ToolCallPart(block.CallId, block.Name, block.Input, block.InputError)
                        : new ToolCallPart(block.CallId, block.Name, Jsn.EmptyObject, $"Tool call was cut off before it completed: {block.Text}"));
                    break;
            }
        }

        return parts;
    }

    /// <summary>
    /// 折成一条助手消息。取消时标 <see cref="StopReason.Aborted"/>;其它错误追加一个只给界面的 <see cref="ErrorPart"/>
    /// (回放时整条跳过)。来源取自路由补进 <see cref="MessageStart"/> 的三元组。
    /// </summary>
    public Message ToMessage(MessageId id, DateTimeOffset createdAt)
    {
        var parts = Parts().ToList();
        StopReason? stop = Stop;
        if (Error is { } error)
        {
            if (error.Code == AgentErrorCode.Aborted)
            {
                stop = StopReason.Aborted;
            }
            else
            {
                parts.Add(new ErrorPart(error));
            }
        }

        var source = Api is not null && ProviderId is not null && UpstreamModel is not null ? new Provenance(Api, ProviderId, UpstreamModel) : null;
        return new Message(id, Role.Assistant, parts, createdAt, false, source, stop);
    }
}
