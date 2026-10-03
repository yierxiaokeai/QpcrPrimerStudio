namespace QpcrPrimerStudio.Core;

public enum RecommendationSearchKind { Pairs, Sense, Antisense, Both, CompatibleWithSense, CompatibleWithAntisense }

public sealed record ParameterTrial(string Name, DesignParameters Parameters, int Returned, int Retained,
    string Evidence, string RawInput, string RawOutput)
{
    public string Summary => $"{Name}：返回 {Returned}，保留 {Retained}";
}

public sealed record ParameterRecommendation(string TargetId, string TemplateSha256, RecommendationSearchKind Kind,
    DateTimeOffset CreatedAt, string EngineSha256, TargetRegionSettings Regions, List<ParameterTrial> Trials,
    DesignParameters? Parameters, string Report);

public sealed class ParameterRecommendationEngine(string executable)
{
    public async Task<ParameterRecommendation> RecommendAsync(SequenceTarget target, DesignParameters current,
        RecommendationSearchKind kind, string forward, string reverse, CancellationToken token,
        TargetRegionSettings? regions = null, ReferenceWorkspace? reference = null)
    {
        token.ThrowIfCancellationRequested();
        target.Validate();
        target = target with { ParameterOverride = null };
        regions ??= new();
        var pairSearch = kind is RecommendationSearchKind.Pairs or RecommendationSearchKind.CompatibleWithSense or RecommendationSearchKind.CompatibleWithAntisense;
        if (pairSearch && target.Sequence.Length < 60)
            throw new ArgumentException("当前模板不足 60 nt，无法推荐本工具的 qPCR 产物范围。请提供更完整的转录本或 cDNA 序列。");
        var fixedForward = kind == RecommendationSearchKind.CompatibleWithSense ? SequenceFiles.NormalizePrimer(forward) : null;
        var fixedReverse = kind == RecommendationSearchKind.CompatibleWithAntisense ? SequenceFiles.NormalizePrimer(reverse) : null;
        var standard = current with { MinLength = 18, OptLength = 20, MaxLength = 24,
            MinTm = 58, OptTm = 60, MaxTm = 62, MaxTmDifference = 2, MinGc = 40, MaxGc = 60,
            MinProduct = pairSearch ? (target.Sequence.Length < 80 ? 60 : 80) : current.MinProduct,
            MaxProduct = pairSearch ? Math.Min(200, target.Sequence.Length) : current.MaxProduct, Count = 10 };
        var profiles = new[] { ("常规 qPCR", standard),
            ("扩大引物长度", standard with { OptLength = 22, MaxLength = 30 }),
            ("扩大 GC 搜索范围", standard with { OptLength = 22, MaxLength = 30, MinGc = 30, MaxGc = 70 }) };
        var trials = new List<ParameterTrial>();
        DesignParameters? recommended = null;
        var engine = new Primer3Engine(executable);
        foreach (var (name, parameters) in profiles)
        {
            token.ThrowIfCancellationRequested();
            parameters.Validate(target.Sequence.Length);
            ParameterTrial trial;
            if (pairSearch)
            {
                var run = await engine.DesignAsync(target, parameters, token, fixedForward, fixedReverse, regions, reference);
                trial = new(name, parameters, run.Candidates.Count, run.AcceptedCount, run.Message, run.RawInput, run.RawOutput);
            }
            else
            {
                var directions = kind == RecommendationSearchKind.Both ? new[] { false, true } : new[] { kind == RecommendationSearchKind.Antisense };
                var searches = new List<SinglePrimerSearch>();
                foreach (var direction in directions)
                    searches.Add(await engine.SearchSingleAsync(target, parameters, direction, token, regions, reference));
                var retained = searches.Min(s => s.Primers.Count);
                trial = new(name, parameters, searches.Sum(s => s.Primers.Count), retained,
                    string.Join("\n", searches.Select(s => s.Summary)),
                    string.Join("\n", searches.Select(s => s.RawInput)), string.Join("\n", searches.Select(s => s.RawOutput)));
            }
            trials.Add(trial);
            if (trial.Retained > 0) { recommended = parameters; break; }
        }
        var knownBases = target.Sequence.Count(c => "ACGT".Contains(c));
        var gc = knownBases == 0 ? "不可计算" : FormattableString.Invariant($"{100d * target.Sequence.Count(c => c is 'G' or 'C') / knownBases:F1}%");
        var lines = new List<string> { $"模板 {target.Sequence.Length:N0} nt；明确碱基 GC {gc}；歧义碱基 {target.Sequence.Length - knownBases:N0} 个。" };
        lines.AddRange(trials.Select(t => t.Summary));
        if (recommended is null)
        {
            lines.Add("三套参数均未找到可保留候选，当前参数保持不变。请核对可设计区域、连接位点、固定引物或模板质量。");
            lines.Add(trials[^1].Evidence);
        }
        else
        {
            lines.Add($"建议采用“{trials[^1].Name}”：引物 {recommended.MinLength}–{recommended.MaxLength} nt，最佳 {recommended.OptLength} nt；" +
                $"Tm {recommended.MinTm}–{recommended.MaxTm} °C，最佳 {recommended.OptTm} °C；GC {recommended.MinGc}–{recommended.MaxGc}%。");
            if (pairSearch) lines.Add($"产物 {recommended.MinProduct}–{recommended.MaxProduct} bp；F/R Tm 差 ≤ {recommended.MaxTmDifference} °C。试算保留 {trials[^1].Retained} 对。");
            else lines.Add("单引物试算已通过引擎条件；成对产物和特异性需继续检查。Both 要求两个方向都有候选。");
            if (trials.Count > 1) lines.Add("推荐扩大了搜索范围，请重点检查候选的结构、特异性及实验表现。");
            lines.Add("反应浓度、结构限制、区域策略和连接位点要求沿用当前设置。点击“应用推荐参数”后可继续编辑，再开始正式搜索。");
        }
        lines.Add("试算未进行数据库特异性检查；正式设计后需检查特异性并完成实验验证。");
        return new(target.Id, target.Sha256, kind, DateTimeOffset.Now, Hashing.File(executable), regions, trials, recommended, string.Join("\n", lines));
    }
}

public static class SearchParameterSharing
{
    public static DesignParameters CopySearchSettings(DesignParameters destination, DesignParameters source) => destination with
    {
        MinLength = source.MinLength, OptLength = source.OptLength, MaxLength = source.MaxLength,
        MinTm = source.MinTm, OptTm = source.OptTm, MaxTm = source.MaxTm,
        MinGc = source.MinGc, MaxGc = source.MaxGc, MaxTmDifference = source.MaxTmDifference,
        MinProduct = source.MinProduct, MaxProduct = source.MaxProduct, Count = source.Count
    };
    public static DesignParameters WholeTemplate(DesignParameters source) => source with
    {
        IncludedStart = 1, IncludedLength = 0, ExcludedRegions = [],
        ForwardRegionStart = 1, ForwardRegionLength = 0, ReverseRegionStart = 1, ReverseRegionLength = 0
    };
}
