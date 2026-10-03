using CommunityToolkit.Mvvm.ComponentModel;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public partial class MainViewModel
{
    private bool synchronizingTargetAndRun;
    private string? navigationTargetId;
    [ObservableProperty] private string resultContext = "请选择基因并设计引物。";

    private void NavigateToTargetResults(SequenceTarget? target)
    {
        if (synchronizingTargetAndRun) return;
        if (target?.Id != navigationTargetId)
        {
            RestorePrimerDraft(target?.Id);
            navigationTargetId = target?.Id;
        }
        var run = target is null ? null : Runs.LastOrDefault(r => r.Target.Id == target.Id && r.Target.Sha256 == target.Sha256);
        if (SelectedRun != run) SelectedRun = run;
        else if (run is null) ClearCurrentResults();
        synchronizingTargetAndRun = true;
        try { SelectedSingleSearch = target is null ? null : SinglePrimerSearches.LastOrDefault(s => s.Target.Id == target.Id && s.Target.Sha256 == target.Sha256); }
        finally { synchronizingTargetAndRun = false; }
        if (SelectedSingleSearch is null) SingleSearchLog = "当前基因尚无单引物搜索结果。";
        RefreshResultContext();
    }
    private void SynchronizeTargetWithRun(TargetRun run)
    {
        if (loading || synchronizingTargetAndRun) return;
        var target = Targets.FirstOrDefault(t => t.Id == run.Target.Id);
        if (target is null || SelectedTarget?.Id == target.Id) return;
        synchronizingTargetAndRun = true;
        try
        {
            SelectedTarget = target;
            navigationTargetId = target.Id;
            RestorePrimerDraft(target.Id);
            SelectedSingleSearch = SinglePrimerSearches.LastOrDefault(s => s.Target.Id == target.Id && s.Target.Sha256 == target.Sha256);
            if (SelectedSingleSearch is null) SingleSearchLog = "当前基因尚无单引物搜索结果。";
        }
        finally { synchronizingTargetAndRun = false; }
    }
    private void ClearCurrentResults()
    {
        SelectedCandidate = null; Candidates.Clear(); DisplayTarget = SelectedTarget;
        ClearCandidateDetails(); LogText = ""; InputExpanded = true; ResultsTabIndex = 0;
        RefreshResultContext();
    }
    private void ClearCandidateDetails()
    {
        SelectedOligoInspection = null; CandidateNotes = "";
        SequenceContext = "当前基因没有可显示的候选结合位点。";
        StructureText = "当前基因没有选中的候选结构。";
        EvidenceText = "当前基因没有选中的特异性结果。";
        QualityText = "当前基因没有选中的候选质量记录。";
    }
    private void RefreshResultContext()
    {
        if (ResultsTabIndex == 1 && SelectedSingleSearch is { } search)
            ResultContext = $"{search.Target.Id} · {(search.Reverse ? "R" : "F")} 单引物 · {search.Primers.Count} 条" +
                (SelectedTarget?.Sha256 != search.Target.Sha256 ? " · 历史模板结果" : "");
        else if (SelectedRun is { } run)
        {
            var historical = SelectedTarget?.Sha256 != run.Target.Sha256 ? " · 历史模板结果" : "";
            ResultContext = $"{run.Target.Id} · {run.State} · {run.Candidates.Count} 对{historical}";
        }
        else if (SelectedTarget is { } target)
            ResultContext = Runs.Any(r => r.Target.Id == target.Id)
                ? $"{target.Id} · 当前模板已更新，尚无对应设计结果。"
                : $"{target.Id} · 尚未进行成对设计。";
        else ResultContext = "请选择基因并设计引物。";
    }
}
