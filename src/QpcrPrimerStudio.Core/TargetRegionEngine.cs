namespace QpcrPrimerStudio.Core;

public static class TargetRegionEngine
{
    public static RegionPlan Plan(SequenceTarget target, TargetRegionSettings settings, ReferenceWorkspace? reference)
    {
        target.Validate();
        if (settings.AvoidFivePrime < 0 || settings.AvoidThreePrime < 0 || settings.VariantTerminalBases < 1)
            throw new ArgumentException("末端避让长度须非负，变异末端窗口须大于零。");
        if (settings.ScoreWeights.Values.Any(w => !double.IsFinite(w) || w < 0)) throw new ArgumentException("评分权重必须是有限的非负数。");
        var annotation = reference?.Find(target.Id);
        if (annotation is not null && annotation.Sequence != target.Sequence)
            throw new InvalidDataException("靶标序列已修改，与参考转录本不一致。请重新选择参考靶标。");
        var allowed = Enumerable.Repeat(true, target.Sequence.Length).ToArray();
        var evidence = new List<string>();
        for (var i = 0; i < allowed.Length; i++)
            if (i < settings.AvoidFivePrime || i >= allowed.Length - settings.AvoidThreePrime) allowed[i] = false;
        evidence.Add($"避开 5′ 前 {settings.AvoidFivePrime} nt、3′ 后 {settings.AvoidThreePrime} nt。");
        if (settings.Mode is not (TargetRegionMode.EntireTranscript or TargetRegionMode.PreferCds) && annotation?.Cds.Count is not > 0)
            throw new ArgumentException("所选 CDS/UTR 模式需要可核对的 CDS 注释。");
        if (annotation?.Cds.Count is > 0)
        {
            var start = annotation.Cds.Min(c => c.Start); var end = annotation.Cds.Max(c => c.Start + c.Length - 1);
            for (var i = 0; i < allowed.Length; i++)
            {
                var pos = i + 1; var inCds = annotation.Cds.Any(c => pos >= c.Start && pos < c.Start + c.Length);
                allowed[i] &= settings.Mode switch
                {
                    TargetRegionMode.CdsOnly or TargetRegionMode.AvoidUtr => inCds,
                    TargetRegionMode.FivePrimeUtr => pos < start,
                    TargetRegionMode.ThreePrimeUtr => pos > end,
                    _ => true
                };
            }
            evidence.Add(settings.Mode == TargetRegionMode.PreferCds ? "CDS 优先通过候选评分执行，保留 UTR 特异性候选。" : $"区域模式：{settings.Mode}。");
        }
        else if (settings.Mode == TargetRegionMode.PreferCds) evidence.Add("CDS 注释不可用：区域回退为 Entire transcript。");
        var required = settings.Expression == ExpressionMode.TemplateOnly ? [] : settings.RequiredTranscripts.ToList();
        var uniqueJunctions = new List<int>();
        if (settings.Expression != ExpressionMode.TemplateOnly)
        {
            if (annotation is null || annotation.GeneId.Length == 0)
                throw new ArgumentException("gene/isoform 模式需要参考项目中明确的 transcript ↔ gene 映射。");
            var family = reference!.Transcripts.Where(t => t.GeneId == annotation.GeneId).ToList();
            required = settings.Expression == ExpressionMode.TotalGene ? family.Select(t => t.Id).ToList()
                : settings.Expression == ExpressionMode.IsoformSpecific ? [target.Id] : required;
            if (required.Count == 0 || required.Any(id => !family.Any(t => t.Id == id)))
                throw new ArgumentException("所选转录本必须属于同一基因，且至少选择一条。");
            if (settings.Expression is ExpressionMode.TotalGene or ExpressionMode.SelectedTranscripts)
            {
                var others = family.Where(t => required.Contains(t.Id, StringComparer.Ordinal)).ToList();
                if (annotation.Exons.Count > 0 && others.All(t => t.Exons.Count > 0))
                {
                    for (var i = 0; i < allowed.Length; i++)
                    {
                        var exon = annotation.ExonAt(i + 1);
                        if (exon is null) { allowed[i] = false; continue; }
                        var genomePos = exon.GenomicPosition(i + 1);
                        allowed[i] &= others.All(t => t.Exons.Any(e => e.SeqId == exon.SeqId && e.Plus == exon.Plus && e.GenomicStart <= genomePos && e.GenomicEnd >= genomePos));
                    }
                    evidence.Add($"已定位 {required.Count} 条目标转录本的共有 exon 碱基；候选另行检查成对覆盖。");
                }
                else evidence.Add("无完整 exon 注释：使用成对精确序列匹配检查目标转录本覆盖；不能定位共有 exon。");
            }
            else
            {
                var siblings = family.Where(t => t.Id != target.Id).ToList();
                uniqueJunctions = annotation.Junctions.Where(j => !siblings.Any(t => t.Junctions.Any(k =>
                    JunctionSignature(annotation, j) == JunctionSignature(t, k)))).ToList();
                evidence.Add($"目标转录本独有连接位点：{string.Join(",", uniqueJunctions)}；候选同时排除同基因其他转录本的精确成对产物。");
            }
        }
        var regions = Regions(allowed, true); var excluded = Regions(allowed, false);
        if (regions.Count == 0) throw new ArgumentException("所选区域与末端避让条件没有保留可设计碱基。");
        return new(regions, excluded, uniqueJunctions, required, evidence);
    }
    private static string JunctionSignature(TranscriptAnnotation t, int j)
    {
        var left = t.ExonAt(j)!; var right = t.ExonAt(j + 1)!;
        return $"{left.SeqId}:{left.Plus}:{left.GenomicPosition(j)}:{right.GenomicPosition(j + 1)}";
    }
    private static List<SequenceRegion> Regions(bool[] mask, bool value)
    {
        var result = new List<SequenceRegion>(); var start = -1;
        for (var i = 0; i <= mask.Length; i++)
        {
            if (i < mask.Length && mask[i] == value) { if (start < 0) start = i; }
            else if (start >= 0) { result.Add(new(start + 1, i - start)); start = -1; }
        }
        return result;
    }
    public static List<(int Start, int End)> ExactProducts(string template, string forward, string reverse, int maximum)
    {
        var result = new List<(int, int)>(); var binding = SequenceFiles.ReverseComplement(reverse);
        for (var f = template.IndexOf(forward, StringComparison.Ordinal); f >= 0; f = template.IndexOf(forward, f + 1, StringComparison.Ordinal))
        for (var r = template.IndexOf(binding, f + forward.Length, StringComparison.Ordinal); r >= 0; r = template.IndexOf(binding, r + 1, StringComparison.Ordinal))
        {
            var size = r + binding.Length - f;
            if (size > maximum) break;
            result.Add((f + 1, r + binding.Length));
        }
        return result;
    }
}
