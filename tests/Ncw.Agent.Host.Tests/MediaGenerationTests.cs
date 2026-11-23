using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Tests.Support;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;
using Ncw.Agent.WebTools.Net;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

public sealed class MediaGenerationTests : IAsyncLifetime
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==";
    private readonly FakeUpstream _http = new();
    private readonly ScriptedLlm _llm = new();
    private readonly ArtifactDownloads _downloads = new();
    private readonly ConversationRig _rig;

    public MediaGenerationTests() => _rig = new ConversationRig(_ => new AgentServices
    {
        Models = new FakeModels(_llm), Http = _http, WebHttp = _downloads,
        CatalogDelayer = new NeverDelay(), Delayer = new NoDelay(),
        Loop = new AgentLoopOptions { ResumeDelays = [] },
    });

    public ValueTask InitializeAsync() => _rig.InitializeAsync();
    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    [Theory]
    [InlineData("image", "openai-images", "image/png", """{"prompt":"a tree"}""")]
    [InlineData("video", "openai-videos", "video/mp4", """{"prompt":"a tree in the wind"}""")]
    [InlineData("audio", "openai-audio", "audio/mpeg", """{"text":"Hello"}""")]
    [InlineData("image", "volcengine-images", "image/png", """{"prompt":"a tree"}""")]
    public async Task A_configured_media_tool_returns_a_readable_committed_session_attachment(string kind, string protocol, string mime, string arguments)
    {
        var client = await _rig.ConnectAsync("owner");
        var project = await _rig.AddProjectAsync(client);
        var session = await client.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(project.WorkspaceId, Model: "sonnet"));
        var tool = $"generate_{kind}";
        var list = await client.CallAsync(ConversationMethods.ToolsList, new ToolsListParams());
        Assert.Contains(list.Tools, t => t.Name == tool && t.NeedsNetwork && !t.ReadOnly);

        var before = await RunAsync(client, session.Id, "before", S.Say("not configured"));
        Assert.Empty(before.Of<ToolEndEventDto>());
        Assert.DoesNotContain(_llm.RequestsOf(session.Id)[0].Tools, t => t.ExternalName == tool);

        const string endpoint = "https://media.example.com/v1";
        await client.CallAsync(ProvidersMethods.Upsert, new ProvidersUpsertParams("media", "Media", protocol, endpoint));
        await client.CallAsync(ProvidersMethods.CredentialSet, new CredentialSetParams("media", "test-secret"));
        await client.CallAsync(ProvidersMethods.ModelsSetAliases, new SetAliasesParams("media", ["media-model"]));
        var pick = new ModelPickDto("media-model", "media");
        var patch = kind switch
        {
            "image" => new DeviceSettingsPatchDto(Image: pick),
            "video" => new DeviceSettingsPatchDto(Video: pick),
            _ => new DeviceSettingsPatchDto(Audio: pick),
        };
        await client.CallAsync(SettingsMethods.Update, new SettingsUpdateParams(patch));

        var bytes = kind switch
        {
            "image" => Convert.FromBase64String(Png),
            "video" => new byte[] { 0, 0, 0, 12, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 0, 0, 0, 0 },
            _ => "ID3test"u8.ToArray(),
        };
        var requestPath = kind switch { "image" => "/images/generations", "video" => "/videos", _ => "/audio/speech" };
        if (kind == "video")
        {
            _http.On("/videos/job/content", () => Binary(bytes, mime));
            _http.On("/videos/job", () => Replies.Json("""{"id":"job","status":"completed"}"""));
            _http.On(requestPath, () => Replies.Json("""{"id":"job","status":"completed"}"""));
        }
        else if (kind == "image")
        {
            _http.On(requestPath, () => Replies.Json(protocol == "volcengine-images"
                ? """{"data":[{"url":"https://cdn.example.com/result.png?signature=private"}]}"""
                : $$"""{"data":[{"b64_json":"{{Png}}"}]}"""));
        }
        else
        {
            _http.On(requestPath, () => Binary(bytes, mime));
        }

        var watch = await RunAsync(client, session.Id, "configured", S.Call("media-call", tool, arguments), S.Say("done"));
        var end = watch.Of<ToolEndEventDto>().Single(e => e.CallId == "media-call");
        Assert.False(end.IsError, end.Content);
        using var details = JsonDocument.Parse(end.DetailsJson!);
        var url = details.RootElement.GetProperty("url").GetString()!;
        Assert.StartsWith($"ncw://attachments/sessions/{session.Id}/", url);
        Assert.Contains(url, end.Content);
        if (protocol == "volcengine-images")
        {
            Assert.DoesNotContain("signature=private", end.Content);
            Assert.DoesNotContain("signature=private", end.DetailsJson);
            Assert.Equal("https://cdn.example.com/result.png?signature=private", _downloads.LastUrl);
            Assert.False(_downloads.HadAuthorization);
        }

        if (kind == "image")
        {
            Assert.Equal(url, Assert.Single(end.Images!).DataRef);
        }

        var result = Assert.Single(_llm.RequestsOf(session.Id)[^1].Messages.SelectMany(m => m.Parts).OfType<ToolResultPart>());
        Assert.Contains(url, result.Output.Content);
        var fetched = await client.CallAsync(AttachmentsMethods.Read, new AttachmentsReadParams(url, 0, bytes.Length));
        Assert.Equal(bytes, fetched.Data);
        Assert.Equal(mime, fetched.Mime);
        Assert.Empty((await client.CallAsync(AttachmentsMethods.ListBySession, new AttachmentsListParams(session.Id))).Attachments);
        var posted = Assert.Single(_http.To(requestPath), r => r.Method == "POST");
        Assert.Contains("media-model", posted.Body);
        Assert.Equal("Bearer test-secret", posted.Headers["authorization"]);
    }

    private async Task<RunWatch> RunAsync(Ncw.Protocol.Session.DeviceClient client, string sessionId, string runId, params Script[] scripts)
    {
        _llm.On(sessionId, scripts);
        await client.CallAsync(ConversationMethods.RunsStart,
            new RunsStartParams(runId, sessionId, Guid.NewGuid().ToString("N"), [new InputPartDto(InputPartKinds.Text, "generate")], new SendOptionsDto("sonnet", PermissionMode: "full")),
            idem: $"start:{runId}");
        var watch = await RunWatch.OpenAsync(client, runId);
        await watch.SettledAsync();
        return watch;
    }

    private static HttpResponseMessage Binary(byte[] bytes, string mime) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue(mime) } },
    };

    private sealed class ArtifactDownloads : IWebHttp
    {
        public string? LastUrl { get; private set; }
        public bool HadAuthorization { get; private set; }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri?.AbsoluteUri;
            HadAuthorization = request.Headers.Authorization is not null;
            return Task.FromResult(Binary(Convert.FromBase64String(Png), "image/png"));
        }
    }
}
