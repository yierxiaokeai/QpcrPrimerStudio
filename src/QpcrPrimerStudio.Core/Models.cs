using System.Security.Cryptography;
using System.Text;

namespace QpcrPrimerStudio.Core;

public sealed record SequenceTarget
{
    public string Id { get; init; } = "target";
    public string Sequence { get; init; } = "";
    public string Source { get; init; } = "粘贴序列";
    public string GeneId { get; init; } = "";
    public List<int> Junctions { get; init; } = [];
    public List<string> ExpectedSubjects { get; init; } = [];
    public List<string> CoverageTranscripts { get; init; } = [];
    public DesignParameters? ParameterOverride { get; init; }
    public List<ExonBlock> ExonBlocks { get; init; } = [];
    public string ReferenceGenomeSha256 { get; init; } = "";
    public string Summary => $"{Id} · {Sequence.Length:N0} nt";
    public string Sha256 => Hashing.Text(Sequence);
    public override string ToString() => Summary;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.IndexOfAny(['\r', '\n', '=']) >= 0)
            throw new ArgumentException("靶标名称不能为空或包含换行与等号。");
        if (Sequence.Length < 40 || Sequence.Any(c => !"ACGTRYSWKMBDHVN".Contains(c)))
            throw new ArgumentException($"{Id}：序列至少 40 nt，只允许 DNA IUPAC 字母。");
        if (Junctions.Any(j => j <= 0 || j >= Sequence.Length))
            throw new ArgumentException("连接位点需填写前一外显子在 cDNA 上的末位坐标（1-based）。");
    }
}

public sealed record DesignParameters
{
    public int MinLength { get; init; } = 18;
    public int OptLength { get; init; } = 20;
    public int MaxLength { get; init; } = 24;
    public double MinTm { get; init; } = 58;
    public double OptTm { get; init; } = 60;
    public double MaxTm { get; init; } = 62;
    public double MinGc { get; init; } = 40;
    public double MaxGc { get; init; } = 60;
    public double MaxTmDifference { get; init; } = 2;
    public int MinProduct { get; init; } = 80;
    public int MaxProduct { get; init; } = 200;
    public int Count { get; init; } = 10;
    public double MonovalentMm { get; init; } = 50;
    public double MagnesiumMm { get; init; } = 1.5;
    public double DntpMm { get; init; } = 0.6;
    public double PrimerNm { get; init; } = 250;
    public double MaxHairpinTm { get; init; } = 47;
    public double MaxSelfAnyTm { get; init; } = 47;
    public double MaxSelfEndTm { get; init; } = 35;
    public double MaxPairAnyTm { get; init; } = 47;
    public double MaxPairEndTm { get; init; } = 35;
    public int MaxHomopolymer { get; init; } = 4;
    public int GcClamp { get; init; }
    public int IncludedStart { get; init; } = 1;
    public int IncludedLength { get; init; }
    public int ForwardRegionStart { get; init; } = 1;
    public int ForwardRegionLength { get; init; }
    public int ReverseRegionStart { get; init; } = 1;
    public int ReverseRegionLength { get; init; }
    public List<SequenceRegion> ExcludedRegions { get; init; } = [];
    public bool RequireJunction { get; init; }
    public int JunctionOverlap5 { get; init; } = 7;
    public int JunctionOverlap3 { get; init; } = 4;
    public void Validate(int sequenceLength)
    {
        if (!(MinLength >= 15 && MinLength <= OptLength && OptLength <= MaxLength && MaxLength <= 36))
            throw new ArgumentException("引物长度需满足 15 ≤ 最小 ≤ 最佳 ≤ 最大 ≤ 36。");
        if (!(MinTm <= OptTm && OptTm <= MaxTm && MinTm >= 20 && MaxTm <= 90))
            throw new ArgumentException("Tm 范围或最佳值无效。");
        if (!(MinGc >= 0 && MinGc <= MaxGc && MaxGc <= 100) || MaxTmDifference < 0)
            throw new ArgumentException("GC 或 Tm 差范围无效。");
        if (MinProduct < 40 || MaxProduct < MinProduct || Count < 1 || Count > 100)
            throw new ArgumentException("产物范围无效；候选数量须为 1–100。");
        double[] values = [MinTm, OptTm, MaxTm, MinGc, MaxGc, MaxTmDifference,
            MonovalentMm, MagnesiumMm, DntpMm, PrimerNm, MaxHairpinTm,
            MaxSelfAnyTm, MaxSelfEndTm, MaxPairAnyTm, MaxPairEndTm];
        if (values.Any(v => !double.IsFinite(v)) || MonovalentMm < 0 || MagnesiumMm < 0 || DntpMm < 0 || PrimerNm <= 0)
            throw new ArgumentException("反应浓度及热力学设置须为有效数值。");
        if (IncludedStart < 1 || IncludedStart > sequenceLength || IncludedLength < 0 ||
            (IncludedLength > 0 && IncludedStart - 1 + IncludedLength > sequenceLength))
            throw new ArgumentException("包含区域超出模板范围。");
        if (ExcludedRegions.Any(r => r.Start < 1 || r.Length < 1 || r.Start - 1 + r.Length > sequenceLength))
            throw new ArgumentException("排除区域超出模板范围。");
        foreach (var region in new[] { (ForwardRegionStart, ForwardRegionLength), (ReverseRegionStart, ReverseRegionLength) })
            if (region.Item1 < 1 || region.Item1 > sequenceLength || region.Item2 < 0 ||
                region.Item2 > sequenceLength - region.Item1 + 1)
                throw new ArgumentException("F/R 搜索区域超出模板范围；长度 0 表示不限。");
        if (MaxHomopolymer < 1 || GcClamp < 0 || GcClamp > MinLength || JunctionOverlap3 < 1 || JunctionOverlap5 < 1)
            throw new ArgumentException("连续相同碱基、GC clamp 或连接位点设置无效。");
    }
}

public sealed record SequenceRegion(int Start, int Length);
public sealed record PrimerMetrics(string Sequence, int Start, int End, double Tm, double Gc,
    double HairpinTm, double SelfAnyTm, double SelfEndTm, string HairpinStructure, string SelfStructure);

public sealed class PrimerCandidate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TargetId { get; set; } = "";
    public int Rank { get; set; }
    public PrimerMetrics Forward { get; set; } = null!;
    public PrimerMetrics Reverse { get; set; } = null!;
    public double Penalty { get; set; }
    public int ProductLength { get; set; }
    public string Amplicon { get; set; } = "";
    public double PairAnyTm { get; set; }
    public double PairEndTm { get; set; }
    public string PairStructure { get; set; } = "";
    public string JunctionEvidence { get; set; } = "未提供外显子连接位点";
    public List<SpecificityReport> Specificity { get; set; } = [];
    public string Notes { get; set; } = "";
    public bool Selected { get; set; }
    public CandidateAssessment Assessment { get; set; } = new();
    public DesignProvenance? Provenance { get; set; }
    public bool Locked { get; set; }
    public string? ParentCandidateId { get; set; }
    public int Revision { get; set; } = 1;
    public string ThermodynamicEvidence { get; set; } = "";
    public List<string> ConstraintWarnings { get; set; } = [];
    public string SpecificitySummary => Specificity.Count == 0 ? "未检查" :
        string.Join("；", Specificity.Select(s => $"{s.Kind}：{s.Status}"));
}

public sealed class TargetRun
{
    public SequenceTarget Target { get; set; } = new();
    public string State { get; set; } = "等待";
    public string Message { get; set; } = "";
    public string RawInput { get; set; } = "";
    public string RawOutput { get; set; } = "";
    public string EngineSha256 { get; set; } = "";
    public List<PrimerCandidate> Candidates { get; set; } = [];
    public DesignParameters? UsedParameters { get; set; }
    public TargetRegionSettings RegionSettings { get; set; } = new();
    public List<string> RegionEvidence { get; set; } = [];
    public Dictionary<string, int> RejectionCounts { get; set; } = [];
    public HairpinScreeningReport? HairpinScreening { get; set; }
    public int AcceptedCount => Candidates.Count(c => c.Assessment.Accepted);
    public override string ToString() => $"{Target.Id} · {State} · {Candidates.Count} 对";
}

public sealed class ProjectDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "qPCR 项目";
    public DateTimeOffset SavedAt { get; set; }
    public DesignParameters Parameters { get; set; } = new();
    public List<SequenceTarget> Targets { get; set; } = [];
    public List<TargetRun> Runs { get; set; } = [];
    public List<ExperimentRecord> Experiments { get; set; } = [];
    public ReferenceWorkspace? Reference { get; set; }
    public TargetRegionSettings RegionSettings { get; set; } = new();
    public List<AssayRecord> Assays { get; set; } = [];
    public List<DatabaseManifest> Databases { get; set; } = [];
    public bool AutoSpecificity { get; set; } = true;
    public List<SinglePrimerSearch> SinglePrimerSearches { get; set; } = [];
    public List<OligoInspection> OligoInspections { get; set; } = [];
    public HairpinScreenSettings HairpinSettings { get; set; } = new();
    public TemplateEditorDraft? EditorDraft { get; set; }
    public TemplateEditorDraft? UnassignedDraft { get; set; }
    public Dictionary<string, TemplateEditorDraft> TargetDrafts { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public string? LoadedFilePath { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string? LoadedFileSha256 { get; set; }
    public List<ParameterRecommendation> ParameterRecommendations { get; set; } = [];
    public bool UseSharedSearchParameters { get; set; }
    public List<SmartExportReport> SmartExportReports { get; set; } = [];
}

public sealed record TemplateEditorDraft(string? SelectedTargetId, string TargetName, string TemplateText,
    string JunctionText, string ExpectedText)
{
    public string CoverageText { get; init; } = "";
    public string ManualForward { get; init; } = "";
    public string ManualReverse { get; init; } = "";
    public string? ParentCandidateId { get; init; }
    public string? EditTemplateSha256 { get; init; }
    public bool PrimerDraftModified { get; init; }
    public int InputTabIndex { get; init; }
    public string SearchKind { get; init; } = "Pairs";
}

public sealed record ExperimentRecord(string CandidateId, DateTimeOffset RecordedAt, string MeltCurve,
    string ProductConfirmation, string StandardCurve, string Efficiency, string Ntc, string NoRt, string Notes);
public sealed record DatabaseManifest(string Name, string Kind, string Prefix, string SourceSha256,
    string SourcePath, int SequenceCount, DateTimeOffset CreatedAt, string ToolVersion);
public sealed record SpecificitySettings(int MaxMismatches = 3, int MaxThreePrimeMismatches = 1,
    int ThreePrimeBases = 5, int MaxProduct = 5000, int MaxSubjects = 10000);
public sealed record BindingSite(string QueryId, string SubjectId, long Start, long End, bool Plus,
    int Mismatches, int ThreePrimeMismatches)
{
    public List<MismatchEvidence> MismatchPositions { get; init; } = [];
    public int IndelBases { get; init; }
}
public sealed record MismatchEvidence(int Position1, int PositionFromThreePrime, char PrimerBase, char SubjectBase, double Weight);
public sealed record PotentialProduct(string SubjectId, long Start, long End, string FirstPrimer,
    string SecondPrimer, int Mismatches, bool Expected)
{
    public string Risk { get; init; } = "未评分";
    public BindingSite? ForwardSite { get; init; }
    public BindingSite? ReverseSite { get; init; }
}
public sealed class SpecificityReport
{
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "未检查";
    public string Evidence { get; set; } = "";
    public DatabaseManifest Database { get; set; } = null!;
    public SpecificitySettings Settings { get; set; } = new();
    public List<BindingSite> Sites { get; set; } = [];
    public List<PotentialProduct> Products { get; set; } = [];
    public string RawXml { get; set; } = "";
    public string QueryPairSha256 { get; set; } = "";
}

public static class Hashing
{
    public static string Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string File(string path) { using var stream = System.IO.File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    public static string Directory(string path) => Text(string.Join("|", System.IO.Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .OrderBy(p => Path.GetRelativePath(path, p), StringComparer.Ordinal).Select(p => Path.GetRelativePath(path, p) + ":" + File(p))));
}
