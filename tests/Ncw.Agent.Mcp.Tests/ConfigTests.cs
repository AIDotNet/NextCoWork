using System.Text.Json;

namespace Ncw.Agent.Mcp.Tests;

/// <summary>
/// 配置 / 校验 / 密钥存放 / 脱敏。移植自 2.x 的 <c>mcp-methods.test.ts</c>(拒绝不安全或畸形的配置;密钥整张 map 写入、未声明的键丢弃、值不回程)
/// 和 <c>ipc/mcp.ts</c> 的 <c>assertValid</c>(id 白名单、名字非空、stdio 需要命令、远程只接受 http / https);<c>mcp-form.test.ts</c> 的表单解析属于界面层,不在这里。
/// </summary>
public sealed class ConfigTests
{
    private static McpServerConfig Valid(Func<McpServerConfig, McpServerConfig>? edit = null)
    {
        var config = McpRig.Stdio("fix");
        return edit is null ? config : edit(config);
    }

    private static string? Key(McpServerConfig config) => McpConfigRules.Validate(config, out _);

    [Fact]
    public void A_valid_stdio_and_remote_configuration_passes()
    {
        Assert.Null(Key(Valid()));
        Assert.Null(Key(McpRig.Remote("far", new Uri("https://example.com/mcp"), McpTransportKind.StreamableHttp, ["Authorization"])));
        Assert.Null(Key(McpRig.Remote("far2", new Uri("http://127.0.0.1:8080/sse"), McpTransportKind.Sse)));
    }

    [Theory]
    [InlineData("bad id!")]
    [InlineData("../etc/passwd")]
    [InlineData("")]
    [InlineData("a-very-long-server-id-that-is-more-than-32-characters")]
    [InlineData("has__ok_but space")]
    public void Server_ids_outside_the_whitelist_are_rejected_because_they_become_part_of_the_tool_name(string id)
    {
        Assert.Equal(McpErrorKeys.InvalidId, Key(Valid(c => c with { Id = id })));
        Assert.False(McpConfigRules.IsValidId(id));
    }

    [Fact]
    public void A_blank_name_and_a_blank_command_are_rejected()
    {
        Assert.Equal(McpErrorKeys.NameInvalid, Key(Valid(c => c with { Name = "   " })));
        Assert.Equal(McpErrorKeys.CommandRequired, Key(Valid(c => c with { Command = "   " })));
    }

    [Theory]
    [InlineData("file:///etc/passwd", McpErrorKeys.UrlScheme)]
    [InlineData("ftp://example.com/x", McpErrorKeys.UrlScheme)]
    [InlineData("not a url", McpErrorKeys.UrlInvalid)]
    [InlineData("https://user:pass@example.com/mcp", McpErrorKeys.UrlCredentials)]
    public void Remote_addresses_must_be_plain_http_or_https_without_embedded_credentials(string url, string expected)
    {
        var config = McpRig.Remote("far", new Uri("https://example.com/"), McpTransportKind.StreamableHttp) with { Url = url };
        var key = McpConfigRules.Validate(config, out var parameters);
        Assert.Equal(expected, key);
        // 参数里只有 scheme,没有整个 URL(URL 可能带凭据)
        Assert.DoesNotContain("pass", string.Concat(parameters.Values));
    }

    [Fact]
    public void Secret_names_are_checked_per_transport_and_fields_of_the_other_transport_are_refused()
    {
        Assert.Equal(McpErrorKeys.SecretNameInvalid, Key(Valid(c => c with { EnvNames = ["bad name!"] })));
        Assert.Equal(McpErrorKeys.SecretNameInvalid, Key(Valid(c => c with { EnvNames = ["TOKEN", "TOKEN"] })));
        Assert.Equal(McpErrorKeys.TransportFieldMismatch, Key(Valid(c => c with { HeaderNames = ["Authorization"] })));
        var remote = McpRig.Remote("far", new Uri("https://example.com/mcp"));
        Assert.Equal(McpErrorKeys.SecretNameInvalid, Key(remote with { HeaderNames = ["Bad Header"] }));
        Assert.Equal(McpErrorKeys.TransportFieldMismatch, Key(remote with { Command = "x" }));
    }

    [Fact]
    public void The_json_shape_is_2x_compatible_and_unknown_fields_survive_a_round_trip()
    {
        const string json = """
            {"id":"fix","name":"Fixture","enabled":true,"transport":"stdio","command":"npx","args":["-y","pkg"],"envNames":["FIX_TOKEN"],"cwd":"sub","futureField":{"a":[1,2]},"description":"d"}
            """;
        var config = McpConfigJson.Parse(json)!;
        Assert.Equal(["-y", "pkg"], config.Args);
        Assert.Equal(["FIX_TOKEN"], config.EnvNames);
        Assert.Equal("sub", config.Cwd);
        Assert.Contains("futureField", config.Extra.Keys);

        var written = McpConfigJson.Write(config);
        using var doc = JsonDocument.Parse(written);
        Assert.Equal(2, doc.RootElement.GetProperty("futureField").GetProperty("a").GetArrayLength());
        Assert.Equal("stdio", doc.RootElement.GetProperty("transport").GetString());
        Assert.Equal("Fixture", McpConfigJson.Parse(written)!.Name);
    }

    [Fact]
    public void Remote_transports_use_the_2x_names_and_unreadable_rows_are_skipped()
    {
        var sse = McpConfigJson.Parse("""{"id":"a","name":"A","enabled":false,"transport":"sse","url":"https://x/sse","headerNames":["Authorization"],"workspaceId":"w1"}""")!;
        Assert.Equal(McpTransportKind.Sse, sse.Transport);
        Assert.False(sse.Enabled);
        Assert.Equal("w1", sse.WorkspaceId);
        Assert.Equal(McpTransportKind.StreamableHttp, McpConfigJson.Parse("""{"id":"b","name":"B","transport":"streamable-http","url":"https://x"}""")!.Transport);
        Assert.Null(McpConfigJson.Parse("""{"id":"c","name":"C","transport":"carrier-pigeon"}"""));
        Assert.Null(McpConfigJson.Parse("not json"));
        Assert.Null(McpConfigJson.Parse("[]"));
    }

    [Fact]
    public void The_configuration_on_disk_holds_names_only_and_the_secret_map_is_written_whole()
    {
        var configs = new MemoryConfigStore();
        var secrets = new MemorySecretStore();
        var config = Valid(c => c with { EnvNames = ["FIX_TOKEN", "OTHER"] });
        configs.Put(config);

        McpSecrets.Write(secrets, config, new Dictionary<string, string> { ["FIX_TOKEN"] = "tok-SECRET-123456", ["UNDECLARED"] = "x" });

        Assert.DoesNotContain("tok-SECRET", configs.Dump());
        Assert.Equal(["mcp:fix:env"], secrets.References);
        // 只取声明过的键;库里那张 map 里没声明的键根本没写进去
        var read = McpSecrets.Read(secrets, config);
        Assert.Equal(["FIX_TOKEN"], read.Keys);
        Assert.DoesNotContain("UNDECLARED", secrets.Get("mcp:fix:env"));

        // 整张 map 替换,不是累加:不带 FIX_TOKEN 的写入把它删掉
        McpSecrets.Write(secrets, config, new Dictionary<string, string> { ["OTHER"] = "second-value" });
        Assert.Equal(["OTHER"], McpSecrets.Read(secrets, config).Keys);
    }

    [Fact]
    public void Remote_secrets_live_under_the_headers_reference_and_removal_clears_both_kinds()
    {
        var secrets = new MemorySecretStore();
        var remote = McpRig.Remote("far", new Uri("https://example.com/mcp"), headers: ["Authorization"]);
        McpSecrets.Write(secrets, remote, new Dictionary<string, string> { ["Authorization"] = "Bearer abcdef123" });
        secrets.Set("mcp:far:env", "{}");
        Assert.Contains("mcp:far:headers", secrets.References);

        McpSecrets.RemoveAll(secrets, "far");

        Assert.Empty(secrets.References);
    }

    [Fact]
    public void A_corrupt_secret_blob_reads_as_no_secrets_instead_of_throwing()
    {
        var secrets = new MemorySecretStore();
        var config = Valid(c => c with { EnvNames = ["TOKEN"] });
        secrets.Set("mcp:fix:env", "{{{ not json");
        Assert.Empty(McpSecrets.Read(secrets, config));
        secrets.Set("mcp:fix:env", "[1,2]");
        Assert.Empty(McpSecrets.Read(secrets, config));
        secrets.Set("mcp:fix:env", """{"TOKEN":5}""");
        Assert.Empty(McpSecrets.Read(secrets, config));
    }

    [Fact]
    public void The_scrubber_masks_values_and_the_token_part_of_prefixed_values_but_leaves_short_values_alone()
    {
        var text = "401 for Bearer abcdef123456 and also abcdef123456 echoed; pw=ab ok";
        var scrubbed = McpScrubber.Scrub(text, ["Bearer abcdef123456", "ab"]);
        Assert.DoesNotContain("abcdef123456", scrubbed);
        Assert.Contains("pw=ab ok", scrubbed); // 2 个字符的值不抹,否则会把正常文字抹碎
        Assert.Equal("nothing here", McpScrubber.Scrub("nothing here", ["secret-value"]));
    }
}
