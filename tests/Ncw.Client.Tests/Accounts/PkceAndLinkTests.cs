using Ncw.Client.Accounts;

namespace Ncw.Client.Tests.Accounts;

public class PkceTests
{
    [Fact]
    public void The_challenge_of_the_RFC_7636_appendix_B_verifier_is_the_published_value()
    {
        // RFC 7636 附录 B
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.ChallengeOf("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void A_created_pair_is_valid_unpadded_base64url_and_its_challenge_matches_its_verifier()
    {
        var pair = Pkce.Create();
        Assert.True(Pkce.IsValidVerifier(pair.Verifier));
        Assert.Equal(64, pair.Verifier.Length); // 48 字节
        Assert.Equal(Pkce.ChallengeOf(pair.Verifier), pair.Challenge);
        Assert.Equal(43, pair.Challenge.Length); // SHA-256 的 base64url,无填充
        Assert.DoesNotContain('=', pair.Challenge);
        Assert.DoesNotContain('+', pair.Verifier + pair.Challenge);
        Assert.DoesNotContain('/', pair.Verifier + pair.Challenge);
    }

    [Fact]
    public void Verifiers_and_states_are_never_repeated()
    {
        var verifiers = Enumerable.Range(0, 200).Select(_ => Pkce.Create().Verifier).ToHashSet();
        var states = Enumerable.Range(0, 200).Select(_ => Pkce.NewState()).ToHashSet();
        Assert.Equal(200, verifiers.Count);
        Assert.Equal(200, states.Count);
        Assert.All(states, s => Assert.Equal(32, s.Length));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("short", false)]
    [InlineData("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", true)]
    [InlineData("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjX=", false)] // 填充不是 unreserved 字符
    [InlineData("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjX+", false)]
    public void Verifier_validity_follows_RFC_7636(string? verifier, bool valid) => Assert.Equal(valid, Pkce.IsValidVerifier(verifier));

    [Fact]
    public void State_comparison_is_exact_and_rejects_empty_values()
    {
        Assert.True(Pkce.StateEquals("abc123", "abc123"));
        Assert.False(Pkce.StateEquals("abc123", "abc124"));
        Assert.False(Pkce.StateEquals("abc123", "abc12"));
        Assert.False(Pkce.StateEquals("", ""));
        Assert.False(Pkce.StateEquals(null, "x"));
        Assert.False(Pkce.StateEquals("x", null));
    }
}

public class NcwLinkParserTests
{
    [Fact]
    public void An_auth_callback_with_code_and_state_is_parsed()
    {
        var link = Assert.IsType<AuthCallbackLink>(NcwLinkParser.Parse("nextcowork://auth/callback?code=abc%20def&state=S-1_x"));
        Assert.Equal(("abc def", "S-1_x", null), (link.Code, link.State, link.Error));
    }

    [Fact]
    public void An_auth_error_callback_is_parsed_with_its_description()
    {
        var link = Assert.IsType<AuthCallbackLink>(NcwLinkParser.Parse("nextcowork://auth/callback?error=access_denied&error_description=User+said+no&state=s"));
        Assert.Equal(("access_denied", "User said no", "s"), (link.Error, link.ErrorDescription, link.State));
    }

    [Fact]
    public void Scheme_and_host_are_case_insensitive_like_URIs_are()
    {
        Assert.IsType<AuthCallbackLink>(NcwLinkParser.Parse("NextCoWork://AUTH/callback?code=c&state=s"));
    }

    [Fact]
    public void Pair_and_open_routes_are_recognised_and_unknown_routes_are_ignored()
    {
        Assert.Equal("INVITE-1", Assert.IsType<PairLink>(NcwLinkParser.Parse("nextcowork://pair?invite=INVITE-1")).Invite);
        Assert.Equal("p1", Assert.IsType<OpenLink>(NcwLinkParser.Parse("nextcowork://open?project=p1")).Query["project"]);
        Assert.Null(NcwLinkParser.Parse("nextcowork://settings/reset"));
        Assert.Null(NcwLinkParser.Parse("nextcowork://delete?all=1"));
        Assert.Null(NcwLinkParser.Parse("nextcowork://pair")); // 没有邀请
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://auth/callback?code=c&state=s")]                           // 别的 scheme
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("nextcowork:auth/callback?code=c&state=s")]                        // 没有 //
    [InlineData("nextcowork://user:pass@auth/callback?code=c&state=s")]            // 用户信息
    [InlineData("nextcowork://auth:8080/callback?code=c&state=s")]                 // 端口
    [InlineData("nextcowork://auth/callback?code=c&state=s#frag")]                 // 片段
    [InlineData("nextcowork://auth/callback/extra?code=c&state=s")]                // 多出来的路径
    [InlineData("nextcowork://auth/callback/../admin?code=c&state=s")]
    [InlineData("nextcowork://auth/callbackX?code=c&state=s")]
    [InlineData("nextcowork://auth.evil.test/callback?code=c&state=s")]            // 主机不是白名单里的
    [InlineData("nextcowork://аuth/callback?code=c&state=s")]                      // 西里尔字母 а 冒充 a
    [InlineData("nextcowork://auth/callback?code=c")]                              // 缺 state
    [InlineData("nextcowork://auth/callback?state=s")]                             // 缺 code 也没有 error
    [InlineData("nextcowork://auth/callback")]
    [InlineData("nextcowork://auth/callback?code=c&code=d&state=s")]               // 参数污染
    [InlineData("nextcowork://auth/callback?code=%zz&state=s")]                    // 非法的百分号编码
    [InlineData("nextcowork://auth/callback?code=a%00b&state=s")]                  // 解码出控制字符
    [InlineData("nextcowork://auth/callback?code=a\nb&state=s")]                   // 原始控制字符
    [InlineData("nextcowork://auth/callback?code=a b&state=s")]                    // 空白
    [InlineData("nextcowork://auth/callback?=x&code=c&state=s")]                   // 空参数名
    public void Hostile_or_malformed_links_are_rejected(string? raw) => Assert.Null(NcwLinkParser.Parse(raw));

    [Fact]
    public void Oversized_links_and_values_and_parameter_floods_are_rejected()
    {
        Assert.Null(NcwLinkParser.Parse("nextcowork://auth/callback?code=" + new string('a', 3000) + "&state=s"));
        Assert.Null(NcwLinkParser.Parse("nextcowork://auth/callback?code=c&state=s&pad=" + new string('a', NcwLinkParser.MaxLength)));
        var flood = string.Join('&', Enumerable.Range(0, NcwLinkParser.MaxParameters + 1).Select(i => $"k{i}=v"));
        Assert.Null(NcwLinkParser.Parse("nextcowork://open?" + flood));
    }

    [Fact]
    public void Unknown_parameters_are_tolerated_but_never_trusted()
    {
        var link = Assert.IsType<AuthCallbackLink>(NcwLinkParser.Parse("nextcowork://auth/callback?code=c&state=s&redirect=https%3A%2F%2Fevil.test&team=other"));
        Assert.Equal(("c", "s"), (link.Code, link.State)); // 多余的参数不进结果
    }
}
