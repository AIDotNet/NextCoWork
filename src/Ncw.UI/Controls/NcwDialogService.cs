using Avalonia;
using Avalonia.Controls;
using Ncw.Client.Services;
using Ncw.UI.Localization;
using TKCode.Arc;

namespace Ncw.UI.Controls;

/// <summary><see cref="IDialogService"/> 的 Arc 实现:对话框盖在 <paramref name="owner"/> 所在的窗口上,Esc 或点遮罩取消。</summary>
public sealed class NcwDialogService(Visual owner) : IDialogService
{
    public async Task<string?> PromptTextAsync(string title, string description, string label, string? initial)
    {
        var input = new Input { Label = label, Text = initial ?? string.Empty };
        var dialog = new Dialog
        {
            Title = title,
            Description = description,
            Content = new StackPanel { Spacing = 16, Margin = new Thickness(0, 4, 0, 0), Children = { input } },
            Actions = Dialog.BuildActions(
            [
                new DialogAction(NcwTexts.Get(NcwTextKind.Cancel), false),
                new DialogAction(NcwTexts.Get(NcwTextKind.Save), true, ButtonVariant.Primary),
            ]),
        };

        var result = await dialog.ShowAsync(owner);
        return result is true ? input.Text : null;
    }
}
