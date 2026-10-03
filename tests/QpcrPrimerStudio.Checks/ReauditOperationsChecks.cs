using System.IO;
using System.Text.Json;
using QpcrPrimerStudio.Core;
using QpcrPrimerStudio.Desktop;

internal static class ReauditOperationsChecks
{
    internal static async Task<int> RunAsync(string root, string work, ProjectDocument seed)
    {
        int count = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); count++; Console.WriteLine("PASS: " + label); }
        static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ProjectStore.JsonOptions), ProjectStore.JsonOptions)!;
        var folder = Path.Combine(work, "reaudit-operations"); Directory.CreateDirectory(folder);
        var vm = new MainViewModel(storage: Path.Combine(folder, "restore-profile")); vm.Restore(Copy(seed));
        var original = JsonSerializer.Serialize(vm.Snapshot(), ProjectStore.JsonOptions);
        var invalid = Copy(seed);
        invalid.Reference = new() { Transcripts = [new() { Id = invalid.Targets[0].Id, Sequence = invalid.Targets[0].Sequence,
            Exons = [new("chr", 1, long.MaxValue, false, 1, invalid.Targets[0].Sequence.Length)] }], Variants = [new("chr", 1, 1, "v", "A", "G", null, "PASS")] };
        bool rejected = false; try { vm.Restore(invalid); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && JsonSerializer.Serialize(vm.Snapshot(), ProjectStore.JsonOptions) == original,
            "Invalid genomic/cDNA exon lengths are rejected before any restore mutation");
        invalid = Copy(seed); invalid.Parameters = invalid.Parameters with { MinLength = -7 };
        rejected = false; try { ProjectValidation.Validate(invalid); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Invalid global parameter values are rejected during project validation");
        invalid = Copy(seed); invalid.Targets[0] = invalid.Targets[0] with { ParameterOverride = invalid.Parameters with { PrimerNm = -5 } };
        rejected = false; try { ProjectValidation.Validate(invalid); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Invalid target override concentrations are rejected as structural errors");
        invalid = Copy(seed); invalid.Parameters = invalid.Parameters with { IncludedStart = int.MaxValue, IncludedLength = 10 };
        rejected = false; try { ProjectValidation.Validate(invalid); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Persisted design region arithmetic rejects integer overflow");
        var failed = Copy(seed); failed.Runs.Clear(); failed.Runs.Add(new() { Target = failed.Targets[0], State = "失败", Message = "区域超出模板", UsedParameters = failed.Parameters with { IncludedStart = int.MaxValue } });
        failed.Parameters = failed.Parameters with { IncludedStart = failed.Targets[0].Sequence.Length + 10 };
        ProjectValidation.Validate(failed);
        Check(true, "A failed target can retain its diagnostic parameters when the global settings are structurally valid");
        foreach (var change in new Action<PrimerCandidate>[] {
            c => c.Forward = c.Forward with { End = c.Forward.End + 1 },
            c => c.Forward = c.Forward with { Sequence = new string(c.Forward.Sequence[0] == 'A' ? 'C' : 'A', c.Forward.Sequence.Length) },
            c => c.ProductLength++, c => c.Amplicon = c.Amplicon[..^1] })
        {
            invalid = Copy(seed); change(invalid.Runs[0].Candidates[0]);
            rejected = false; try { ProjectValidation.Validate(invalid); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Corrupt candidate sequence/coordinate/amplicon invariant is rejected");
        }
        var ambiguous = Copy(seed); ambiguous.Runs = [ambiguous.Runs[0]];
        var run = ambiguous.Runs[0]; var chars = run.Target.Sequence.ToCharArray(); chars[run.Candidates[0].Forward.Start - 1] = 'N';
        run.Target = run.Target with { Sequence = new string(chars) }; ambiguous.Targets = [run.Target];
        foreach (var candidate in run.Candidates)
        {
            candidate.Amplicon = run.Target.Sequence.Substring(candidate.Forward.Start - 1, candidate.ProductLength);
            if (candidate.Provenance is { } provenance) candidate.Provenance = provenance with { TemplateSha256 = run.Target.Sha256 };
        }
        ProjectValidation.Validate(ambiguous);
        Check(true, "Compatible N/IUPAC template histories remain loadable");
        var overlapTarget = seed.Targets[0] with { Sequence = seed.Targets[0].Sequence[..100], ParameterOverride = null, ExonBlocks = [], Junctions = [] };
        var overlap = new PrimerCandidate { TargetId = overlapTarget.Id,
            Forward = new(overlapTarget.Sequence[..30], 1, 30, 60, 50, 0, 0, 0, "", ""),
            Reverse = new(SequenceFiles.ReverseComplement(overlapTarget.Sequence.Substring(20, 30)), 21, 50, 60, 50, 0, 0, 0, "", ""),
            ProductLength = 50, Amplicon = overlapTarget.Sequence[..50] };
        ProjectValidation.Validate(new() { Targets = [overlapTarget], Runs = [new() { Target = overlapTarget, Candidates = [overlap], UsedParameters = new() { MinProduct = 40, MaxLength = 36 } }] });
        Check(true, "A consistent historical pair can retain legitimately overlapping binding sites");
        invalid = Copy(seed); invalid.Runs.Add(Copy(invalid.Runs[0]));
        rejected = false; try { ProjectValidation.Validate(invalid); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Candidate IDs are unique across project runs");
        foreach (var draft in new[] {
            new TemplateEditorDraft(seed.Targets[0].Id, "draft", "unfinished", "", "") { InputTabIndex = 2 },
            new TemplateEditorDraft(seed.Targets[0].Id, "draft", "unfinished", "", "") { SearchKind = "Unknown" },
            new TemplateEditorDraft(seed.Targets[0].Id, "draft", "unfinished", "", "") { ParentCandidateId = seed.Runs[0].Candidates[0].Id, EditTemplateSha256 = "wrong" } })
        {
            invalid = Copy(seed); invalid.EditorDraft = draft;
            rejected = false; try { ProjectValidation.Validate(invalid); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Invalid editor draft mode or parent/template identity is rejected before restoring");
        }
        invalid = Copy(seed); invalid.UnassignedDraft = new(seed.Targets[0].Id, "draft", "unfinished", "", "");
        rejected = false; try { ProjectValidation.Validate(invalid); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "An unassigned draft cannot claim a committed target identity");
        invalid = Copy(seed); invalid.Runs[0].Candidates[0].ProductLength++;
        var invalidPlan = new SmartExportPlan(invalid, [new(invalid.Targets[0].Id, 0, 0)], new()) { ContentFingerprint = SmartExportEngine.DesignFingerprint(invalid) };
        var invalidExportPath = Path.Combine(folder, "invalid-smart.csv");
        rejected = false; try { ResultExporter.ExportSmart(invalidExportPath, invalidPlan); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && !File.Exists(invalidExportPath), "Direct smart export rejects a corrupt candidate graph before writing any file");
        invalid.Runs[0].Candidates[0].Selected = true;
        rejected = false; try { ResultExporter.Export(Path.Combine(folder, "invalid-ordinary.csv"), invalid, true); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && !File.Exists(Path.Combine(folder, "invalid-ordinary.csv")), "Direct ordinary export rejects a corrupt candidate graph before writing any file");
        vm.TemplateText += "\n未完成模板草稿";
        var rawPath = Path.Combine(folder, "raw-draft.qpcrproject"); await ProjectStore.SaveAsync(rawPath, vm.Snapshot());
        Check((await ProjectStore.LoadAsync(rawPath)).EditorDraft!.TemplateText.EndsWith("未完成模板草稿"), "An unfinished original template draft round-trips without template application");
        var cancelPath = Path.Combine(folder, "canceled.qpcrproject"); using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); try { await ProjectStore.SaveAsync(cancelPath, Copy(seed), cancel.Token); } catch (OperationCanceledException) { }
        }
        Check(!File.Exists(cancelPath) && Directory.GetFiles(folder, "canceled*.pending").Length == 0,
            "Pre-canceled saves create no primary or untracked temporary files");
        var backupSource = Path.Combine(folder, "backup-source.qpcrproject"); await ProjectStore.SaveAsync(backupSource, Copy(seed));
        var createBackup = typeof(ProjectBackups).GetMethod("CreateAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        using (var hold = new FileStream(backupSource, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            rejected = false;
            try { await (Task)createBackup.Invoke(null, [backupSource, CancellationToken.None])!; } catch (IOException) { rejected = true; }
        }
        Check(rejected && ProjectBackups.List(backupSource).Count == 0 &&
            Directory.GetFiles(Path.Combine(ProjectBackups.Folder(backupSource), ".recovery"), "*.pending").Length == 1,
            "A failed compressed backup is isolated and never appears as a recoverable archive");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); rejected = false;
            try { await (Task)createBackup.Invoke(null, [backupSource, cancel.Token])!; } catch (OperationCanceledException) { rejected = true; }
        }
        Check(rejected && ProjectBackups.List(backupSource).Count == 0,
            "A canceled compressed backup does not install any unverified archive");
        var lockedPath = Path.Combine(folder, "locked.qpcrproject"); await ProjectStore.SaveAsync(lockedPath, Copy(seed));
        var lockedHash = Hashing.File(lockedPath); var next = await ProjectStore.LoadAsync(lockedPath); next.Name = "later";
        using (var hold = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            rejected = false; try { await ProjectStore.SaveAsync(lockedPath, next); } catch (IOException) { rejected = true; }
        }
        Check(rejected && Hashing.File(lockedPath) == lockedHash && Directory.GetFiles(lockedPath + ".recovery", "*.pending").Length == 1,
            "An occupied primary file stays intact and its failed save snapshot is isolated for recovery");
        var profile = Path.Combine(folder, "pointer-profile"); vm = new(storage: profile); vm.Restore(Copy(seed));
        var autoPath = Path.Combine(folder, "autosaved.qpcrproject"); vm.ProjectPath = autoPath;
        var pointer = Path.Combine(profile, "last-auto-save.json"); await File.WriteAllTextAsync(pointer, JsonSerializer.Serialize(new { ProjectPath = lockedPath }));
        bool saved;
        using (var hold = new FileStream(pointer, FileMode.Open, FileAccess.Read, FileShare.Read))
            saved = await vm.AutoSaveSelectionAsync(vm.Runs[0].Candidates[0], true);
        Check(saved && File.Exists(autoPath) && !vm.HasUnsavedChanges && vm.AutoSaveStatus.Contains("恢复入口更新失败") && vm.Status.Contains("手动打开"),
            "A recovery pointer sharing failure preserves the successful primary save and exposes a specific warning");
        var pointerOnly = Path.Combine(folder, "concurrent-pointer.json");
        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => ProjectStore.WriteRecoveryPointerAsync(pointerOnly, Path.Combine(folder, "project-" + i)))));
        using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(pointerOnly)))
            Check(json.RootElement.GetProperty("ProjectPath").GetString()!.Contains("project-"), "Concurrent recovery pointer writers install one complete JSON record");
        var exportPath = Path.Combine(folder, "ordinary.csv"); await File.WriteAllTextAsync(exportPath, "PREVIOUS_EXPORT");
        var exportSeed = Copy(seed); foreach (var c in exportSeed.Runs.SelectMany(r => r.Candidates)) c.Selected = true;
        using (var hold = new FileStream(exportPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            rejected = false; try { ResultExporter.Export(exportPath, exportSeed, true); } catch (IOException) { rejected = true; }
        }
        Check(rejected && await File.ReadAllTextAsync(exportPath) == "PREVIOUS_EXPORT", "Ordinary export replacement failure preserves the existing CSV");
        ResultExporter.Export(exportPath, exportSeed, true);
        Check(Directory.GetFiles(folder, "ordinary.csv.backup-*").Length == 1 && await File.ReadAllTextAsync(Directory.GetFiles(folder, "ordinary.csv.backup-*")[0]) == "PREVIOUS_EXPORT",
            "A successful ordinary export retains a complete previous-file backup");
        return count;
    }
}
