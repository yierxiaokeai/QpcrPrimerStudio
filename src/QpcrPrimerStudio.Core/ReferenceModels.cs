namespace QpcrPrimerStudio.Core;

public enum TargetRegionMode { EntireTranscript, CdsOnly, ThreePrimeUtr, FivePrimeUtr, PreferCds, AvoidUtr }
public enum ExpressionMode { TemplateOnly, TotalGene, IsoformSpecific, SelectedTranscripts }
public sealed record TargetRegionSettings
{
    public TargetRegionMode Mode { get; init; } = TargetRegionMode.PreferCds;
    public ExpressionMode Expression { get; init; } = ExpressionMode.TemplateOnly;
    public int AvoidFivePrime { get; init; } = 50;
    public int AvoidThreePrime { get; init; } = 50;
    public bool AvoidVariants { get; init; } = true;
    public int VariantTerminalBases { get; init; } = 5;
    public bool RejectTerminalVariants { get; init; } = true;
    public List<string> RequiredTranscripts { get; init; } = [];
    public Dictionary<string, double> ScoreWeights { get; init; } = [];
}
public sealed record ReferenceAsset(string Role, string Path, string Sha256);
public sealed record ExonBlock(string SeqId, long GenomicStart, long GenomicEnd, bool Plus, int Start, int End)
{
    public long GenomicPosition(int position) => Contains(position)
        ? checked(Plus ? GenomicStart + (position - Start) : GenomicEnd - (position - Start))
        : throw new ArgumentOutOfRangeException(nameof(position), "坐标不在 exon 内。");
    public bool Contains(int position) => position >= Start && position <= End;
}
public sealed record TranscriptAnnotation
{
    public string Id { get; init; } = "";
    public string GeneId { get; init; } = "";
    public string Sequence { get; init; } = "";
    public List<ExonBlock> Exons { get; init; } = [];
    public List<SequenceRegion> Cds { get; init; } = [];
    public List<int> Junctions => Exons.OrderBy(e => e.Start).SkipLast(1).Select(e => e.End).ToList();
    public ExonBlock? ExonAt(int coordinate) => Exons.FirstOrDefault(e => e.Contains(coordinate));
}
public sealed record VariantSite(string SeqId, long Position, long End, string Id, string Ref, string Alt, double? Frequency, string Filter);
public sealed record TranscriptVariant(int Start, int End, VariantSite Variant);
public sealed class ReferenceWorkspace
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTimeOffset ImportedAt { get; set; }
    public List<ReferenceAsset> Assets { get; set; } = [];
    public List<TranscriptAnnotation> Transcripts { get; set; } = [];
    public List<VariantSite> Variants { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public bool HasGenome => Assets.Any(a => a.Role == "Genome");
    public string Identity => Hashing.Text(Version + string.Join("|", Assets.Select(a => a.Role + a.Sha256)));
    public TranscriptAnnotation? Find(string id) => Transcripts.FirstOrDefault(t => t.Id == id);
    public List<TranscriptVariant> ProjectVariants(TranscriptAnnotation transcript)
    {
        var result = new List<TranscriptVariant>();
        foreach (var exon in transcript.Exons)
        foreach (var v in Variants.Where(v => v.SeqId == exon.SeqId && v.Position <= exon.GenomicEnd && v.End >= exon.GenomicStart))
        {
            var first = Math.Max(v.Position, exon.GenomicStart); var last = Math.Min(v.End, exon.GenomicEnd);
            var start = checked(exon.Start + (exon.Plus ? first - exon.GenomicStart : exon.GenomicEnd - last));
            var end = checked(exon.Start + (exon.Plus ? last - exon.GenomicStart : exon.GenomicEnd - first));
            if (start < exon.Start || end > exon.End || end < start)
                throw new InvalidDataException("参考 exon 基因组与 cDNA 坐标长度不对应。");
            result.Add(new(checked((int)start), checked((int)end), v));
        }
        return result;
    }
}
public sealed record ReferenceImportRequest(string Name, string Version, string Transcripts, string? Genome = null,
    string? Gff = null, string? Vcf = null, string? GeneMap = null, string? Cds = null, string? Protein = null);
public sealed record RegionPlan(List<SequenceRegion> Allowed, List<SequenceRegion> Excluded, List<int> UniqueJunctions,
    List<string> RequiredTranscripts, List<string> Evidence);
