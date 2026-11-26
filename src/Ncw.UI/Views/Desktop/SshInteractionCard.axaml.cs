using Avalonia.Controls;
using Avalonia.Input;
using Ncw.Client.ViewModels;

namespace Ncw.UI.Views.Desktop;

public partial class SshInteractionCard : UserControl
{
    public SshInteractionCard() => InitializeComponent();

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is SshInteractionPromptViewModel vm && vm.SubmitCommand.CanExecute(null))
        {
            vm.SubmitCommand.Execute(null);
            e.Handled = true;
        }
    }
}
