using System.Text.Json;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Catalog.Tests;

/// <summary>移植自 2.x 的 <c>kernel/upstream/__tests__/model-list.test.ts</c>:这一组守的坏全是静默的(列表少了一截、多出一个永远 404 的别名)。</summary>
public class ModelListTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ── 路径 ──

    [Fact]
    public void Anthropic_base_has_no_v1_and_the_path_adds_it()
    {
        Assert.StartsWith("https://api.routin.ai/v1/models", ModelList.Request(WireProtocol.Anthropic, "https://api.routin.ai", "k").Url);
    }

    [Fact]
    public void The_openai_family_keeps_the_version_segment_in_the_base()
    {
        Assert.Equal("https://api.openai.com/v1/models", ModelList.Request(WireProtocol.OpenAiChat, "https://api.openai.com/v1", "k").Url);
    }

    [Theory]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "https://open.bigmodel.cn/api/paas/v4/models")]
    [InlineData("https://ark.cn-beijing.volces.com/api/v3", "https://ark.cn-beijing.volces.com/api/v3/models")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai/", "https://generativelanguage.googleapis.com/v1beta/openai/models")]
    public void Providers_whose_version_segment_is_not_v1_and_trailing_slashes_are_handled(string baseUrl, string expected)
    {
        Assert.Equal(expected, ModelList.Request(WireProtocol.OpenAiChat, baseUrl, null).Url);
    }

    [Fact]
    public void A_hand_typed_anthropic_v1_base_does_not_become_v1_v1()
    {
        Assert.DoesNotContain("/v1/v1/", ModelList.Request(WireProtocol.Anthropic, "https://api.deepinfra.com/v1/anthropic/v1", "k").Url);
    }

    [Fact]
    public void The_list_paths_follow_the_same_version_convention_as_the_request_paths()
    {
        foreach (var protocol in Enum.GetValues<WireProtocol>())
        {
            Assert.Equal(protocol.PathOf().StartsWith("/v1/"), ModelList.PathOf(protocol).StartsWith("/v1/"));
        }

        Assert.Equal(ModelList.PathOf(WireProtocol.OpenAiChat), ModelList.PathOf(WireProtocol.OpenAiResponses));
    }

    // ── 鉴权头 ──

    [Fact]
    public void Anthropic_authenticates_with_x_api_key_and_a_version_header()
    {
        var headers = ModelList.Request(WireProtocol.Anthropic, "https://api.routin.ai", "sk-ant").Headers;
        Assert.Equal("sk-ant", headers["x-api-key"]);
        Assert.Equal("2023-06-01", headers["anthropic-version"]);
        Assert.False(headers.ContainsKey("authorization"));
    }

    [Fact]
    public void The_openai_family_uses_a_bearer_header()
    {
        var headers = ModelList.Request(WireProtocol.OpenAiChat, "https://api.openai.com/v1", "sk-oa").Headers;
        Assert.Equal("Bearer sk-oa", headers["authorization"]);
        Assert.False(headers.ContainsKey("x-api-key"));
    }

    [Fact]
    public void A_null_key_means_no_auth_header_at_all_not_an_empty_bearer()
    {
        var openai = ModelList.Request(WireProtocol.OpenAiChat, "https://openrouter.ai/api/v1", null).Headers;
        Assert.False(openai.ContainsKey("authorization"));
        var anthropic = ModelList.Request(WireProtocol.Anthropic, "https://api.routin.ai", null).Headers;
        Assert.False(anthropic.ContainsKey("x-api-key"));
        Assert.Equal("2023-06-01", anthropic["anthropic-version"]);
    }

    // ── 分页 ──

    [Fact]
    public void Anthropic_must_ask_for_a_big_page_because_the_default_is_20()
    {
        var url = new Uri(ModelList.Request(WireProtocol.Anthropic, "https://api.routin.ai", "k").Url);
        Assert.True(int.Parse(System.Web.HttpUtility.ParseQueryString(url.Query)["limit"]!) > 100);
        Assert.DoesNotContain('?', ModelList.Request(WireProtocol.OpenAiChat, "https://api.openai.com/v1", "k").Url);
    }

    // ── 解析 ──

    [Fact]
    public void Anthropic_data_with_display_names()
    {
        var models = ModelList.Parse(WireProtocol.Anthropic, Json("""{"data":[{"type":"model","id":"claude-a","display_name":"Claude A"},{"type":"model","id":"claude-b","display_name":"Claude B"}],"has_more":false}"""));
        Assert.Equal([new FetchedModel("claude-a", "Claude A"), new FetchedModel("claude-b", "Claude B")], models.ToArray());
    }

    [Fact]
    public void Openai_has_no_display_name_and_does_not_read_one()
    {
        var models = ModelList.Parse(WireProtocol.OpenAiChat, Json("""{"object":"list","data":[{"id":"gpt-x","object":"model","owned_by":"openai","display_name":"must be ignored"}]}"""));
        Assert.Equal([new FetchedModel("gpt-x")], models.ToArray());
    }

    [Fact]
    public void Tolerates_a_bare_array_and_a_models_key_and_keeps_upstream_order_without_duplicates()
    {
        Assert.Equal(["a", "b"], ModelList.Parse(WireProtocol.OpenAiChat, Json("""[{"id":"a"},"b"]""")).Select(m => m.Id).ToArray());
        Assert.Equal(["c"], ModelList.Parse(WireProtocol.OpenAiChat, Json("""{"models":[{"id":"c"}]}""")).Select(m => m.Id).ToArray());
        Assert.Equal(["z-new", "a-old"], ModelList.Parse(WireProtocol.OpenAiChat, Json("""{"data":[{"id":"z-new"},{"id":"a-old"}]}""")).Select(m => m.Id).ToArray());
        Assert.Single(ModelList.Parse(WireProtocol.OpenAiChat, Json("""{"data":[{"id":"a"},{"id":"a"}]}""")));
    }

    [Fact]
    public void Entries_without_a_usable_id_are_dropped_rather_than_invented()
    {
        var models = ModelList.Parse(WireProtocol.OpenAiChat, Json("""{"data":[{"id":"good"},{"object":"model"},{"id":123},{"id":"   "},null,42]}"""));
        Assert.Equal(["good"], models.Select(m => m.Id).ToArray());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"nope\"")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("""{"data":"nope"}""")]
    public void A_response_of_the_wrong_shape_gives_an_empty_list_without_throwing(string json)
    {
        Assert.Empty(ModelList.Parse(WireProtocol.OpenAiChat, Json(json)));
    }

    [Fact]
    public void An_undefined_payload_gives_an_empty_list()
    {
        Assert.Empty(ModelList.Parse(WireProtocol.OpenAiChat, default));
    }

    [Fact]
    public void Ids_are_trimmed()
    {
        Assert.Equal(["gpt-x"], ModelList.Parse(WireProtocol.OpenAiChat, Json("""{"data":[{"id":"  gpt-x  "}]}""")).Select(m => m.Id).ToArray());
    }

    // ── 失败归类 ──

    [Fact]
    public void Auth_failures_have_their_own_key_and_carry_the_upstream_message()
    {
        var failure = ModelList.Classify(401, """{"error":{"message":"invalid x-api-key"}}""");
        Assert.Equal((ProviderErrorKeys.FetchRejected, 401, "invalid x-api-key"), (failure.Key, failure.Status, failure.Detail));
        Assert.Equal(ProviderErrorKeys.FetchRejected, ModelList.Classify(403, "").Key);
    }

    [Fact]
    public void A_404_points_at_the_version_segment()
    {
        Assert.Equal(ProviderErrorKeys.FetchNotFound, ModelList.Classify(404, "").Key);
    }

    [Fact]
    public void An_html_error_page_is_never_passed_on()
    {
        var failure = ModelList.Classify(502, "<!DOCTYPE html>\n<html><body>Bad Gateway</body></html>");
        Assert.Null(failure.Detail);
        Assert.Equal(ProviderErrorKeys.FetchFailed, failure.Key);
        Assert.Equal(502, failure.Status);
    }

    [Fact]
    public void Plain_text_keeps_only_the_first_line_truncated()
    {
        var failure = ModelList.Classify(500, new string('x', 500) + "\nsecond line");
        Assert.DoesNotContain("second line", failure.Detail);
        Assert.True(failure.Detail!.Length < 260);
    }

    [Fact]
    public void A_top_level_message_without_an_error_wrapper_is_understood()
    {
        Assert.Equal("model list disabled", ModelList.Classify(400, """{"message":"model list disabled"}""").Detail);
        Assert.Equal("plain error", ModelList.Classify(400, """{"error":"plain error"}""").Detail);
    }

    // ── 凭据形状 ──

    private static readonly ProviderConfig Ollama = new("ollama-cloud", "Ollama Cloud", "https://ollama.com/v1", WireProtocol.OpenAiChat, 60, true, "provider:ollama-cloud");

    private sealed class FakeSigner : IRequestSigner
    {
        public SignedRequest Sign(string method, string path) =>
            new(new Dictionary<string, string> { ["authorization"] = "public-key-line:c2lnbmF0dXJl" }, new Dictionary<string, string> { ["ts"] = "1763100000" });
    }

    private sealed class SigningTransport : ITransportDecorator
    {
        public TransportDecoration Decorate(ProviderConfig provider, WireProtocol protocol, ProviderCredential credential, RequestContext context) =>
            new(Signer: new FakeSigner());
    }

    private static readonly OAuthCredential SignedCredential = new("ollama-cloud", "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----", "ssh-ed25519 AAAA", null, "tester");

    [Fact]
    public void A_signing_credential_never_puts_the_private_key_in_a_header()
    {
        // 2.x 实测事故:私钥 PEM 被拼成 Bearer 头,多行头抛异常,私钥整段进了错误弹窗。
        var request = ModelList.RequestFor(Ollama, WireProtocol.OpenAiChat, SignedCredential, new SigningTransport(), new RequestContext("w"));

        Assert.StartsWith("https://ollama.com/v1/models?ts=", request.Url);
        Assert.Equal("public-key-line:c2lnbmF0dXJl", request.Headers["authorization"]);
        Assert.DoesNotContain("Bearer", request.Headers["authorization"]);
        Assert.DoesNotContain("OPENSSH PRIVATE KEY", string.Join(' ', request.Headers.Values) + request.Url);
    }

    [Fact]
    public void Signing_keeps_the_anthropic_page_limit_and_appends_the_timestamp_after_it()
    {
        var request = ModelList.RequestFor(Ollama with { BaseUrl = "https://ollama.com" }, WireProtocol.Anthropic, SignedCredential, new SigningTransport(), new RequestContext("w"));
        var uri = new Uri(request.Url);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("/v1/models", uri.AbsolutePath);
        Assert.Equal("1000", query["limit"]);
        Assert.Equal("1763100000", query["ts"]);
        Assert.False(request.Headers.ContainsKey("x-api-key"));
    }

    [Fact]
    public void Api_key_credentials_are_unchanged_and_no_credential_means_no_auth_header()
    {
        var key = ModelList.RequestFor(Ollama, WireProtocol.OpenAiChat, new ApiKeyCredential("sk-1"), NoTransportDecoration.Instance, new RequestContext("w"));
        Assert.Equal("https://ollama.com/v1/models", key.Url);
        Assert.Equal("Bearer sk-1", key.Headers["authorization"]);

        var none = ModelList.RequestFor(Ollama, WireProtocol.OpenAiChat, null, NoTransportDecoration.Instance, new RequestContext("w"));
        Assert.False(none.Headers.ContainsKey("authorization"));
    }

    [Fact]
    public void A_non_signing_oauth_credential_still_uses_its_access_token_as_bearer()
    {
        var oauth = new OAuthCredential("chatgpt", "at-1", "rt-1", null, "u");
        var request = ModelList.RequestFor(Ollama with { Id = "codex" }, WireProtocol.OpenAiChat, oauth, NoTransportDecoration.Instance, new RequestContext("w"));
        Assert.Equal("Bearer at-1", request.Headers["authorization"]);
    }

    [Fact]
    public void A_signature_credential_such_as_tc3_sends_no_bearer_at_all()
    {
        var tc3 = new SignatureCredential(SignatureScheme.TencentTc3, "AKID", "secret");
        var request = ModelList.RequestFor(Ollama, WireProtocol.OpenAiChat, tc3, NoTransportDecoration.Instance, new RequestContext("w"));
        Assert.False(request.Headers.ContainsKey("authorization"));
    }

    // ── 脱敏 ──

    [Fact]
    public void A_private_key_block_is_replaced_in_error_text()
    {
        var leaked = "Headers.append: \"Bearer -----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----\" is an invalid header value.";
        var safe = ModelList.RedactSecrets(leaked);
        Assert.DoesNotContain("PRIVATE KEY-----", safe);
        Assert.DoesNotContain("b3BlbnNzaC1rZXktdjEAAAAA", safe);
        Assert.Contains("[redacted key]", safe);
        Assert.Equal("ECONNREFUSED 127.0.0.1:11434", ModelList.RedactSecrets("ECONNREFUSED 127.0.0.1:11434"));
    }
}
