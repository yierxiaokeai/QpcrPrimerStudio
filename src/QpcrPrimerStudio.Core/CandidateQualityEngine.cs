namespace QpcrPrimerStudio.Core;

public static class CandidateQualityEngine
{
    public static CandidateAssessment Evaluate(PrimerCandidate c, SequenceTarget target, DesignParameters p,
        TargetRegionSettings settings, RegionPlan plan, ReferenceWorkspace? reference)
    {
        var result = new CandidateAssessment(); var annotation = reference?.Find(target.Id);
        result.Rejections.AddRange(c.ConstraintWarnings.Select(w => "引物不满足设计条件：" + w));
        var required = plan.RequiredTranscripts;
        if (required.Count > 0)
        {
            foreach (var id in required)
            {
                var transcript = reference?.Find(id)
                    ?? throw new ArgumentException($"参考项目缺少要求覆盖的转录本：{id}。请核对覆盖 ID。");
                if (TargetRegionEngine.ExactProducts(transcript.Sequence, c.Forward.Sequence, c.Reverse.Sequence, p.MaxProduct).Any(x => x.End - x.Start + 1 >= p.MinProduct))
                    result.CoveredTranscripts.Add(id);
                else result.MissingTranscripts.Add(id);
            }
            if (result.MissingTranscripts.Count > 0) result.Rejections.Add("目标转录本覆盖不足：" + string.Join(",", result.MissingTranscripts));
        }
        if (settings.Expression == ExpressionMode.IsoformSpecific && annotation is not null)
        {
            var siblings = reference!.Transcripts.Where(t => t.GeneId == annotation!.GeneId && t.Id != target.Id);
            var otherProducts = siblings.Where(t => TargetRegionEngine.ExactProducts(t.Sequence, c.Forward.Sequence, c.Reverse.Sequence, 5000).Count > 0).Select(t => t.Id).ToList();
            if (otherProducts.Count > 0) result.Rejections.Add("同基因其他转录本存在精确成对产物：" + string.Join(",", otherProducts));
            result.Warnings.Add("Isoform 覆盖使用精确结合检查；存在错配的旁系/同基因产物仍需转录组特异性搜索。");
        }
        if (settings.Mode is TargetRegionMode.CdsOnly or TargetRegionMode.AvoidUtr or TargetRegionMode.FivePrimeUtr or TargetRegionMode.ThreePrimeUtr)
        {
            if (!plan.Allowed.Any(r => c.Forward.Start >= r.Start && c.Reverse.End < r.Start + r.Length))
                result.Rejections.Add("扩增子未完全落在所选区域内。");
        }
        if (c.Forward.Start <= settings.AvoidFivePrime || c.Reverse.End > target.Sequence.Length - settings.AvoidThreePrime)
            result.Rejections.Add("扩增子落入转录本末端避让区。");
        if (annotation is not null && reference!.Assets.Any(a => a.Role == "Variants"))
        {
            foreach (var v in reference.ProjectVariants(annotation))
            foreach (var entry in new[] { (Name: "F", Primer: c.Forward, Reverse: false), (Name: "R", Primer: c.Reverse, Reverse: true) })
            {
                var start = Math.Max(entry.Primer.Start, v.Start); var end = Math.Min(entry.Primer.End, v.End);
                if (start > end) continue;
                var fromEnd = entry.Reverse ? start - entry.Primer.Start + 1 : entry.Primer.End - end + 1;
                var reject = settings.AvoidVariants && settings.RejectTerminalVariants && fromEnd <= settings.VariantTerminalBases;
                result.Variants.Add(new(entry.Name, fromEnd, v, reject));
                if (reject) result.Rejections.Add($"{entry.Name} 引物 3′ 第 {fromEnd} 位出现已知变异：{v.Variant.SeqId}:{v.Variant.Position}。");
            }
        }
        result.Dimers = [Complementarity("F–F", c.Forward.Sequence, c.Forward.Sequence),
            Complementarity("R–R", c.Reverse.Sequence, c.Reverse.Sequence), Complementarity("F–R", c.Forward.Sequence, c.Reverse.Sequence)];
        result.Amplicon = AnalyzeAmplicon(c.Amplicon);
        result.Gdna = Gdna(c, target, annotation, reference, p);
        static double Quality(double value, double maximum) => Math.Clamp(1 - value / Math.Max(maximum, 0.001), 0, 1);
        void Add(string name, double weight, double? quality, string evidence) => result.Components.Add(new(name, weight, quality, evidence));
        Add("Tm", 10, Quality(Math.Max(Math.Abs(c.Forward.Tm - p.OptTm), Math.Abs(c.Reverse.Tm - p.OptTm)), 5), $"{c.Forward.Tm:F2}/{c.Reverse.Tm:F2} °C");
        Add("ΔTm", 10, Quality(Math.Abs(c.Forward.Tm - c.Reverse.Tm), 4), $"{Math.Abs(c.Forward.Tm - c.Reverse.Tm):F2} °C");
        Add("GC", 5, Quality(Math.Max(Math.Abs(c.Forward.Gc - 50), Math.Abs(c.Reverse.Gc - 50)), 50), $"{c.Forward.Gc:F1}/{c.Reverse.Gc:F1}%");
        Add("Amplicon", 10, Quality(Math.Abs(c.ProductLength - (p.MinProduct + p.MaxProduct) / 2d), p.MaxProduct - p.MinProduct + 1), $"{c.ProductLength} bp；GC {result.Amplicon.Gc:F1}%");
        Add("Hairpin", 10, Quality(Math.Max(c.Forward.HairpinTm, c.Reverse.HairpinTm), p.MaxHairpinTm + 10), "Primer3 热力学结构 Tm");
        Add("Self-dimer", 10, Quality(Math.Max(c.Forward.SelfAnyTm, c.Reverse.SelfAnyTm), p.MaxSelfAnyTm + 10), "F–F / R–R");
        Add("Heterodimer", 15, Quality(c.PairAnyTm, p.MaxPairAnyTm + 10), $"ANY {c.PairAnyTm:F2} °C；END {c.PairEndTm:F2} °C");
        Add("3′ complementarity", 15, Quality(result.Dimers.Max(d => d.ThreePrimeBases), 8), string.Join("；", result.Dimers.Select(d => $"{d.Type} {d.ThreePrimeBases} nt {d.Risk}")));
        foreach (var kind in new[] { "转录本", "基因组" })
        {
            var report = c.Specificity.LastOrDefault(r => r.Kind == kind);
            var queryMatches = report is not null && CandidateEvidenceOwnership.QueryMatches(report, c);
            var known = queryMatches && !report!.Status.Contains("失败") && !report.Status.Contains("不完整") && !report.Status.Contains("未检出预期") && !report.Status.Contains("不匹配");
            Add(kind + "特异性", 20, known ? report!.Products.Any(r => !r.Expected) ? 0 : 1 : null, report?.Status ?? "未检查");
            if (report is not null && !queryMatches)
                result.Rejections.Add(kind + "特异性证据缺少有效查询引物绑定或与当前序列不匹配，请重新检查。");
            if (report?.Products.Any(r => !r.Expected) == true) result.Rejections.Add(kind + "检出潜在非靶向产物，需要复核。");
        }
        Add("gDNA", 5, result.Gdna.Level switch { "A" => 1, "B" => 0.8, "C" => 0.2, _ => null }, result.Gdna.Evidence);
        var variantsAvailable = reference?.Assets.Any(a => a.Role == "Variants") == true;
        Add("Variants", 5, variantsAvailable ? settings.AvoidVariants ? Math.Max(0, 1 - result.Variants.Count * 0.3) : 1 : null,
            variantsAvailable ? $"引物区域变异 {result.Variants.Count} 个；策略 {(settings.AvoidVariants ? "启用" : "关闭")}" : "未提供变异数据");
        if (settings.Mode == TargetRegionMode.PreferCds)
            Add("CDS preference", 5, annotation?.Cds.Count > 0 ? Enumerable.Range(c.Forward.Start, c.ProductLength)
                .All(pos => annotation.Cds.Any(r => pos >= r.Start && pos < r.Start + r.Length)) ? 1 : 0.5 : null, "优先 CDS；保留 UTR 候选");
        result.Components = result.Components.Select(c => c with { Weight = settings.ScoreWeights.GetValueOrDefault(c.Metric, c.Weight) }).ToList();
        var evaluated = result.Components.Where(s => s.Quality.HasValue).ToList();
        var weight = result.Components.Sum(s => s.Weight); var evaluatedWeight = evaluated.Sum(s => s.Weight);
        result.EvidenceCoverage = weight > 0 ? evaluatedWeight / weight * 100 : 0;
        result.Score = evaluatedWeight > 0 ? Math.Round(evaluated.Sum(s => s.Weight * s.Quality!.Value) / evaluatedWeight * 100, 1) : null;
        result.Warnings.Add("综合分数为已评指标的加权启发式排名；缺失检查不计为通过，不代表实验成功概率。");
        result.EvidenceFingerprint = EvidenceFingerprint(c, target, p, settings, reference);
        c.Assessment = result; return result;
    }
    public static string EvidenceFingerprint(PrimerCandidate c, SequenceTarget target, DesignParameters p,
        TargetRegionSettings settings, ReferenceWorkspace? reference) => Hashing.Text(System.Text.Json.JsonSerializer.Serialize(new
        {
            Policy = "qPCR-heuristic-2", target, p, settings, ReferenceIdentity = reference?.Identity,
            Annotation = reference?.Find(target.Id),
            Family = settings.Expression == ExpressionMode.TemplateOnly ? null : reference?.Transcripts.Where(t => t.GeneId == reference.Find(target.Id)?.GeneId).ToList(),
            Variants = reference?.Find(target.Id) is { } annotation ? reference.ProjectVariants(annotation) : null,
            c.Forward, c.Reverse, c.ProductLength, c.Amplicon, c.PairAnyTm, c.PairEndTm, c.ConstraintWarnings, c.Specificity
        }));

    public static void Refresh(PrimerCandidate c, TargetRun run, DesignParameters fallback, ReferenceWorkspace? reference)
    {
        var p = run.UsedParameters ?? fallback;
        var fingerprint = EvidenceFingerprint(c, run.Target, p, run.RegionSettings, reference);
        if (c.Assessment.EvidenceFingerprint == fingerprint) return;
        try { Evaluate(c, run.Target, p, run.RegionSettings, TargetRegionEngine.Plan(run.Target, run.RegionSettings, reference), reference); }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            c.Assessment = new() { EvidenceFingerprint = fingerprint, Rejections = ["当前参考无法重新评估此候选，请核对参考并重新设计：" + ex.Message] };
            foreach (var report in c.Specificity.Where(r => r.Products.Any(p => !p.Expected)))
                c.Assessment.Rejections.Add(report.Kind + "检出潜在非靶向产物，需要复核。");
        }
    }
    public static void InvalidateCurrentEvidence(PrimerCandidate candidate, IReadOnlyList<string> reasons)
    {
        candidate.Assessment = new()
        {
            PolicyVersion = CandidateEvidenceOwnership.InvalidatedPolicy,
            EvidenceFingerprint = Hashing.Text(string.Join("\n", reasons)),
            Rejections = reasons.ToList(),
            Warnings = ["原设计、数据库命中与热力学证据保留；此评分因当前检测目标或参考变化已失效。"]
        };
    }
    public static DimerComplementarity Complementarity(string type, string first, string second)
    {
        var reverse = SequenceFiles.ReverseComplement(second); var longest = 0; var terminal = 0;
        for (var offset = -reverse.Length + 1; offset < first.Length; offset++)
        {
            var run = 0;
            for (var i = Math.Max(0, offset); i < Math.Min(first.Length, offset + reverse.Length); i++)
            {
                var j = i - offset;
                run = first[i] == reverse[j] ? run + 1 : 0; longest = Math.Max(longest, run);
                if (run > 0 && (i == first.Length - 1 || j - run + 1 == 0)) terminal = Math.Max(terminal, run);
            }
        }
        return new(type, longest, terminal, terminal >= 5 ? "High" : terminal >= 3 ? "Moderate" : "Low");
    }
    public static AmpliconQuality AnalyzeAmplicon(string sequence)
    {
        static double Gc(string s) => 100d * s.Count(c => c is 'G' or 'C') / Math.Max(1, s.Length);
        var width = Math.Min(30, sequence.Length); var windows = Enumerable.Range(0, sequence.Length - width + 1).Select(i => Gc(sequence.Substring(i, width))).ToList();
        var longest = 0; var run = 0; char last = '\0'; var repeats = new List<SequenceRegion>();
        for (var i = 0; i < sequence.Length; i++) { run = sequence[i] == last ? run + 1 : 1; longest = Math.Max(longest, run); last = sequence[i]; }
        for (var i = 0; i <= sequence.Length - 8; i++)
        {
            var part = sequence.Substring(i, 8);
            if (part.Distinct().Count() <= 1 || Enumerable.Range(2, 6).All(k => part[k] == part[k % 2])) repeats.Add(new(i + 1, 8));
        }
        return new(Gc(sequence), windows.Min(), windows.Max(), width, longest, repeats, "未计算；结构预测需专门模型及实验条件");
    }
    private static GdnaAssessment Gdna(PrimerCandidate c, SequenceTarget target, TranscriptAnnotation? annotation, ReferenceWorkspace? reference, DesignParameters p)
    {
        if (annotation is null || annotation.Exons.Count == 0 || reference?.HasGenome != true)
            return new("Unknown", "参考基因组/exon 注释不可用；手动 junction 不能确定基因组污染风险。", null, null);
        if (annotation.Junctions.Any(j => Spans(c.Forward, j, p, false) || Spans(c.Reverse, j, p, true)))
            return new("A", "引物跨 exon–exon junction；需排查加工假基因与其他基因组产物。", null, null);
        var first = annotation.ExonAt(c.Forward.Start); var last = annotation.ExonAt(c.Reverse.End);
        if (first is null || last is null) return new("Unknown", "引物位点缺少完整 exon 注释。", null, null);
        var size = Math.Abs(last.GenomicPosition(c.Reverse.End) - first.GenomicPosition(c.Forward.Start)) + 1;
        var intron = size - c.ProductLength;
        return new(intron >= 1000 ? "B" : "C", intron >= 1000 ? $"预测基因组产物 {size} bp，包含 {intron} bp intron。"
            : intron == 0 ? "同 exon / 无内含子；gDNA 与 cDNA 产物长度相同。" : $"预测基因组产物 {size} bp；intron {intron} bp，区分能力有限。", size, intron);
    }
    public static bool Spans(PrimerMetrics primer, int junction, DesignParameters p, bool reverse)
    {
        var left = junction - primer.Start + 1; var right = primer.End - junction;
        return left >= (reverse ? p.JunctionOverlap3 : p.JunctionOverlap5) && right >= (reverse ? p.JunctionOverlap5 : p.JunctionOverlap3);
    }
}
