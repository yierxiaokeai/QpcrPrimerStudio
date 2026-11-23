using System.IO;
using System.Windows;

namespace QpcrPrimerStudio.Desktop;
public partial class App : Application
{
    public bool IsIntegrationTest { get; init; }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (IsIntegrationTest) return;
        if (e.Args.Length == 2 && e.Args[0] == "--verify-package")
        {
            try
            {
                await PackageVerification.RunAsync(this, e.Args[1]);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(e.Args[1], ex.ToString());
                Shutdown(1);
            }
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QpcrPrimerStudio");
            var warning = DiagnosticLog.TryAppend(folder, args.Exception);
            MessageBox.Show("操作失败：" + args.Exception.Message + "\n" + (warning ?? "详细错误已写入日志。"), "qPCR Primer Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var viewModel = new MainViewModel();
        await viewModel.RecoverLastAutoSaveAsync();
        MainWindow = new MainWindow(viewModel); MainWindow.Show();
    }
}
