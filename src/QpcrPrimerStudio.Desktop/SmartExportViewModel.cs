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
            if (SelectedTarget is { } target && (TargetName.Trim() != target.Id || NormalizeTemplate() != target.Sequence ||
                !SameInputIds(ExpectedText, target.ExpectedSubjects) || !SameInputIds(CoverageText, CoverageFor(target)) ||
                !JunctionText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Int).ToHashSet().SetEquals(target.Junctions)))
                throw new InvalidOperationException("当前目标序列或预期 ID、覆盖 ID、连接位点尚未保存，请先保存目标信息并重新设计该基因。");
            var source = Snapshot();
            var plan = await new SmartExportEngine(Path.Combine(Path.GetDirectoryName(paths.Primer3)!, "ntthal.exe"))
                .PrepareAsync(source, HairpinSettings, token, (done, total, id) =>
                {
                    Progress = done * 100d / total; Status = $"智能导出：发卡核对 {done}/{total} · {id}";
                });
            token.ThrowIfCancellationRequested();
            if (SmartExportEngine.DesignFingerprint(Snapshot()) != plan.Report.SourceFingerprint)
                throw new InvalidOperationException("筛选期间设计数据发生变化，本次预览未应用，请重新准备。");
            foreach (var index in plan.Report.Targets.Where(t => t.RunIndex is not null).Select(t => t.RunIndex!.Value).Distinct())
            {
                Runs[index].HairpinScreening = plan.Project.Runs[index].HairpinScreening;
                for (var c = 0; c < Runs[index].Candidates.Count; c++)
                    Runs[index].Candidates[c].Assessment = plan.Project.Runs[index].Candidates[c].Assessment;
            }
            smartExportReports.Add(plan.Report); dirty = true; RefreshHairpinView();
            Status = $"智能导出预览：{plan.Choices.Count} 个基因已选出，{plan.MissingGenes} 个基因无可导出结果。";
            result = plan;
        });
        return result;
    }
    private static bool SameInputIds(string text, IEnumerable<string> ids) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal).SetEquals(ids);
}
