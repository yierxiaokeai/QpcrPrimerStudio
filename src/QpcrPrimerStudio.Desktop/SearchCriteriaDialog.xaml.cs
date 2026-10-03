using System.Windows;
using System.Windows.Shell;
using System.ComponentModel;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public partial class SearchCriteriaDialog : Window
{
    private readonly int sequenceLength;
    private readonly Func<SearchRequest, CancellationToken, Task<ParameterRecommendation>>? recommend;
    private CancellationTokenSource? recommendationCancellation;
    private ParameterRecommendation? recommendation;
    private ParameterRecommendation? appliedRecommendation;
    private bool applyingRecommendation;
    public SearchRequest? Request { get; private set; }
    public SearchCriteriaDialog(DesignParameters parameters, int sequenceLength, PrimerSearchKind kind, string forward, string reverse, bool advancedMode = false,
        Func<SearchRequest, CancellationToken, Task<ParameterRecommendation>>? recommend = null)
    {
        InitializeComponent(); this.sequenceLength = sequenceLength; this.recommend = recommend;
        var model = new SearchCriteriaModel(parameters, kind, forward, reverse, advancedMode);
        DataContext = model;
        model.PropertyChanged += InvalidateRecommendation;
        RecommendParametersButton.IsEnabled = recommend is not null;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 42, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
    }
    private void StartSearch(object sender, RoutedEventArgs e)
    {
        try { Request = ((SearchCriteriaModel)DataContext).Read(sequenceLength) with { Recommendation = appliedRecommendation }; DialogResult = true; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        { Feedback.Text = "请检查搜索条件：" + ex.Message; }
    }
    private void InvalidateRecommendation(object? sender, PropertyChangedEventArgs e)
    {
        if (applyingRecommendation || e.PropertyName is nameof(SearchCriteriaModel.IsRecommending) or nameof(SearchCriteriaModel.CanEdit) or
            nameof(SearchCriteriaModel.ApplyToWholeProject) or nameof(SearchCriteriaModel.AdvancedMode)) return;
        recommendation = null; appliedRecommendation = null; ApplyRecommendationButton.IsEnabled = false;
        RecommendationText.Text = "搜索条件已变化，可点击“智能推荐参数”重新试算。";
    }
    private async void RecommendParameters(object sender, RoutedEventArgs e) => await RecommendAsync();
    public async Task RecommendAsync()
    {
        var model = (SearchCriteriaModel)DataContext;
        if (recommend is null || model.IsRecommending) return;
        recommendation = null; appliedRecommendation = null; ApplyRecommendationButton.IsEnabled = false;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        recommendationCancellation = cancellation;
        try
        {
            // Commit any focused text field before reading its draft binding.
            if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox input)
                input.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            System.Windows.Input.Keyboard.ClearFocus();
            var draft = model.Read(sequenceLength);
            model.IsRecommending = true;
            CancelRecommendationButton.Visibility = Visibility.Visible;
            Feedback.Text = "正在按当前模板试算，最多检查三套参数…";
            recommendation = await recommend(draft, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            RecommendationText.Text = recommendation.Report;
            RecommendationDetails.IsExpanded = true;
            ApplyRecommendationButton.IsEnabled = recommendation.Parameters is not null;
            Feedback.Text = recommendation.Parameters is null ? "未找到可用推荐，当前参数保留。" : "试算完成，点击“应用推荐参数”可载入建议。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { Feedback.Text = "试算已停止或超过一分钟，当前参数保留。"; }
        catch (Exception ex)
        { Feedback.Text = "推荐未完成：" + ex.Message; }
        finally
        {
            recommendationCancellation = null;
            model.IsRecommending = false;
            CancelRecommendationButton.Visibility = Visibility.Collapsed;
        }
    }
    private void ApplyRecommendation(object sender, RoutedEventArgs e) => ApplyRecommendedParameters();
    public void ApplyRecommendedParameters()
    {
        if (recommendation?.Parameters is not { } parameters) return;
        applyingRecommendation = true;
        try { ((SearchCriteriaModel)DataContext).ApplyRecommendation(parameters); appliedRecommendation = recommendation; }
        finally { applyingRecommendation = false; }
        Feedback.Text = "推荐参数已载入；可继续编辑，点击“开始搜索”后用于正式设计。";
    }
    private void CancelRecommendation(object sender, RoutedEventArgs e) => recommendationCancellation?.Cancel();
    protected override void OnClosing(CancelEventArgs e)
    {
        recommendationCancellation?.Cancel();
        base.OnClosing(e);
    }
}
