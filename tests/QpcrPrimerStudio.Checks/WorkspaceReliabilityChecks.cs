using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CsvHelper;
using QpcrPrimerStudio.Core;
using QpcrPrimerStudio.Desktop;

internal static class WorkspaceReliabilityChecks
{
    internal static async Task<int> RunAsync(string root, string work)
    {
        var folder = Path.Combine(work, "two-gene-workflow");
        Directory.CreateDirectory(folder);
        var failures = new List<string>(); var passed = 0;
        void Check(bool ok, string description)
        {
            if (!ok) failures.Add(description); else passed++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {description}");
        }
        var random = new Random(1729);
        string Template() => new(Enumerable.Range(0, 600).Select(_ => "ACGT"[random.Next(4)]).ToArray());
        var fasta = Path.Combine(folder, "two-genes.fa");
        await File.WriteAllTextAsync(fasta, $">gene-A\n{Template()}\n>gene-B\n{Template()}\n");
        var targets = SequenceFiles.Read(fasta);
        var engineFolder = Path.Combine(root, "src/QpcrPrimerStudio.Desktop/bin/Debug/net10.0-windows/tools");
        var profile = Path.Combine(folder, "profile");
        MainViewModel Open(ProjectDocument project)
        {
            var model = new MainViewModel(engineFolder, profile); model.Restore(project); return model;
        }
        var vm = Open(new() { Targets = targets, AutoSpecificity = false });
        await vm.DesignAllCommand.ExecuteAsync(null);
        Check(vm.Runs.Count == 2 && vm.Runs.All(r => r.AcceptedCount > 0), "Imported FASTA targets both have their own real paired design");
        vm.SelectedTarget = vm.Targets[0];
        await vm.ApplyHairpinScreeningAsync(new(20));
        var manual = vm.SelectedRun!.HairpinScreening!;
        var excluded = vm.Candidates.First(c => manual.DecisionFor(c)?.Excluded == true && c.Assessment.Accepted);
        await vm.AutoSaveSelectionAsync(excluded, true);
        vm.SelectedTarget = vm.Targets[1];
        var selectedB = vm.Candidates.First(c => c.Assessment.Accepted);
        await vm.AutoSaveSelectionAsync(selectedB, true);
        var reopened = new MainViewModel(engineFolder, profile);
        Check(await reopened.RecoverLastAutoSaveAsync() && reopened.SelectedTarget?.Id == "gene-B" &&
            reopened.SelectedRun?.Target.Id == "gene-B" && reopened.Runs.SelectMany(r => r.Candidates).Count(c => c.Selected) == 2,
            "Reopening the autosave restores the selected target, its results, and both manual selections");
        List<string> ExportChecked(string name)
        {
            var path = Path.Combine(folder, name + ".csv");
            ResultExporter.Export(path, reopened.Snapshot(), true);
            using var reader = new StreamReader(path); using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            csv.Read(); csv.ReadHeader(); var ids = new List<string>();
            while (csv.Read()) ids.Add(csv.GetField("CandidateId")!);
            return ids;
        }
        var before = ExportChecked("checked-before");
        Check(before.SequenceEqual([selectedB.Id]), "Ordinary export initially respects the manually applied hairpin exclusion");
        reopened.HairpinSettings = new(90); // Configure the smart pass; do not reapply the ordinary filter.
        var plan = await reopened.PrepareSmartExportAsync();
        Check(plan?.Choices.Count == 2, "Smart export ranks one pair per gene independently of manual checks");
        Check(ExportChecked("checked-after").SequenceEqual(before) &&
            reopened.Runs[0].HairpinScreening?.Settings.AnnealingC == 20 && reopened.Runs[1].HairpinScreening is null,
            "Opening smart preview preserves the ordinary export set and each applied manual filter");
        if (plan is not null) ResultExporter.ExportSmart(Path.Combine(folder, "smart.csv"), plan);
        reopened.SelectedTarget = reopened.Targets[0]; reopened.TemplateText += "A";
        reopened.SelectedTarget = reopened.Targets[1];
        var history = reopened.Snapshot().SmartExportReports.Count;
        var stale = await reopened.PrepareSmartExportAsync();
        Check(stale is null && reopened.Status.Contains("gene-A") && reopened.Snapshot().SmartExportReports.Count == history,
            "Smart preview identifies the edited background gene without exporting its old template");
        var runCount = reopened.Runs.Count;
        await reopened.DesignAllCommand.ExecuteAsync(null);
        Check(reopened.Runs.Count == runCount && reopened.Status.Contains("gene-A"),
            "Batch design stops before starting any target when a background template has pending edits");
        reopened.SelectedTarget = reopened.Targets[0];
        Check(reopened.TemplateText == targets[0].Sequence + "A", "The unapplied template draft survives navigation and both preflights");

        var retry = Open(await ProjectStore.LoadAsync(vm.ProjectPath)); var oldRuns = retry.Runs.Count;
        bool paused = false;
        PropertyChangedEventHandler pause = (_, e) =>
        {
            if (!paused && e.PropertyName == nameof(retry.Status) && retry.Status.StartsWith("设计 1/"))
            { paused = true; retry.PauseResumeCommand.Execute(null); }
        };
        NotifyCollectionChangedEventHandler cancel = (_, _) =>
        { if (paused && retry.Runs.Count == oldRuns + 1) retry.CancelCommand.Execute(null); };
        retry.PropertyChanged += pause; retry.Runs.CollectionChanged += cancel;
        try { await retry.DesignAllCommand.ExecuteAsync(null); }
        finally { retry.PropertyChanged -= pause; retry.Runs.CollectionChanged -= cancel; }
        Check(paused && retry.Runs.Count == oldRuns + 2 && retry.Runs[^2].State == "完成" &&
            retry.Runs[^1].Target.Id == "gene-B" && retry.Runs[^1].State == "取消",
            "Cancellation at a paused batch boundary records each unstarted target and keeps completed work");
        var afterCancel = await retry.PrepareSmartExportAsync();
        Check(afterCancel?.Report.Genes.Single(g => g.Gene == "gene-B") is { Status: "未导出" } miss && miss.Reason.Contains("取消"),
            "Smart export after cancellation never substitutes a previous run for the skipped gene");
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures));
        return passed;
    }
}
