using Avalonia.Controls;
using Avalonia.Threading;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;

namespace Ncw.UI.Views.Desktop.Conversation;

public partial class WorkflowRunCard : UserControl
{
    private bool _dialogOpen;

    public WorkflowRunCard()
    {
        InitializeComponent();
        WorkflowCanvas.NodeSelected += OnNodeSelected;
    }

    private void OnNodeSelected(string? nodeId)
    {
        if (DataContext is not WorkflowRunCardViewModel vm)
        {
            return;
        }

        vm.SelectedNodeId = nodeId;
        if (string.IsNullOrEmpty(nodeId) || _dialogOpen)
        {
            return;
        }

        _ = ShowNodeDialogAsync(vm);
    }

    private async Task ShowNodeDialogAsync(WorkflowRunCardViewModel vm)
    {
        _dialogOpen = true;
        try
        {
            await NcwDialogService.ShowWorkflowNodeDetailsAsync(this, vm);
        }
        finally
        {
            _dialogOpen = false;
            // 关闭弹窗后清除画布选中态,这样再次点击同一个节点也能重新打开详情弹窗
            Dispatcher.UIThread.Post(() =>
            {
                WorkflowCanvas.ClearSelection();
                vm.SelectedNodeId = null;
            }, DispatcherPriority.Background);
        }
    }
}
