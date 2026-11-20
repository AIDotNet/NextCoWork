using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Ncw.Client.Services;
using Ncw.Client.ViewModels;
using Ncw.UI.Localization;
using Ncw.UI.Views.Dialogs;
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

    public async Task<string?> PickFolderAsync(string title)
    {
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { CanPickFolder: true } storage)
        {
            return null;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title.Length > 0 ? title : NcwTexts.Get(NcwTextKind.FilePickBackupFolder), AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<PickedFile?> PickFileAsync(FilePickKind kind)
    {
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }

        var (title, filter) = kind switch
        {
            FilePickKind.SkillZip => (NcwTextKind.FilePickSkillZip, new FilePickerFileType("ZIP") { Patterns = ["*.zip"] }),
            FilePickKind.PluginZip => (NcwTextKind.PluginFilePickTitle, new FilePickerFileType("ZIP") { Patterns = ["*.zip"] }),
            FilePickKind.ImportData => (NcwTextKind.FilePickImport, new FilePickerFileType("JSON") { Patterns = ["*.json"] }),
            _ => (NcwTextKind.FilePickBackup, null),
        };
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = NcwTexts.Get(title),
            AllowMultiple = false,
            FileTypeFilter = filter is null ? null : [filter],
        });
        if (files.Count == 0)
        {
            return null;
        }

        var file = files[0];
        var properties = await file.GetBasicPropertiesAsync();
        return new PickedFile(file.Name, file.TryGetLocalPath(), (long)(properties.Size ?? 0), async () => await file.OpenReadAsync());
    }

    public async Task<Stream?> PickSaveStreamAsync(FilePickKind kind, string suggestedName)
    {
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { CanSave: true } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = NcwTexts.Get(NcwTextKind.FilePickExport),
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        return file is null ? null : await file.OpenWriteAsync();
    }

    /// <summary>「新建项目」:Arc Dialog 包住 <see cref="NewProjectDialog"/>;「添加」走 ViewModel 的命令,成功后才关,失败时原因留在窗口里。</summary>
    public async Task<bool> ShowNewProjectAsync(NewProjectViewModel form)
    {
        var content = new NewProjectDialog { DataContext = form };
        var submit = new NcwButton { Variant = ButtonVariant.Primary, Content = NcwTexts.Get(NcwTextKind.NewProjectCreate), Command = form.SubmitCommand };
        var dialog = new Dialog
        {
            Title = NcwTexts.Get(NcwTextKind.NewProjectTitle),
            Description = NcwTexts.Get(NcwTextKind.NewProjectDescription),
            Content = content,
            Actions = ActionRow(CancelButton(), submit),
        };
        void Close() => dialog.Close(true);
        form.Completed += Close;
        dialog.Opened += (_, _) => Dispatcher.UIThread.Post(content.FocusPath, DispatcherPriority.Input);
        try
        {
            return await dialog.ShowAsync(owner) is true && form.Created is not null;
        }
        finally
        {
            form.Completed -= Close;
        }
    }

    /// <summary>「项目图标」:左边「恢复默认」,右边「取消 / 保存」;保存和恢复默认都由 ViewModel 落地后关窗口。</summary>
    public async Task<bool> ShowProjectIconAsync(ProjectIconViewModel form)
    {
        var reset = new NcwButton { Variant = ButtonVariant.Ghost, Content = NcwTexts.Get(NcwTextKind.ProjectIconReset), Command = form.ResetCommand };
        var save = new NcwButton { Variant = ButtonVariant.Primary, Content = NcwTexts.Get(NcwTextKind.Save), Command = form.SaveCommand };
        var actions = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(reset, Dock.Left);
        var right = ActionRow(CancelButton(), save);
        DockPanel.SetDock(right, Dock.Right);
        actions.Children.Add(reset);
        actions.Children.Add(right);
        var dialog = new Dialog
        {
            Title = NcwTexts.Get(NcwTextKind.ProjectIconTitle),
            Description = NcwTexts.Format(NcwTextKind.ProjectIconDescription, form.ProjectName),
            Content = new ProjectIconDialog { DataContext = form },
            Actions = actions,
        };
        var completed = false;
        void Close()
        {
            completed = true;
            dialog.Close(true);
        }

        form.Completed += Close;
        try
        {
            await dialog.ShowAsync(owner);
            return completed;
        }
        finally
        {
            form.Completed -= Close;
        }
    }

    /// <summary>
    /// 「添加 / 编辑供应商」(docs/design/07 §3「添加供应商」):Arc Dialog 包住 <see cref="ProviderEditorDialog"/>,宽 640(Arc 只有 360 / 440 / 560 三档,
    /// 这里在这个弹窗上覆盖 Large 档的宽度 token;窗口窄时 Arc 自己收到「窗口宽 − 32」)。保存成功、「保留并关闭」、「去导入」都由 ViewModel 的事件关窗口。
    /// 表单里有改动时点遮罩不关(防误触),Esc 和「取消」照常关。
    /// </summary>
    public async Task ShowProviderEditorAsync(ProviderEditorViewModel form)
    {
        var content = new ProviderEditorDialog { DataContext = form, Launcher = new NcwUrlLauncher(owner) };
        var footer = new ProviderEditorFooter { DataContext = form };
        var dialog = new Dialog
        {
            Title = NcwTexts.Get(form.IsNew ? NcwTextKind.AddProvider : NcwTextKind.ProviderEditTitle),
            Content = content,
            Actions = footer,
            Size = ControlSize.Large,
        };
        dialog.Resources[TKCode.Arc.DesignTokens.DialogSurfaceTokenKind.PanelWidthLG] = ProviderEditorWidth;
        UpdateDescription();
        void UpdateDescription() => dialog.Description = NcwTexts.Get(form switch
        {
            { IsNew: false } => NcwTextKind.ProviderEditorEditDescription,
            { IsPickStep: true } => NcwTextKind.ProviderEditorPickDescription,
            { IsCustom: true } => NcwTextKind.ProviderEditorCustomDescription,
            _ => NcwTextKind.ProviderEditorFormDescription,
        });
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ProviderEditorViewModel.Step) or nameof(ProviderEditorViewModel.Template))
            {
                UpdateDescription();
            }
            else if (e.PropertyName is nameof(ProviderEditorViewModel.IsDirty))
            {
                dialog.CloseOnOutsidePress = !form.IsDirty; // 填了东西就不让点遮罩误关;Esc、取消照常
            }
        }

        void Close() => dialog.Close(true);
        footer.Cancel.Click += (_, _) => dialog.Close(false);
        form.PropertyChanged += OnChanged;
        form.Completed += Close;
        form.DiscoveryRequested += Close;
        dialog.Opened += (_, _) => Dispatcher.UIThread.Post(content.FocusStep, DispatcherPriority.Input);
        try
        {
            await dialog.ShowAsync(owner);
        }
        finally
        {
            form.PropertyChanged -= OnChanged;
            form.Completed -= Close;
            form.DiscoveryRequested -= Close;
        }
    }

    /// <summary>
    /// 「编辑模型」(docs/design/07「编辑模型」):Arc Dialog(Large 档)包住 <see cref="ModelEditorDialog"/>,按钮行「取消 / 保存」。
    /// 保存成功由 ViewModel 的 <see cref="ModelEditorViewModel.Completed"/> 关窗口;失败时窗口不关,原因在里面。改过东西后点遮罩不关(防误触),Esc 和「取消」照常关。
    /// </summary>
    public async Task ShowModelEditorAsync(ModelEditorViewModel form)
    {
        var content = new ModelEditorDialog { DataContext = form };
        var save = new NcwButton { Variant = ButtonVariant.Primary, Content = NcwTexts.Get(NcwTextKind.Save), Command = form.SaveCommand };
        var dialog = new Dialog
        {
            Title = NcwTexts.Get(NcwTextKind.ModelEditorTitle),
            Description = NcwTexts.Get(NcwTextKind.ModelEditorDescription),
            Content = content,
            Actions = ActionRow(CancelButton(), save),
            Size = ControlSize.Large,
        };
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ModelEditorViewModel.IsDirty))
            {
                dialog.CloseOnOutsidePress = !form.IsDirty;
            }
        }

        void Close() => dialog.Close(true);
        form.PropertyChanged += OnChanged;
        form.Completed += Close;
        dialog.Opened += (_, _) => Dispatcher.UIThread.Post(content.FocusFirst, DispatcherPriority.Input);
        try
        {
            await dialog.ShowAsync(owner);
        }
        finally
        {
            form.PropertyChanged -= OnChanged;
            form.Completed -= Close;
        }
    }

    /// <summary>「添加供应商」弹窗的宽度:三列模版卡片、表单两列字段都放得下(docs/design/07 §3)。</summary>
    internal const double ProviderEditorWidth = 640;

    /// <summary>「供应商调试」弹窗的宽度:左栏 320 的请求配置 + 右栏的结果(docs/design/07「供应商调试」)。</summary>
    internal const double ProviderDebugWidth = 900;

    /// <summary>
    /// 「供应商调试」(docs/design/07「供应商调试」):Arc Dialog 包住 <see cref="ProviderDebugDialog"/>,宽 900(覆盖 Large 档的宽度 token;窗口窄时 Arc 收到「窗口宽 − 32」)。
    /// 打开时拉一次模型候选;按钮行是「使用这个协议」(只在从编辑弹窗打开、刚跑通了别的协议时出现)和「关闭」。关窗口时取消还在跑的请求。
    /// 可以叠在「添加 / 编辑供应商」弹窗上面打开。
    /// </summary>
    public async Task ShowProviderDebugAsync(ProviderDebugViewModel form)
    {
        var content = new ProviderDebugDialog { DataContext = form };
        var use = new NcwButton { Variant = ButtonVariant.Primary, Content = NcwTexts.Get(NcwTextKind.ProviderDebugUseProtocol), Command = form.UseProtocolCommand, IsVisible = form.CanUseProtocol };
        var close = new NcwButton { Variant = ButtonVariant.Secondary, Content = NcwTexts.Get(NcwTextKind.Close) };
        var dialog = new Dialog
        {
            Title = NcwTexts.Format(NcwTextKind.ProviderDebugTitle, form.ProviderName),
            Description = NcwTexts.Get(NcwTextKind.ProviderDebugDescription),
            Content = content,
            Actions = ActionRow(use, close),
            Size = ControlSize.Large,
        };
        dialog.Resources[TKCode.Arc.DesignTokens.DialogSurfaceTokenKind.PanelWidthLG] = ProviderDebugWidth;
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ProviderDebugViewModel.CanUseProtocol))
            {
                use.IsVisible = form.CanUseProtocol;
            }
        }

        void Close() => dialog.Close(true);
        close.Click += (_, _) => dialog.Close(false);
        form.PropertyChanged += OnChanged;
        form.Completed += Close;
        dialog.Opened += (_, _) =>
        {
            Dispatcher.UIThread.Post(content.FocusFirst, DispatcherPriority.Input);
            _ = form.LoadModelsAsync();
        };
        try
        {
            await dialog.ShowAsync(owner);
        }
        finally
        {
            form.PropertyChanged -= OnChanged;
            form.Completed -= Close;
            form.Close();
        }
    }

    /// <summary>选一张图片并压缩成项目图标(<see cref="NcwIconImages"/>);取消返回 null。</summary>
    public async Task<PickedIcon?> PickIconImageAsync()
    {
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = NcwTexts.Get(NcwTextKind.FilePickIconImage),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(NcwTexts.Get(NcwTextKind.FilePickIconImageType))
            {
                Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp"],
                MimeTypes = ["image/png", "image/jpeg", "image/webp", "image/gif", "image/bmp"],
                AppleUniformTypeIdentifiers = ["public.png", "public.jpeg", "org.webmproject.webp", "com.compuserve.gif", "com.microsoft.bmp"],
            }],
        });
        if (files.Count == 0)
        {
            return null;
        }

        var file = files[0];
        var properties = await file.GetBasicPropertiesAsync();
        return await NcwIconImages.CompressAsync(async () => await file.OpenReadAsync(), (long)(properties.Size ?? 0));
    }

    private static NcwButton CancelButton()
    {
        var cancel = new NcwButton { Variant = ButtonVariant.Secondary, Content = NcwTexts.Get(NcwTextKind.Cancel) };
        Dialog.SetResult(cancel, false);
        return cancel;
    }

    private static StackPanel ActionRow(params Control[] buttons)
    {
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        foreach (var button in buttons)
        {
            row.Children.Add(button);
        }

        return row;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel) =>
        Dialog.ConfirmAsync(owner, title, message, confirmLabel, NcwTexts.Get(NcwTextKind.Cancel), true);

    public async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new Dialog
        {
            Title = title,
            Description = message,
            Actions = Dialog.BuildActions([new DialogAction(NcwTexts.Get(NcwTextKind.Ok), true, ButtonVariant.Primary)]),
        };
        await dialog.ShowAsync(owner);
    }

    /// <summary>工作流节点点击后的详细消息弹窗:展示状态、耗时、错误、输入/提示词、完整执行输出及重试/复制操作。</summary>
    public static async Task ShowWorkflowNodeDetailsAsync(Visual owner, WorkflowRunCardViewModel vm)
    {
        var node = vm.SelectedNodeDef;
        if (node is null)
        {
            return;
        }

        var content = new WorkflowNodeDetailDialog { DataContext = vm };
        var buttons = new List<Control>();

        if (!string.IsNullOrWhiteSpace(vm.SelectedNodeOutputText ?? vm.SelectedNodeRun?.Error))
        {
            var copyText = vm.SelectedNodeOutputText ?? vm.SelectedNodeRun?.Error ?? "";
            var copy = new NcwButton { Variant = ButtonVariant.Ghost, Content = NcwTexts.Get(NcwTextKind.WorkflowNodeCopyOutput) };
            copy.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(owner)?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(copyText);
                }
            };
            buttons.Add(copy);
        }

        Dialog? dialog = null;
        if (vm.CanRetrySelectedNode)
        {
            var retry = new NcwButton { Variant = ButtonVariant.Secondary, Content = NcwTexts.Get(NcwTextKind.WorkflowRetryNode) };
            retry.Click += (_, _) =>
            {
                vm.RetrySelectedNodeCommand.Execute(null);
                dialog?.Close(true);
            };
            buttons.Add(retry);
        }

        var close = new NcwButton { Variant = ButtonVariant.Primary, Content = NcwTexts.Get(NcwTextKind.WorkflowNodeClose) };
        Dialog.SetResult(close, false);
        buttons.Add(close);

        dialog = new Dialog
        {
            Title = node.Title,
            Description = vm.Title,
            Content = content,
            Actions = ActionRow([.. buttons]),
            Size = ControlSize.Large,
        };
        dialog.Resources[TKCode.Arc.DesignTokens.DialogSurfaceTokenKind.PanelWidthLG] = 680.0;

        await dialog.ShowAsync(owner);
    }
}

/// <summary><see cref="IUrlLauncher"/> 的 Avalonia 实现:用系统默认浏览器打开(OAuth 授权页)。只放行 http / https。</summary>
public sealed class NcwUrlLauncher(Visual owner) : IUrlLauncher
{
    public async Task OpenAsync(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            && TopLevel.GetTopLevel(owner) is { } top)
        {
            await top.Launcher.LaunchUriAsync(uri);
        }
    }
}
