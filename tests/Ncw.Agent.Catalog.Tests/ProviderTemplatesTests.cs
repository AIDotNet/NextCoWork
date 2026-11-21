using Ncw.Agent.Catalog.Templates;
using Ncw.Agent.Llm.Media;

namespace Ncw.Agent.Catalog.Tests;

/// <summary>
/// 添加供应商的内置模版(docs/design/07 §3「添加供应商」):和自动发现共用一张已知服务表、一个端点一条、id 可以直接当供应商 id。
/// </summary>
public sealed class ProviderTemplatesTests
{
    [Fact]
    public void Every_known_service_has_its_default_template()
    {
        foreach (var service in ProviderTemplates.KnownServiceIds)
        {
            var main = Assert.Single(ProviderTemplates.All, t => t.Id == service);
            Assert.Equal(service, main.Service);
            Assert.Null(main.Variant);
        }
    }

    [Fact]
    public void Ids_are_unique_and_usable_as_provider_ids()
    {
        Assert.Equal(ProviderTemplates.All.Count, ProviderTemplates.All.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(ProviderTemplates.All, t => Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", t.Id));
    }

    [Fact]
    public void Each_endpoint_is_its_own_template()
    {
        var deepseek = ProviderTemplates.All.Where(t => t.Service == "deepseek").ToList();
        Assert.Equal(["deepseek", "deepseek-anthropic"], deepseek.Select(t => t.Id));
        Assert.Equal(ProviderProtocol.Anthropic, deepseek[1].Protocol);
        Assert.Equal("https://api.deepseek.com/anthropic", deepseek[1].BaseUrl);

        var kimi = ProviderTemplates.All.Where(t => t.Service == "moonshot").Select(t => (t.Variant, t.BaseUrl)).ToList();
        Assert.Equal(
            [(null, "https://api.moonshot.cn/v1"), (ProviderTemplateVariants.International, "https://api.moonshot.ai/v1"), (ProviderTemplateVariants.Anthropic, "https://api.moonshot.cn/anthropic")],
            kimi);

        // 其余文本端点变体不进「常用」,只有默认端点或独立媒体模版可能进
        Assert.All(ProviderTemplates.All.Where(t => t.Kind == ProviderKind.Text && t.Variant is not null), t => Assert.False(t.Featured));
    }

    [Fact]
    public void Groups_variants_and_urls_are_well_formed()
    {
        Assert.All(ProviderTemplates.All, t =>
        {
            Assert.Contains(t.Group, ProviderTemplateGroups.All);
            Assert.True(t.Variant is null || ProviderTemplateVariants.All.Contains(t.Variant), t.Id);
            Assert.True(Uri.TryCreate(t.BaseUrl, UriKind.Absolute, out _), t.Id);
            // 要 key 的都给拿 key 的地址(https);本机服务不要 key 也没有
            if (t.NeedsKey)
            {
                Assert.StartsWith("https://", t.KeyUrl, StringComparison.Ordinal);
            }
            else
            {
                Assert.Null(t.KeyUrl);
                Assert.Equal(ProviderTemplateGroups.Local, t.Group);
            }
        });
    }

    [Fact]
    public void Anthropic_endpoints_use_the_anthropic_protocol()
    {
        Assert.All(ProviderTemplates.All.Where(t => t.Variant == ProviderTemplateVariants.Anthropic), t => Assert.Equal(ProviderProtocol.Anthropic, t.Protocol));
        Assert.Contains(ProviderTemplates.All, t => t.Featured && t.Id == "anthropic");
        Assert.Contains(ProviderTemplates.All, t => t.Featured && t.Id == "ollama" && !t.NeedsKey);
    }
}
