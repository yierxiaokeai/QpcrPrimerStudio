using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public partial class MainViewModel
{
    [ObservableProperty] private string backupSummary = "工程备份：保存后可查看";
    private void RefreshBackupSummary(string? path = null)
    {
        path ??= ProjectPath;
        if (path == "尚未保存") { BackupSummary = "工程备份：保存后可查看"; return; }
        var records = ProjectBackups.List(path);
        BackupSummary = $"压缩备份 {records.Count} 份 · {records.Sum(r => r.Bytes) / 1048576d:F2} MB";
    }
    public async Task RestoreBackupAsync(string path)
    {
        await FlushSelectionSavesAsync();
        var project = await ProjectBackups.LoadAsync(path);
        Restore(project); dirty = true;
        BackupSummary = "已恢复备份，请保存为独立工程";
        Status = "备份已恢复到独立工程；原工程保留。请点击保存选择新的工程文件。";
    }
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task ManageBackupsAsync()
    {
        await FlushSelectionSavesAsync();
        if (!CanWork()) return;
        if (ProjectPath == "尚未保存") { Status = "保存工程后可查看和恢复备份。"; return; }
        try
        {
            var dialog = new BackupWindow(ProjectPath) { Owner = Application.Current.MainWindow };
            if (dialog.ShowDialog() == true && dialog.SelectedBackup is { } backup && ConfirmDiscard())
                await Busy(_ => RestoreBackupAsync(backup.Path));
            else RefreshBackupSummary();
        }
        catch (Exception ex) { ReportError(ex); }
    }
}
