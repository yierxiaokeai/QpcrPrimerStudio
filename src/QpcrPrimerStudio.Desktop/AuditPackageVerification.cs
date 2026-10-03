using System.IO;
using System.Text.Json;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

internal static class AuditPackageVerification
{
    internal static async Task CheckAsync(string folder, EnginePaths paths, ProjectDocument seed)
    {
        static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ProjectStore.JsonOptions), ProjectStore.JsonOptions)!;
        var work = Path.Combine(folder, "audit-repairs"); Directory.CreateDirectory(work);
        MainViewModel Vm(string name) => new(storage: Path.Combine(work, name));
        var target = seed.Targets[0]; var second = target with { Id = "portable-coverage-B", ExpectedSubjects = ["portable-coverage-B"], CoverageTranscripts = ["portable-coverage-B"] };
        var a = target with { CoverageTranscripts = [target.Id] };
        var reference = new ReferenceWorkspace { Version = "portable-new-reference", Transcripts = [new() { Id = a.Id, GeneId = "A", Sequence = a.Sequence }, new() { Id = second.Id, GeneId = "B", Sequence = second.Sequence }] };
        var batch = Vm("batch"); batch.Restore(new() { Targets = [a, second], Parameters = seed.Parameters, Reference = reference, AutoSpecificity = false,
            RegionSettings = new() { Expression = ExpressionMode.SelectedTranscripts, AvoidFivePrime = 0, AvoidThreePrime = 0 } });
        batch.SelectedTarget = second; await batch.DesignTargetsAsync(batch.Targets.ToList(), seed.Parameters, default);
        if (batch.Runs.Any(r => r.AcceptedCount == 0 || r.RegionSettings.RequiredTranscripts.Any(id => id != r.Target.Id)))
            throw new InvalidOperationException("Portable per-target batch coverage failed.");
        var fasta = Path.Combine(work, "hazards.fasta");
        await File.WriteAllTextAsync(fasta, $">{target.Id}\n{target.Sequence}\n>off-target\n{target.Sequence}\n>third\n{target.Sequence}\n");
        var db = await new SpecificityEngine(paths).BuildDatabaseAsync(fasta, "portable risk", "转录本", Path.Combine(work, "databases"), default);
        var risk = Vm("risk"); risk.Restore(Copy(seed)); risk.BindDatabase(db); risk.MaxSubjects = "2";
        if (!risk.HasUnsavedChanges) throw new InvalidOperationException("Portable database binding is not marked modified.");
        await risk.CheckAllCommand.ExecuteAsync(null);
        if (risk.Runs.SelectMany(r => r.Candidates).Any(c => c.Assessment.Accepted || !c.Specificity.Any(s => s.Status.Contains("不完整") && s.Products.Any(p => !p.Expected))))
            throw new InvalidOperationException("Portable incomplete-search risk rejection failed.");
        var smart = await risk.PrepareSmartExportAsync();
        if (smart is null || smart.Choices.Count != 0) throw new InvalidOperationException("Portable stale-evidence intelligent export failed.");
        var drafts = Vm("drafts"); drafts.Restore(Copy(seed)); drafts.Targets.Add(second);
        drafts.TemplateText = target.Sequence + "\n未完成 A"; drafts.SelectedTarget = second; drafts.TemplateText = "待核对 B"; drafts.SelectedTarget = drafts.Targets[0];
        if (!drafts.TemplateText.EndsWith("未完成 A")) throw new InvalidOperationException("Portable per-gene draft navigation failed.");
        var searches = new SinglePrimerSearch { Target = second, Reverse = true, Parameters = seed.Parameters, Primers = [seed.Runs[0].Candidates[0].Reverse] };
        drafts.SinglePrimerSearches.Add(searches); drafts.SelectedSingleSearch = searches;
        if (drafts.SelectedTarget != second || !drafts.ResultContext.Contains("R 单引物")) throw new InvalidOperationException("Portable single-search navigation failed.");
        var original = JsonSerializer.Serialize(drafts.Snapshot(), ProjectStore.JsonOptions); var invalid = Copy(seed); invalid.Runs[0].Candidates[0].Forward = null!;
        bool rejected = false; try { drafts.Restore(invalid); } catch (InvalidDataException) { rejected = true; }
        if (!rejected || JsonSerializer.Serialize(drafts.Snapshot(), ProjectStore.JsonOptions) != original) throw new InvalidOperationException("Portable invalid-project restore was destructive.");
        var path = Path.Combine(work, "shared.qpcrproject"); await ProjectStore.SaveAsync(path, Copy(seed));
        var first = Vm("first"); first.Restore(await ProjectStore.LoadAsync(path)); first.ProjectPath = path;
        var later = Vm("later"); later.Restore(await ProjectStore.LoadAsync(path)); later.ProjectPath = path;
        if (!await first.AutoSaveSelectionAsync(first.Runs[0].Candidates[0], true) || await later.AutoSaveSelectionAsync(later.Runs[0].Candidates[1], true) || !later.AutoSaveStatus.Contains("冲突"))
            throw new InvalidOperationException("Portable competing saves lost a project revision.");
        var backup = ProjectBackups.List(path).Single(); var previous = await ProjectBackups.LoadAsync(backup.Path);
        if (previous.Runs[0].RawOutput != seed.Runs[0].RawOutput) throw new InvalidOperationException("Portable compressed recovery lost engine output.");
        await drafts.RestoreBackupAsync(backup.Path);
        if (drafts.ProjectPath != "尚未保存" || !drafts.HasUnsavedChanges) throw new InvalidOperationException("Portable backup recovery did not create an independent project.");
    }
}
