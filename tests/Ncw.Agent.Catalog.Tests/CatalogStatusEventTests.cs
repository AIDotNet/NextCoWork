using System.Net;

namespace Ncw.Agent.Catalog.Tests;

/// <summary>
/// <see cref="ModelCatalogService.StatusChanged"/>:每次检查结束(更新、304、失败、被拒)都触发一次,参数是检查后的状态——
/// 定时器自己发起的检查控制端看不见,靠它把「上次检查时间 / 最近一次错误」推出去(协议 <c>catalog.changed</c>)。目录有可见变化时 <see cref="ModelCatalogService.Changed"/> 先触发。
/// </summary>
public class CatalogStatusEventTests : IDisposable
{
    private readonly FakeHttp _http = new();
    private readonly ManualClock _clock = new();
    private readonly ModelCatalogService _service;

    public CatalogStatusEventTests() =>
        _service = new ModelCatalogService(_http, new MemoryCatalogStore(), new MemoryOverrideStore(), _clock, new ManualDelayer());

    public void Dispose() => _service.Dispose();

    [Fact]
    public async Task Every_check_reports_its_status_and_a_visible_change_is_announced_before_it()
    {
        var order = new List<string>();
        var statuses = new List<CatalogStatus>();
        _service.Changed += _ => order.Add("changed");
        _service.StatusChanged += status =>
        {
            order.Add("status");
            statuses.Add(status);
        };
        _http.Ok(Samples.ModelsDev(30), etag: "\"e1\"").Status(HttpStatusCode.NotModified).Status(HttpStatusCode.BadGateway).Ok("{ not json");

        await _service.RefreshAsync(); // 更新
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _service.RefreshAsync(); // 304
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _service.RefreshAsync(); // 失败
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _service.RefreshAsync(); // 被拒

        Assert.Equal(["changed", "status", "status", "status", "status"], order); // 只有更新那次有 changed,且在 status 之前
        Assert.Null(statuses[0].LastError);
        Assert.Null(statuses[1].LastError); // 304 清掉旧错误并更新检查时间
        Assert.True(statuses[1].LastCheckedAt > statuses[0].LastCheckedAt);
        Assert.Equal(CatalogErrorKeys.HttpStatus, statuses[2].LastError!.Key);
        Assert.Equal(CatalogErrorKeys.InvalidJson, statuses[3].LastError!.Key);
        Assert.Equal(statuses[0].Version, statuses[3].Version); // 失败 / 被拒不改版本
    }

    [Fact]
    public async Task A_throwing_status_subscriber_cannot_break_the_refresh()
    {
        _service.StatusChanged += _ => throw new InvalidOperationException("subscriber bug");
        _http.Ok(Samples.ModelsDev(30));

        var result = await _service.RefreshAsync();

        Assert.Equal(CatalogRefreshOutcome.Updated, result.Outcome);
    }
}
