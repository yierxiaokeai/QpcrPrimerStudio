using System.Diagnostics;
using System.Text;

namespace QpcrPrimerStudio.Core;

public sealed record ProcessResult(string Output, string Error);
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        string? input, CancellationToken cancellationToken, TimeSpan? timeout = null, string? workingDirectory = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到计算引擎，请检查工具配置。", executable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false), WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable)!
        };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        using var timer = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, cancellationToken);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), linked.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(linked.Token);
            var result = new ProcessResult(await stdout, await stderr);
            if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} 退出码 {process.ExitCode}：{result.Error}\n{result.Output}");
            return result;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"{Path.GetFileName(executable)} 超过运行时限。");
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }
}

public sealed record EnginePaths(string Primer3, string BlastN, string MakeBlastDb, string BlastDbCmd)
{
    public static EnginePaths FromFolder(string folder) => new(
        Path.Combine(folder, "primer3", "primer3_core.exe"), Path.Combine(folder, "blast", "blastn.exe"),
        Path.Combine(folder, "blast", "makeblastdb.exe"), Path.Combine(folder, "blast", "blastdbcmd.exe"));
}
