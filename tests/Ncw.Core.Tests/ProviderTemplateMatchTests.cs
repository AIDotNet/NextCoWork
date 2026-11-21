using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

/// <summary>供应商 → 内置模版(logo、「获取 key」链接,docs/design/07 §3「添加供应商」)。</summary>
public class ProviderTemplateMatchTests
{
    private static readonly TemplateRef[] Templates =
    [
        new("deepseek", "deepseek", "https://api.deepseek.com", "openai-chat"),
        new("deepseek-anthropic", "deepseek", "https://api.deepseek.com/anthropic", "anthropic"),
        new("moonshot", "moonshot", "https://api.moonshot.cn/v1", "openai-chat"),
        new("moonshot-intl", "moonshot", "https://api.moonshot.ai/v1", "openai-chat"),
        new("ollama", "ollama", "http://localhost:11434/v1", "openai-chat"),
    ];

    [Theory]
    [InlineData("https://api.deepseek.com", "openai-chat", "deepseek")]
    [InlineData("https://api.deepseek.com/", "openai-chat", "deepseek")]                   // 末尾斜杠
    [InlineData("https://API.DeepSeek.com/anthropic", "anthropic", "deepseek-anthropic")]  // 主机名不分大小写
    [InlineData("https://api.deepseek.com/v1", "openai-chat", "deepseek")]                 // 地址不完全一样:同主机、同协议
    [InlineData("https://api.deepseek.com/beta", "anthropic", "deepseek-anthropic")]
    [InlineData("https://api.deepseek.com/anthropic", "openai-chat", "deepseek-anthropic")] // 完整地址相同优先于协议
    [InlineData("https://api.moonshot.ai/v1", "openai-chat", "moonshot-intl")]
    [InlineData("http://127.0.0.1:11434/v1", "openai-chat", "ollama")]                     // 本机按端口
    public void Finds_the_template_for_an_address(string url, string protocol, string expected) =>
        Assert.Equal(expected, ProviderTemplateMatch.Find(url, protocol, Templates)?.Id);

    [Theory]
    [InlineData("https://gateway.example.com/v1")]
    [InlineData("http://localhost:8080/v1")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Unknown_addresses_have_no_template(string url) =>
        Assert.Null(ProviderTemplateMatch.Find(url, "openai-chat", Templates));

    [Fact]
    public void No_templates_means_no_match() =>
        Assert.Null(ProviderTemplateMatch.Find("https://api.deepseek.com", "openai-chat", []));

    [Fact]
    public void Suggested_ids_never_collide()
    {
        Assert.Equal("deepseek", ProviderTemplateMatch.SuggestId("deepseek", ["openai"]));
        Assert.Equal("deepseek-2", ProviderTemplateMatch.SuggestId("deepseek", ["deepseek"]));
        Assert.Equal("deepseek-3", ProviderTemplateMatch.SuggestId("deepseek", ["deepseek", "deepseek-2"]));
    }

    [Fact]
    public void Shared_gateway_addresses_disambiguate_by_provider_kind()
    {
        TemplateRef[] ark =
        [
            new("volcengine", "volcengine", "https://ark.cn-beijing.volces.com/api/v3", "openai-chat", ProviderKinds.Text),
            new("volcengine-image", "volcengine", "https://ark.cn-beijing.volces.com/api/v3", "volcengine-images", ProviderKinds.Image),
            new("volcengine-video", "volcengine", "https://ark.cn-beijing.volces.com/api/v3", "volcengine-videos", ProviderKinds.Video),
        ];

        Assert.Equal("volcengine", ProviderTemplateMatch.Find("https://ark.cn-beijing.volces.com/api/v3", "openai-chat", ark, ProviderKinds.Text)?.Id);
        Assert.Equal("volcengine-image", ProviderTemplateMatch.Find("https://ark.cn-beijing.volces.com/api/v3", "volcengine-images", ark, ProviderKinds.Image)?.Id);
        Assert.Equal("volcengine-video", ProviderTemplateMatch.Find("https://ark.cn-beijing.volces.com/api/v3", "volcengine-videos", ark, ProviderKinds.Video)?.Id);
        // 音频下火山没有模版:不能误匹配到文本或图片模版
        Assert.Null(ProviderTemplateMatch.Find("https://ark.cn-beijing.volces.com/api/v3", "openai-audio", ark, ProviderKinds.Audio));
    }
}
