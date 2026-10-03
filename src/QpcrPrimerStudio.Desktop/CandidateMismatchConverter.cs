using System.Globalization;
using System.Windows.Data;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public sealed class CandidateMismatchConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not PrimerCandidate candidate) return "";
        var template = values[1] is SequenceTarget target ? Template(candidate, target) : "未核对";
        var database = new CandidateListConverter().Convert(candidate, typeof(string), "Mismatches", culture);
        return $"模板 {template}\n数据库 {database}";
    }
    private static string Template(PrimerCandidate candidate, SequenceTarget target)
    {
        if (candidate.TargetId != target.Id || (candidate.Provenance is { } p && p.TemplateSha256 != target.Sha256)) return "指纹不匹配";
        (int Total, int End)? Count(PrimerMetrics primer, bool reverse)
        {
            if (primer.Start < 1 || primer.End - primer.Start + 1 != primer.Sequence.Length || primer.End > target.Sequence.Length) return null;
            var site = target.Sequence.Substring(primer.Start - 1, primer.Sequence.Length);
            if (reverse) site = SequenceFiles.ReverseComplement(site);
            if (site.Any(c => !"ACGT".Contains(c))) return null;
            var positions = Enumerable.Range(0, primer.Sequence.Length).Where(i => primer.Sequence[i] != site[i]).ToArray();
            return (positions.Length, positions.Count(i => i >= primer.Sequence.Length - 5));
        }
        var f = Count(candidate.Forward, false); var r = Count(candidate.Reverse, true);
        return f is null || r is null ? "未核对" : $"{f.Value.Total}/{r.Value.Total} · 3′ {f.Value.End}/{r.Value.End}";
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
