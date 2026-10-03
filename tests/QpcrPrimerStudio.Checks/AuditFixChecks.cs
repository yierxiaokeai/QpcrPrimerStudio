using System.IO;
using System.Text.Json;
using QpcrPrimerStudio.Core;
using QpcrPrimerStudio.Desktop;

internal static class AuditFixChecks
{
    internal static async Task<int> RunAsync(string root, string work, ProjectDocument seed, string primer3)
    {
        int count = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); count++; Console.WriteLine("PASS: " + label); }
        static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ProjectStore.JsonOptions), ProjectStore.JsonOptions)!;
        var folder = Path.Combine(work, "audit-fixes"); Directory.CreateDirectory(folder);
        var tools = Path.Combine(root, "src/QpcrPrimerStudio.Desktop/bin/Debug/net10.0-windows/tools");
        MainViewModel Vm(string profile) => new(tools, Path.Combine(folder, profile));
        var target = seed.Targets[0] with { ExpectedSubjects = ["external-accession"] };
        var regions = new TargetRegionSettings { AvoidFivePrime = 0, AvoidThreePrime = 0, RequiredTranscripts = ["external-accession"] };
        var core = new Primer3Engine(primer3);
        var external = await core.DesignAsync(target, seed.Parameters, default, regions: regions);
        Check(external.AcceptedCount > 0 && external.Candidates.All(c => c.Assessment.MissingTranscripts.Count == 0),
            "External database expected IDs work without reference annotation in TemplateOnly mode");
        var reference = new ReferenceWorkspace { Version = "audit-fixes", Transcripts = [new() { Id = target.Id, GeneId = "A", Sequence = target.Sequence }] };
        var externalMapped = await core.DesignAsync(target, seed.Parameters, default, regions: regions, reference: reference);
        Check(externalMapped.AcceptedCount > 0, "External expected IDs do not become reference coverage requirements");
        bool invalidCoverage = false;
        try { await core.DesignAsync(target, seed.Parameters, default, regions: regions with { Expression = ExpressionMode.SelectedTranscripts }, reference: reference); }
        catch (ArgumentException ex) { invalidCoverage = ex.Message.Contains("同一基因"); }
        Check(invalidCoverage, "An unknown coverage ID produces a clear input error before candidate evaluation");

        var random = new Random(776655);
        var a = target with { GeneId = "A", ExpectedSubjects = [target.Id], CoverageTranscripts = [target.Id] };
        var b = a with { Id = "audit-geneB", GeneId = "B", Sequence = new string(Enumerable.Range(0, 600).Select(_ => "ACGT"[random.Next(4)]).ToArray()), ExpectedSubjects = ["audit-geneB"], CoverageTranscripts = ["audit-geneB"] };
        var twoReference = new ReferenceWorkspace { Version = "two-genes", Transcripts = [new() { Id = a.Id, GeneId = "A", Sequence = a.Sequence }, new() { Id = b.Id, GeneId = "B", Sequence = b.Sequence }] };
        foreach (var mode in new[] { ExpressionMode.TemplateOnly, ExpressionMode.SelectedTranscripts, ExpressionMode.TotalGene, ExpressionMode.IsoformSpecific })
        {
            var batch = Vm("batch-" + mode);
            batch.Restore(new() { Targets = [a, b], Parameters = seed.Parameters, Reference = twoReference, AutoSpecificity = false, RegionSettings = regions with { Expression = mode, RequiredTranscripts = [] } });
            batch.SelectedTarget = batch.Targets[1];
            await batch.DesignTargetsAsync(batch.Targets.ToList(), seed.Parameters, default);
            Check(batch.Runs.Count == 2 && batch.Runs.All(r => r.AcceptedCount > 0), "Real multi-gene batch succeeds for " + mode);
            Check(batch.Runs.All(r => r.RegionSettings.RequiredTranscripts.All(id => id == r.Target.Id)) &&
                batch.Runs.All(r => r.Candidates.All(c => c.Assessment.MissingTranscripts.Count == 0)), "Batch inputs and coverage stay owned by each target for " + mode);
        }

        var paths = EnginePaths.FromFolder(tools);
        var fasta = Path.Combine(folder, "specificity.fasta");
        await File.WriteAllTextAsync(fasta, $">{target.Id}\n{target.Sequence}\n>offtarget\n{target.Sequence}\n>third\n{target.Sequence}\n");
        var db = await new SpecificityEngine(paths).BuildDatabaseAsync(fasta, "audit hazards", "转录本", Path.Combine(folder, "databases"), default);
        var changedReference = Copy(reference); changedReference.Version = "replacement-reference";
        var history = Copy(seed.Runs[0]); var c0 = history.Candidates[0];
        var incomplete = await new SpecificityEngine(paths).CheckAsync(c0, history.Target, db, new(MaxSubjects: 2), folder, default);
        Check(incomplete.Status.Contains("不完整") && incomplete.Products.Any(p => !p.Expected), "Real limited BLAST search exposes known off-target products and incomplete coverage");
        c0.Specificity = [incomplete];
        CandidateQualityEngine.Refresh(c0, history, seed.Parameters, changedReference);
        Check(!c0.Assessment.Accepted && c0.Assessment.Rejections.Any(r => r.Contains("非靶向")) && c0.Assessment.Components.Single(c => c.Metric == "转录本特异性").Quality is null,
            "Known off-target products are rejected while incomplete specificity remains unknown");
        var vmRisk = Vm("reference-recheck");
        vmRisk.Restore(new() { Targets = [history.Target], Runs = [Copy(seed.Runs[0])], Parameters = seed.Parameters, Reference = changedReference, Databases = [db], AutoSpecificity = false });
        await vmRisk.CheckAllCommand.ExecuteAsync(null);
        Check(vmRisk.Runs[0].Candidates.All(c => !c.Assessment.Accepted && c.Assessment.Rejections.Any(r => r.Contains("非靶向"))), "Rechecking against a replacement reference updates old scores and all observed risk exclusions");
        var ntthal = Path.Combine(Path.GetDirectoryName(primer3)!, "ntthal.exe");
        var smart = await new SmartExportEngine(ntthal).PrepareAsync(vmRisk.Snapshot(), new(), default);
        Check(smart.Choices.Count == 0, "Intelligent export excludes risks discovered after the design reference changed");
        var stale = Copy(seed); stale.Runs[0].Candidates.ForEach(c => c.Specificity = [incomplete]);
        var stalePlan = await new SmartExportEngine(ntthal).PrepareAsync(stale, new(), default);
        Check(stalePlan.Choices.Count == 0 && stalePlan.Project.Runs[0].Candidates.All(c => !c.Assessment.Accepted), "Export refresh detects evidence added without updating the assessment");

        var conflictPath = Path.Combine(folder, "shared.qpcrproject"); await ProjectStore.SaveAsync(conflictPath, Copy(seed));
        var first = Vm("writer-a"); first.Restore(await ProjectStore.LoadAsync(conflictPath)); first.ProjectPath = conflictPath;
        var second = Vm("writer-b"); second.Restore(await ProjectStore.LoadAsync(conflictPath)); second.ProjectPath = conflictPath;
        Check(await first.AutoSaveSelectionAsync(first.Runs[0].Candidates[0], true), "First session saves against its loaded disk revision");
        Check(!await second.AutoSaveSelectionAsync(second.Runs[0].Candidates[1], true) && second.HasUnsavedChanges && second.AutoSaveStatus.Contains("冲突"), "A second session reports conflicting changes without marking them saved");
        var copies = Directory.GetFiles(folder, "shared.qpcrproject.conflict-*.qpcrproject");
        var original = await ProjectStore.LoadAsync(conflictPath); var conflict = await ProjectStore.LoadAsync(copies.Single());
        Check(original.Runs[0].Candidates[0].Selected && !original.Runs[0].Candidates[1].Selected && conflict.Runs[0].Candidates[1].Selected,
            "Conflict handling preserves both independently selected project versions");
        var nextPath = Path.Combine(folder, "conflict-independent.qpcrproject"); second.ProjectPath = nextPath;
        Check(await second.AutoSaveSelectionAsync(second.Runs[0].Candidates[1], true), "Conflicted changes can be saved to an independent project");
        // Launch concurrent writers from the same on-disk revision to exercise the cross-session lock.
        var racePath = Path.Combine(folder, "race.qpcrproject"); await ProjectStore.SaveAsync(racePath, Copy(seed));
        var raceA = await ProjectStore.LoadAsync(racePath); var raceB = await ProjectStore.LoadAsync(racePath);
        raceA.Name = "writer A"; raceB.Name = "writer B";
        async Task<bool> Race(ProjectDocument p) { try { await ProjectStore.SaveAsync(racePath, p); return true; } catch (ProjectConflictException) { return false; } }
        var raceResults = await Task.WhenAll(Race(raceA), Race(raceB));
        Check(raceResults.Count(x => x) == 1 && Directory.GetFiles(folder, "race.qpcrproject.conflict-*.qpcrproject").Length == 1, "Concurrent revision checks allow one writer and preserve the competing version");

        var draftVm = Vm("multi-drafts"); draftVm.Restore(new() { Targets = [a, b], Parameters = seed.Parameters });
        var rawA = a.Sequence + "\r\n待核对 A"; var rawB = ">B\r\nACGT\r\n未完成 B";
        draftVm.TemplateText = rawA; draftVm.ExpectedText = "external-A"; draftVm.CoverageText = a.Id;
        draftVm.SelectedTarget = b; draftVm.TemplateText = rawB; draftVm.SelectedTarget = a;
        Check(draftVm.TemplateText == rawA && draftVm.ExpectedText == "external-A", "Switching genes restores the exact invalid raw draft and external IDs");
        var draftPath = Path.Combine(folder, "multi-drafts.qpcrproject"); await ProjectStore.SaveAsync(draftPath, draftVm.Snapshot());
        var recoveredDrafts = Vm("recovered-multi-drafts"); recoveredDrafts.Restore(await ProjectStore.LoadAsync(draftPath)); recoveredDrafts.SelectedTarget = recoveredDrafts.Targets[1];
        Check(recoveredDrafts.TemplateText == rawB && recoveredDrafts.Snapshot().TargetDrafts[a.Id].TemplateText == rawA, "Project round-trip preserves independent unfinished drafts for both genes");

        var retryVm = Vm("retry-history"); var success = Copy(seed.Runs[0]); success.Target = a;
        var oldFailed = new TargetRun { Target = a with { Sequence = a.Sequence + "A" }, State = "失败" };
        retryVm.Restore(new() { Targets = [a, b], Runs = [oldFailed, success, new() { Target = b, State = "无候选" }], Parameters = seed.Parameters, AutoSpecificity = false });
        await retryVm.RetryFailedCommand.ExecuteAsync(null);
        Check(retryVm.Runs.Count == 4 && retryVm.Runs.Last().Target == b && retryVm.Runs.Last().AcceptedCount > 0, "Retry ignores obsolete failures and retries only the latest failed current template");

        var active = Vm("invalid-load"); active.Restore(Copy(seed)); var before = JsonSerializer.Serialize(active.Snapshot(), ProjectStore.JsonOptions);
        var malformed = Copy(seed); malformed.Runs[0].Candidates[0].Forward = null!;
        bool restoreRejected = false; try { active.Restore(malformed); } catch (InvalidDataException) { restoreRejected = true; }
        Check(restoreRejected && JsonSerializer.Serialize(active.Snapshot(), ProjectStore.JsonOptions) == before, "Invalid candidate graph is rejected before changing the active project");
        var badPath = Path.Combine(folder, "malformed.qpcrproject");
        // Edit serialized JSON structurally: serialization would otherwise evaluate the null graph's computed members.
        var badJson = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(seed, ProjectStore.JsonOptions))!;
        badJson["Runs"]![0]!["Candidates"]![0]!["Forward"] = null; await File.WriteAllTextAsync(badPath, badJson.ToJsonString());
        bool loadRejected = false; try { await ProjectStore.LoadAsync(badPath); } catch (InvalidDataException) { loadRejected = true; }
        Check(loadRejected && JsonSerializer.Serialize(active.Snapshot(), ProjectStore.JsonOptions) == before, "Malformed JSON candidate data cannot replace the current project");
        var wrongReference = Copy(seed); wrongReference.Runs[0].Candidates[0].TargetId = "wrong-target";
        bool referenceRejected = false; try { ProjectValidation.Validate(wrongReference); } catch (InvalidDataException) { referenceRejected = true; }
        Check(referenceRejected, "Candidate target references are validated before restore");
        active.BindDatabase(db);
        Check(active.HasUnsavedChanges && active.Snapshot().Databases.Single() == db, "Binding a newly built database marks the project as changed");
        var boundPath = Path.Combine(folder, "database-bound.qpcrproject"); await ProjectStore.SaveAsync(boundPath, active.Snapshot());
        var boundVm = Vm("database-restored"); boundVm.Restore(await ProjectStore.LoadAsync(boundPath));
        Check(boundVm.Snapshot().Databases.Single().SourceSha256 == db.SourceSha256 && boundVm.DatabaseSummary.Contains(db.Name), "Saved database identity and summary restore correctly");

        var large = Copy(seed); large.Runs[0].RawOutput += new string('A', 2 * 1024 * 1024);
        var backupPath = Path.Combine(folder, "large-project.qpcrproject"); await ProjectStore.SaveAsync(backupPath, large);
        for (var i = 0; i < 15; i++) { large.Runs[0].Candidates[0].Selected = i % 2 == 0; await ProjectStore.SaveAsync(backupPath, large); }
        var backups = ProjectBackups.List(backupPath);
        Check(backups.Count == 15 && backups.Count(b => !b.Archived) <= ProjectBackups.RecentLimit && backups.Count(b => b.Archived) == 5,
            "Repeated checkbox saves keep a bounded recent set and archive every older recovery record");
        Check(backups.Sum(b => b.Bytes) < new FileInfo(backupPath).Length && backups.Where(b => !b.Archived).Sum(b => b.Bytes) <= ProjectBackups.RecentBytesLimit,
            "Compressed backup storage reduces actual repeated-save disk use and observes its recent byte limit");
        var archived = backups.Last(b => b.Archived); var restoredBackup = await ProjectBackups.LoadAsync(archived.Path);
        Check(restoredBackup.Runs[0].RawOutput == large.Runs[0].RawOutput, "An archived compressed record preserves the complete original engine output");
        var backupVm = Vm("restore-backup"); backupVm.Restore(Copy(seed)); await backupVm.RestoreBackupAsync(archived.Path);
        Check(backupVm.ProjectPath == "尚未保存" && backupVm.HasUnsavedChanges && backupVm.Runs[0].RawOutput == large.Runs[0].RawOutput, "Backup recovery opens an independent unsaved project and preserves the active disk file");
        await File.WriteAllTextAsync(Path.Combine(folder, "backup-footprint.json"), JsonSerializer.Serialize(new { ProjectBytes = new FileInfo(backupPath).Length, CompressedBytes = backups.Sum(b => b.Bytes), Records = backups.Count, Archived = backups.Count(b => b.Archived) }, ProjectStore.JsonOptions));
        return count;
    }
}
