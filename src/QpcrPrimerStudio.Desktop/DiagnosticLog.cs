using System.IO;

namespace QpcrPrimerStudio.Desktop;

internal static class DiagnosticLog
{
    internal static string? TryAppend(string folder, Exception error)
    {
        try
        {
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "errors.log"), $"{DateTimeOffset.Now:O}\n{error}\n");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "无法写入错误日志：" + ex.Message;
        }
    }
}
