using CommunityToolkit.Mvvm.ComponentModel;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public enum PrimerSearchKind { Pairs, Sense, Antisense, Both, CompatibleWithSense, CompatibleWithAntisense }
public sealed record SearchChoice(PrimerSearchKind Kind, string Label);
public sealed record SearchRequest(PrimerSearchKind Kind, DesignParameters Parameters, string Forward, string Reverse)
{
    public bool ApplyToWholeProject { get; init; }
    public bool ClearCustomRegions { get; init; }
    public ParameterRecommendation? Recommendation { get; init; }
}

public partial class SearchCriteriaModel : ObservableObject
{
    public SearchChoice[] Choices { get; } = [new(PrimerSearchKind.Pairs, "Pairs · 引物对"),
        new(PrimerSearchKind.Sense, "Sense · 正向引物"), new(PrimerSearchKind.Antisense, "Anti-sense · 反向引物"),
        new(PrimerSearchKind.Both, "Both · 正反向独立列表"), new(PrimerSearchKind.CompatibleWithSense, "Compatible with Sense · 固定 F 找 R"),
        new(PrimerSearchKind.CompatibleWithAntisense, "Compatible with Anti-sense · 固定 R 找 F")];
    private DesignParameters original;
    [ObservableProperty] private bool applyToWholeProject;
    [ObservableProperty] private bool clearCustomRegions;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit))] private bool isRecommending;
    public bool CanEdit => !IsRecommending;
    [ObservableProperty] private bool advancedMode;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPairSearch), nameof(UsesForwardRange), nameof(UsesReverseRange), nameof(NeedsFixedForward), nameof(NeedsFixedReverse))] private PrimerSearchKind kind;
    public bool IsPairSearch => Kind is PrimerSearchKind.Pairs or PrimerSearchKind.CompatibleWithSense or PrimerSearchKind.CompatibleWithAntisense;
    public bool UsesForwardRange => Kind is not (PrimerSearchKind.Antisense or PrimerSearchKind.CompatibleWithSense);
    public bool UsesReverseRange => Kind is not (PrimerSearchKind.Sense or PrimerSearchKind.CompatibleWithAntisense);
    public bool NeedsFixedForward => Kind == PrimerSearchKind.CompatibleWithSense;
    public bool NeedsFixedReverse => Kind == PrimerSearchKind.CompatibleWithAntisense;
    [ObservableProperty] private string minLength;
    [ObservableProperty] private string optLength;
    [ObservableProperty] private string maxLength;
    [ObservableProperty] private string minTm;
    [ObservableProperty] private string optTm;
    [ObservableProperty] private string maxTm;
    [ObservableProperty] private string minGc;
    [ObservableProperty] private string maxGc;
    [ObservableProperty] private string minProduct;
    [ObservableProperty] private string maxProduct;
    [ObservableProperty] private string count;
    [ObservableProperty] private string forwardStart;
    [ObservableProperty] private string forwardEnd;
    [ObservableProperty] private string reverseStart;
    [ObservableProperty] private string reverseEnd;
    [ObservableProperty] private string forward;
    [ObservableProperty] private string reverse;
    public SearchCriteriaModel(DesignParameters parameters, PrimerSearchKind kind, string forward, string reverse, bool advancedMode = false)
    {
        original = parameters; this.kind = kind; this.forward = forward; this.reverse = reverse; this.advancedMode = advancedMode;
        static string N(object value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;
        minLength = N(parameters.MinLength); optLength = N(parameters.OptLength); maxLength = N(parameters.MaxLength);
        minTm = N(parameters.MinTm); optTm = N(parameters.OptTm); maxTm = N(parameters.MaxTm);
        minGc = N(parameters.MinGc); maxGc = N(parameters.MaxGc); minProduct = N(parameters.MinProduct); maxProduct = N(parameters.MaxProduct);
        count = N(parameters.Count); forwardStart = N(parameters.ForwardRegionStart);
        forwardEnd = parameters.ForwardRegionLength == 0 ? "" : N(parameters.ForwardRegionStart + parameters.ForwardRegionLength - 1);
        reverseStart = N(parameters.ReverseRegionStart);
        reverseEnd = parameters.ReverseRegionLength == 0 ? "" : N(parameters.ReverseRegionStart + parameters.ReverseRegionLength - 1);
    }
    public SearchRequest Read(int sequenceLength)
    {
        static int I(string text) => int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        static double D(string text) => double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        static (int Start, int Length) Range(bool used, string startText, string endText, int total)
        {
            if (!used) return (1, 0);
            var start = I(startText); var end = string.IsNullOrWhiteSpace(endText) ? total : I(endText);
            if (start < 1 || end < start || end > total) throw new ArgumentException("搜索范围须为模板上的 1-based 起点至末位，且起点 ≤ 末位。");
            return start == 1 && end == total ? (1, 0) : (start, end - start + 1);
        }
        var fRange = Range(UsesForwardRange && !ClearCustomRegions, ForwardStart, ForwardEnd, sequenceLength);
        var rRange = Range(UsesReverseRange && !ClearCustomRegions, ReverseStart, ReverseEnd, sequenceLength);
        var basis = ClearCustomRegions ? SearchParameterSharing.WholeTemplate(original) : original;
        var parameters = basis with { MinLength = I(MinLength), OptLength = I(OptLength), MaxLength = I(MaxLength),
            MinTm = D(MinTm), OptTm = D(OptTm), MaxTm = D(MaxTm), MinGc = D(MinGc), MaxGc = D(MaxGc),
            MinProduct = IsPairSearch ? I(MinProduct) : original.MinProduct, MaxProduct = IsPairSearch ? I(MaxProduct) : original.MaxProduct, Count = I(Count),
            ForwardRegionStart = fRange.Start, ForwardRegionLength = fRange.Length,
            ReverseRegionStart = rRange.Start, ReverseRegionLength = rRange.Length };
        parameters.Validate(sequenceLength);
        var f = Kind == PrimerSearchKind.CompatibleWithSense ? SequenceFiles.NormalizePrimer(Forward) : Forward;
        var r = Kind == PrimerSearchKind.CompatibleWithAntisense ? SequenceFiles.NormalizePrimer(Reverse) : Reverse;
        return new(Kind, parameters, f, r) { ApplyToWholeProject = ApplyToWholeProject, ClearCustomRegions = ClearCustomRegions };
    }
    public void ApplyRecommendation(DesignParameters parameters)
    {
        original = parameters;
        static string N(object value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;
        MinLength = N(parameters.MinLength); OptLength = N(parameters.OptLength); MaxLength = N(parameters.MaxLength);
        MinTm = N(parameters.MinTm); OptTm = N(parameters.OptTm); MaxTm = N(parameters.MaxTm);
        MinGc = N(parameters.MinGc); MaxGc = N(parameters.MaxGc);
        MinProduct = N(parameters.MinProduct); MaxProduct = N(parameters.MaxProduct); Count = N(parameters.Count);
        ForwardStart = N(parameters.ForwardRegionStart);
        ForwardEnd = parameters.ForwardRegionLength == 0 ? "" : N(parameters.ForwardRegionStart + parameters.ForwardRegionLength - 1);
        ReverseStart = N(parameters.ReverseRegionStart);
        ReverseEnd = parameters.ReverseRegionLength == 0 ? "" : N(parameters.ReverseRegionStart + parameters.ReverseRegionLength - 1);
    }
}
