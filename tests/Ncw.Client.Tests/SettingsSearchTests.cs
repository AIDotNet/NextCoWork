using Ncw.Client.ViewModels;

namespace Ncw.Client.Tests;

/// <summary>设置搜索(docs/design/03 §11.6):每个词都要在同一个分区出现;分区名命中的排前面;摘要是命中的那一句。</summary>
public class SettingsSearchTests
{
    private static readonly (SettingsSection, string, IReadOnlyList<string>)[] Index =
    [
        (SettingsSection.Appearance, "外观", ["动效", "完整 / 减弱 / 关闭"]),
        (SettingsSection.General, "通用", ["默认模型", "上游空闲超时(秒)", "使用代理", "用于这台设备访问模型供应商的请求。"]),
        (SettingsSection.Remote, "连接与远程", ["允许远程连接", "中继地址"]),
        (SettingsSection.Gateway, "网关", ["开放网关", "代理到本机的请求"]),
    ];

    [Fact]
    public void Finds_settings_by_their_name_or_description()
    {
        var hits = SettingsSearch.Find("代理", Index);
        Assert.Equal([SettingsSection.General, SettingsSection.Gateway], hits.Select(h => h.Section));
        Assert.Equal("使用代理", hits[0].Snippet);

        Assert.Equal(SettingsSection.General, Assert.Single(SettingsSearch.Find("供应商 请求", Index)).Section);
    }

    [Fact]
    public void Section_name_hits_come_first_and_empty_queries_find_nothing()
    {
        var hits = SettingsSearch.Find("网关", Index);
        Assert.Equal(SettingsSection.Gateway, hits[0].Section);
        Assert.Equal("网关", hits[0].Snippet);
        Assert.Empty(SettingsSearch.Find("   ", Index));
        Assert.Empty(SettingsSearch.Find("zzz", Index));
    }
}
