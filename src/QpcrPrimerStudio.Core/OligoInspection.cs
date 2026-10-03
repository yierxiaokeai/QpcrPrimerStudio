namespace QpcrPrimerStudio.Core;

public sealed record OligoInspection(string Forward, string Reverse, DesignParameters Parameters,
    DateTimeOffset AnalyzedAt, string EngineSha256, string RawInput, string RawOutput, string Report)
{
    public string Summary => $"{AnalyzedAt:MM-dd HH:mm} · F {Forward.Length} / R {Reverse.Length} nt";
}

public sealed partial class Primer3Engine
{
    public async Task<OligoInspection> InspectOligosAsync(string forward, string reverse, DesignParameters parameters, CancellationToken token)
    {
        forward = string.IsNullOrWhiteSpace(forward) ? "" : SequenceFiles.NormalizePrimer(forward);
        reverse = string.IsNullOrWhiteSpace(reverse) ? "" : SequenceFiles.NormalizePrimer(reverse);
        if (forward.Length == 0 && reverse.Length == 0) throw new ArgumentException("请至少输入一条引物。");
        // check_primers officially supports oligos without a template. No binding coordinates or product are inferred.
        var p = parameters with { IncludedStart = 1, IncludedLength = 0, ForwardRegionStart = 1, ForwardRegionLength = 0,
            ReverseRegionStart = 1, ReverseRegionLength = 0, ExcludedRegions = [], RequireJunction = false };
        p.Validate(40);
        var tags = ReadRecord(CreateInput(new SequenceTarget { Id = "oligo-analysis" }, p,
            forward.Length == 0 ? null : forward, reverse.Length == 0 ? null : reverse));
        tags.Remove("SEQUENCE_TEMPLATE");
        tags["PRIMER_TASK"] = "check_primers"; tags["PRIMER_PICK_ANYWAY"] = "1";
        tags["PRIMER_PICK_LEFT_PRIMER"] = forward.Length == 0 ? "0" : "1";
        tags["PRIMER_PICK_RIGHT_PRIMER"] = reverse.Length == 0 ? "0" : "1";
        var input = string.Join("\n", tags.Select(kv => $"{kv.Key}={kv.Value}")) + "\n=\n";
        var process = await ProcessRunner.RunAsync(executable, ["--strict_tags", "--default_version=2"], input, token);
        var data = ReadRecord(process.Output);
        if (data.TryGetValue("PRIMER_ERROR", out var error) && !string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException(error);
        if (Int(data, "PRIMER_LEFT_NUM_RETURNED") != (forward.Length == 0 ? 0 : 1) ||
            Int(data, "PRIMER_RIGHT_NUM_RETURNED") != (reverse.Length == 0 ? 0 : 1))
            throw new InvalidDataException("引擎未返回所输入引物的分析指标。");
        var lines = new List<string> { "引物序列分析 · 无模板结合坐标或产物结论" };
        foreach (var (name, prefix, sequence) in new[] { ("Forward", "PRIMER_LEFT_0", forward), ("Reverse", "PRIMER_RIGHT_0", reverse) })
        {
            if (sequence.Length == 0) continue;
            lines.Add($"{name} 5′→3′ {sequence} · {sequence.Length} nt");
            lines.Add(FormattableString.Invariant($"Tm {Number(data, prefix + "_TM"):F2} °C；GC {Number(data, prefix + "_GC_PERCENT"):F1}%；发卡 Tm {Number(data, prefix + "_HAIRPIN_TH"):F2} °C"));
            lines.Add(FormattableString.Invariant($"自二聚体 ANY {Number(data, prefix + "_SELF_ANY_TH"):F2} °C；3′ END {Number(data, prefix + "_SELF_END_TH"):F2} °C"));
            if (data.TryGetValue(prefix + "_PROBLEMS", out var problems)) lines.Add("条件警告：" + problems);
        }
        return new(forward, reverse, parameters, DateTimeOffset.Now, Hashing.File(executable), input, process.Output,
            string.Join("\n", lines));
    }
}
