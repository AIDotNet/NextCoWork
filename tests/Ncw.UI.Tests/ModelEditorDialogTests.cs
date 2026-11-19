using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Ncw.Client.Actions;
using Ncw.Client.ViewModels;
using Ncw.Protocol.Methods;
using Ncw.UI.Controls;
using Ncw.UI.Views.Dialogs;
using TKCode.Arc;

namespace Ncw.UI.Tests;

/// <summary>
/// 「编辑模型」弹窗(docs/design/07「编辑模型」):四段字段都在、思考方式切换时细节跟着出现、客户端拦下的错误显示在窗口里、保存成功关窗口、Esc 关窗口。
/// 截图在 <c>screenshots/model-editor-*.png</c>。
/// </summary>
public class ModelEditorDialogTests
{
    private sealed class FakeCommands : IModelEditorCommands
    {
        public List<ModelUpdateParams> Updates { get; } = [];

        public Task<ActionResult<ModelAliasDto>> UpdateModelAsync(string deviceId, ModelUpdateParams update, CancellationToken ct = default)
        {
            Updates.Add(update);
            return Task.FromResult(new ActionResult<ModelAliasDto>(ActionOutcome.Ok));
        }

        public Task<ActionResult<ModelAliasDto>> RenameModelAsync(string deviceId, string providerId, string alias, string nextAlias, CancellationToken ct = default) =>
            Task.FromResult(new ActionResult<ModelAliasDto>(ActionOutcome.Ok));
    }

    private static readonly ModelAliasDto Alias = new("deepseek/deepseek-flash[1m]", "local-gw", "deepseek/deepseek-flash", 200_000, 8192, null, null, null, null,
        true, true, [], null, null);

    private static readonly ModelOptionDto Effective = new("local-gw", "127.0.0.1", Alias.Alias, Alias.Alias, Alias.UpstreamModel, 1_000_000, 65_536, true, true, false, true,
        "effort", ["low", "medium", "high"], "anthropic");

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

    private static (Window Window, NcwDialogService Dialogs) Host(bool dark, double height = 900)
    {
        ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
        var root = new Border { Background = Avalonia.Media.Brushes.Transparent };
        var window = new Window { Width = 1180, Height = height, Content = root };
        window.Styles.Add(new Ncw.UI.Themes.NcwStyles());
        window.Show();
        Dispatch();
        return (window, new NcwDialogService(root));
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task All_sections_show_thinking_details_follow_the_mode_and_saving_closes(bool dark)
    {
        var (window, dialogs) = Host(dark);
        try
        {
            var commands = new FakeCommands();
            var form = new ModelEditorViewModel(commands, "local", Alias, Effective);
            var shown = dialogs.ShowModelEditorAsync(form);
            Dispatch();

            var content = window.GetVisualDescendants().OfType<ModelEditorDialog>().Single();
            Assert.True(Named<NcwInput>(content, "AliasInput").IsEffectivelyVisible);
            Assert.True(Named<NcwNumberField>(content, "ContextField").IsEffectivelyVisible);
            Assert.True(Named<NcwSwitch>(content, "VisionSwitch").IsEffectivelyVisible);
            Assert.False(Named<ItemsControl>(content, "EffortList").IsEffectivelyVisible); // 跟随目录:没有细节
            Snapshot(window, $"model-editor-{(dark ? "dark" : "light")}");

            form.ThinkingMode = "effort";
            form.Efforts.Single(e => e.Value == "none").IsSelected = true;
            Dispatch();
            Assert.True(Named<ItemsControl>(content, "EffortList").IsEffectivelyVisible);
            Assert.False(Named<NcwNumberField>(content, "BudgetField").IsEffectivelyVisible);
            content.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "Body").ScrollToEnd();
            Snapshot(window, $"model-editor-effort-{(dark ? "dark" : "light")}");

            form.MaxOutputTokens = 2_000_000;
            await form.SaveCommand.ExecuteAsync(null);
            Dispatch();
            Assert.True(Named<TextBlock>(content, "ErrorText").IsEffectivelyVisible);
            Assert.False(shown.IsCompleted); // 窗口还开着,原因在里面
            Assert.Empty(commands.Updates);

            form.MaxOutputTokens = 65_536;
            await form.SaveCommand.ExecuteAsync(null);
            await shown.WaitAsync(TimeSpan.FromSeconds(5));
            var update = Assert.Single(commands.Updates);
            Assert.Contains("\"none\"", update.ReasoningEffortsJson, StringComparison.Ordinal);
        }
        finally
        {
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Escape_closes_without_saving()
    {
        var (window, dialogs) = Host(dark: true, height: 640);
        try
        {
            var commands = new FakeCommands();
            var shown = dialogs.ShowModelEditorAsync(new ModelEditorViewModel(commands, "local", Alias, Effective));
            Dispatch();
            Snapshot(window, "model-editor-short-window");

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatch();
            await shown.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(commands.Updates);
        }
        finally
        {
            window.Close();
        }
    }
}
