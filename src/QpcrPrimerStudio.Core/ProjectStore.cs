using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace QpcrPrimerStudio.Core;

public static class ProjectStore
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static async Task SaveAsync(string path, ProjectDocument project, CancellationToken token = default, string? expectedSha256 = null)
    {
        token.ThrowIfCancellationRequested();
        ProjectValidation.Validate(project);
        project.SavedAt = DateTimeOffset.Now;
        var absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        var staging = absolute + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, project, JsonOptions, token);
                await stream.FlushAsync(token);
                stream.Flush(flushToDisk: true);
            }
            var verified = await LoadAsync(staging, token);
            expectedSha256 ??= string.Equals(project.LoadedFilePath, absolute, StringComparison.OrdinalIgnoreCase) ? project.LoadedFileSha256 : null;
            await using var writeLock = await ProjectWriteLock.AcquireAsync(absolute, token);
            var actual = File.Exists(absolute) ? Hashing.File(absolute) : ProjectWriteLock.Missing;
            if (expectedSha256 is not null && actual != expectedSha256)
            {
                var conflict = absolute + ".conflict-" + Guid.NewGuid().ToString("N") + ".qpcrproject";
                File.Move(staging, conflict);
                throw new ProjectConflictException(conflict);
            }
            token.ThrowIfCancellationRequested();
            if (File.Exists(absolute))
            {
                await ProjectBackups.CreateAsync(absolute, token);
                token.ThrowIfCancellationRequested();
                File.Replace(staging, absolute, null);
            }
            else File.Move(staging, absolute);
            project.LoadedFilePath = absolute; project.LoadedFileSha256 = verified.LoadedFileSha256;
        }
        catch (Exception error)
        {
            string? recovery;
            try { recovery = QuarantinePending(staging, absolute); }
            catch (Exception recoveryError) { throw new AggregateException("工程保存失败，暂存文件隔离也失败；请保留 pending 文件。", error, recoveryError); }
            if (recovery is not null && error is not OperationCanceledException)
                throw new IOException("工程保存未完成；本次暂存已隔离供恢复：" + recovery + "\n" + error.Message, error);
            throw;
        }
    }
    public static async Task WriteRecoveryPointerAsync(string path, string projectPath, CancellationToken token = default)
    {
        var absolute = Path.GetFullPath(path);
        var staging = absolute + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            await using var writeLock = await ProjectWriteLock.AcquireAsync(absolute, token);
            await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(new { ProjectPath = Path.GetFullPath(projectPath) }), token);
            if (File.Exists(absolute)) File.Replace(staging, absolute, absolute + ".backup-" + Guid.NewGuid().ToString("N"));
            else File.Move(staging, absolute);
        }
        catch (Exception error)
        {
            try { QuarantinePending(staging, absolute); }
            catch (Exception recoveryError) { throw new AggregateException("恢复入口更新失败，暂存隔离也失败。", error, recoveryError); }
            throw;
        }
    }
    private static string? QuarantinePending(string staging, string absolute)
    {
        if (!File.Exists(staging)) return null;
        var folder = absolute + ".recovery";
        Directory.CreateDirectory(folder);
        var recovery = Path.Combine(folder, Path.GetFileName(staging));
        File.Move(staging, recovery);
        return recovery;
    }
    public static async Task<ProjectDocument> LoadAsync(string path, CancellationToken token = default)
    {
        await using var stream = File.OpenRead(path);
        using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes, token);
        bytes.Position = 0;
        var project = await JsonSerializer.DeserializeAsync<ProjectDocument>(bytes, JsonOptions, token)
            ?? throw new InvalidDataException("项目文件为空。");
        ProjectValidation.Validate(project);
        project.LoadedFilePath = Path.GetFullPath(path);
        project.LoadedFileSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.ToArray()));
        return project;
    }
}

public sealed class PrimerLibrary
{
    private readonly string connectionString;
    public PrimerLibrary(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS primers(id TEXT PRIMARY KEY, target TEXT NOT NULL, saved TEXT NOT NULL, document TEXT NOT NULL)";
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }
    public void Save(PrimerCandidate candidate)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO primers VALUES($id,$target,$saved,$document) ON CONFLICT(id) DO UPDATE SET saved=$saved, document=$document";
        command.Parameters.AddWithValue("$id", candidate.Id);
        command.Parameters.AddWithValue("$target", candidate.TargetId);
        command.Parameters.AddWithValue("$saved", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$document", JsonSerializer.Serialize(candidate));
        command.ExecuteNonQuery();
    }
    public List<PrimerCandidate> Read(string search = "")
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM primers WHERE target LIKE $search ORDER BY saved DESC";
        command.Parameters.AddWithValue("$search", "%" + search + "%");
        using var reader = command.ExecuteReader();
        var results = new List<PrimerCandidate>();
        while (reader.Read()) results.Add(JsonSerializer.Deserialize<PrimerCandidate>(reader.GetString(0))!);
        return results;
    }
}
