using System.Globalization;
using System.IO;
using System.Text.Json;
using ClosedXML.Excel;
using CsvHelper;
using QpcrPrimerStudio.Core;
using QpcrPrimerStudio.Desktop;

internal static class HairpinScreeningChecks
{
    internal static async Task<int> RunAsync(string root, string work, SequenceTarget target, TargetRun original, string primer3)
    {
        var count = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            count++; Console.WriteLine("PASS: " + label);
        }
        var p = original.UsedParameters!; var conditions = HairpinConditions.From(p); var settings = new HairpinScreenSettings();
        HairpinEvidence Example(double tm, double dg, bool terminal) => new("ACGTACGTACGTACGTACGT", conditions, true, tm, dg, terminal, "", "test evidence");
        var none = new HairpinEvidence("AAAAAAAAAAAAAAAAAAAA", conditions, false, null, null, false, "", "no structure");
        bool Excludes(HairpinEvidence evidence, HairpinScreenSettings? rule = null) => HairpinScreeningEngine.Decide("test", evidence, none, rule ?? settings).Excluded;
        Check(Excludes(Example(60, -1, false)) && !Excludes(Example(59.99, -1, false)), "Hairpin Tm cutoff includes equality at the actual 60 C annealing temperature");
        Check(!Excludes(Example(35, -5, true)), "A hairpin far below annealing temperature is kept despite negative delta G and terminal pairing");
        Check(!Excludes(Example(56, -4, false)) && Excludes(Example(56, -4, true)), "Near-annealing terminal pairing changes the decision while internal hairpins are kept");
        Check(!Excludes(Example(56, -1.9, true)) && !Excludes(Example(56, -2, true)), "A negative delta G alone and the tolerated boundary do not trigger exclusion");
        Check(!Excludes(Example(54.99, -4, true)) && Excludes(Example(55, -4, true)), "The configurable terminal temperature window has a verified boundary");
        Check(!Excludes(Example(56, -4, true), settings with { CheckTerminalHairpins = false }) &&
            !Excludes(Example(56, -4, true), settings with { TerminalDeltaGKcal = -5 }), "Terminal rule and stability threshold can be adjusted independently");
        Check(!Excludes(none), "An explicitly absent predicted structure is retained");
        var invalid = false; try { (settings with { AnnealingC = double.NaN }).Validate(); } catch (ArgumentException) { invalid = true; }
        Check(invalid, "Invalid annealing temperature is rejected");
        var engine = new HairpinScreeningEngine(Path.Combine(Path.GetDirectoryName(primer3)!, "ntthal.exe"));
        var terminal = await engine.InspectAsync("GCGCGCAAAAAAGCGCGC", conditions, default);
        var internalHairpin = await engine.InspectAsync("GCGCGCAAAAAAGCGCGCTTTT", conditions, default);
        var noHairpin = await engine.InspectAsync(none.Sequence, conditions, default);
        Check(terminal.ThreePrimePaired && terminal.PairingPattern.Length == terminal.Sequence.Length &&
            terminal.TmC > 80 && terminal.DeltaGKcal < -7 && !internalHairpin.ThreePrimePaired && !noHairpin.HasStructure,
            "Real ntthal output supplies terminal pairing, structure Tm and delta G in kcal/mol");
        var referenceRule = settings with { AnnealingC = 85 };
        Check(Excludes(terminal, referenceRule) && !Excludes(internalHairpin, referenceRule),
            "Real structures distinguish near-annealing terminal and internal hairpins under the same reaction conditions");
        var alteredSalt = await engine.InspectAsync(terminal.Sequence, conditions with { MagnesiumMm = 0 }, default);
        Check(alteredSalt.RawOutput != terminal.RawOutput && alteredSalt.Conditions.MagnesiumMm == 0,
            "Hairpin screening uses the candidate reaction conditions rather than fixed salt concentrations");
        var run = JsonSerializer.Deserialize<TargetRun>(JsonSerializer.Serialize(original))!;
        var vm = new MainViewModel(Path.Combine(root, "src/QpcrPrimerStudio.Desktop/bin/Debug/net10.0-windows/tools"), Path.Combine(work, "hairpin-profile"));
        vm.Restore(new ProjectDocument { Targets = [target], Runs = [run], Parameters = p, AutoSpecificity = false });
        var ids = run.Candidates.Select(c => c.Id).ToArray(); var scores = run.Candidates.Select(c => c.Assessment.Score).ToArray();
        await vm.ApplyHairpinScreeningAsync(settings with { AnnealingC = 30, CheckTerminalHairpins = false });
        var report = run.HairpinScreening ?? throw new InvalidOperationException(vm.Status);
        var excluded = run.Candidates.Count(c => report.DecisionFor(c)?.Excluded == true);
        Check(excluded > 0 && excluded < run.Candidates.Count && vm.Status.Contains("筛选完成") &&
            ids.SequenceEqual(run.Candidates.Select(c => c.Id)) && scores.SequenceEqual(run.Candidates.Select(c => c.Assessment.Score)),
            "Real screening partitions candidates while preserving every original ID, sequence and score");
        Check(report.Decisions.Count == run.Candidates.Count && report.DeltaGTemperatureC == 37 && report.EngineSha256.Length == 64 &&
            report.Decisions.Where(d => d.Excluded).All(d => d.Reasons.Count > 0), "Every exclusion retains the thresholds, engine identity and raw evidence");
        var project = vm.Snapshot(); var path = Path.Combine(work, "hairpin-screened.qpcrproject");
        await ProjectStore.SaveAsync(path, project); var restored = await ProjectStore.LoadAsync(path);
        Check(restored.Runs[0].Candidates.Count == ids.Length && restored.Runs[0].HairpinScreening!.Decisions.Count == ids.Length && restored.HairpinSettings.AnnealingC == 30,
            "Project round-trip preserves excluded candidates, screening evidence and actual annealing temperature");
        var csv = Path.Combine(work, "hairpin-screened.csv"); var xlsx = Path.Combine(work, "hairpin-screened.xlsx");
        ResultExporter.Export(csv, restored, false); ResultExporter.Export(xlsx, restored, false);
        using (var reader = new StreamReader(csv))
        using (var table = new CsvReader(reader, CultureInfo.InvariantCulture))
            Check(table.GetRecords<ResultExporter.ExportRow>().Count() == ids.Length - excluded, "CSV exports retained candidates only after screening");
        using (var workbook = new XLWorkbook(xlsx))
            Check(workbook.Worksheet("Primers").LastRowUsed()!.RowNumber() == ids.Length - excluded + 1 &&
                workbook.Worksheet("Hairpin Screening").LastRowUsed()!.RowNumber() == ids.Length + 1 &&
                workbook.Worksheet("Primers").Row(1).CellsUsed().Any(c => c.GetString() == "Score"),
                "Excel exports scores and retained primers while keeping the full screening audit");
        var failedVm = new MainViewModel(Path.Combine(work, "missing-hairpin-tool"), Path.Combine(work, "failed-hairpin-profile"));
        failedVm.Restore(restored); var previousReport = failedVm.SelectedRun!.HairpinScreening;
        await failedVm.ApplyHairpinScreeningAsync(settings);
        Check(failedVm.Status.Contains("操作失败") && failedVm.SelectedRun.HairpinScreening == previousReport,
            "A missing thermodynamic engine preserves the previous completed filter");
        vm.ClearHairpinScreeningCommand.Execute(null);
        Check(run.HairpinScreening is null && vm.Candidates.Count == ids.Length, "Clearing screening restores all original candidates");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var stopped = false; try { await engine.ScreenAsync(run.Candidates, p, settings, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        Check(stopped, "Cancelled screening cannot produce a completed report");
        return count;
    }
}
