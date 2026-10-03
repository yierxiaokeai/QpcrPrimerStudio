namespace QpcrPrimerStudio.Core;

public static class SmartBindingScreen
{
    public static List<string> Rejections(PrimerCandidate candidate, SequenceTarget target,
        IReadOnlyList<DatabaseManifest> databases)
    {
        var reasons = new List<string>();
        if (candidate.TargetId != target.Id)
            reasons.Add("候选与目标模板不对应。");
        CheckTemplate(candidate.Forward, false, "F");
        CheckTemplate(candidate.Reverse, true, "R");
        var reports = candidate.Specificity.GroupBy(r => r.Kind).Select(g => g.Last()).ToList();
        foreach (var kind in databases.Select(d => d.Kind).Distinct())
            if (reports.All(r => r.Kind != kind))
                reasons.Add($"已绑定{kind}数据库，但尚未完成特异性检查。");
        foreach (var report in reports)
        {
            if (!CandidateEvidenceOwnership.QueryMatches(report, candidate) ||
                new[] { "未检查", "失败", "不完整", "未检出预期", "不匹配" }.Any(report.Status.Contains))
            {
                reasons.Add($"{report.Kind}结合证据尚未核对完成，请重新检查：{report.Status}");
                continue;
            }
            if (report.Products.Any(p => !p.Expected))
                reasons.Add($"{report.Kind}检出潜在非靶向产物。");
            var expected = report.Kind == "转录本"
                ? (target.ExpectedSubjects.Count > 0 ? target.ExpectedSubjects : new List<string> { target.Id })
                : report.Products.Where(p => p.Expected).Select(p => p.SubjectId).Distinct().ToList();
            foreach (var id in expected.Distinct(StringComparer.Ordinal))
            {
                var products = report.Products.Where(p => p.Expected && p.SubjectId == id &&
                    p.FirstPrimer != p.SecondPrimer).ToList();
                if (products.Any(p => ExactPair(p, candidate))) continue;
                var detail = products.Count == 0 ? "未检出预期成对产物" :
                    $"最少总错配 {products.Min(p => p.Mismatches)}；还需完整结合位点且无插缺";
                reasons.Add($"{report.Kind}预期靶标 {id} 未确认 F/R 零错配、无插缺：{detail}。");
            }
        }
        return reasons.Distinct(StringComparer.Ordinal).ToList();

        void CheckTemplate(PrimerMetrics primer, bool reverse, string name)
        {
            if (primer.Start < 1 || primer.End > target.Sequence.Length ||
                primer.End - primer.Start + 1 != primer.Sequence.Length)
            {
                reasons.Add($"{name} 模板结合坐标无法核对。"); return;
            }
            var binding = target.Sequence.Substring(primer.Start - 1, primer.Sequence.Length);
            if (reverse) binding = SequenceFiles.ReverseComplement(binding);
            if (binding.Concat(primer.Sequence).Any(b => !"ACGT".Contains(char.ToUpperInvariant(b))))
            {
                reasons.Add($"{name} 目标结合位点或引物含歧义碱基，无法确认零错配。"); return;
            }
            var mismatches = Enumerable.Range(0, binding.Length)
                .Where(i => char.ToUpperInvariant(binding[i]) != char.ToUpperInvariant(primer.Sequence[i])).ToList();
            if (mismatches.Count > 0)
                reasons.Add($"{name} 与目标模板错配 {mismatches.Count} 处，其中 3′末端 5 nt 内 {mismatches.Count(i => i >= binding.Length - 5)} 处。");
        }
    }

    private static bool ExactPair(PotentialProduct product, PrimerCandidate candidate)
    {
        var sites = new[] { product.ForwardSite, product.ReverseSite };
        return product.Mismatches == 0 && sites.Any(s => s?.QueryId == "F") && sites.Any(s => s?.QueryId == "R") &&
            sites.All(s => s is not null && s.Mismatches == 0 && s.ThreePrimeMismatches == 0 &&
                s.IndelBases == 0 && s.MismatchPositions.Count == 0 && s.SubjectId == product.SubjectId &&
                s.Start >= 1 && s.End - s.Start + 1 ==
                    (s.QueryId == "F" ? candidate.Forward.Sequence.Length : candidate.Reverse.Sequence.Length));
    }
}
