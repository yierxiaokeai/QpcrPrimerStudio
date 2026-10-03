using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public partial class MainViewModel
{
    private ListCollectionView? candidateView;
    public ICollectionView CandidateView => candidateView ??= new ListCollectionView(Candidates) { Filter = VisibleAfterHairpinScreen };
    [ObservableProperty] private HairpinScreenSettings hairpinSettings = new();
    [ObservableProperty] private bool showHairpinExcluded;
    [ObservableProperty] private string hairpinFilterSummary = "";
    public bool HasHairpinScreening => SelectedRun?.HairpinScreening is not null;
    private bool CanScreenHairpins() => CanWork() && SelectedRun?.Candidates.Count > 0;
    private bool CanClearHairpinScreening() => CanWork() && HasHairpinScreening;
    private bool VisibleAfterHairpinScreen(object item) => item is PrimerCandidate c &&
        (ShowHairpinExcluded || SelectedRun?.HairpinScreening?.DecisionFor(c)?.Excluded != true);
    partial void OnShowHairpinExcludedChanged(bool value) => RefreshHairpinView();
    partial void OnHairpinSettingsChanged(HairpinScreenSettings value) { if (!loading) dirty = true; }

    [RelayCommand(CanExecute = nameof(CanScreenHairpins))]
    private async Task ScreenHairpinsAsync() => await ApplyHairpinScreeningAsync(HairpinSettings);
    public async Task ApplyHairpinScreeningAsync(HairpinScreenSettings settings)
    {
        if (!CanWork()) return;
        await Busy(async token =>
        {
            settings.Validate();
            var run = SelectedRun ?? throw new ArgumentException("请先生成或选择候选任务。");
            var candidates = run.Candidates.ToArray();
            Status = $"正在按 {settings.AnnealingC:F1} °C 退火温度核对发卡 Tm、3′端配对与 ΔG…";
            var report = await new HairpinScreeningEngine(Path.Combine(Path.GetDirectoryName(paths.Primer3)!, "ntthal.exe"))
                .ScreenAsync(candidates, run.UsedParameters ?? ReadParameters(), settings, token);
            token.ThrowIfCancellationRequested();
            run.HairpinScreening = report; HairpinSettings = settings; dirty = true;
            var excluded = candidates.Count(c => report.DecisionFor(c)?.Excluded == true);
            if (SelectedRun == run) { ShowHairpinExcluded = false; RefreshHairpinView(); }
            Status = $"发卡筛选完成：保留 {candidates.Length - excluded} 对，排除 {excluded} 对。原候选保留，可显示已排除项或清除筛选。";
        });
    }
    [RelayCommand(CanExecute = nameof(CanWork))]
    private void ConfigureHairpinScreening() => TryAction(() =>
    {
        var dialog = new HairpinSettingsDialog(HairpinSettings) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() == true)
        {
            HairpinSettings = dialog.Settings!;
            Status = $"发卡筛选条件已设置：退火 {HairpinSettings.AnnealingC:F1} °C。点击排除高风险发卡应用到当前候选。";
        }
    });
    [RelayCommand(CanExecute = nameof(CanClearHairpinScreening))]
    private void ClearHairpinScreening() => TryAction(() =>
    {
        SelectedRun!.HairpinScreening = null; ShowHairpinExcluded = false; dirty = true;
        RefreshHairpinView(); Status = "发卡筛选已清除，原候选全部恢复；评分与原始证据保留。";
    });
    private void RefreshHairpinView(bool changedRun = false)
    {
        if (changedRun) ShowHairpinExcluded = false;
        candidateView?.Refresh();
        if (SelectedCandidate is null || !Candidates.Contains(SelectedCandidate) || !VisibleAfterHairpinScreen(SelectedCandidate))
            SelectedCandidate = Candidates.FirstOrDefault(c => VisibleAfterHairpinScreen(c));
        var report = SelectedRun?.HairpinScreening;
        var excluded = Candidates.Count(c => report?.DecisionFor(c)?.Excluded == true);
        HairpinFilterSummary = report is null ? "" : $"发卡筛选 · 退火 {report.Settings.AnnealingC:F1} °C · 保留 {Candidates.Count - excluded} / 排除 {excluded} 对 · 原候选可恢复";
        OnPropertyChanged(nameof(HasHairpinScreening));
        ScreenHairpinsCommand.NotifyCanExecuteChanged(); ClearHairpinScreeningCommand.NotifyCanExecuteChanged();
        RefreshHairpinQuality();
    }
    private void RefreshHairpinQuality()
    {
        if (SelectedCandidate is not { } candidate)
        {
            QualityText = HasHairpinScreening ? "当前筛选没有保留的候选。勾选显示已排除可查看原候选与理由；清除筛选可恢复全部。" : "选择候选查看评分、覆盖与排除原因。";
            return;
        }
        QualityText = candidate.Assessment.Details;
        if (SelectedRun?.HairpinScreening is not { } report || report.DecisionFor(candidate) is not { } decision) return;
        static string Evidence(string name, HairpinEvidence e) => !e.HasStructure ? $"{name}：本次未检出稳定发卡" :
            $"{name}：发卡 Tm {e.TmC:F1} °C；ΔG₃₇ {e.DeltaGKcal:F2} kcal/mol；3′末端碱基配对 {(e.ThreePrimePaired ? "是" : "否")}";
        QualityText += $"\n\n发卡筛选 · 退火 {report.Settings.AnnealingC:F1} °C · {(decision.Excluded ? "已排除" : "未触发排除条件")}\n" +
            Evidence("F", decision.Forward) + "\n" + Evidence("R", decision.Reverse) + "\n" + string.Join("\n", decision.Reasons) +
            "\nΔG 统一在 37 °C、各候选原设计反应体系下计算；筛选阈值可调整。";
    }
}
