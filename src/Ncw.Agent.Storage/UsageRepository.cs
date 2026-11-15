namespace Ncw.Agent.Storage;

/// <summary>
/// 一次 HTTP 尝试的用量记录(<c>usage_records</c> 的一行,2.x 迁移 3 / 8 的结构)。费用在写入那一刻冻结,事后改价格不回算:
/// <see cref="CostMicros"/> 为 null = 查不到价格(「未知」),绝不是 0。币种是 <c>USD</c> / <c>CNY</c>。
/// </summary>
public sealed record UsageRow(
    string Id,
    long At,
    string RunId,
    string WorkspaceId,
    string SessionId,
    int Attempt,
    string ProviderId,
    string ProviderName,
    string Protocol,
    string Endpoint,
    string Alias,
    string UpstreamModel,
    string? ResponseModel,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long CacheWrite1hTokens,
    long? ThinkingTokens,
    int LatencyMs,
    int? TimeToFirstTokenMs,
    bool Ok,
    int? HttpStatus,
    string? ErrorKind,
    string? ErrorMessage,
    string? StopReason,
    int ToolCalls,
    long? CostMicros,
    string? Currency,
    int? PricingTier,
    string? PricingWindow,
    bool ThinkingTokensEstimated = false,
    int ToolErrors = 0);

/// <summary>用量记录的写入(<c>usage_records</c> 表)。聚合(每个 Run 的累计)在 <c>SessionRepository.RunUsage</c>,读写同一张表。</summary>
public sealed partial class UsageRepository(AgentDatabase database)
{
    public void Insert(UsageRow row) => database.Run("""
        INSERT OR REPLACE INTO usage_records (
          id, at, run_id, provider_id, alias, upstream_model,
          input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cache_write_1h_tokens,
          latency_ms, ok, http_status, error_kind, cost_micros, currency, pricing_tier, pricing_window, tool_calls, tool_errors, thinking_tokens_estimated,
          workspace_id, session_id, attempt, provider_name, protocol, endpoint, response_model, thinking_tokens, time_to_first_token_ms,
          stop_reason, error_message)
        VALUES (
          $id, $at, $run, $provider, $alias, $upstream,
          $in, $out, $cr, $cw, $cw1,
          $latency, $ok, $status, $kind, $cost, $currency, $tier, $window, $tools, $toolErrors, $thinkEst,
          $ws, $session, $attempt, $pname, $protocol, $endpoint, $resp, $think, $ttft,
          $stop, $err)
        """,
        ("$id", row.Id), ("$at", row.At), ("$run", row.RunId), ("$provider", row.ProviderId), ("$alias", row.Alias), ("$upstream", row.UpstreamModel),
        ("$in", row.InputTokens), ("$out", row.OutputTokens), ("$cr", row.CacheReadTokens), ("$cw", row.CacheWriteTokens), ("$cw1", row.CacheWrite1hTokens),
        ("$latency", row.LatencyMs), ("$ok", row.Ok ? 1 : 0), ("$status", row.HttpStatus), ("$kind", row.ErrorKind),
        ("$cost", row.CostMicros), ("$currency", row.Currency), ("$tier", row.PricingTier), ("$window", row.PricingWindow), ("$tools", row.ToolCalls),
        ("$ws", row.WorkspaceId), ("$session", row.SessionId), ("$attempt", row.Attempt), ("$pname", row.ProviderName), ("$protocol", row.Protocol),
        ("$endpoint", row.Endpoint), ("$resp", row.ResponseModel), ("$think", row.ThinkingTokens), ("$ttft", row.TimeToFirstTokenMs),
        ("$stop", row.StopReason), ("$err", row.ErrorMessage), ("$toolErrors", row.ToolErrors), ("$thinkEst", row.ThinkingTokensEstimated ? 1 : 0));
}
