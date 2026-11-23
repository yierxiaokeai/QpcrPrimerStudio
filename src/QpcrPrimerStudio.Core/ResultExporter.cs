using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using CsvHelper;

namespace QpcrPrimerStudio.Core;

public static partial class ResultExporter
{
    public sealed record OrdinaryExportScope(int Pairs, int Targets, string? EmptyReason);
    private static IEnumerable<(TargetRun Run, PrimerCandidate Candidate)> OrdinaryCandidates(IEnumerable<TargetRun> runs, bool selectedOnly) =>
        runs.SelectMany(run => run.Candidates.Where(c => (!selectedOnly || c.Selected) &&
            run.HairpinScreening?.DecisionFor(c)?.Excluded != true).Select(c => (run, c)));
    private static string NoOrdinaryCandidates(IEnumerable<TargetRun> runs, bool selectedOnly)
    {
        if (!runs.Any(r => r.Candidates.Count > 0)) return "暂无设计结果。请先为靶标设计引物。";
        if (selectedOnly && !runs.Any(r => r.Candidates.Any(c => c.Selected)))
            return "尚未勾选引物。请在候选表左侧勾选，或取消“仅导出勾选项”以导出全部保留项。";
        return selectedOnly ? "勾选的引物均已被发卡筛选排除。请勾选其他候选，或查看已排除项。" :
            "候选均已被发卡筛选排除。请在候选表查看已排除项。";
    }
    public static OrdinaryExportScope DescribeOrdinaryExport(IEnumerable<TargetRun> runs, bool selectedOnly)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal); var pairs = 0;
        foreach (var (run, _) in OrdinaryCandidates(runs, selectedOnly)) { targets.Add(run.Target.Id); pairs++; }
        return new(pairs, targets.Count, pairs == 0 ? NoOrdinaryCandidates(runs, selectedOnly) : null);
    }
    public static void Export(string path, ProjectDocument project, bool selectedOnly)
    {
        ProjectValidation.Validate(project);
        var rows = OrdinaryCandidates(project.Runs, selectedOnly).Select(item => Row(item.Run, item.Candidate)).ToList();
        if (rows.Count == 0) throw new InvalidOperationException(NoOrdinaryCandidates(project.Runs, selectedOnly));
        var absolute = Path.GetFullPath(path);
        var pending = Path.Combine(Path.GetDirectoryName(absolute)!, ".export-" + Guid.NewGuid().ToString("N") + Path.GetExtension(absolute));
        try
        {
            WriteExport(pending, project, rows);
            if (File.Exists(absolute)) File.Replace(pending, absolute, absolute + ".backup-" + Guid.NewGuid().ToString("N"));
            else File.Move(pending, absolute);
        }
        catch (Exception ex) when (File.Exists(pending))
        {
            throw new IOException("导出未完成；暂存文件保留供核对：" + pending, ex);
        }
    }
    private static void WriteExport(string path, ProjectDocument project, List<ExportRow> rows, SmartExportReport? smart = null)
    {
        if (Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            using var workbook = new XLWorkbook();
            var sheet = workbook.AddWorksheet("Primers");
            sheet.Cell(1, 1).InsertTable(rows, "Primers", true);
            sheet.SheetView.FreezeRows(1);
            sheet.Columns().AdjustToContents(8, 60);
            var evidence = workbook.AddWorksheet("Targets");
            evidence.Cell(1, 1).InsertTable(project.Runs.Select(r => new { Target = r.Target.Id,
                r.State, r.Message, SequenceSha256 = r.Target.Sha256, r.EngineSha256 }).ToList());
            evidence.Columns().AdjustToContents(8, 80);
            var parameters = workbook.AddWorksheet("Parameters");
            parameters.Cell(1, 1).Value = "设计参数及反应条件（工程预设，须核对实验体系）";
            parameters.Cell(2, 1).Value = System.Text.Json.JsonSerializer.Serialize(project.Parameters, ProjectStore.JsonOptions);
            parameters.Cell(3, 1).Value = System.Text.Json.JsonSerializer.Serialize(project.RegionSettings, ProjectStore.JsonOptions);
            var quality = workbook.AddWorksheet("Quality");
            quality.Cell(1, 1).InsertTable(project.Runs.SelectMany(r => r.Candidates).Select(c => new { c.Id, c.TargetId,
                Accepted = c.Assessment.Accepted, c.Assessment.Score, c.Assessment.EvidenceCoverage, Gdna = c.Assessment.Gdna.Level,
                GenomicProduct = c.Assessment.Gdna.GenomicProduct, Covered = string.Join(",", c.Assessment.CoveredTranscripts),
                Missing = string.Join(",", c.Assessment.MissingTranscripts), Reasons = string.Join(";", c.Assessment.Rejections),
                Details = c.Assessment.Details, c.Locked, c.Revision, c.ParentCandidateId }).ToList());
            quality.Columns().AdjustToContents(8, 80);
            var screenSources = smart is null
                ? project.Runs.Select(r => (Target: r.Target.Id, Screen: r.HairpinScreening))
                : smart.Targets.Where(t => t.RunIndex.HasValue).Select(t =>
                    (Target: t.Target, Screen: project.Runs[t.RunIndex!.Value].SmartExportScreening));
            var screens = screenSources.Where(s => s.Screen is not null).SelectMany(s => s.Screen!.Decisions.Select(d => new
            {
                d.CandidateId, s.Target, d.Excluded, Reasons = string.Join(";", d.Reasons),
                s.Screen!.Settings.AnnealingC, s.Screen.Settings.TerminalMarginC, s.Screen.Settings.TerminalDeltaGKcal,
                s.Screen.Settings.CheckTerminalHairpins, s.Screen.DeltaGTemperatureC,
                ForwardTmC = d.Forward.TmC, ReverseTmC = d.Reverse.TmC, ForwardDeltaGKcal = d.Forward.DeltaGKcal, ReverseDeltaGKcal = d.Reverse.DeltaGKcal,
                ForwardThreePrimePaired = d.Forward.ThreePrimePaired, ReverseThreePrimePaired = d.Reverse.ThreePrimePaired,
                Conditions = System.Text.Json.JsonSerializer.Serialize(d.Forward.Conditions), s.Screen.EngineSha256, s.Screen.AnalyzedAt,
                ForwardEvidence = d.Forward.RawOutput, ReverseEvidence = d.Reverse.RawOutput
            })).ToList();
            if (screens.Count > 0)
            {
                var screening = workbook.AddWorksheet("Hairpin Screening"); screening.Cell(1, 1).InsertTable(screens);
                screening.Columns().AdjustToContents(8, 80);
            }
            var provenance = workbook.AddWorksheet("Provenance");
            provenance.Cell(1, 1).InsertTable(project.Runs.SelectMany(r => r.Candidates).Select(c => new { c.Id,
                App = c.Provenance?.AppVersion ?? "Unknown", Engine = c.Provenance?.EngineVersion ?? "Unknown",
                Reference = c.Provenance?.ReferenceIdentity ?? "", Template = c.Provenance?.TemplateSha256 ?? "",
                Time = c.Provenance?.DesignedAt.ToString("O") ?? "", Parameters = System.Text.Json.JsonSerializer.Serialize(c.Provenance?.Parameters),
                Regions = System.Text.Json.JsonSerializer.Serialize(c.Provenance?.Regions) }).ToList());
            if (project.Assays.Count > 0)
            {
                var miqe = workbook.AddWorksheet("MIQE Assays");
                miqe.Cell(1, 1).InsertTable(project.Assays.Select(a =>
                {
                    var pair = project.Runs.SelectMany(r => r.Candidates).FirstOrDefault(c => c.Id == a.CandidateId);
                    return new { a.Id, a.CandidateId, a.Status, Target = pair?.TargetId ?? "Unknown", F5to3 = pair?.Forward.Sequence ?? "",
                        R5to3 = pair?.Reverse.Sequence ?? "", ProductBp = pair?.ProductLength, a.SampleMaterial, a.Instrument, a.ReactionConditions,
                        a.Efficiency, a.RSquared, a.MeltCurve, a.Gel, a.Ntc, a.NoRt, a.ReferenceGene, a.BiologicalReplicates, a.TechnicalReplicates, a.Notes };
                }).ToList());
                miqe.Columns().AdjustToContents(8, 60);
            }
            if (smart is not null)
            {
                var audit = workbook.AddWorksheet("Smart Export");
                audit.Cell(1, 1).Value = smart.RankingRule;
                audit.Cell(2, 1).Value = JsonReport(smart);
                audit.Cell(4, 1).InsertTable(smart.Genes);
                audit.SheetView.FreezeRows(4); audit.Columns().AdjustToContents(8, 80);
                var targets = workbook.AddWorksheet("Smart Target Audit");
                targets.Cell(1, 1).InsertTable(smart.Targets.Select(t => new { t.Gene, t.Target, t.RunIndex,
                    t.Candidates, t.QualityExcluded, t.Unscored, t.MismatchExcluded,
                    MismatchReasons = string.Join("；", t.MismatchReasons), t.HairpinExcluded, t.Retained, t.Reason }));
                targets.Columns().AdjustToContents(8, 80);
            }
            workbook.SaveAs(path);
        }
        else
        {
            using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
            using var csv = new CsvWriter(writer, new CsvHelper.Configuration.CsvConfiguration(CultureInfo.InvariantCulture)
                { InjectionOptions = CsvHelper.Configuration.InjectionOptions.Escape, InjectionEscapeCharacter = '\'' });
            csv.WriteRecords(rows);
        }
    }
    private static ExportRow Row(TargetRun run, PrimerCandidate c) => new(c.TargetId, c.Rank, c.Selected,
        c.Forward.Sequence, c.Reverse.Sequence, c.ProductLength, c.Forward.Tm, c.Reverse.Tm,
        c.Forward.Gc, c.Reverse.Gc, c.Penalty, c.Forward.Start, c.Forward.End, c.Reverse.Start, c.Reverse.End,
        c.Forward.HairpinTm, c.Reverse.HairpinTm, c.PairAnyTm, c.PairEndTm,
        c.SpecificitySummary, c.JunctionEvidence, c.Notes, run.EngineSha256, c.Amplicon, c.Assessment.Score, c.Assessment.EvidenceCoverage)
        {
            CandidateId = c.Id, Gene = run.Target.GeneId, TemplateSha256 = run.Target.Sha256,
            Accepted = c.Assessment.Accepted,
            Rejections = string.Join("；", c.Assessment.Rejections),
            Warnings = string.Join("；", c.Assessment.Warnings)
        };
    public sealed record ExportRow(string Target, int Rank, bool Selected, string Forward5to3, string Reverse5to3,
        int ProductBp, double ForwardTmC, double ReverseTmC, double ForwardGcPercent, double ReverseGcPercent,
        double Primer3Penalty, int ForwardStart1, int ForwardEnd1, int ReverseStart1, int ReverseEnd1,
        double ForwardHairpinTmC, double ReverseHairpinTmC, double PairAnyTmC, double PairEndTmC,
        string Specificity, string JunctionEvidence, string Notes, string EngineSha256, string Amplicon, double? Score, double EvidenceCoverage)
    {
        public string Gene { get; init; } = "";
        public string CandidateId { get; init; } = "";
        public string TemplateSha256 { get; init; } = "";
        public bool Accepted { get; init; }
        public string Rejections { get; init; } = "";
        public string Warnings { get; init; } = "";
    }
}
