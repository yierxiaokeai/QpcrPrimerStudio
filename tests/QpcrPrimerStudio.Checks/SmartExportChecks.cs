using System.Globalization;
using System.IO;
using System.Text.Json;
using ClosedXML.Excel;
using CsvHelper;
using QpcrPrimerStudio.Core;
using QpcrPrimerStudio.Desktop;

internal static class SmartExportChecks
{
    internal static async Task<int> RunAsync(string root, string work, SequenceTarget originalTarget, TargetRun originalRun, string primer3)
    {
        var count = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); count++; Console.WriteLine("PASS: " + label); }
        static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ProjectStore.JsonOptions), ProjectStore.JsonOptions)!;
        string Identity(ProjectDocument p) => JsonSerializer.Serialize(p, ProjectStore.JsonOptions);
        var parameters = originalRun.UsedParameters!;
        var template = originalTarget.Sequence.ToCharArray();
        "NNNNNNNNNNNNNNNNNNNN".CopyTo(0, template, 59, 20);
        "AAAAAAAAAAAAAAAAAAAA".CopyTo(0, template, 99, 20);
        "GCGCGCAAAAAAGCGCGC".CopyTo(0, template, 139, 18);
        "TTTTTTTTTTTTTTTTTTTT".CopyTo(0, template, 219, 20);
        var target = originalTarget with { GeneId = "geneA", Sequence = new string(template) };
        // Deliberate ranking fixtures isolate selection and use actual ntthal for safe/risky sequences.
        PrimerCandidate Pair(string id, string targetId, int rank, double? score, bool risky = false, bool ambiguous = false)
        {
            var c = Copy(originalRun.Candidates[0]); c.Id = id; c.TargetId = targetId; c.Rank = rank;
            c.Assessment = new CandidateAssessment { Score = score }; c.Selected = id == "tie-second";
            var forward = ambiguous ? "NNNNNNNNNNNNNNNNNNNN" : risky ? "GCGCGCAAAAAAGCGCGC" : "AAAAAAAAAAAAAAAAAAAA";
            var start = ambiguous ? 60 : risky ? 140 : 100;
            c.Forward = c.Forward with { Sequence = forward, Start = start, End = start + forward.Length - 1 };
            c.Reverse = c.Reverse with { Sequence = "AAAAAAAAAAAAAAAAAAAA", Start = 220, End = 239 };
            c.ProductLength = 240 - start; c.Amplicon = target.Sequence.Substring(start - 1, c.ProductLength);
            c.Provenance = c.Provenance! with { TemplateSha256 = target.Sha256 };
            return c;
        }
        TargetRun Run(SequenceTarget t, params PrimerCandidate[] candidates)
        {
            var result = new TargetRun { Target = t, State = "完成", UsedParameters = parameters, Candidates = candidates.ToList() };
            // Ranking fixtures have explicitly controlled assessments; mark the evidence they describe.
            foreach (var c in candidates) c.Assessment.EvidenceFingerprint = CandidateQualityEngine.EvidenceFingerprint(c, t, parameters, result.RegionSettings, null);
            return result;
        }
        var rejection = Pair("rejected", target.Id, 1, 100); rejection.Assessment.Rejections.Add("fixture quality rejection");
        var latest = Run(target, Pair("risky-top", target.Id, 1, 99, true), Pair("tie-second", target.Id, 2, 90),
            Pair("tie-first", target.Id, 1, 90), Pair("lower", target.Id, 1, 80), rejection, Pair("unknown", target.Id, 1, null));
        var source = new ProjectDocument { Parameters = parameters, Targets = [target], Runs = [Run(target, Pair("old-top", target.Id, 1, 100)), latest] };
        var before = Identity(source);
        var engine = new SmartExportEngine(Path.Combine(Path.GetDirectoryName(primer3)!, "ntthal.exe"));
        var plan = await engine.PrepareAsync(source, new(), default);
        var chosen = SmartExportEngine.Candidate(plan.Project, plan.Choices.Single());
        Check(chosen.Id == "tie-first", "Smart selection removes risky/quality-rejected/unscored pairs and resolves score ties by original rank");
        Check(plan.Choices[0].RunIndex == 1 && plan.Report.Targets[0].HairpinExcluded == 1 && plan.Report.Targets[0].QualityExcluded == 1 && plan.Report.Targets[0].Unscored == 1,
            "Smart selection uses the latest matching run and records each exclusion category");
        Check(Identity(source) == before && source.Runs[1].HairpinScreening is null && source.Runs[1].Candidates[1].Selected,
            "Preparing smart export does not mutate source data or manual selections");
        Check(plan.Project.Runs[1].HairpinScreening!.Decisions.Count == 4 && plan.Report.HairpinEngineSha256.Length == 64,
            "Smart screening preserves actual thermodynamic evidence and engine identity");
        var cachedTime = plan.Project.Runs[1].HairpinScreening!.AnalyzedAt;
        var reused = await engine.PrepareAsync(plan.Project, new(), default);
        Check(reused.Project.Runs[1].HairpinScreening!.AnalyzedAt == cachedTime, "Unchanged trustworthy hairpin evidence is reused");
        plan.Project.Runs[1].Candidates[2].Provenance = null;
        plan.Project.Runs[1].UsedParameters = parameters with { MagnesiumMm = 0 };
        var changed = await engine.PrepareAsync(plan.Project, new(), default);
        Check(changed.Project.Runs[1].HairpinScreening!.DecisionFor(changed.Project.Runs[1].Candidates[2])!.Forward.Conditions.MagnesiumMm == 0,
            "Changed fallback reaction conditions invalidate cached hairpin evidence even without candidate provenance");
        var freshSettings = await engine.PrepareAsync(source, new(85), default);
        Check(freshSettings.Report.Settings.AnnealingC == 85 && freshSettings.Project.Runs[1].HairpinScreening!.Settings.AnnealingC == 85,
            "Configured annealing thresholds are applied throughout intelligent export");

        var t2 = target with { Id = "geneA.t2" }; var b = target with { Id = "geneB", GeneId = "" };
        var failed = target with { Id = "failed", GeneId = "" }; var updated = target with { Id = "updated", GeneId = "", Sequence = target.Sequence + "A" };
        var missing = target with { Id = "missing", GeneId = "" }; var risky = target with { Id = "risky", GeneId = "" };
        var unscored = target with { Id = "unscored", GeneId = "" }; var invalid = target with { Id = "invalid", GeneId = "" };
        var invalidPair = Pair("invalid-pair", invalid.Id, 1, 99, ambiguous: true);
        var multi = Copy(source); multi.Targets.AddRange([t2, b, failed, updated, missing, risky, unscored, invalid]);
        multi.Runs.AddRange([Run(t2, Pair("transcript-best", t2.Id, 7, 92)), Run(b, Pair("geneB-best", b.Id, 1, 89)),
            Run(failed, Pair("failed-old", failed.Id, 1, 100)), new TargetRun { Target = failed, State = "失败", Message = "deliberate failure" },
            Run(updated with { Sequence = target.Sequence }, Pair("updated-old", updated.Id, 1, 100)),
            Run(risky, Pair("risky-only", risky.Id, 1, 100, true)), Run(unscored, Pair("unscored-only", unscored.Id, 1, null)), Run(invalid, invalidPair)]);
        var multiBefore = Identity(multi); var multiPlan = await engine.PrepareAsync(multi, new(), default);
        Check(multiPlan.Choices.Count == 2 && multiPlan.Report.Genes.Count == 8 && multiPlan.MissingGenes == 6,
            "Mapped transcripts export once per gene and independent unmapped targets keep their own IDs");
        Check(multiPlan.Report.Genes.Single(g => g.Gene == "geneA").CandidateId == "transcript-best", "Highest score across mapped transcripts determines the gene choice");
        Check(multiPlan.Report.Genes.Single(g => g.Gene == "failed").Reason.Contains("deliberate failure"), "A latest failed run does not silently export an older successful run");
        Check(multiPlan.Report.Genes.Single(g => g.Gene == "updated").Reason.Contains("模板已更新") && multiPlan.Report.Genes.Single(g => g.Gene == "missing").Reason.Contains("尚未设计"),
            "Changed templates and never-designed genes have explicit missing-result reasons");
        Check(multiPlan.Report.Genes.Single(g => g.Gene == "risky").Reason.Contains("全部触发") && multiPlan.Report.Genes.Single(g => g.Gene == "unscored").Reason.Contains("未评分"),
            "All-risky and unscored genes are reported without exporting a substitute");
        Check(multiPlan.Report.Genes.Single(g => g.Gene == "invalid").Reason.Contains("发卡核对失败") && Identity(multi) == multiBefore,
            "A local hairpin computation failure remains explicit and other genes can still export without changing the source");
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); var cancelled = false;
        try { await engine.PrepareAsync(multi, new(), cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && Identity(multi) == multiBefore, "Cancelled intelligent export cannot apply partial source changes");
        var toolsMissing = false;
        try { await new SmartExportEngine(Path.Combine(work, "missing-ntthal.exe")).PrepareAsync(multi, new(), default); } catch (FileNotFoundException) { toolsMissing = true; }
        Check(toolsMissing && Identity(multi) == multiBefore, "Missing thermodynamic tools cannot create successful smart-export evidence");
        var mapped = Copy(source); mapped.Targets[0] = mapped.Targets[0] with { GeneId = "" };
        mapped.Reference = new ReferenceWorkspace { Transcripts = [new TranscriptAnnotation { Id = target.Id, GeneId = "reference-gene", Sequence = target.Sequence }] };
        foreach (var r in mapped.Runs)
        foreach (var c in r.Candidates) c.Assessment.EvidenceFingerprint = CandidateQualityEngine.EvidenceFingerprint(c, r.Target, parameters, r.RegionSettings, mapped.Reference);
        var referencePlan = await engine.PrepareAsync(mapped, new(), default);
        Check(referencePlan.Choices.Single().Gene == "reference-gene", "Reference gene mapping supplies grouping when the target has no explicit GeneId");

        var excel = Path.Combine(work, "smart.xlsx"); var csvPath = Path.Combine(work, "smart.csv");
        var excelReceipt = ResultExporter.ExportSmart(excel, multiPlan); var csvReceipt = ResultExporter.ExportSmart(csvPath, multiPlan);
        using (var book = new XLWorkbook(excel))
            Check(book.Worksheet("Primers").LastRowUsed()!.RowNumber() == 3 && book.Worksheet("Primers").Cell(2, 4).GetString() == multiPlan.Report.Genes[0].Forward5to3 &&
                book.Worksheet("Smart Export").LastRowUsed()!.RowNumber() == 12 && book.Worksheet("Smart Target Audit").LastRowUsed()!.RowNumber() == 10 &&
                book.Worksheet("Hairpin Screening").LastRowUsed()!.RowNumber() > 3, "Excel contains one pair per gene, all missing reasons, target diagnostics and the full screening evidence");
        using (var reader = new StreamReader(csvPath))
        using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
        {
            csv.Read(); csv.ReadHeader(); var rows = new List<(string Gene, int Rank)>();
            while (csv.Read()) rows.Add((csv.GetField("Gene")!, csv.GetField<int>("Rank")));
            Check(rows.Count == 2 && rows.Select(r => r.Gene).SequenceEqual(["geneA", "geneB"]) && rows[0].Rank == 7,
                "CSV contains the highest score per gene rather than the smallest original rank or checked-only rows");
        }
        Check(excelReceipt.Count == 1 && csvReceipt.Count == 2 && File.ReadAllText(csvReceipt[1]).Contains("发卡核对失败") && File.ReadAllText(csvReceipt[1]).Contains("GCGCGCAAAAAAGCGCGC"),
            "CSV export delivers a companion record including unavailable genes and serialized hairpin evidence");
        var oldBytes = File.ReadAllBytes(csvPath);
        using (var locked = new FileStream(csvReceipt[1], FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failedWrite = false; try { ResultExporter.ExportSmart(csvPath, multiPlan); } catch (IOException) { failedWrite = true; }
            Check(failedWrite && File.ReadAllBytes(csvPath).SequenceEqual(oldBytes), "A companion file write failure restores the original CSV and retains recovery files");
        }
        var store = Path.Combine(work, "smart.qpcrproject"); await ProjectStore.SaveAsync(store, multiPlan.Project); var restored = await ProjectStore.LoadAsync(store);
        Check(restored.SmartExportReports.Single().Genes.Count == 8 && restored.Runs[1].Candidates.Single(c => c.Id == "tie-second").Selected,
            "Project round-trip retains intelligent export decisions while preserving manual selections");
        var profile = Path.Combine(work, "smart-export-profile"); var vm = new MainViewModel(Path.Combine(root, "src/QpcrPrimerStudio.Desktop/bin/Debug/net10.0-windows/tools"), profile);
        vm.Restore(multi); var vmPlan = await vm.PrepareSmartExportAsync();
        Check(vmPlan?.Choices.Count == 2 && vm.Snapshot().SmartExportReports.Count == 1 && vm.Snapshot().Runs[1].Candidates[1].Selected &&
            SmartExportEngine.DesignFingerprint(vm.Snapshot()) == vmPlan.ContentFingerprint, "Desktop preparation applies completed screening records and produces a consistent export preview");
        vm.TemplateText += "A"; var priorReports = vm.Snapshot().SmartExportReports.Count; var draftPlan = await vm.PrepareSmartExportAsync();
        Check(draftPlan is null && vm.Status.Contains("尚未保存") && vm.Snapshot().SmartExportReports.Count == priorReports,
            "An unsaved template edit requires saving and redesign before smart export");
        multiPlan.Project.Runs[1].Candidates[0].Assessment.Score = 50; var changedPreview = false;
        try { ResultExporter.ExportSmart(Path.Combine(work, "changed.xlsx"), multiPlan); } catch (InvalidOperationException) { changedPreview = true; }
        Check(changedPreview && !File.Exists(Path.Combine(work, "changed.xlsx")), "Changes after preview cannot write a stale intelligent export");
        return count;
    }
}
