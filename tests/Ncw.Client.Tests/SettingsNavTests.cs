using System.Collections.Immutable;
using Ncw.Client.Mock;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>设置页的导航(docs/design/07 §3,方案 D):分区归哪个导航项、列表栏、点导航项回到上次的子分区、模型页的供应商主从选择。</summary>
public class SettingsNavTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 10, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Every_section_belongs_to_exactly_one_page_and_the_pages_list_it()
    {
        foreach (var section in Enum.GetValues<SettingsSection>())
        {
            var page = SettingsNav.PageOf(section);
            Assert.Contains(section, SettingsNav.SectionsOf(page));
            Assert.Single(SettingsNav.Pages, p => SettingsNav.SectionsOf(p).Contains(section));
        }

        Assert.Equal(11, SettingsNav.Pages.Count);
        Assert.Equal(Enum.GetValues<SettingsSection>().Length, SettingsNav.Pages.Sum(p => SettingsNav.SectionsOf(p).Count));
    }

    [Fact]
    public void Only_pages_with_sub_sections_and_models_have_a_list_column()
    {
        Assert.Equal([SettingsPage.Models, SettingsPage.Extensions, SettingsPage.Connection, SettingsPage.Data],
            SettingsNav.Pages.Where(SettingsNav.HasList));
    }

    [Fact]
    public void Clicking_a_page_returns_to_its_last_sub_section_until_settings_is_reopened()
    {
        var settings = new ShellViewModel(MockWorkspace.Create(Now)).Settings;
        settings.OpenCommand.Execute(null);

        settings.ShowPageCommand.Execute(SettingsPage.Extensions);
        Assert.Equal(SettingsSection.Skills, settings.Section);
        Assert.True(settings.ShowsSubSections);
        Assert.Equal(SettingsSection.Skills, Assert.Single(settings.SubSections, s => s.IsCurrent).Section);

        settings.ShowCommand.Execute(SettingsSection.Mcp);
        Assert.Equal(SettingsSection.Mcp, Assert.Single(settings.SubSections, s => s.IsCurrent).Section);
        settings.ShowPageCommand.Execute(SettingsPage.Appearance);
        Assert.Empty(settings.SubSections);
        Assert.False(settings.ShowsList);
        settings.ShowPageCommand.Execute(SettingsPage.Extensions);
        Assert.Equal(SettingsSection.Mcp, settings.Section);

        // 搜索 / 命令面板直接改分区:导航项跟着走
        settings.ShowCommand.Execute(SettingsSection.Gateway);
        Assert.Equal(SettingsPage.Connection, settings.Page);

        settings.CloseCommand.Execute(null);
        settings.OpenCommand.Execute(null);
        settings.ShowPageCommand.Execute(SettingsPage.Extensions);
        Assert.Equal(SettingsSection.Skills, settings.Section); // 重新打开:从头来
    }

    [Fact]
    public void Models_list_selects_a_provider_and_falls_back_to_the_catalog_when_it_disappears()
    {
        var stores = MockWorkspace.Create(Now);
        SeedProviders(stores, "anthropic", "openai");
        var models = new ShellViewModel(stores).Settings.Models;
        Assert.True(models.ShowsCatalog);
        Assert.Null(models.SelectedProvider);

        models.Providers.Single(p => p.Id == "openai").SelectCommand.Execute(null);
        Assert.Equal("openai", models.SelectedProvider?.Id);
        Assert.False(models.ShowsCatalog);
        Assert.Equal(["openai"], models.Providers.Where(p => p.IsSelected).Select(p => p.Id));

        SeedProviders(stores, "anthropic"); // openai 在设备上被删了
        Assert.Null(models.SelectedProviderId);
        Assert.True(models.ShowsCatalog);
        Assert.DoesNotContain(models.Providers, p => p.IsSelected);

        models.Providers.Single().SelectCommand.Execute(null);
        models.SelectCatalogCommand.Execute(null);
        Assert.True(models.ShowsCatalog);
    }

    private static void SeedProviders(ClientStores stores, params string[] ids) =>
        stores.Providers.Update(_ => new ProvidersMirror(ImmutableDictionary<string, DeviceProviders>.Empty.Add("local",
            DeviceProviders.Unknown with
            {
                Known = true,
                Providers = [.. ids.Select(id => new ProviderDto(id, id, "openai-chat", "https://x", 50, true, "5m", null, false, null))],
            })));
}
