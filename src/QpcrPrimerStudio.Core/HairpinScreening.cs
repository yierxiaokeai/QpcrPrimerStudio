using System.Globalization;
using System.Text.RegularExpressions;

namespace QpcrPrimerStudio.Core;

public sealed record HairpinScreenSettings(double AnnealingC = 60, double TerminalMarginC = 5,
    double TerminalDeltaGKcal = -2, bool CheckTerminalHairpins = true)
{
    public void Validate()
    {
        if (!double.IsFinite(AnnealingC) || AnnealingC is < 20 or > 90 ||
            !double.IsFinite(TerminalMarginC) || TerminalMarginC is < 0 or > 20 ||
            !double.IsFinite(TerminalDeltaGKcal) || TerminalDeltaGKcal is < -20 or > 0)
            throw new ArgumentException("退火温度须为 20–90 °C；3′温差须为 0–20 °C；ΔG 阈值须为 −20 至 0 kcal/mol。");
    }
}
public sealed record HairpinConditions(double MonovalentMm, double MagnesiumMm, double DntpMm, double PrimerNm)
{
    public static HairpinConditions From(DesignParameters p) => new(p.MonovalentMm, p.MagnesiumMm, p.DntpMm, p.PrimerNm);
}
public sealed record HairpinEvidence(string Sequence, HairpinConditions Conditions, bool HasStructure,
    double? TmC, double? DeltaGKcal, bool ThreePrimePaired, string PairingPattern, string RawOutput);
public sealed record HairpinDecision(string CandidateId, HairpinEvidence Forward, HairpinEvidence Reverse,
    bool Excluded, List<string> Reasons);
public sealed class HairpinScreeningReport
{
    public HairpinScreenSettings Settings { get; set; } = new();
    public DesignParameters Parameters { get; set; } = new();
    public DateTimeOffset AnalyzedAt { get; set; } = DateTimeOffset.Now;
    public string EngineSha256 { get; set; } = "";
    public double DeltaGTemperatureC { get; set; } = 37;
    public List<HairpinDecision> Decisions { get; set; } = [];
    public HairpinDecision? DecisionFor(PrimerCandidate candidate)
    {
        var conditions = HairpinConditions.From(candidate.Provenance?.Parameters ?? Parameters);
        return Decisions.FirstOrDefault(d => d.CandidateId == candidate.Id && d.Forward.Sequence == candidate.Forward.Sequence &&
            d.Reverse.Sequence == candidate.Reverse.Sequence && d.Forward.Conditions == conditions && d.Reverse.Conditions == conditions);
    }
}

public sealed partial class HairpinScreeningEngine(string executable)
{
    public async Task<HairpinScreeningReport> ScreenAsync(IEnumerable<PrimerCandidate> candidates, DesignParameters parameters,
        HairpinScreenSettings settings, CancellationToken token)
    {
        settings.Validate();
        var report = new HairpinScreeningReport { Settings = settings, Parameters = parameters, EngineSha256 = Hashing.File(executable) };
        var cache = new Dictionary<(string Sequence, HairpinConditions Conditions), HairpinEvidence>();
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            var conditions = HairpinConditions.From(candidate.Provenance?.Parameters ?? parameters);
            async Task<HairpinEvidence> Inspect(string sequence)
            {
                var key = (sequence, conditions);
                if (!cache.TryGetValue(key, out var evidence))
                {
                    evidence = await InspectAsync(sequence, conditions, token); cache.Add(key, evidence);
                }
                return evidence;
            }
            var forward = await Inspect(candidate.Forward.Sequence); var reverse = await Inspect(candidate.Reverse.Sequence);
            report.Decisions.Add(Decide(candidate.Id, forward, reverse, settings));
        }
        if (report.Decisions.Count == 0) throw new ArgumentException("请先生成候选引物对。");
        return report;
    }
    public async Task<HairpinEvidence> InspectAsync(string sequence, HairpinConditions conditions, CancellationToken token)
    {
        sequence = SequenceFiles.NormalizePrimer(sequence);
        if (new[] { conditions.MonovalentMm, conditions.MagnesiumMm, conditions.DntpMm, conditions.PrimerNm }.Any(x => !double.IsFinite(x)) ||
            conditions.MonovalentMm < 0 || conditions.MagnesiumMm < 0 || conditions.DntpMm < 0 || conditions.PrimerNm <= 0)
            throw new ArgumentException("发卡计算的盐、Mg²⁺、dNTP 与引物浓度无效。");
        static string N(double value) => value.ToString(CultureInfo.InvariantCulture);
        var output = await ProcessRunner.RunAsync(executable, ["-a", "HAIRPIN", "-s1", sequence,
            "-mv", N(conditions.MonovalentMm), "-dv", N(conditions.MagnesiumMm), "-n", N(conditions.DntpMm),
            "-d", N(conditions.PrimerNm), "-t", "37"], null, token);
        var raw = output.Output + output.Error;
        if (raw.Contains("No secondary structure could be calculated", StringComparison.Ordinal))
            return new(sequence, conditions, false, null, null, false, "", raw);
        var energy = Energy().Match(raw); var temperature = Temperature().Match(raw);
        var pattern = Pattern().Match(raw); var bases = Bases().Match(raw);
        if (!energy.Success || !temperature.Success || !pattern.Success || !bases.Success || bases.Groups[1].Value != sequence ||
            pattern.Groups[1].Length != sequence.Length)
            throw new InvalidDataException("发卡引擎输出不完整，无法核对结构与 3′端配对；本次筛选未应用。");
        var dg = double.Parse(energy.Groups[1].Value, CultureInfo.InvariantCulture) / 1000;
        var tm = double.Parse(temperature.Groups[1].Value, CultureInfo.InvariantCulture);
        var pairing = pattern.Groups[1].Value;
        if (!double.IsFinite(dg) || !double.IsFinite(tm) || pairing.Count(c => c == '/') != pairing.Count(c => c == '\\'))
            throw new InvalidDataException("发卡引擎返回的热力学或结构数据无效；本次筛选未应用。");
        return new(sequence, conditions, true, tm, dg, pairing[^1] is '/' or '\\', pairing, raw);
    }
    public static HairpinDecision Decide(string id, HairpinEvidence forward, HairpinEvidence reverse, HairpinScreenSettings settings)
    {
        settings.Validate(); var reasons = new List<string>();
        foreach (var (name, evidence) in new[] { ("F", forward), ("R", reverse) })
        {
            if (!evidence.HasStructure) continue;
            if (evidence.TmC is not { } tm || evidence.DeltaGKcal is not { } dg || !double.IsFinite(tm) || !double.IsFinite(dg))
                throw new InvalidDataException("发卡证据缺少有效 Tm 或 ΔG，筛选未应用。");
            if (tm >= settings.AnnealingC)
                reasons.Add($"{name}：发卡 Tm {tm:F1} °C ≥ 退火温度 {settings.AnnealingC:F1} °C");
            else if (settings.CheckTerminalHairpins && evidence.ThreePrimePaired && tm >= settings.AnnealingC - settings.TerminalMarginC && dg < settings.TerminalDeltaGKcal)
                reasons.Add($"{name}：3′末端碱基配对；发卡 Tm {tm:F1} °C 位于退火温度下方 {settings.TerminalMarginC:F1} °C 窗口；ΔG₃₇ {dg:F2} < {settings.TerminalDeltaGKcal:F2} kcal/mol");
        }
        return new(id, forward, reverse, reasons.Count > 0, reasons);
    }
    [GeneratedRegex(@"\bdG\s*=\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex Energy();
    [GeneratedRegex(@"\bt\s*=\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex Temperature();
    [GeneratedRegex(@"^SEQ\t([-/\\]+)\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
    [GeneratedRegex(@"^STR\t([ACGT]+)\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Bases();
}
