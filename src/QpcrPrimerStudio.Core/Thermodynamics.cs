using System.Globalization;

namespace QpcrPrimerStudio.Core;

public sealed class Thermodynamics(string executable)
{
    public async Task<string> AnalyzeAsync(string forward, string reverse, DesignParameters p, CancellationToken token)
    {
        forward = string.IsNullOrWhiteSpace(forward) ? "" : SequenceFiles.NormalizePrimer(forward);
        reverse = string.IsNullOrWhiteSpace(reverse) ? "" : SequenceFiles.NormalizePrimer(reverse);
        if (forward.Length == 0 && reverse.Length == 0) throw new ArgumentException("请至少输入一条引物。");
        var result = new List<string>();
        foreach (var (label, mode, first, second) in new[]
        {
            ("Forward 发卡", "HAIRPIN", forward, ""), ("Reverse 发卡", "HAIRPIN", reverse, ""),
            ("Forward 自二聚体", "ANY", forward, forward), ("Reverse 自二聚体", "ANY", reverse, reverse),
            ("异二聚体", "ANY", forward, reverse), ("Forward 3′ 异二聚体", "END1", forward, reverse),
            ("Reverse 3′ 异二聚体", "END2", forward, reverse)
        })
        {
            if (first.Length == 0 || mode != "HAIRPIN" && second.Length == 0) continue;
            var args = new List<string> { "-a", mode, "-s1", first, "-mv", Number(p.MonovalentMm),
                "-dv", Number(p.MagnesiumMm), "-n", Number(p.DntpMm), "-d", Number(p.PrimerNm), "-t", "37" };
            if (second.Length > 0) args.AddRange(["-s2", second]);
            var output = await ProcessRunner.RunAsync(executable, args, null, token);
            result.Add($"{label}\n{output.Output}{output.Error}");
        }
        return "Primer3 ntthal；ΔG 单位 cal/mol，ΔH cal/mol，ΔS cal/(mol·K)，t 为结构 Tm °C。ΔG 计算温度 37 °C。\n" +
            "使用当前设计参数中的单价盐、Mg²⁺、dNTP 和引物浓度。\n\n" + string.Join("\n\n", result);
    }
    private static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);
}
