using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public partial class MainViewModel
{
    public System.Collections.ObjectModel.ObservableCollection<OligoInspection> OligoInspections { get; } = [];
    [ObservableProperty] private OligoInspection? selectedOligoInspection;
    partial void OnSelectedOligoInspectionChanged(OligoInspection? value)
    {
        if (value is not null) StructureText = value.Report;
    }
    [ObservableProperty] private string forwardRegionStart = "1";
    [ObservableProperty] private string forwardRegionLength = "0";
    [ObservableProperty] private string reverseRegionStart = "1";
    [ObservableProperty] private string reverseRegionLength = "0";
    [ObservableProperty] private string editSummary = "两条序列均按 5′→3′ 输入；重算会保留原候选。";
    private PrimerCandidate? editParent;
    private string? editTemplateHash;
    partial void OnManualForwardChanged(string value) => MarkPrimerEdit();
    partial void OnManualReverseChanged(string value) => MarkPrimerEdit();
    private void MarkPrimerEdit()
    {
        if (!loading && !loadingPrimerDraft && !synchronizingTargetAndRun) { dirty = true; primerDraftModified = true; }
        EditorAnalysisText = "";
        EditSummary = "输入序列已更新，请点 Analyze 更新指标；Apply 核对模板并保存新修订。";
    }

    [RelayCommand(CanExecute = nameof(CanWork))]
    private void TransformPrimer(string action) => TryAction(() =>
    {
        var forward = action.StartsWith("F:", StringComparison.Ordinal);
        var sequence = SequenceFiles.NormalizePrimer(forward ? ManualForward : ManualReverse);
        var changed = action[2..] switch
        {
            "Reverse" => new string(sequence.Reverse().ToArray()),
            "Complement" => new string(SequenceFiles.ReverseComplement(sequence).Reverse().ToArray()),
            "ReverseComplement" => SequenceFiles.ReverseComplement(sequence),
            _ => throw new ArgumentException("未知序列方向操作。")
        };
        if (forward) ManualForward = changed; else ManualReverse = changed;
        EditSummary = "序列方向已变换，请重新分析或重算引物对。";
    });

    [RelayCommand(CanExecute = nameof(CanWork))]
    private void LoadManual() => TryAction(() =>
    {
        var candidate = RequiredCandidate();
        var run = Runs.First(r => r.Candidates.Contains(candidate));
        var target = Targets.FirstOrDefault(t => t.Id == run.Target.Id && t.Sha256 == run.Target.Sha256);
        if (target is null)
            throw new InvalidOperationException("此任务的靶标序列已被修改。请恢复原序列后编辑候选。");
        SelectedTarget = target;
        RequireCommittedTemplateForEditing(target);
        RequirePrimerDraftPreserved(candidate.Forward.Sequence, candidate.Reverse.Sequence);
        TargetName = target.Id; TemplateText = target.Sequence;
        JunctionText = string.Join(",", target.Junctions); ExpectedText = string.Join(",", target.ExpectedSubjects);
        LoadParameters(candidate.Provenance?.Parameters ?? run.UsedParameters ?? ReadParameters());
        LoadRegions(candidate.Provenance?.Regions ?? run.RegionSettings);
        ManualForward = candidate.Forward.Sequence; ManualReverse = candidate.Reverse.Sequence;
        editParent = candidate; editTemplateHash = target.Sha256;
        primerDraftModified = false;
        InputTabIndex = 1;
        InputExpanded = true;
        EditSummary = $"正在编辑 {candidate.TargetId} #{candidate.Rank} · 修订 {candidate.Revision}；原候选保留。";
    });

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task EvaluateAsync() => await Busy(async token =>
    {
        // Empty input must not silently turn an evaluation into an unconstrained search.
        var draftForward = ManualForward; var draftReverse = ManualReverse;
        var forward = SequenceFiles.NormalizePrimer(ManualForward);
        var reverse = SequenceFiles.NormalizePrimer(ManualReverse);
        ApplyTemplate();
        var target = SelectedTarget! with { ParameterOverride = null };
        var parent = editParent; var parentTemplateHash = editTemplateHash;
        var parameters = ReadParameters();
        var run = await new Primer3Engine(paths.Primer3).DesignAsync(target, parameters, token,
            forward, reverse, ReadRegionSettings(), reference);
        if (parent is not null && parent.TargetId == target.Id && parentTemplateHash == target.Sha256)
            foreach (var candidate in run.Candidates)
            {
                candidate.ParentCandidateId = parent.Id;
                candidate.Revision = parent.Revision + 1;
            }
        await CheckRunSpecificityAsync(run, token);
        Runs.Add(run); SelectedRun = run; dirty = true;
        if (run.Candidates.Count > 0 && SelectedTarget?.Id == target.Id && ManualForward == draftForward && ManualReverse == draftReverse)
            primerDraftModified = false;
        ResultsTabIndex = 0;
        Status = "引物编辑重算结束：" + run.State + "；请查看质量报告和任务日志。";
    });

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task SearchWithForwardAsync() => await SearchCompatibleAsync(true);

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task SearchWithReverseAsync() => await SearchCompatibleAsync(false);

    private async Task SearchCompatibleAsync(bool keepForward) => await Busy(async token =>
    {
        var draftForward = ManualForward; var draftReverse = ManualReverse;
        var fixedPrimer = SequenceFiles.NormalizePrimer(keepForward ? ManualForward : ManualReverse);
        ApplyTemplate();
        var target = SelectedTarget!;
        var run = await new Primer3Engine(paths.Primer3).DesignAsync(target, ReadParameters(), token,
            keepForward ? fixedPrimer : null, keepForward ? null : fixedPrimer, ReadRegionSettings(), reference);
        await CheckRunSpecificityAsync(run, token);
        Runs.Add(run); SelectedRun = run; dirty = true;
        if (run.Candidates.Count > 0 && SelectedTarget?.Id == target.Id && ManualForward == draftForward && ManualReverse == draftReverse)
            primerDraftModified = false;
        ResultsTabIndex = 0;
        Status = $"固定 {(keepForward ? "Forward" : "Reverse")} 搜索配对结束：{run.State}。";
    });
}
