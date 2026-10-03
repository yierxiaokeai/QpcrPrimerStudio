using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public sealed class BackupWindow : Window
{
    private readonly ListBox list = new() { DisplayMemberPath = nameof(ProjectBackupInfo.Label), Margin = new(0, 12, 0, 12) };
    public ProjectBackupInfo? SelectedBackup => list.SelectedItem as ProjectBackupInfo;
    public BackupWindow(string projectPath)
    {
        NameScope.SetNameScope(this, new NameScope());
        Title = "工程备份"; Width = 640; Height = 480; MinWidth = 420; MinHeight = 300; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var records = ProjectBackups.List(projectPath); list.ItemsSource = records;
        var panel = new DockPanel { Margin = new(20) }; Content = panel;
        var heading = new TextBlock { TextWrapping = TextWrapping.Wrap,
            Text = $"压缩备份共 {records.Count} 份，占用 {records.Sum(r => r.Bytes) / 1048576d:F2} MB。近期保留最多 10 份、50 MB，较早记录按月归档。所有历史保留，可从备份位置转移归档文件以释放本机空间。恢复会创建独立工程。" };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var location = new Button { Content = "打开备份位置", Margin = new(4) };
        location.Click += (_, _) => { var folder = ProjectBackups.Folder(projectPath); Directory.CreateDirectory(folder); Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = true }); };
        var restore = new Button { Content = "恢复所选", Margin = new(4), IsEnabled = false };
        list.SelectionChanged += (_, _) => restore.IsEnabled = SelectedBackup is not null;
        restore.Click += (_, _) => DialogResult = true;
        var close = new Button { Name = "CloseDialogButton", Content = "关闭", Margin = new(4), IsCancel = true };
        RegisterName(close.Name, close);
        close.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(location); buttons.Children.Add(restore); buttons.Children.Add(close); panel.Children.Add(list);
    }
}
