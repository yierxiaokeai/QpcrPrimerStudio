using System.Globalization;

namespace QpcrPrimerStudio.Core;

public sealed class SinglePrimerSearch
{
    public SequenceTarget Target { get; set; } = new();
    public bool Reverse { get; set; }
    public DesignParameters Parameters { get; set; } = new();
    public TargetRegionSettings Regions { get; set; } = new();
    public List<PrimerMetrics> Primers { get; set; } = [];
    public string RawInput { get; set; } = "";
    public string RawOutput { get; set; } = "";
    public string EngineSha256 { get; set; } = "";
    public DateTimeOffset SearchedAt { get; set; } = DateTimeOffset.Now;
    public string Summary => $"{Target.Id} · {(Reverse ? "Reverse" : "Forward")} · {Primers.Count} 条";
    public override string ToString() => Summary;
}

public sealed partial class Primer3Engine
{
    public async Task<SinglePrimerSearch> SearchSingleAsync(SequenceTarget target, DesignParameters parameters,
        bool reverse, CancellationToken token, TargetRegionSettings? regions = null, ReferenceWorkspace? reference = null)
    {
        target.Validate();
        var p = target.ParameterOverride ?? parameters;
        p.Validate(target.Sequence.Length);
        regions ??= new();
        if (p.RequireJunction)
            throw new ArgumentException("跨连接位点约束需使用成对设计；单引物搜索请关闭该条件。");
        var plan = TargetRegionEngine.Plan(target, regions, reference);
        var start = reverse ? p.ReverseRegionStart : p.ForwardRegionStart;
        var length = reverse ? p.ReverseRegionLength : p.ForwardRegionLength;
        var includedStart = p.IncludedLength > 0 ? p.IncludedStart : 1;
        var includedEnd = p.IncludedLength > 0 ? p.IncludedStart + p.IncludedLength - 1 : target.Sequence.Length;
        if (length > 0)
        {
            includedStart = Math.Max(includedStart, start);
            includedEnd = Math.Min(includedEnd, start + length - 1);
        }
        if (includedEnd < includedStart) throw new ArgumentException("单引物搜索区域与包含区域没有交集。");
        var used = p with { IncludedStart = includedStart, IncludedLength = includedEnd - includedStart + 1,
            ExcludedRegions = p.ExcludedRegions.Concat(plan.Excluded).Distinct().ToList() };
        used.Validate(target.Sequence.Length);
        var tags = ReadRecord(CreateInput(target, used));
        tags["PRIMER_PICK_LEFT_PRIMER"] = reverse ? "0" : "1";
        tags["PRIMER_PICK_RIGHT_PRIMER"] = reverse ? "1" : "0";
        tags.Remove("SEQUENCE_PRIMER_PAIR_OK_REGION_LIST");
        var input = string.Join("\n", tags.Select(kv => $"{kv.Key}={kv.Value}")) + "\n=\n";
        var process = await ProcessRunner.RunAsync(executable, ["--strict_tags", "--default_version=2"], input, token);
        var data = ReadRecord(process.Output);
        if (data.TryGetValue("PRIMER_ERROR", out var error) && !string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException(error);
        var result = new SinglePrimerSearch { Target = target, Reverse = reverse, Parameters = p, Regions = regions,
            RawInput = input, RawOutput = process.Output, EngineSha256 = Hashing.File(executable) };
        var prefix = reverse ? "PRIMER_RIGHT" : "PRIMER_LEFT";
        var count = Int(data, prefix + "_NUM_RETURNED");
        for (var i = 0; i < count; i++)
        {
            var primer = ReadPrimer(data, $"{prefix}_{i.ToString(CultureInfo.InvariantCulture)}", reverse);
            if (primer.Start < includedStart || primer.End > includedEnd || primer.End > target.Sequence.Length)
                throw new InvalidDataException("单引物返回坐标超出搜索区域。");
            var template = target.Sequence.Substring(primer.Start - 1, primer.Sequence.Length);
            if ((reverse ? SequenceFiles.ReverseComplement(template) : template) != primer.Sequence)
                throw new InvalidDataException("单引物序列与结合坐标不一致。");
            result.Primers.Add(primer);
        }
        return result;
    }
}
