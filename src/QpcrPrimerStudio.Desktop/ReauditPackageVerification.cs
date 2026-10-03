using System.IO;
using System.Text.Json;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

internal static class ReauditPackageVerification
{
    internal static async Task CheckAsync(string folder, EnginePaths paths, ProjectDocument seed)
    {
        static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ProjectStore.JsonOptions), ProjectStore.JsonOptions)!;
        var work = Path.Combine(folder, "independent-reaudit"); Directory.CreateDirectory(work);
        var vm = new MainViewModel(storage: Path.Combine(work, "profile")); vm.Restore(Copy(seed));
        var target = vm.SelectedTarget!;
        vm.ManualForward = "raw F draft"; vm.ManualReverse = "raw R draft";
        if (!vm.HasUnsavedChanges) throw new InvalidOperationException("Portable manual primer draft was not marked modified.");
        var path = Path.Combine(work, "draft.qpcrproject"); await ProjectStore.SaveAsync(path, vm.Snapshot());
        vm.Restore(await ProjectStore.LoadAsync(path));
        if (vm.ManualForward != "raw F draft" || vm.ManualReverse != "raw R draft") throw new InvalidOperationException("Portable manual primer draft did not survive disk recovery.");
        vm.AddTargetCommand.Execute(null); vm.TargetName = "pending-gene"; vm.TemplateText = "unfinished 原始草稿";
        vm.SelectedTarget = vm.Targets[0]; vm.AddTargetCommand.Execute(null);
        if (vm.TargetName != "pending-gene" || vm.TemplateText != "unfinished 原始草稿") throw new InvalidOperationException("Portable unattached draft was lost during navigation.");
        await ProjectStore.SaveAsync(path, vm.Snapshot()); vm.Restore(await ProjectStore.LoadAsync(path));
        if (vm.TemplateText != "unfinished 原始草稿") throw new InvalidOperationException("Portable unattached draft was lost during disk recovery.");
        vm.SelectedTarget = vm.Targets[0]; vm.TemplateText = target.Sequence + "\nunfinished";
        vm.EditSelectedCommand.Execute(null);
        if (!vm.TemplateText.EndsWith("unfinished")) throw new InvalidOperationException("Portable candidate editing overwrote a template draft.");
        vm.Restore(Copy(seed)); vm.SelectedCandidate!.Locked = true; vm.CandidateNotes = "portable revision";
        var original = vm.SelectedCandidate; vm.NotesCommand.Execute(null);
        if (vm.SelectedCandidate == original || !vm.Candidates.Contains(vm.SelectedCandidate!) || vm.SelectedCandidate!.Notes != "portable revision")
            throw new InvalidOperationException("Portable locked-candidate revision was hidden.");
        var invalid = Copy(seed); var sequence = invalid.Targets[0].Sequence;
        invalid.Reference = new() { Transcripts = [new() { Id = target.Id, Sequence = sequence,
            Exons = [new("chr", 1, long.MaxValue, false, 1, sequence.Length)] }], Variants = [new("chr", 1, 1, "bad", "A", "T", null, "PASS")] };
        var before = JsonSerializer.Serialize(vm.Snapshot(), ProjectStore.JsonOptions);
        bool rejected = false; try { vm.Restore(invalid); } catch (InvalidDataException) { rejected = true; }
        if (!rejected || JsonSerializer.Serialize(vm.Snapshot(), ProjectStore.JsonOptions) != before) throw new InvalidOperationException("Portable invalid reference restore changed the active project.");
        var fasta = Path.Combine(work, "clean.fasta"); await File.WriteAllTextAsync(fasta, $">{target.Id}\n{sequence}\n");
        var db = await new SpecificityEngine(paths).BuildDatabaseAsync(fasta, "clean", "转录本", Path.Combine(work, "databases"), default);
        var project = Copy(seed); var run = project.Runs[0]; var candidate = run.Candidates[0];
        run.Candidates = [candidate];
        candidate.Specificity = [await new SpecificityEngine(paths).CheckAsync(candidate, run.Target, db, new(), work, default)];
        project.Databases = [db with { SourceSha256 = "changed-database", Prefix = db.Prefix + "-changed" }];
        var ntthal = Path.Combine(Path.GetDirectoryName(paths.Primer3)!, "ntthal.exe");
        var plan = await new SmartExportEngine(ntthal).PrepareAsync(project, new(), default);
        if (plan.Choices.Count != 0) throw new InvalidOperationException("Portable intelligent export used specificity from a replaced database.");
        var export = Path.Combine(work, "ordinary.csv"); await File.WriteAllTextAsync(export, "previous export");
        ResultExporter.Export(export, seed, false);
        if (!File.ReadAllText(export).Contains("Accepted") || !Directory.EnumerateFiles(work, "ordinary.csv.backup-*").Any())
            throw new InvalidOperationException("Portable ordinary export lost prior data or risk columns.");
    }
}
