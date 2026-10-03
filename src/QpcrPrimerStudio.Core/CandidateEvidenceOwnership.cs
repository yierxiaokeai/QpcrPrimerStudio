namespace QpcrPrimerStudio.Core;

public static class CandidateEvidenceOwnership
{
    public const string InvalidatedPolicy = "qPCR-current-context-1";
    public static string QueryPairSha256(string forward, string reverse) => Hashing.Text("F=" + forward + "\nR=" + reverse);
    public static bool QueryMatches(SpecificityReport report, PrimerCandidate candidate) =>
        report.QueryPairSha256.Length > 0 && report.QueryPairSha256 == QueryPairSha256(candidate.Forward.Sequence, candidate.Reverse.Sequence);

    public static List<string> Invalidations(PrimerCandidate candidate, TargetRun run,
        SequenceTarget current, ReferenceWorkspace? reference, IReadOnlyList<DatabaseManifest> databases)
    {
        var reasons = new List<string>();
        var currentExpected = current.ExpectedSubjects;
        if (currentExpected.Count == 0)
        {
            try
            {
                var required = TargetRegionEngine.Plan(current, run.RegionSettings, reference).RequiredTranscripts;
                currentExpected = required.Count > 0 ? required : [current.Id];
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
            {
                reasons.Add("当前参考无法核对预期表达目标；请核对参考并重新设计：" + ex.Message);
                currentExpected = [current.Id];
            }
        }
        var previousExpected = run.Target.ExpectedSubjects.Count > 0 ? run.Target.ExpectedSubjects : [run.Target.Id];
        if (!SameIds(currentExpected, previousExpected))
            reasons.Add("当前预期数据库 ID 已变化，历史特异性证据失效；请按当前预期 ID 重新设计并检查特异性。");
        if (!SameIds(current.CoverageTranscripts, run.Target.CoverageTranscripts))
            reasons.Add("当前要求覆盖的转录本已变化，历史覆盖评分失效；请按当前覆盖 ID 重新设计。");
        var currentJunctions = reference?.Find(current.Id)?.Junctions ?? current.Junctions;
        if (!currentJunctions.ToHashSet().SetEquals(run.Target.Junctions))
            reasons.Add("当前外显子连接位点已变化，历史连接位点评估失效；请核对当前连接位点并重新设计。");
        if (candidate.Provenance?.ReferenceIdentity is { Length: > 0 } identity && identity != reference?.Identity)
            reasons.Add("当前参考版本与候选设计来源不同，历史证据失效；请核对当前参考并重新设计、检查特异性。");
        foreach (var report in candidate.Specificity.GroupBy(r => r.Kind).Select(g => g.Last()))
        {
            if (!QueryMatches(report, candidate))
                reasons.Add(report.QueryPairSha256.Length == 0
                    ? $"{report.Kind}历史特异性记录缺少查询引物指纹，当前证据无法核对；请重新检查特异性。"
                    : $"{report.Kind}特异性证据对应的引物序列与当前候选不匹配；请重新检查特异性。");
            var bound = databases.LastOrDefault(d => d.Kind == report.Kind);
            if (bound is not null && !SameDatabase(bound, report.Database))
                reasons.Add($"{report.Kind}数据库已变化，历史特异性证据失效；请在当前数据库重新检查特异性。");
            var role = report.Kind == "转录本" ? "Transcriptome" : report.Kind == "基因组" ? "Genome" : "";
            var asset = reference?.Assets.FirstOrDefault(a => a.Role == role);
            if (asset is not null && (report.Kind == "基因组" || bound is null) && asset.Sha256 != report.Database.SourceSha256)
                reasons.Add($"{report.Kind}检查数据库与当前参考文件指纹不匹配；请绑定当前参考数据库并重新检查特异性。");
        }
        return reasons.Distinct(StringComparer.Ordinal).ToList();
    }

    private static bool SameIds(IEnumerable<string> first, IEnumerable<string> second) =>
        first.ToHashSet(StringComparer.Ordinal).SetEquals(second);

    private static bool SameDatabase(DatabaseManifest first, DatabaseManifest second) =>
        first.Kind == second.Kind && first.SourceSha256 == second.SourceSha256 &&
        first.SequenceCount == second.SequenceCount && first.ToolVersion == second.ToolVersion;
}
