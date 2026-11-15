using Ncw.Client.Services;

namespace Ncw.UI.Localization;

/// <summary>检查器的弹窗文字,按当前语言(<see cref="IInspectorText"/> 的 UI 实现)。</summary>
public sealed class NcwInspectorText : IInspectorText
{
    public static readonly NcwInspectorText Instance = new();

    public string NameLabel => NcwTexts.Get(NcwTextKind.NameLabel);

    public string NewFileTitle => NcwTexts.Get(NcwTextKind.DialogNewFile);

    public string NewFolderTitle => NcwTexts.Get(NcwTextKind.DialogNewFolder);

    public string NewItemDescription => NcwTexts.Get(NcwTextKind.DialogNewItemDescription);

    public string RenameTitle => NcwTexts.Get(NcwTextKind.Rename);

    public string RenameDescription => NcwTexts.Get(NcwTextKind.DialogRenameDescription);

    public string DeleteTitle => NcwTexts.Get(NcwTextKind.DialogDeleteTitle);

    public string DeleteMessage(string name) => NcwTexts.Format(NcwTextKind.DialogDeleteMessage, name);

    public string DeleteLabel => NcwTexts.Get(NcwTextKind.FilesMoveToTrash);

    public string DiscardTitle => NcwTexts.Get(NcwTextKind.DialogDiscardTitle);

    public string DiscardMessage => NcwTexts.Get(NcwTextKind.DialogDiscardMessage);

    public string DiscardLabel => NcwTexts.Get(NcwTextKind.FileDiscardChanges);

    public string NewBranchTitle => NcwTexts.Get(NcwTextKind.DialogNewBranchTitle);

    public string NewBranchDescription => NcwTexts.Get(NcwTextKind.DialogNewBranchDescription);

    public string BranchNameLabel => NcwTexts.Get(NcwTextKind.DialogBranchNameLabel);

    public string ErrorTitle => NcwTexts.Get(NcwTextKind.DialogErrorTitle);
}
