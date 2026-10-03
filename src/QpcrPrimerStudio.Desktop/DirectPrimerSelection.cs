using CommunityToolkit.Mvvm.ComponentModel;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public partial class MainViewModel
{
    [ObservableProperty] private string editorAnalysisText = "";

    public async Task SelectTemplatePrimerAsync(bool reverse, int start, int length)
    {
        if (!CanWork()) return;
        try
        {
            var sequence = NormalizeTemplate();
            if (start < 1 || length < 15 || length > 36 || start > sequence.Length - length + 1)
                throw new ArgumentException("请选择模板上连续的 15–36 nt；选区仅用于一条引物。");
            var selected = sequence.Substring(start - 1, length);
            var primer = SequenceFiles.NormalizePrimer(reverse ? SequenceFiles.ReverseComplement(selected) : selected);
            var priorHash = editTemplateHash;
            ApplyTemplate();
            if (priorHash is not null && priorHash != SelectedTarget!.Sha256)
            { ManualForward = ""; ManualReverse = ""; }
            editParent = null; editTemplateHash = SelectedTarget!.Sha256;
            if (reverse) ManualReverse = primer; else ManualForward = primer;
            InputExpanded = true; InputTabIndex = 1;
            EditSummary = $"直接选取 {(reverse ? "R" : "F")}：模板 {start}–{start + length - 1}；最终序列为 5′→3′。Apply 可核对完整引物对。";
            await AnalyzeManualAsync();
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private void SetEditorAnalysis(OligoInspection inspection)
    {
        static string Clean(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).Select(char.ToUpperInvariant).ToArray());
        if (Clean(ManualForward) != inspection.Forward || Clean(ManualReverse) != inspection.Reverse) return;
        EditorAnalysisText = inspection.Report;
        EditSummary = inspection.Forward.Length > 0 && inspection.Reverse.Length > 0
            ? "当前两条引物已完成自身属性与结构分析；Apply 核对模板结合位点与成对产物。"
            : "当前单引物已完成自身属性与结构分析；可通过 Search 寻找另一条配对引物。";
    }
}
