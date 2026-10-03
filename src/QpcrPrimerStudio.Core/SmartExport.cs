using System.Text.Json;

namespace QpcrPrimerStudio.Core;

public sealed record SmartExportChoice(string Gene, int RunIndex, int CandidateIndex);
public sealed record SmartGeneResult(string Gene, string Target, string CandidateId, int? Rank, double? Score,
    string Forward5to3, string Reverse5to3, int? ProductBp, string Status, string Reason);
public sealed record SmartTargetResult(string Gene, string Target, int? RunIndex, int Candidates, int QualityExcluded,
    int Unscored, int HairpinExcluded, int Retained, string Reason);
public sealed class SmartExportReport
{
    public DateTimeOffset PreparedAt { get; set; } = DateTimeOffset.Now;
    public string SourceFingerprint { get; set; } = "";
    public string HairpinEngineSha256 { get; set; } = "";
    public HairpinScreenSettings Settings { get; set; } = new();
    public string RankingRule { get; set; } = "排除质量规则拒绝项、未评分项及高风险发卡后，按综合评分降序；同分按原始排名、Primer3 penalty、靶标 ID、候选 ID 升序。";
    public List<SmartGeneResult> Genes { get; set; } = [];
    public List<SmartTargetResult> Targets { get; set; } = [];
}
public sealed record SmartExportPlan(ProjectDocument Project, List<SmartExportChoice> Choices, SmartExportReport Report)
{
    public string ContentFingerprint { get; init; } = "";
    public int MissingGenes => Report.Genes.Count(g => g.Status != "已选出");
}

public sealed class SmartExportEngine(string ntthal)
{
    public static string DesignFingerprint(ProjectDocument project) => Hashing.Text(JsonSerializer.Serialize(new
    {
        project.Targets, project.Runs, project.Parameters, project.RegionSettings, project.Reference,
        project.HairpinSettings, project.Databases, project.AutoSpecificity, project.UseSharedSearchParameters
    }, ProjectStore.JsonOptions));

    public async Task<SmartExportPlan> PrepareAsync(ProjectDocument source, HairpinScreenSettings settings,
        CancellationToken token, Action<int, int, string>? progress = null)
    {
        token.ThrowIfCancellationRequested(); settings.Validate();
        if (source.Targets.Count == 0) throw new ArgumentException("请先导入基因并完成设计。");
        ProjectValidation.Validate(source);
        if (source.Targets.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != source.Targets.Count)
            throw new InvalidDataException("项目靶标 ID 重复，无法确定每个基因的设计结果。");
        var project = JsonSerializer.Deserialize<ProjectDocument>(JsonSerializer.Serialize(source, ProjectStore.JsonOptions), ProjectStore.JsonOptions)!;
        var report = new SmartExportReport { SourceFingerprint = DesignFingerprint(source), Settings = settings,
            HairpinEngineSha256 = Hashing.File(ntthal) };
        var viable = new List<SmartExportChoice>();
        var groups = new Dictionary<string, List<SmartTargetResult>>(StringComparer.Ordinal);
        var screening = new HairpinScreeningEngine(ntthal);
        for (var targetIndex = 0; targetIndex < project.Targets.Count; targetIndex++)
        {
            token.ThrowIfCancellationRequested();
            var target = project.Targets[targetIndex];
            var gene = GeneFor(target, project.Reference);
            if (!groups.TryGetValue(gene, out var records)) groups.Add(gene, records = []);
            progress?.Invoke(targetIndex, project.Targets.Count, target.Id);
            var runIndex = project.Runs.FindLastIndex(r => r.Target.Id == target.Id && r.Target.Sha256 == target.Sha256);
            SmartTargetResult record;
            if (runIndex < 0)
                record = new(gene, target.Id, null, 0, 0, 0, 0, 0, project.Runs.Any(r => r.Target.Id == target.Id)
                    ? "当前模板已更新，尚无对应设计结果。" : "尚未设计。");
            else
            {
                var run = project.Runs[runIndex];
                var invalidations = new List<string>();
                foreach (var candidate in run.Candidates)
                {
                    var reasons = CandidateEvidenceOwnership.Invalidations(candidate, run, target, project.Reference, project.Databases);
                    if (reasons.Count > 0)
                    {
                        invalidations.AddRange(reasons);
                        CandidateQualityEngine.InvalidateCurrentEvidence(candidate, reasons);
                    }
                    else if (candidate.Assessment.Score.HasValue || candidate.Assessment.PolicyVersion == CandidateEvidenceOwnership.InvalidatedPolicy)
                        CandidateQualityEngine.Refresh(candidate, run, project.Parameters, project.Reference);
                }
                var rejected = run.Candidates.Count(c => !c.Assessment.Accepted);
                var unscored = run.Candidates.Count(c => c.Assessment.Accepted && !IsScored(c));
                var eligible = run.Candidates.Where(c => c.Assessment.Accepted && IsScored(c)).ToList();
                if (run.State is "失败" or "取消" || run.Candidates.Count == 0)
                    record = new(gene, target.Id, runIndex, run.Candidates.Count, rejected, unscored, 0, 0,
                        $"最近任务：{run.State}。{run.Message}");
                else if (eligible.Count == 0)
                    record = new(gene, target.Id, runIndex, run.Candidates.Count, rejected, unscored, 0, 0,
                        $"没有可用评分的合格候选：质量规则排除 {rejected} 对，未评分或评分无效 {unscored} 对。" +
                        string.Join("；", invalidations.Distinct(StringComparer.Ordinal)));
                else
                {
                    try
                    {
                        var parameters = run.UsedParameters ?? project.Parameters;
                        if (!CanReuse(run.HairpinScreening, eligible, parameters, settings, report.HairpinEngineSha256))
                            run.HairpinScreening = await screening.ScreenAsync(eligible, parameters, settings, token);
                        var screen = run.HairpinScreening!;
                        // Re-evaluate the decision from the cached physical evidence and current thresholds.
                        screen.Decisions = eligible.Select(c =>
                        {
                            var d = screen.DecisionFor(c) ?? throw new InvalidDataException("发卡筛选证据与候选不匹配。");
                            return HairpinScreeningEngine.Decide(c.Id, d.Forward, d.Reverse, settings);
                        }).ToList();
                        var retained = eligible.Where(c => screen.DecisionFor(c)?.Excluded == false).ToList();
                        viable.AddRange(retained.Select(c => new SmartExportChoice(gene, runIndex, run.Candidates.IndexOf(c))));
                        record = new(gene, target.Id, runIndex, run.Candidates.Count, rejected, unscored,
                            eligible.Count - retained.Count, retained.Count, retained.Count == 0 ? "合格且已评分的候选全部触发高风险发卡排除条件。" : "完成筛选。");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
                    {
                        record = new(gene, target.Id, runIndex, run.Candidates.Count, rejected, unscored, 0, 0,
                            "发卡核对失败，本靶标未参与导出：" + ex.Message);
                    }
                }
            }
            records.Add(record); report.Targets.Add(record);
        }
        var choices = new List<SmartExportChoice>();
        foreach (var (gene, records) in groups)
        {
            var best = viable.Where(v => v.Gene == gene)
                .OrderByDescending(v => Candidate(project, v).Assessment.Score)
                .ThenBy(v => Candidate(project, v).Rank).ThenBy(v => Candidate(project, v).Penalty)
                .ThenBy(v => project.Runs[v.RunIndex].Target.Id, StringComparer.Ordinal)
                .ThenBy(v => Candidate(project, v).Id, StringComparer.Ordinal).FirstOrDefault();
            if (best is null)
                report.Genes.Add(new(gene, "", "", null, null, "", "", null, "未导出", string.Join("；", records.Select(r => $"{r.Target}：{r.Reason}"))));
            else
            {
                choices.Add(best); var c = Candidate(project, best);
                report.Genes.Add(new(gene, c.TargetId, c.Id, c.Rank, c.Assessment.Score, c.Forward.Sequence,
                    c.Reverse.Sequence, c.ProductLength, "已选出", "最高综合评分；特异性与实验验证状态见质量证据。"));
            }
        }
        token.ThrowIfCancellationRequested();
        project.SmartExportReports.Add(report);
        progress?.Invoke(project.Targets.Count, project.Targets.Count, "完成");
        return new(project, choices, report) { ContentFingerprint = DesignFingerprint(project) };
    }
    public static PrimerCandidate Candidate(ProjectDocument project, SmartExportChoice choice) => project.Runs[choice.RunIndex].Candidates[choice.CandidateIndex];
    public static bool IsScored(PrimerCandidate c) => c.Assessment.Score is { } score && double.IsFinite(score) && score is >= 0 and <= 100;
    public static string GeneFor(SequenceTarget target, ReferenceWorkspace? reference)
    {
        var gene = target.GeneId.Trim();
        if (gene.Length == 0) gene = reference?.Find(target.Id)?.GeneId.Trim() ?? "";
        return gene.Length == 0 ? target.Id : gene;
    }
    private static bool CanReuse(HairpinScreeningReport? report, List<PrimerCandidate> candidates, DesignParameters parameters,
        HairpinScreenSettings settings, string engineHash) => report is not null && report.Settings == settings &&
        report.EngineSha256 == engineHash && report.DeltaGTemperatureC == 37 && candidates.All(c =>
        {
            var d = report.DecisionFor(c); var conditions = HairpinConditions.From(c.Provenance?.Parameters ?? parameters);
            return d is not null && d.Forward.Conditions == conditions && d.Reverse.Conditions == conditions;
        });
}
