using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// 内置模版(<c>providers.templates.*</c>)的线上形状:代表性载荷往返、方法表声明、冻结向量(<see cref="GoldenV1ProviderTemplates"/>)。
/// 只新增方法 / 载荷类型(后加的能力 <c>providers.templates</c>),没改帧格式也没改任何已冻结类型,所以仍是协议 v1。
/// </summary>
public class ProviderTemplatesDtoTests
{
    private static readonly ProviderTemplatesResult Sample = new(
    [
        new ProviderTemplateDto("deepseek", "deepseek", "DeepSeek", ProviderTemplateGroups.China, null, "openai-chat", "https://api.deepseek.com",
            "https://platform.deepseek.com/api_keys", true, true),
        new ProviderTemplateDto("deepseek-anthropic", "deepseek", "DeepSeek", ProviderTemplateGroups.China, ProviderTemplateVariants.Anthropic, "anthropic",
            "https://api.deepseek.com/anthropic", "https://platform.deepseek.com/api_keys", true, false),
        new ProviderTemplateDto("ollama", "ollama", "Ollama", ProviderTemplateGroups.Local, null, "openai-chat", "http://localhost:11434/v1", null, false, true),
    ]);

    private static byte[] Encode(ProviderTemplatesResult value) => FrameCodec.EncodePayload(value, PayloadShapes.GetShape<ProviderTemplatesResult>());

    [Fact]
    public void Sample_round_trips_to_the_same_bytes()
    {
        var bytes = Encode(Sample);
        Assert.Equal(Convert.ToHexStringLower(bytes), Convert.ToHexStringLower(Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<ProviderTemplatesResult>()))));
    }

    [Fact]
    public void Golden_vectors_v1_provider_templates() =>
        Assert.Equal(GoldenV1ProviderTemplates.Of("templates.list"), Convert.ToHexStringLower(Encode(Sample)));

    [Fact]
    public void List_is_a_member_query_under_its_own_capability()
    {
        var list = Assert.Single(ProviderTemplatesMethods.All);
        Assert.Equal("providers.templates.list", list.Name);
        Assert.Equal(MethodKind.Query, list.Kind);
        Assert.Equal(Role.Member, list.MinRole); // 和 providers.list 一样:能看供应商就能看模版(logo 也靠它)
        Assert.Equal(Capabilities.ProviderTemplates, list.Capability);

        // 新能力的方法不混进已冻结的 providers 方法表
        Assert.DoesNotContain(ProvidersMethods.All, m => m.Name.StartsWith("providers.templates.", StringComparison.Ordinal));
    }

    [Fact]
    public void No_template_field_could_carry_a_key() =>
        Assert.DoesNotContain(typeof(ProviderTemplateDto).GetProperties(), p => p.Name is "ApiKey" or "Key" or "Secret" or "Token" or "Credential");
}
