using System.Windows;

namespace QpcrPrimerStudio.Desktop;
public partial class MainWindow : Window
{
    internal NativeWindowPresentation Presentation { get; }
    public MainWindow() : this(new MainViewModel()) { }
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        Presentation = new NativeWindowPresentation(this);
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.F1) { OpenHelp(this, new RoutedEventArgs()); e.Handled = true; } };
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width * 0.8);
        MinHeight = Math.Min(MinHeight, workArea.Height * 0.8);
        Width = Math.Max(MinWidth, Math.Min(1240, workArea.Width * 0.86));
        Height = Math.Max(MinHeight, Math.Min(820, workArea.Height * 0.84));
        DataContext = viewModel;
        System.ComponentModel.PropertyChangedEventHandler modeChanged = (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.AdvancedMode) && !viewModel.AdvancedMode && WorkspaceTabs.SelectedIndex == 1)
                WorkspaceTabs.SelectedIndex = 0;
        };
        viewModel.PropertyChanged += modeChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= modeChanged;
        var closingAfterSave = false;
        var waitingForSave = false;
        Closing += async (_, e) =>
        {
            if (closingAfterSave || DataContext is not MainViewModel vm) return;
            if (!vm.PendingSelectionSave.IsCompleted)
            {
                e.Cancel = true;
                if (waitingForSave) return;
                waitingForSave = true;
                await vm.FlushSelectionSavesAsync();
                waitingForSave = false;
                if (vm.RequestClose()) { closingAfterSave = true; Close(); }
            }
            else if (!vm.RequestClose()) e.Cancel = true;
        };
    }
    private void MinimizeWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeRestoreWindow(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            var bounds = RestoreBounds;
            WindowState = WindowState.Normal;
            if (!bounds.IsEmpty && bounds.Width > 0)
            {
                Width = bounds.Width; Height = bounds.Height; Left = bounds.Left; Top = bounds.Top;
            }
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }
    private void CloseWindow(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
    private void OpenHelp(object sender, RoutedEventArgs e)
    {
        var existing = OwnedWindows.OfType<HelpWindow>().FirstOrDefault();
        if (existing is not null) { existing.Activate(); return; }
        new HelpWindow { Owner = this }.Show();
    }
}
