using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using CsvHelper;

namespace QpcrPrimerStudio.Core;

public static partial class ResultExporter
{
    public static void Export(string path, ProjectDocument project, bool selectedOnly)
    {
        ProjectValidation.Validate(project);
        var rows = project.Runs.SelectMany(r => r.Candidates.Where(c => r.HairpinScreening?.DecisionFor(c)?.Excluded != true).Select(c => Row(r, c)))
            .Where(r => !selectedOnly || r.Selected).ToList();
        if (rows.Count == 0) throw new InvalidOperationException("没有可导出的候选。");
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
            var screens = project.Runs.Where(r => r.HairpinScreening is not null).SelectMany(r => r.HairpinScreening!.Decisions.Select(d => new
            {
                d.CandidateId, Target = r.Target.Id, d.Excluded, Reasons = string.Join(";", d.Reasons),
                r.HairpinScreening!.Settings.AnnealingC, r.HairpinScreening.Settings.TerminalMarginC, r.HairpinScreening.Settings.TerminalDeltaGKcal,
                r.HairpinScreening.Settings.CheckTerminalHairpins, DeltaGTemperatureC = r.HairpinScreening.DeltaGTemperatureC,
                ForwardTmC = d.Forward.TmC, ReverseTmC = d.Reverse.TmC, ForwardDeltaGKcal = d.Forward.DeltaGKcal, ReverseDeltaGKcal = d.Reverse.DeltaGKcal,
                ForwardThreePrimePaired = d.Forward.ThreePrimePaired, ReverseThreePrimePaired = d.Reverse.ThreePrimePaired,
                Conditions = System.Text.Json.JsonSerializer.Serialize(d.Forward.Conditions), r.HairpinScreening.EngineSha256, r.HairpinScreening.AnalyzedAt,
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
            CandidateId = c.Id, TemplateSha256 = run.Target.Sha256,
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
