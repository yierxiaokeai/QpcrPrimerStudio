namespace QpcrPrimerStudio.Core;

public sealed record ScoreComponent(string Metric, double Weight, double? Quality, string Evidence);
public sealed record VariantImpact(string Primer, int PositionFromThreePrime, TranscriptVariant Variant, bool Rejected);
public sealed record DimerComplementarity(string Type, int LongestStretch, int ThreePrimeBases, string Risk);
public sealed record AmpliconQuality(double Gc, double MinWindowGc, double MaxWindowGc, int WindowSize,
    int LongestHomopolymer, List<SequenceRegion> LowComplexity, string SecondaryStructureStatus);
public sealed record GdnaAssessment(string Level, string Evidence, long? GenomicProduct, long? IntronicBases)
{
    public string Display => Level switch { "A" => "★★★★★ Junction", "B" => "★★★★☆ Large intron", "C" => "★★☆☆☆ Limited discrimination", _ => "Unknown" };
}
public sealed class CandidateAssessment
{
    public string PolicyVersion { get; set; } = "qPCR-heuristic-2";
    public string EvidenceFingerprint { get; set; } = "";
    public bool Accepted => Rejections.Count == 0;
    public double? Score { get; set; }
    public double EvidenceCoverage { get; set; }
    public List<string> Rejections { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<ScoreComponent> Components { get; set; } = [];
    public List<string> CoveredTranscripts { get; set; } = [];
    public List<string> MissingTranscripts { get; set; } = [];
    public List<VariantImpact> Variants { get; set; } = [];
    public List<DimerComplementarity> Dimers { get; set; } = [];
    public AmpliconQuality? Amplicon { get; set; }
    public GdnaAssessment Gdna { get; set; } = new("Unknown", "参考基因组/外显子注释不可用", null, null);
    public string Summary => $"{(Accepted ? "保留" : "排除")} · {(Score.HasValue ? Score.Value.ToString("F1") : "未评分")} / 100 · 已评证据 {EvidenceCoverage:F0}%";
    public string Details => Summary + "\n" + string.Join("\n", Rejections.Select(r => "排除：" + r)) + "\n" +
        string.Join("\n", Components.Select(c => $"{c.Metric}：{(c.Quality.HasValue ? c.Quality.Value.ToString("F2") : "未检查")}；权重 {c.Weight}；{c.Evidence}")) +
        "\ngDNA：" + Gdna.Display + " · " + Gdna.Evidence +
        $"\n目标覆盖：{CoveredTranscripts.Count}/{CoveredTranscripts.Count + MissingTranscripts.Count}（0/0 表示未指定多转录本目标）" +
        "\n缺失转录本：" + string.Join(",", MissingTranscripts) +
        (Amplicon is null ? "" : $"\nAmplicon：GC {Amplicon.Gc:F1}%；{Amplicon.WindowSize} nt 窗口 GC {Amplicon.MinWindowGc:F1}–{Amplicon.MaxWindowGc:F1}%；最长 homopolymer {Amplicon.LongestHomopolymer} nt；简单重复窗口 {Amplicon.LowComplexity.Count}；二级结构：{Amplicon.SecondaryStructureStatus}") +
        "\n" + string.Join("\n", Dimers.Select(d => $"{d.Type}：最长连续互补 {d.LongestStretch} nt；3′互补 {d.ThreePrimeBases} nt；{d.Risk}")) +
        "\n" + string.Join("\n", Variants.Select(v => $"{v.Primer} 3′第 {v.PositionFromThreePrime} 位：{v.Variant.Variant.SeqId}:{v.Variant.Variant.Position} {v.Variant.Variant.Ref}>{v.Variant.Variant.Alt} AF={v.Variant.Variant.Frequency?.ToString() ?? "Unknown"} FILTER={v.Variant.Variant.Filter}")) +
        "\n" + string.Join("\n", Warnings);
}
public sealed record DesignProvenance(string AppVersion, string EngineVersion, string EngineSha256, string TemplateSha256,
    string ReferenceIdentity, DateTimeOffset DesignedAt, DesignParameters Parameters, TargetRegionSettings Regions, string ThermodynamicConfigSha256 = "");
public sealed record AssayRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string CandidateId { get; init; } = "";
    public string Status { get; init; } = "Designed";
    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.Now;
    public string SampleMaterial { get; init; } = "";
    public string Instrument { get; init; } = "";
    public string ReactionConditions { get; init; } = "";
    public string Efficiency { get; init; } = "";
    public string RSquared { get; init; } = "";
    public string MeltCurve { get; init; } = "";
    public string Gel { get; init; } = "";
    public string Ntc { get; init; } = "";
    public string NoRt { get; init; } = "";
    public string ReferenceGene { get; init; } = "";
    public string BiologicalReplicates { get; init; } = "";
    public string TechnicalReplicates { get; init; } = "";
    public string Notes { get; init; } = "";
}
