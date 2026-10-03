using System.IO.Compression;
using System.Text.Json;

namespace QpcrPrimerStudio.Core;

public sealed record ProjectBackupInfo(string Path, long Bytes, DateTime LastWriteUtc, bool Archived)
{
    public string Label => $"{LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Bytes / 1024d:F1} KB · {(Archived ? "归档" : "近期")}";
}
public static class ProjectBackups
{
    public const int RecentLimit = 10;
    public const long RecentBytesLimit = 50 * 1024 * 1024;
    public static string Folder(string projectPath) => Path.GetFullPath(projectPath) + ".backups";
    public static List<ProjectBackupInfo> List(string projectPath)
    {
        var folder = Folder(projectPath);
        return !Directory.Exists(folder) ? [] : Directory.EnumerateFiles(folder, "*.zip", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p)).Select(f => new ProjectBackupInfo(f.FullName, f.Length, f.LastWriteTimeUtc,
                !string.Equals(f.DirectoryName, folder, StringComparison.OrdinalIgnoreCase))).OrderByDescending(i => i.LastWriteUtc).ToList();
    }
    internal static async Task CreateAsync(string projectPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var folder = Folder(projectPath); Directory.CreateDirectory(folder);
        var zipPath = Path.Combine(folder, $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.zip");
        var staging = zipPath + ".pending";
        try
        {
            using (var zip = ZipFile.Open(staging, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("project.qpcrproject", CompressionLevel.SmallestSize);
                await using var output = entry.Open(); await using var input = File.OpenRead(projectPath);
                await input.CopyToAsync(output, token);
            }
            _ = await LoadAsync(staging, token);
            token.ThrowIfCancellationRequested();
            File.Move(staging, zipPath);
            ArchiveOlder(projectPath);
        }
        catch (Exception error)
        {
            try
            {
                if (File.Exists(staging))
                {
                    var recovery = Path.Combine(folder, ".recovery"); Directory.CreateDirectory(recovery);
                    File.Move(staging, Path.Combine(recovery, Path.GetFileName(staging)));
                }
            }
            catch (Exception isolationError) { throw new AggregateException("压缩备份失败，暂存隔离也失败；请保留 pending 文件。", error, isolationError); }
            throw;
        }
    }
    public static void ArchiveOlder(string projectPath)
    {
        var folder = Folder(projectPath); long keptBytes = 0; int kept = 0;
        foreach (var item in List(projectPath).Where(i => !i.Archived))
        {
            if (kept < RecentLimit && keptBytes + item.Bytes <= RecentBytesLimit) { kept++; keptBytes += item.Bytes; continue; }
            var destination = Path.Combine(folder, "archive", item.LastWriteUtc.ToString("yyyy-MM"), Path.GetFileName(item.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(item.Path, destination);
        }
    }
    public static async Task<ProjectDocument> LoadAsync(string zipPath, CancellationToken token = default)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry("project.qpcrproject") ?? throw new InvalidDataException("备份中缺少工程文件。");
        await using var stream = entry.Open();
        var project = await JsonSerializer.DeserializeAsync<ProjectDocument>(stream, ProjectStore.JsonOptions, token)
            ?? throw new InvalidDataException("工程备份为空。");
        ProjectValidation.Validate(project); return project;
    }
}
