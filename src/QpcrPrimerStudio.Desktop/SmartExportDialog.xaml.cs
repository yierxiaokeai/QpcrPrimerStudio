using System.Windows;
using System.Windows.Shell;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public partial class SmartExportDialog : Window
{
    public SmartExportDialog(SmartExportPlan plan)
    {
        InitializeComponent();
        ExportSummary.Text = $"{plan.Report.Genes.Count} 个基因 · 已选出 {plan.Choices.Count} 对 · 未导出 {plan.MissingGenes} 个基因";
        ExportRules.Text = plan.Report.RankingRule + $"\n发卡筛选退火温度 {plan.Report.Settings.AnnealingC:F1} °C；使用各候选原设计反应条件。";
        SmartChoicesGrid.ItemsSource = plan.Report.Genes.Where(g => g.Status == "已选出").ToList();
        SmartMissingGrid.ItemsSource = plan.Report.Genes.Where(g => g.Status != "已选出").ToList();
        SmartTargetsGrid.ItemsSource = plan.Report.Targets;
        ExportSmartButton.IsEnabled = plan.Choices.Count > 0;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 42, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
    }
    private void ConfirmExport(object sender, RoutedEventArgs e) => DialogResult = true;
}
