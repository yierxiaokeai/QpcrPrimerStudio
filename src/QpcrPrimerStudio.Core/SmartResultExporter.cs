using System.Globalization;
using System.Text;
using System.Text.Json;
using CsvHelper;

namespace QpcrPrimerStudio.Core;

public static partial class ResultExporter
{
    public static IReadOnlyList<string> ExportSmart(string path, SmartExportPlan plan)
    {
        ProjectValidation.Validate(plan.Project);
        if (plan.Choices.Count == 0) throw new InvalidOperationException("没有可导出的基因，请查看未导出原因。");
        if (SmartExportEngine.DesignFingerprint(plan.Project) != plan.ContentFingerprint)
            throw new InvalidOperationException("预览中的设计数据已变化，请重新生成智能导出预览。");
        if (plan.Choices.Select(c => c.Gene).Distinct(StringComparer.Ordinal).Count() != plan.Choices.Count)
            throw new InvalidDataException("智能导出包含重复基因。");
        var rows = plan.Choices.Select(choice =>
        {
            var run = plan.Project.Runs[choice.RunIndex]; var c = SmartExportEngine.Candidate(plan.Project, choice);
            var target = plan.Project.Targets.Single(t => t.Id == run.Target.Id && t.Sha256 == run.Target.Sha256);
            if (!c.Assessment.Accepted || !SmartExportEngine.IsScored(c) || run.HairpinScreening?.DecisionFor(c)?.Excluded != false ||
                choice.Gene != SmartExportEngine.GeneFor(target, plan.Project.Reference) ||
                SmartBindingScreen.Rejections(c, run.Target, plan.Project.Databases).Count > 0)
                throw new InvalidDataException("智能导出的候选与筛选证据不一致，请重新准备。");
            return Row(run, c) with { Gene = choice.Gene };
        }).ToList();
        path = Path.GetFullPath(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not ".xlsx" and not ".csv") throw new ArgumentException("请选择 Excel 或 CSV 格式。");
        var destinations = new List<string> { path };
        if (extension == ".csv") destinations.Add(Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-智能导出记录.csv"));
        var pending = destinations.Select(p => Path.Combine(Path.GetDirectoryName(p)!, "." + Guid.NewGuid().ToString("N") + Path.GetExtension(p))).ToArray();
        WriteExport(pending[0], plan.Project, rows, plan.Report);
        if (destinations.Count > 1)
        {
            using var writer = new StreamWriter(pending[1], false, new UTF8Encoding(true));
            using var csv = new CsvWriter(writer, new CsvHelper.Configuration.CsvConfiguration(CultureInfo.InvariantCulture)
                { InjectionOptions = CsvHelper.Configuration.InjectionOptions.Escape, InjectionEscapeCharacter = '\'' });
            csv.WriteRecords(plan.Report.Genes.Select(g => new
            {
                g.Gene, g.Status, g.Target, g.CandidateId, g.Score, g.Rank, g.Reason, plan.Report.PreparedAt,
                plan.Report.RankingRule, plan.Report.SourceFingerprint, plan.Report.HairpinEngineSha256,
                Thresholds = JsonSerializer.Serialize(plan.Report.Settings, ProjectStore.JsonOptions),
                TargetAudit = JsonSerializer.Serialize(plan.Report.Targets.Where(t => t.Gene == g.Gene), ProjectStore.JsonOptions),
                HairpinEvidence = JsonSerializer.Serialize(plan.Report.Targets.Where(t => t.Gene == g.Gene && t.RunIndex is not null)
                    .Select(t => new { t.Target, Screening = plan.Project.Runs[t.RunIndex!.Value].HairpinScreening }), ProjectStore.JsonOptions)
            }));
        }
        // Stage all files before replacing any destination. Keep recoverable backups on success and failure.
        var installed = new List<(string Destination, string? Backup)>();
        try
        {
            for (var i = 0; i < destinations.Count; i++)
            {
                var destination = destinations[i]; string? backup = null;
                if (File.Exists(destination))
                {
                    backup = destination + ".backup-" + Guid.NewGuid().ToString("N");
                    File.Replace(pending[i], destination, backup);
                }
                else File.Move(pending[i], destination);
                installed.Add((destination, backup));
            }
        }
        catch (Exception writeError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var (destination, backup) in installed.AsEnumerable().Reverse())
            {
                try
                {
                    File.Move(destination, destination + ".incomplete-" + Guid.NewGuid().ToString("N"));
                    if (backup is not null) File.Move(backup, destination);
                }
                catch (Exception ex) { rollbackErrors.Add(ex); }
            }
            if (rollbackErrors.Count > 0) throw new AggregateException("导出写入及恢复均遇到错误；请保留备份和 incomplete 文件。", new[] { writeError }.Concat(rollbackErrors));
            throw;
        }
        return destinations;
    }
    private static string JsonReport(SmartExportReport report) => JsonSerializer.Serialize(new
    {
        report.PreparedAt, report.SourceFingerprint, report.HairpinEngineSha256, report.Settings,
        GeneCount = report.Genes.Count, Exported = report.Genes.Count(g => g.Status == "已选出")
    }, ProjectStore.JsonOptions);
}
