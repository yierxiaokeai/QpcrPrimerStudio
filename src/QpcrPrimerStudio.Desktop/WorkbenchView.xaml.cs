using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace QpcrPrimerStudio.Desktop;

public partial class WorkbenchView : UserControl
{
    public WorkbenchView() => InitializeComponent();
    private async void SaveCandidateSelection(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: QpcrPrimerStudio.Core.PrimerCandidate candidate } && DataContext is MainViewModel vm)
            await vm.AutoSaveSelectionAsync(candidate, candidate.Selected);
    }
    private async void SelectForward(object sender, RoutedEventArgs e) => await SelectPrimer(false);
    private async void SelectReverse(object sender, RoutedEventArgs e) => await SelectPrimer(true);
    private async System.Threading.Tasks.Task SelectPrimer(bool reverse)
    {
        if (DataContext is not MainViewModel vm) return;
        var start = TargetSequenceInput.Text.Take(TargetSequenceInput.SelectionStart).Count(c => !char.IsWhiteSpace(c)) + 1;
        var length = TargetSequenceInput.SelectedText.Count(c => !char.IsWhiteSpace(c));
        await vm.SelectTemplatePrimerAsync(reverse, start, length);
    }
    private void CandidateDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not DataGridRow)
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        if (source is DataGridRow && DataContext is MainViewModel vm && vm.EditSelectedCommand.CanExecute(null))
            vm.EditSelectedCommand.Execute(null);
    }
}
