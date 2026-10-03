using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace QpcrPrimerStudio.Core;

public sealed partial class Primer3Engine(string executable)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public async Task<TargetRun> DesignAsync(SequenceTarget target, DesignParameters p,
        CancellationToken token, string? forward = null, string? reverse = null,
        TargetRegionSettings? regions = null, ReferenceWorkspace? reference = null)
    {
        target.Validate();
        p = target.ParameterOverride ?? p;
        regions ??= new TargetRegionSettings();
        var plan = TargetRegionEngine.Plan(target, regions, reference);
        if (reference?.Find(target.Id) is { } annotation)
            target = target with { Junctions = annotation.Junctions, GeneId = annotation.GeneId, ExonBlocks = annotation.Exons,
                ReferenceGenomeSha256 = reference!.Assets.FirstOrDefault(a => a.Role == "Genome")?.Sha256 ?? "",
                ExpectedSubjects = target.ExpectedSubjects.Count == 0 && plan.RequiredTranscripts.Count > 0
                    ? plan.RequiredTranscripts : target.ExpectedSubjects };
        var used = p with { ExcludedRegions = p.ExcludedRegions.Concat(plan.Excluded).Distinct().ToList() };
        used.Validate(target.Sequence.Length);
        if (p.RequireJunction && target.Junctions.Count == 0)
            throw new ArgumentException($"{target.Id}：要求跨连接位点，但没有可用注释。");
        var input = CreateInput(target, used, forward, reverse);
        var output = await ProcessRunner.RunAsync(executable, ["--strict_tags", "--default_version=2"], input, token);
        var data = ReadRecord(output.Output);
        if (data.TryGetValue("PRIMER_ERROR", out var error) && !string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException(error);
        var run = new TargetRun { Target = target, RawInput = input, RawOutput = output.Output,
            EngineSha256 = Hashing.File(executable), State = "完成", UsedParameters = used, RegionSettings = regions, RegionEvidence = plan.Evidence };
        var engineVersion = await ProcessRunner.RunAsync(executable, ["--about"], null, token);
        var appVersion = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(Primer3Engine).Assembly)?.InformationalVersion ?? "unknown";
        var provenance = new DesignProvenance(appVersion, engineVersion.Output.Trim(), run.EngineSha256, target.Sha256,
            reference?.Identity ?? "", DateTimeOffset.Now, used, regions, Hashing.Directory(Path.Combine(Path.GetDirectoryName(executable)!, "primer3_config")));
        var count = Int(data, "PRIMER_PAIR_NUM_RETURNED");
        for (var i = 0; i < count; i++)
        {
            var left = ReadPrimer(data, $"PRIMER_LEFT_{i}", false);
            var right = ReadPrimer(data, $"PRIMER_RIGHT_{i}", true);
            var size = Int(data, $"PRIMER_PAIR_{i}_PRODUCT_SIZE");
            if (left.Start < 1 || right.End > target.Sequence.Length || right.End - left.Start + 1 != size)
                throw new InvalidDataException("引擎返回的扩增子坐标不一致。");
            if (target.Sequence.Substring(left.Start - 1, left.Sequence.Length) != left.Sequence ||
                SequenceFiles.ReverseComplement(target.Sequence.Substring(right.Start - 1, right.Sequence.Length)) != right.Sequence)
                throw new InvalidDataException("引物与模板结合坐标不一致。");
            var junctions = target.Junctions.Where(j =>
                CandidateQualityEngine.Spans(left, j, p, false) || CandidateQualityEngine.Spans(right, j, p, true)).ToList();
            run.Candidates.Add(new PrimerCandidate
            {
                TargetId = target.Id, Rank = i + 1, Forward = left, Reverse = right, Provenance = provenance,
                ProductLength = size, Amplicon = target.Sequence.Substring(left.Start - 1, size),
                Penalty = Number(data, $"PRIMER_PAIR_{i}_PENALTY"),
                PairAnyTm = Number(data, $"PRIMER_PAIR_{i}_COMPL_ANY_TH"),
                PairEndTm = Number(data, $"PRIMER_PAIR_{i}_COMPL_END_TH"),
                PairStructure = Structure(data, $"PRIMER_PAIR_{i}_COMPL_ANY_STUCT"),
                ConstraintWarnings = new[] { $"PRIMER_LEFT_{i}_PROBLEMS", $"PRIMER_RIGHT_{i}_PROBLEMS", $"PRIMER_PAIR_{i}_PROBLEMS" }
                    .Where(data.ContainsKey).Select(key => $"{key}：{data[key]}").ToList(),
                JunctionEvidence = target.Junctions.Count == 0 ? "外显子策略未检查" : junctions.Count == 0
                    ? "未跨连接位点" : $"跨连接位点：{string.Join(", ", junctions)}（cDNA 前一外显子末位）"
            });
            CandidateQualityEngine.Evaluate(run.Candidates[^1], target, used, regions, plan, reference);
        }
        run.RejectionCounts = run.Candidates.SelectMany(c => c.Assessment.Rejections).GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
        run.State = count == 0 ? "无候选" : run.AcceptedCount == 0 ? "候选全部被质量规则排除" : "完成";
        run.Message = string.Join(Environment.NewLine, data.Where(kv => kv.Key.EndsWith("_EXPLAIN") || kv.Key == "PRIMER_WARNING")
            .Select(kv => $"{kv.Key}：{kv.Value}")) + "\n\n" + string.Join("\n", plan.Evidence) +
            $"\nPrimer3 返回 {count} 对；质量保留 {run.AcceptedCount} 对；排除 {count - run.AcceptedCount} 对。\n" +
            "Primer3 EXPLAIN 是引擎内部搜索统计，质量排除计数以返回的成对候选为单位。\n" +
            string.Join("\n", run.RejectionCounts.Select(kv => $"{kv.Value} 对：{kv.Key}"));
        return run;
    }

    public static string CreateInput(SequenceTarget t, DesignParameters p, string? forward = null, string? reverse = null)
    {
        var values = new Dictionary<string, object>
        {
            ["SEQUENCE_ID"] = t.Id, ["SEQUENCE_TEMPLATE"] = t.Sequence,
            ["PRIMER_TASK"] = forward is not null && reverse is not null ? "check_primers" : "generic",
            ["PRIMER_FIRST_BASE_INDEX"] = 0, ["PRIMER_PICK_LEFT_PRIMER"] = 1,
            ["PRIMER_PICK_RIGHT_PRIMER"] = 1, ["PRIMER_PICK_INTERNAL_OLIGO"] = 0,
            ["PRIMER_NUM_RETURN"] = p.Count, ["PRIMER_MIN_SIZE"] = p.MinLength,
            ["PRIMER_OPT_SIZE"] = p.OptLength, ["PRIMER_MAX_SIZE"] = p.MaxLength,
            ["PRIMER_MIN_TM"] = p.MinTm, ["PRIMER_OPT_TM"] = p.OptTm, ["PRIMER_MAX_TM"] = p.MaxTm,
            ["PRIMER_MIN_GC"] = p.MinGc, ["PRIMER_MAX_GC"] = p.MaxGc,
            ["PRIMER_PAIR_MAX_DIFF_TM"] = p.MaxTmDifference,
            ["PRIMER_PRODUCT_SIZE_RANGE"] = $"{p.MinProduct}-{p.MaxProduct}",
            ["PRIMER_SALT_MONOVALENT"] = p.MonovalentMm, ["PRIMER_SALT_DIVALENT"] = p.MagnesiumMm,
            ["PRIMER_DNTP_CONC"] = p.DntpMm, ["PRIMER_DNA_CONC"] = p.PrimerNm,
            ["PRIMER_THERMODYNAMIC_OLIGO_ALIGNMENT"] = 1, ["PRIMER_THERMODYNAMIC_TEMPLATE_ALIGNMENT"] = 0,
            ["PRIMER_MAX_HAIRPIN_TH"] = p.MaxHairpinTm, ["PRIMER_MAX_SELF_ANY_TH"] = p.MaxSelfAnyTm,
            ["PRIMER_MAX_SELF_END_TH"] = p.MaxSelfEndTm, ["PRIMER_PAIR_MAX_COMPL_ANY_TH"] = p.MaxPairAnyTm,
            ["PRIMER_PAIR_MAX_COMPL_END_TH"] = p.MaxPairEndTm,
            ["PRIMER_MAX_POLY_X"] = p.MaxHomopolymer, ["PRIMER_GC_CLAMP"] = p.GcClamp,
            ["PRIMER_MAX_NS_ACCEPTED"] = 0, ["PRIMER_TM_FORMULA"] = 1,
            ["PRIMER_SALT_CORRECTIONS"] = 1, ["PRIMER_EXPLAIN_FLAG"] = 1,
            ["PRIMER_SECONDARY_STRUCTURE_ALIGNMENT"] = 1
        };
        if (p.IncludedLength > 0) values["SEQUENCE_INCLUDED_REGION"] = $"{p.IncludedStart - 1},{p.IncludedLength}";
        if (p.ForwardRegionLength > 0 || p.ReverseRegionLength > 0)
        {
            var left = p.ForwardRegionLength > 0 ? $"{p.ForwardRegionStart - 1},{p.ForwardRegionLength}" : ",";
            var right = p.ReverseRegionLength > 0 ? $"{p.ReverseRegionStart - 1},{p.ReverseRegionLength}" : ",";
            values["SEQUENCE_PRIMER_PAIR_OK_REGION_LIST"] = $"{left},{right}";
        }
        if (p.ExcludedRegions.Count > 0) values["SEQUENCE_EXCLUDED_REGION"] = string.Join(" ", p.ExcludedRegions.Select(r => $"{r.Start - 1},{r.Length}"));
        if (p.RequireJunction)
        {
            values["SEQUENCE_OVERLAP_JUNCTION_LIST"] = string.Join(" ", t.Junctions.Select(j => j - 1));
            values["PRIMER_MIN_5_PRIME_OVERLAP_OF_JUNCTION"] = p.JunctionOverlap5;
            values["PRIMER_MIN_3_PRIME_OVERLAP_OF_JUNCTION"] = p.JunctionOverlap3;
        }
        if (forward is not null) values["SEQUENCE_PRIMER"] = SequenceFiles.NormalizePrimer(forward);
        if (reverse is not null) values["SEQUENCE_PRIMER_REVCOMP"] = SequenceFiles.NormalizePrimer(reverse);
        if (forward is not null && reverse is not null)
        {
            values["PRIMER_PICK_ANYWAY"] = 1;
        }
        return string.Join("\n", values.Select(kv => $"{kv.Key}={Convert.ToString(kv.Value, Invariant)}")) + "\n=\n";
    }

    public static Dictionary<string, string> ReadRecord(string boulder)
    {
        // A single Primer3 record is key=value data; delegate parsing to Microsoft's INI provider.
        // Only the documented Boulder record terminator is removed. No multi-record input is accepted.
        var lines = boulder.Replace("\r\n", "\n").Split('\n');
        if (lines.Count(line => line == "=") != 1 || lines.SkipWhile(l => l != "=").Skip(1).Any(l => !string.IsNullOrWhiteSpace(l)))
            throw new InvalidDataException("预期一个完整 Primer3 记录。");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines.TakeWhile(l => l != "="))));
        var config = new ConfigurationBuilder().AddIniStream(stream).Build();
        return config.AsEnumerable().ToDictionary(kv => kv.Key, kv => kv.Value ?? "", StringComparer.Ordinal);
    }
    private static PrimerMetrics ReadPrimer(Dictionary<string, string> d, string key, bool reverse)
    {
        var coords = d[key].Split(',').Select(s => int.Parse(s, Invariant)).ToArray();
        var start = reverse ? coords[0] - coords[1] + 2 : coords[0] + 1;
        var end = reverse ? coords[0] + 1 : coords[0] + coords[1];
        return new(d[key + "_SEQUENCE"], start, end, Number(d, key + "_TM"), Number(d, key + "_GC_PERCENT"),
            Number(d, key + "_HAIRPIN_TH"), Number(d, key + "_SELF_ANY_TH"), Number(d, key + "_SELF_END_TH"),
            Structure(d, key + "_HAIRPIN_STUCT"), Structure(d, key + "_SELF_ANY_STUCT"));
    }
    private static int Int(Dictionary<string, string> d, string key) => int.Parse(d[key], Invariant);
    private static double Number(Dictionary<string, string> d, string key) => double.Parse(d[key], Invariant);
    private static string Structure(Dictionary<string, string> d, string key) => d.GetValueOrDefault(key, "").Replace("\\n", "\n");
}
