using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public partial class MainViewModel
{
    public ObservableCollection<SinglePrimerSearch> SinglePrimerSearches { get; } = [];
    public ObservableCollection<PrimerMetrics> SinglePrimers { get; } = [];
    [ObservableProperty] private SinglePrimerSearch? selectedSingleSearch;
    [ObservableProperty] private PrimerMetrics? selectedSinglePrimer;
    [ObservableProperty] private int inputTabIndex;
    [ObservableProperty] private int resultsTabIndex;
    [ObservableProperty] private bool inputExpanded = true;
    [ObservableProperty] private string singleSearchLog = "单引物搜索分别列出 F 或 R；配对后检查产物与特异性。";

    partial void OnSelectedSingleSearchChanged(SinglePrimerSearch? value)
    {
        SinglePrimers.Clear();
        SelectedSinglePrimer = null;
        if (value is null) { RefreshResultContext(); return; }
        if (!loading && !synchronizingTargetAndRun)
        {
            synchronizingTargetAndRun = true;
            try
            {
                var changedTarget = SelectedTarget?.Id != value.Target.Id;
                SelectedTarget = Targets.FirstOrDefault(t => t.Id == value.Target.Id);
                navigationTargetId = SelectedTarget?.Id;
                SelectedRun = SelectedTarget is null ? null : Runs.LastOrDefault(r => r.Target.Id == SelectedTarget.Id && r.Target.Sha256 == SelectedTarget.Sha256);
                if (changedTarget) RestorePrimerDraft(SelectedTarget?.Id);
                if (!IsBusy) lastSearchKind = value.Reverse ? PrimerSearchKind.Antisense : PrimerSearchKind.Sense;
                DisplayTarget = value.Target; ResultsTabIndex = 1;
            }
            finally { synchronizingTargetAndRun = false; }
        }
        foreach (var primer in value.Primers) SinglePrimers.Add(primer);
        SelectedSinglePrimer = SinglePrimers.FirstOrDefault();
        SingleSearchLog = value.Summary + "\n单引物结果尚未进行成对产物及特异性检查。\n" + value.RawOutput;
        RefreshResultContext();
    }
    partial void OnResultsTabIndexChanged(int value) => RefreshResultContext();

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task SearchForwardAsync() => await SearchSingleAsync(false);
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task SearchReverseAsync() => await SearchSingleAsync(true);
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task SearchBothAsync() => await Busy(async token =>
    {
        ApplyTemplate();
        var target = SelectedTarget!; var p = ReadParameters(); var regions = ReadRegionSettings();
        foreach (var reverse in new[] { false, true })
        {
            var result = await new Primer3Engine(paths.Primer3).SearchSingleAsync(target, p, reverse, token, regions, reference);
            SinglePrimerSearches.Add(result); SelectedSingleSearch = result; dirty = true;
        }
        ResultsTabIndex = 1; InputExpanded = false;
        Status = "F 和 R 独立搜索完成；在单引物搜索页切换两个列表，选择一条再寻找配对。";
    });

    private async Task SearchSingleAsync(bool reverse) => await Busy(async token =>
    {
        ApplyTemplate();
        var result = await new Primer3Engine(paths.Primer3).SearchSingleAsync(SelectedTarget!, ReadParameters(), reverse,
            token, ReadRegionSettings(), reference);
        SinglePrimerSearches.Add(result); SelectedSingleSearch = result; dirty = true;
        ResultsTabIndex = 1;
        InputExpanded = false;
        Status = result.Summary + "；在单引物搜索页查看，可载入并搜索配对。";
    });

    [RelayCommand(CanExecute = nameof(CanWork))]
    private void UseSinglePrimer() => TryAction(() =>
    {
        if (SelectedSingleSearch is not { } search || SelectedSinglePrimer is not { } primer)
            throw new InvalidOperationException("请选择单引物搜索结果。");
        var target = Targets.FirstOrDefault(t => t.Id == search.Target.Id && t.Sha256 == search.Target.Sha256)
            ?? throw new InvalidOperationException("搜索对应的靶标已修改，请恢复原序列。");
        SelectedTarget = target;
        RequireCommittedTemplateForEditing(target);
        RequirePrimerDraftPreserved(search.Reverse ? "" : primer.Sequence, search.Reverse ? primer.Sequence : "");
        TargetName = target.Id; TemplateText = target.Sequence;
        JunctionText = string.Join(",", target.Junctions); ExpectedText = string.Join(",", target.ExpectedSubjects);
        LoadParameters(search.Parameters); LoadRegions(search.Regions);
        if (search.Reverse) { ManualReverse = primer.Sequence; ManualForward = ""; }
        else { ManualForward = primer.Sequence; ManualReverse = ""; }
        editParent = null; editTemplateHash = null;
        primerDraftModified = false;
        InputTabIndex = 1;
        InputExpanded = true;
        EditSummary = "已载入单引物，请使用对应的固定 F / 固定 R 搜索配对。";
        Status = EditSummary;
    });
}
