using System.Globalization;
using System.Windows.Data;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public sealed class CandidateListConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => parameter switch
    {
        "Metrics" when value is PrimerMetrics p => $"Tm {p.Tm:F1} °C · {p.Sequence.Length} nt · GC {p.Gc:F1}%",
        "PropertyRows" when value is PrimerCandidate c => new[]
        {
            new PrimerPropertyRow("指标", "Forward", "Reverse"),
            new PrimerPropertyRow("长度 nt", $"{c.Forward.Sequence.Length}", $"{c.Reverse.Sequence.Length}"),
            new PrimerPropertyRow("Tm °C", $"{c.Forward.Tm:F1}", $"{c.Reverse.Tm:F1}"),
            new PrimerPropertyRow("GC%", $"{c.Forward.Gc:F1}", $"{c.Reverse.Gc:F1}"),
            new PrimerPropertyRow("起点", $"{c.Forward.Start}", $"{c.Reverse.Start}"),
            new PrimerPropertyRow("末位", $"{c.Forward.End}", $"{c.Reverse.End}"),
            new PrimerPropertyRow("发卡 Tm °C", $"{c.Forward.HairpinTm:F1}", $"{c.Reverse.HairpinTm:F1}"),
            new PrimerPropertyRow("自身 ANY °C", $"{c.Forward.SelfAnyTm:F1}", $"{c.Reverse.SelfAnyTm:F1}"),
            new PrimerPropertyRow("自身 3′ END °C", $"{c.Forward.SelfEndTm:F1}", $"{c.Reverse.SelfEndTm:F1}")
        },
        "PropertyRows" => Array.Empty<PrimerPropertyRow>(),
        "Structure" => string.IsNullOrWhiteSpace(value as string) ? "本次引擎未返回稳定发卡结构" : System.Net.WebUtility.HtmlDecode((string)value)
            .Replace("U+2510", "┐").Replace("U+2518", "┘").Replace("U+250C", "┌").Replace("U+2514", "└"),
        "Mismatches" when value is PrimerCandidate c => Mismatches(c),
        _ => ""
    };
    private static string Mismatches(PrimerCandidate candidate)
    {
        if (candidate.Specificity.Count == 0) return "未检查";
        var products = candidate.Specificity.SelectMany(r => r.Products).ToList();
        string Pair(bool expected)
        {
            var product = products.Where(p => p.Expected == expected).OrderBy(p =>
                (p.ForwardSite?.ThreePrimeMismatches ?? int.MaxValue) + (long)(p.ReverseSite?.ThreePrimeMismatches ?? int.MaxValue))
                .ThenBy(p => p.Mismatches).FirstOrDefault();
            if (product is null) return expected ? "未检出" : "未检出成对产物";
            var sites = new[] { product.ForwardSite, product.ReverseSite };
            var f = sites.FirstOrDefault(s => s?.QueryId == "F"); var r = sites.FirstOrDefault(s => s?.QueryId == "R");
            return f is null || r is null ? $"总计 {product.Mismatches}" : $"{f.Mismatches}/{r.Mismatches} · 3′ {f.ThreePrimeMismatches}/{r.ThreePrimeMismatches}";
        }
        if (candidate.Specificity.All(r => r.Status.Contains("失败"))) return "检查失败";
        return $"预期 {Pair(true)}\n非靶 {Pair(false)}" + (candidate.Specificity.Any(r => r.Status.Contains("失败")) ? "\n部分检查失败" : "");
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed record PrimerPropertyRow(string Name, string Forward, string Reverse);
