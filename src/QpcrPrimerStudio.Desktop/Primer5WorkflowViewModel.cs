using System.Windows;
using CommunityToolkit.Mvvm.Input;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public partial class MainViewModel
{
    private PrimerSearchKind lastSearchKind = PrimerSearchKind.Pairs;
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task ConfigureCompatibleSearchAsync()
    {
        lastSearchKind = string.IsNullOrWhiteSpace(ManualForward) ? PrimerSearchKind.CompatibleWithAntisense : PrimerSearchKind.CompatibleWithSense;
        await ConfigureSearchAsync();
    }
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task ConfigureSearchAsync()
    {
        try
        {
            var sequence = NormalizeTemplate();
            var target = (SelectedTarget ?? new SequenceTarget()) with { Id = TargetName.Trim(), Sequence = sequence,
                Junctions = JunctionText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Int).Distinct().Order().ToList(),
                ExpectedSubjects = ExpectedText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), ParameterOverride = null };
            target.Validate();
            var regions = ReadRegionSettings(); var currentReference = reference;
            var dialog = new SearchCriteriaDialog(SelectedTarget?.ParameterOverride ?? ReadParameters(), sequence.Length,
                lastSearchKind, ManualForward, ManualReverse, AdvancedMode,
                (draft, token) => new ParameterRecommendationEngine(paths.Primer3).RecommendAsync(target, draft.Parameters,
                    Enum.Parse<RecommendationSearchKind>(draft.Kind.ToString()), draft.Forward, draft.Reverse, token, regions, currentReference))
                { Owner = Application.Current.MainWindow };
            ((SearchCriteriaModel)dialog.DataContext).ApplyToWholeProject = UseSharedSearchParameters;
            var confirmed = dialog.ShowDialog() == true;
            AdvancedMode = ((SearchCriteriaModel)dialog.DataContext).AdvancedMode;
            if (confirmed) await ExecuteSearchRequestAsync(dialog.Request!);
        }
        catch (Exception ex) { ReportError(ex); }
    }
    public async Task ExecuteSearchRequestAsync(SearchRequest request)
    {
        if (!CanWork()) return;
        request.Parameters.Validate(NormalizeTemplate().Length);
        var hadUnappliedPrimerDraft = primerDraftModified;
        var draftForward = ManualForward; var draftReverse = ManualReverse;
        ApplyTemplate();
        ApplySearchParameters(request);
        ManualForward = request.Forward; ManualReverse = request.Reverse;
        primerDraftModified = hadUnappliedPrimerDraft && request.Kind is not (PrimerSearchKind.CompatibleWithSense or PrimerSearchKind.CompatibleWithAntisense) &&
            request.Forward == draftForward && request.Reverse == draftReverse;
        lastSearchKind = request.Kind;
        switch (request.Kind)
        {
            case PrimerSearchKind.Pairs: await DesignCurrentAsync(); break;
            case PrimerSearchKind.Sense: await SearchSingleAsync(false); break;
            case PrimerSearchKind.Antisense: await SearchSingleAsync(true); break;
            case PrimerSearchKind.Both: await SearchBothAsync(); break;
            case PrimerSearchKind.CompatibleWithSense: await SearchCompatibleAsync(true); break;
            case PrimerSearchKind.CompatibleWithAntisense: await SearchCompatibleAsync(false); break;
            default: throw new ArgumentException("未知搜索类型。");
        }
    }
    [RelayCommand(CanExecute = nameof(CanWork))]
    private void ShowResults()
    {
        InputExpanded = false;
        ResultsTabIndex = lastSearchKind is PrimerSearchKind.Sense or PrimerSearchKind.Antisense or PrimerSearchKind.Both ? 1 : 0;
    }
    [RelayCommand(CanExecute = nameof(CanWork))]
    private void EditSelected()
    {
        if (ResultsTabIndex == 1) UseSinglePrimer(); else LoadManual();
    }
}
