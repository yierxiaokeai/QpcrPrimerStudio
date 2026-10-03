namespace QpcrPrimerStudio.Core;

public sealed class ProjectConflictException(string copyPath) : IOException("工程已被另一个窗口修改；本次修改已保存到冲突副本：" + copyPath)
{
    public string CopyPath { get; } = copyPath;
}

internal static class ProjectWriteLock
{
    internal const string Missing = "missing";
    internal static async Task<FileStream> AcquireAsync(string path, CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path + ".write-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && started.Elapsed < TimeSpan.FromSeconds(10))
            { await Task.Delay(50, token); }
        }
    }
}
