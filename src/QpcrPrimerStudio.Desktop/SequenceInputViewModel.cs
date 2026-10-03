using CommunityToolkit.Mvvm.ComponentModel;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public partial class MainViewModel
{
    [ObservableProperty] private string templateInputError = "";
    [ObservableProperty] private int templateErrorOffset = -1;

    private void ClearTemplateInputError()
    {
        TemplateInputError = ""; TemplateErrorOffset = -1;
    }
    private string NormalizeTemplate()
    {
        ClearTemplateInputError();
        try { return SequenceFiles.Normalize(TemplateText); }
        catch (SequenceInputException ex)
        {
            InputExpanded = true; InputTabIndex = 0;
            TemplateInputError = ex.Message; TemplateErrorOffset = ex.Offset;
            throw;
        }
    }
}
