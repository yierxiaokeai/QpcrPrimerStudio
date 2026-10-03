using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public partial class MainViewModel
{
    private readonly SemaphoreSlim projectWriteGate = new(1, 1);
    private long autoSaveSession;
    private long selectionSaveVersion;
    private string? selectionDraftPath;
    private Task pendingSelectionSave = Task.CompletedTask;
    private Dictionary<string, string> projectVersions = new(StringComparer.OrdinalIgnoreCase);
    private string? recoveryPointerWarning;
    [ObservableProperty] private string autoSaveStatus = "勾选引物后自动保存";
    public Task PendingSelectionSave => pendingSelectionSave;
    public bool HasUnsavedChanges => dirty;
    private string RecoveryPointerPath => Path.Combine(storageRoot, "last-auto-save.json");
    private sealed record RecoveryPointer(string ProjectPath);

    private void ResetSelectionAutoSave()
    {
        autoSaveSession++;
        projectVersions = new(StringComparer.OrdinalIgnoreCase);
        selectionDraftPath = null;
        ProjectPath = "尚未保存";
        AutoSaveStatus = "勾选引物后自动保存";
        recoveryPointerWarning = null;
    }
    private void InitializeProjectVersion(ProjectDocument project)
    {
        if (project.LoadedFilePath is { } path && project.LoadedFileSha256 is { } hash)
            projectVersions[Path.GetFullPath(path)] = hash;
    }
    partial void OnProjectPathChanged(string value)
    {
        if (value == "尚未保存") return;
        var path = Path.GetFullPath(value);
        if (!projectVersions.ContainsKey(path)) projectVersions[path] = File.Exists(path) ? Hashing.File(path) : "missing";
        RefreshBackupSummary();
    }

    public Task<bool> AutoSaveSelectionAsync(PrimerCandidate candidate, bool selected)
    {
        if (IsBusy)
        {
            Status = "任务正在运行，请等待结束后勾选引物。";
            return Task.FromResult(false);
        }
        if (!Runs.Any(run => run.Candidates.Contains(candidate)))
        {
            Status = "该候选已不属于当前项目，请重新选择。";
            return Task.FromResult(false);
        }
        candidate.Selected = selected;
        dirty = true;
        var version = ++selectionSaveVersion;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(Snapshot(), ProjectStore.JsonOptions);
            var detached = JsonSerializer.Deserialize<ProjectDocument>(bytes, ProjectStore.JsonOptions)!;
            var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
            var path = ProjectPath == "尚未保存"
                ? selectionDraftPath ??= Path.Combine(storageRoot, "drafts", $"qPCR-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.qpcrproject")
                : Path.GetFullPath(ProjectPath);
            AutoSaveStatus = "正在自动保存勾选…";
            var task = SaveSelectionCoreAsync(path, detached, fingerprint, autoSaveSession, version);
            pendingSelectionSave = task;
            return task;
        }
        catch (Exception ex)
        {
            AutoSaveStatus = "自动保存失败，勾选仍保留在界面";
            ReportError(ex);
            return Task.FromResult(false);
        }
    }

    private async Task<bool> SaveSelectionCoreAsync(string path, ProjectDocument snapshot, string fingerprint, long session, long version)
    {
        try
        {
            await WriteProjectSnapshotAsync(path, snapshot, session, CancellationToken.None);
            if (session == autoSaveSession && version == selectionSaveVersion)
            {
                ProjectPath = path;
                dirty = !SnapshotMatches(fingerprint);
                AutoSaveStatus = recoveryPointerWarning is null ? "勾选已自动保存" : "勾选已保存；自动恢复入口更新失败";
                Status = "勾选已自动保存：" + path + (recoveryPointerWarning is null ? "" : "\n" + recoveryPointerWarning);
            }
            return true;
        }
        catch (Exception ex)
        {
            if (session == autoSaveSession && version == selectionSaveVersion)
            {
                dirty = true;
                AutoSaveStatus = ex is ProjectConflictException ? "保存冲突，已保留本次修改的独立副本" : "自动保存失败，勾选仍保留在界面";
                ReportError(ex);
            }
            return false;
        }
    }

    private bool SnapshotMatches(string fingerprint)
    {
        try
        {
            var current = JsonSerializer.SerializeToUtf8Bytes(Snapshot(), ProjectStore.JsonOptions);
            return Convert.ToHexString(SHA256.HashData(current)) == fingerprint;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            return false; // A newer, incomplete parameter edit still needs saving.
        }
    }

    private async Task WriteProjectSnapshotAsync(string path, ProjectDocument snapshot, long session, CancellationToken token)
    {
        var versions = projectVersions;
        var absolute = Path.GetFullPath(path);
        if (!versions.ContainsKey(absolute)) versions[absolute] = File.Exists(absolute) ? Hashing.File(absolute) : "missing";
        await projectWriteGate.WaitAsync(token);
        try
        {
            await ProjectStore.SaveAsync(path, snapshot, token, versions[absolute]);
            versions[absolute] = snapshot.LoadedFileSha256!;
            if (session == autoSaveSession)
            {
                RefreshBackupSummary(path);
                recoveryPointerWarning = null;
                try { await ProjectStore.WriteRecoveryPointerAsync(RecoveryPointerPath, path, token); }
                catch (Exception ex)
                {
                    recoveryPointerWarning = "工程文件已保存；自动恢复入口更新失败，下次启动请手动打开此工程：" + path + "\n" + ex.Message;
                    LogText += "\n" + ex;
                }
            }
        }
        finally { projectWriteGate.Release(); }
    }

    public async Task FlushSelectionSavesAsync()
    {
        Task previous;
        do { previous = pendingSelectionSave; await previous; }
        while (!ReferenceEquals(previous, pendingSelectionSave));
    }

    public async Task<bool> RecoverLastAutoSaveAsync()
    {
        await FlushSelectionSavesAsync();
        if (!File.Exists(RecoveryPointerPath)) return false;
        try
        {
            var pointer = JsonSerializer.Deserialize<RecoveryPointer>(await File.ReadAllTextAsync(RecoveryPointerPath))
                ?? throw new InvalidDataException("自动保存记录为空。");
            var project = await ProjectStore.LoadAsync(pointer.ProjectPath);
            Restore(project);
            ProjectPath = pointer.ProjectPath;
            AutoSaveStatus = "已恢复自动保存";
            Status = "已恢复自动保存的项目：" + pointer.ProjectPath;
            return true;
        }
        catch (Exception ex)
        {
            AutoSaveStatus = "恢复自动保存失败";
            ReportError(ex);
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task RecoverAutoSaveAsync()
    {
        await FlushSelectionSavesAsync();
        if (ConfirmDiscard() && !await RecoverLastAutoSaveAsync() && !File.Exists(RecoveryPointerPath))
            Status = "尚无自动保存记录；勾选引物后会自动创建。";
    }
}
