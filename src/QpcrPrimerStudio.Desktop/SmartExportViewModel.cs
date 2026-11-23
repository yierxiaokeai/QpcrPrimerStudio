using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public partial class MainViewModel
{
    private List<SmartExportReport> smartExportReports = [];

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task SmartExportAsync()
    {
        var plan = await PrepareSmartExportAsync();
        if (plan is null) return;
        var preview = new SmartExportDialog(plan) { Owner = Application.Current.MainWindow };
        if (preview.ShowDialog() != true) return;
        TryAction(() =>
        {
            if (SmartExportEngine.DesignFingerprint(Snapshot()) != plan.ContentFingerprint)
                throw new InvalidOperationException("设计数据已变化，请重新准备智能导出。");
            var dialog = new SaveFileDialog { Filter = "Excel 工作簿|*.xlsx|CSV 表格|*.csv", FileName = "qPCR-智能引物.xlsx" };
            if (dialog.ShowDialog() != true) return;
            var files = ResultExporter.ExportSmart(dialog.FileName, plan);
            Status = $"智能导出完成：{plan.Choices.Count} 个基因各一对，{plan.MissingGenes} 个基因未导出；" + string.Join("；", files);
        });
    }

    public async Task<SmartExportPlan?> PrepareSmartExportAsync()
    {
        SmartExportPlan? result = null;
        await Busy(async token =>
        {
            await FlushSelectionSavesAsync();
            RequireSavedTargetInputs(includeSelected: true);
            var source = Snapshot();
            var plan = await new SmartExportEngine(Path.Combine(Path.GetDirectoryName(paths.Primer3)!, "ntthal.exe"))
                .PrepareAsync(source, HairpinSettings, token, (done, total, id) =>
                {
                    Progress = done * 100d / total; Status = $"智能导出：核对候选 {done}/{total} · {id}";
                });
            token.ThrowIfCancellationRequested();
            RequireSavedTargetInputs(includeSelected: true);
            if (SmartExportEngine.DesignFingerprint(Snapshot()) != plan.Report.SourceFingerprint)
                throw new InvalidOperationException("筛选期间设计数据发生变化，本次预览未应用，请重新准备。");
            foreach (var index in plan.Report.Targets.Where(t => t.RunIndex is not null).Select(t => t.RunIndex!.Value).Distinct())
            {
                Runs[index].SmartExportScreening = plan.Project.Runs[index].SmartExportScreening;
                for (var c = 0; c < Runs[index].Candidates.Count; c++)
                    Runs[index].Candidates[c].Assessment = plan.Project.Runs[index].Candidates[c].Assessment;
            }
            smartExportReports.Add(plan.Report); dirty = true; RefreshHairpinView();
            Status = $"智能导出预览：{plan.Choices.Count} 个基因已选出，{plan.MissingGenes} 个基因无可导出结果。";
            result = plan;
        });
        return result;
    }
}
