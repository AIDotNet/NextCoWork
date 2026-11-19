using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Ncw.Client.Actions;
using Ncw.Client.ViewModels;
using Ncw.Protocol.Methods;
using Ncw.UI.Controls;
using Ncw.UI.Views.Dialogs;
using TKCode.Arc;

namespace Ncw.UI.Tests;

/// <summary>
/// 「添加 / 编辑供应商」弹窗与供应商 logo(docs/design/07 §3「添加供应商」):内置 logo 都能解析、弹窗宽 640(窄窗口收到「窗口宽 − 32」、网格变两列)、
/// 两步切换、测试失败时原因留在窗口里、Esc 关窗口。截图在 <c>screenshots/provider-editor-*.png</c>。
/// </summary>
public class ProviderEditorDialogTests
{
    private sealed class FakeCommands : IProviderEditorCommands
    {
        public Func<string, ProviderTestResultDto> OnTest { get; init; } = _ => new ProviderTestResultDto(true, 90, 200, null, null);

        public Task<ActionResult<ProviderDto>> UpsertAsync(string deviceId, ProvidersUpsertParams provider, CancellationToken ct = default) =>
            Task.FromResult(new ActionResult<ProviderDto>(ActionOutcome.Ok));

        public Task<ActionResult<CredentialInfoDto>> SetApiKeyAsync(string deviceId, string providerId, string apiKey, CancellationToken ct = default) =>
            Task.FromResult(new ActionResult<CredentialInfoDto>(ActionOutcome.Ok, new CredentialInfoDto(true, apiKey[^4..], null, null, null)));

        public Task<ActionResult<ProviderTestResultDto>> TestAsync(string deviceId, string providerId, CancellationToken ct = default) =>
            Task.FromResult(new ActionResult<ProviderTestResultDto>(ActionOutcome.Ok, OnTest(providerId)));
    }

    /// <summary>截图用的模版:和设备内置表同样的形状(常用、国际、国内、聚合、本机,带一个 Anthropic 兼容端点和一个没有 logo 的)。</summary>
    private static readonly ProviderTemplateDto[] Templates =
    [
        T("anthropic", "Anthropic", ProviderTemplateGroups.International, "anthropic", "https://api.anthropic.com", featured: true),
        T("openai", "OpenAI", ProviderTemplateGroups.International, "openai-responses", "https://api.openai.com/v1", featured: true),
        T("deepseek", "DeepSeek", ProviderTemplateGroups.China, "openai-chat", "https://api.deepseek.com", featured: true),
        T("moonshot", "Kimi", ProviderTemplateGroups.China, "openai-chat", "https://api.moonshot.cn/v1", featured: true),
        T("openrouter", "OpenRouter", ProviderTemplateGroups.Aggregator, "openai-chat", "https://openrouter.ai/api/v1", featured: true),
        T("ollama", "Ollama", ProviderTemplateGroups.Local, "openai-chat", "http://localhost:11434/v1", featured: true, needsKey: false),
        T("gemini", "Google Gemini", ProviderTemplateGroups.International, "openai-chat", "https://generativelanguage.googleapis.com/v1beta/openai"),
        T("xai", "xAI", ProviderTemplateGroups.International, "openai-chat", "https://api.x.ai/v1"),
        T("mistral", "Mistral", ProviderTemplateGroups.International, "openai-chat", "https://api.mistral.ai/v1"),
        T("deepseek-anthropic", "DeepSeek", ProviderTemplateGroups.China, "anthropic", "https://api.deepseek.com/anthropic", service: "deepseek", variant: ProviderTemplateVariants.Anthropic),
        T("moonshot-intl", "Kimi", ProviderTemplateGroups.China, "openai-chat", "https://api.moonshot.ai/v1", service: "moonshot", variant: ProviderTemplateVariants.International),
        T("zhipu", "智谱 GLM", ProviderTemplateGroups.China, "openai-chat", "https://open.bigmodel.cn/api/paas/v4"),
        T("dashscope", "通义千问", ProviderTemplateGroups.China, "openai-chat", "https://dashscope.aliyuncs.com/compatible-mode/v1"),
        T("baichuan", "百川", ProviderTemplateGroups.China, "openai-chat", "https://api.baichuan-ai.com/v1"),
        T("siliconflow", "硅基流动", ProviderTemplateGroups.Aggregator, "openai-chat", "https://api.siliconflow.cn/v1"),
        T("lmstudio", "LM Studio", ProviderTemplateGroups.Local, "openai-chat", "http://localhost:1234/v1", needsKey: false),
    ];

    private static ProviderTemplateDto T(string id, string name, string group, string protocol, string url, bool featured = false, bool needsKey = true,
        string? service = null, string? variant = null) =>
        new(id, service ?? id, name, group, variant, protocol, url, needsKey ? $"https://{id}.example/keys" : null, needsKey, featured);

    private static void Dispatch()
    {
        for (var i = 0; i < 6; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Snapshot(Window window, string name)
    {
        Dispatch();
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"), new PngBitmapEncoderOptions());
    }

    private static (Window Window, NcwDialogService Dialogs) Host(bool dark, double width = 1180, double height = 820)
    {
        ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
        var root = new Border { Background = Avalonia.Media.Brushes.Transparent };
        var window = new Window { Width = width, Height = height, Content = root };
        window.Styles.Add(new Ncw.UI.Themes.NcwStyles());
        window.Show();
        Dispatch();
        return (window, new NcwDialogService(root));
    }

    private static ProviderEditorViewModel Form(FakeCommands commands, int discoverable = 3) =>
        new(commands, "dev", Templates, [], discoverable);

    [AvaloniaFact]
    public void Every_bundled_logo_parses_into_something_drawable()
    {
        var logos = AssetLoader.GetAssets(new Uri("avares://Ncw.UI/Assets/ProviderLogos/"), null)
            .Select(u => Path.GetFileNameWithoutExtension(u.AbsolutePath))
            .Where(n => !n.StartsWith("LICENSE", StringComparison.Ordinal))
            .ToList();

        Assert.True(logos.Count >= 20, string.Join(", ", logos));
        Assert.All(logos, id =>
        {
            var logo = NcwProviderLogo.Load(id);
            Assert.True(logo is not null, id);
            Assert.All(logo!.Shapes, s => Assert.True(s.Geometry.Bounds.Width > 0 || s.StrokeWidth is not null, id));
        });
        Assert.False(NcwProviderLogo.HasLogo("baichuan")); // 没有 logo 的退回首字母
        Assert.False(NcwProviderLogo.HasLogo("../secrets")); // 名字只认 id 字符
    }

    [Theory]
    [InlineData("OpenAI", "Op")]
    [InlineData("xAI", "Xa")]
    [InlineData("01.AI", "01")]
    [InlineData("百川", "百")]
    [InlineData("  ", "")]
    public void Initials_fall_back_to_the_first_letters(string name, string expected) => Assert.Equal(expected, NcwProviderLogo.Initials(name));

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_dialog_is_640_wide_shows_the_template_grid_then_the_form_and_keeps_a_failed_test_inside(bool dark)
    {
        var (window, dialogs) = Host(dark);
        try
        {
            var commands = new FakeCommands { OnTest = _ => new ProviderTestResultDto(false, null, 401, "providers.fetchRejected", null) };
            var form = Form(commands);
            var shown = dialogs.ShowProviderEditorAsync(form);
            Dispatch();

            var content = window.GetVisualDescendants().OfType<ProviderEditorDialog>().Single();
            var surface = content.GetVisualAncestors().OfType<DialogSurface>().First();
            Assert.Equal(NcwDialogService.ProviderEditorWidth, surface.Bounds.Width, 0.5);
            Assert.Equal(3, content.Tag);
            Assert.True(content.GetVisualDescendants().OfType<NcwButton>().Single(b => b.Name == "DiscoveryButton").IsEffectivelyVisible);
            Assert.Contains(content.GetVisualDescendants().OfType<NcwProviderLogo>(), l => l.Service == "deepseek" && l.IsEffectivelyVisible);
            Snapshot(window, $"provider-editor-pick-{(dark ? "dark" : "light")}");

            form.Query = "deep";
            Dispatch();
            Snapshot(window, $"provider-editor-search-{(dark ? "dark" : "light")}");

            form.PickCommand.Execute(form.Groups[0].Items[0]); // 搜「deep」的第一条:DeepSeek
            form.ApiKey = "sk-wrong-0000";
            Dispatch();
            Assert.True(content.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "KeyInput").IsEffectivelyVisible);
            Assert.Equal(NcwDialogService.ProviderEditorWidth, surface.Bounds.Width, 0.5); // 换步骤不改宽度
            Snapshot(window, $"provider-editor-form-{(dark ? "dark" : "light")}");

            await form.SaveCommand.ExecuteAsync(null);
            Dispatch();
            Assert.True(form.TestFailed);
            Assert.False(shown.IsCompleted); // 窗口还开着,原因在里面
            Assert.Contains(Ncw.UI.Converters.ProviderErrorTextConverter.Text("providers.fetchRejected"),
                window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "TestFailedText").Text ?? "", StringComparison.Ordinal);
            Snapshot(window, $"provider-editor-test-failed-{(dark ? "dark" : "light")}");

            form.KeepCommand.Execute(null);
            await shown.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("deepseek", form.SavedProviderId);
        }
        finally
        {
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_narrow_window_shrinks_the_dialog_and_the_grid_drops_to_two_columns()
    {
        var (window, dialogs) = Host(dark: false, width: 600, height: 700);
        try
        {
            var shown = dialogs.ShowProviderEditorAsync(Form(new FakeCommands(), discoverable: 0));
            Dispatch();

            var content = window.GetVisualDescendants().OfType<ProviderEditorDialog>().Single();
            var surface = content.GetVisualAncestors().OfType<DialogSurface>().First();
            Assert.True(surface.Bounds.Width <= 600 - 32 + 0.5, surface.Bounds.Width.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(2, content.Tag);
            Snapshot(window, "provider-editor-narrow");

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatch();
            await shown.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task The_import_banner_closes_the_dialog_and_asks_for_discovery()
    {
        var (window, dialogs) = Host(dark: true);
        try
        {
            var form = Form(new FakeCommands());
            var asked = 0;
            form.DiscoveryRequested += () => asked++;
            var shown = dialogs.ShowProviderEditorAsync(form);
            Dispatch();

            form.GoToDiscoveryCommand.Execute(null);
            await shown.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, asked);
            Assert.Null(form.SavedProviderId);
        }
        finally
        {
            window.Close();
        }
    }
}
