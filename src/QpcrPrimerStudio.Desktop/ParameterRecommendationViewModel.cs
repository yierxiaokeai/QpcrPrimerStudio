using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public partial class MainViewModel
{
    private List<ParameterRecommendation> parameterRecommendations = [];
    public bool UseSharedSearchParameters { get; private set; }
    public void ApplySearchParameters(SearchRequest request)
    {
        UseSharedSearchParameters = request.ApplyToWholeProject;
        if (request.ApplyToWholeProject)
        {
            var shared = SearchParameterSharing.CopySearchSettings(ReadParameters(), request.Parameters);
            if (request.ClearCustomRegions) shared = SearchParameterSharing.WholeTemplate(shared);
            LoadParameters(shared);
            var selectedId = SelectedTarget?.Id;
            for (var i = 0; i < Targets.Count; i++)
            {
                var target = Targets[i];
                if (target.Id == selectedId) Targets[i] = target with { ParameterOverride = request.Parameters };
                else if (target.ParameterOverride is { } own)
                    Targets[i] = target with { ParameterOverride = SearchParameterSharing.CopySearchSettings(own, request.Parameters) };
            }
            SelectedTarget = Targets.FirstOrDefault(t => t.Id == selectedId);
        }
        else
        {
            LoadParameters(request.Parameters);
            if (SelectedTarget?.ParameterOverride is not null)
            {
                var old = SelectedTarget; var updated = old with { ParameterOverride = request.Parameters };
                Targets[Targets.IndexOf(old)] = updated; SelectedTarget = updated;
            }
        }
        if (request.Recommendation is { } recommendation) parameterRecommendations.Add(recommendation);
        dirty = true;
    }
}
