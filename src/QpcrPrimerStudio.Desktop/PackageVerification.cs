using System.IO;
using System.Text.Json;
using System.Windows;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

internal static class PackageVerification
{
    internal static async Task RunAsync(Application app, string reportPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Directory.CreateDirectory(folder);
        var tools = Path.Combine(AppContext.BaseDirectory, "tools");
        var paths = EnginePaths.FromFolder(tools);
        var random = new Random(1729);
        var target = new SequenceTarget { Id = "package.t1", Sequence = new string(Enumerable.Range(0, 600).Select(_ => "ACGT"[random.Next(4)]).ToArray()) };
        var parameters = new DesignParameters();
        var recommendation = await new ParameterRecommendationEngine(paths.Primer3).RecommendAsync(target,
            parameters, RecommendationSearchKind.Pairs, "", "", CancellationToken.None);
        if (recommendation.Parameters is null || recommendation.Trials[^1].Retained == 0)
            throw new InvalidOperationException("Bundled parameter recommendation failed.");
        var run = await new Primer3Engine(paths.Primer3).DesignAsync(target, parameters, CancellationToken.None);
        if (run.Candidates.Count == 0) throw new InvalidOperationException("Bundled Primer3 returned no candidates.");
        var fastaPath = Path.Combine(folder, "portable-input.fasta");
        await File.WriteAllTextAsync(fastaPath, $">{target.Id}\n{target.Sequence}\n");
        if (SequenceFiles.Read(fastaPath).Single().Sequence != target.Sequence || SequenceFiles.CountFasta(fastaPath) != 1)
            throw new InvalidOperationException("Bundled FASTA import failed.");
        var genBankPath = Path.Combine(folder, "portable-input.gb");
        var genBankSequence = new Bio.Sequence(Bio.Alphabets.DNA, target.Sequence) { ID = target.Id };
        genBankSequence.Metadata["GenBank"] = new Bio.IO.GenBank.GenBankMetadata
        {
            Locus = new Bio.IO.GenBank.GenBankLocusInfo { Name = target.Id, SequenceLength = target.Sequence.Length,
                SequenceType = "bp", MoleculeType = Bio.IO.GenBank.MoleculeType.DNA,
                StrandTopology = Bio.IO.GenBank.SequenceStrandTopology.Linear, Date = new DateTime(2026, 1, 1) }
        };
        using (var output = File.Create(genBankPath))
            new Bio.IO.GenBank.GenBankFormatter().Format(output, genBankSequence);
        if (SequenceFiles.Read(genBankPath).Single().Sequence != target.Sequence)
            throw new InvalidOperationException("Bundled GenBank import failed.");
        var candidate = run.Candidates[0];
        var structure = await new Thermodynamics(Path.Combine(tools, "primer3", "ntthal.exe"))
            .AnalyzeAsync(candidate.Forward.Sequence, candidate.Reverse.Sequence, parameters, CancellationToken.None);
        if (!structure.Contains("dG")) throw new InvalidOperationException("Bundled thermodynamics check failed.");
        var blast = await ProcessRunner.RunAsync(paths.BlastN, ["-version"], null, CancellationToken.None);
        var parser = await ProcessRunner.RunAsync(Path.Combine(tools, "reference", "node.exe"),
            ["--input-type=module", "-e", "await import('@gmod/gff'); await import('@gmod/vcf'); console.log('GMOD parsers loaded')"], null,
            CancellationToken.None, workingDirectory: Path.Combine(tools, "reference"));
        var library = new PrimerLibrary(Path.Combine(folder, "library.sqlite"));
        library.Save(candidate);
        if (library.Read().Count != 1) throw new InvalidOperationException("Bundled SQLite check failed.");
        var vm = new MainViewModel(storage: Path.Combine(folder, "profile"));
        vm.Restore(new ProjectDocument { Targets = [target], Runs = [run], Parameters = parameters });
        vm.ApplySearchParameters(new(PrimerSearchKind.Pairs, recommendation.Parameters, "", "")
            { ApplyToWholeProject = true, Recommendation = recommendation });
        var other = target with { Id = "package.t2", Sequence = target.Sequence + "ACGT" };
        var otherRun = await new Primer3Engine(paths.Primer3).DesignAsync(other, parameters, CancellationToken.None);
        vm.Targets.Add(other); vm.Runs.Add(otherRun); vm.SelectedTarget = other;
        if (vm.SelectedRun != otherRun || vm.Candidates.Any(c => c.TargetId != other.Id) || vm.TemplateText != other.Sequence || !vm.SelectedOnly)
            throw new InvalidOperationException("Bundled target navigation or checked-only export default failed.");
        var smart = await vm.PrepareSmartExportAsync() ?? throw new InvalidOperationException(vm.Status);
        if (smart.Choices.Count != 2 || smart.MissingGenes != 0) throw new InvalidOperationException("Bundled intelligent export preparation failed.");
        var smartFiles = ResultExporter.ExportSmart(Path.Combine(folder, "portable-smart.xlsx"), smart)
            .Concat(ResultExporter.ExportSmart(Path.Combine(folder, "portable-smart.csv"), smart)).ToList();
        if (smartFiles.Count != 3 || smartFiles.Any(p => new FileInfo(p).Length == 0))
            throw new InvalidOperationException("Bundled intelligent export writing failed.");
        var uncheckedProject = vm.Snapshot();
        uncheckedProject.Databases = [new("unchecked", "转录本", "unchecked", "unchecked", "unchecked", 1, DateTimeOffset.Now, "fixture")];
        var uncheckedPlan = await new SmartExportEngine(Path.Combine(Path.GetDirectoryName(paths.Primer3)!, "ntthal.exe"))
            .PrepareAsync(uncheckedProject, new(), default);
        if (uncheckedPlan.Choices.Count != 0 || uncheckedPlan.Report.Targets.Sum(t => t.MismatchExcluded) == 0)
            throw new InvalidOperationException("Bundled intelligent export accepted an unchecked bound database.");
        var mismatchCandidate = JsonSerializer.Deserialize<PrimerCandidate>(JsonSerializer.Serialize(run.Candidates[0], ProjectStore.JsonOptions), ProjectStore.JsonOptions)!;
        var mismatchSequence = mismatchCandidate.Forward.Sequence.ToCharArray();
        mismatchSequence[^1] = mismatchSequence[^1] == 'A' ? 'C' : 'A';
        mismatchCandidate.Forward = mismatchCandidate.Forward with { Sequence = new string(mismatchSequence) };
        if (!SmartBindingScreen.Rejections(mismatchCandidate, run.Target, []).Any(r => r.Contains("F 与目标模板错配")))
            throw new InvalidOperationException("Bundled intelligent export did not reject a template binding mismatch.");
        if (!await vm.AutoSaveSelectionAsync(vm.Candidates[0], true)) throw new InvalidOperationException("Bundled selection autosave failed.");
        var recovered = new MainViewModel(storage: Path.Combine(folder, "profile"));
        if (!await recovered.RecoverLastAutoSaveAsync() || !recovered.Candidates[0].Selected)
            throw new InvalidOperationException("Bundled selection recovery failed.");
        if (!recovered.UseSharedSearchParameters || recovered.Snapshot().ParameterRecommendations.Count != 1)
            throw new InvalidOperationException("Bundled recommendation project recovery failed.");
        await AuditPackageVerification.CheckAsync(folder, paths, new() { Targets = [target], Runs = [run], Parameters = parameters });
        await ReauditPackageVerification.CheckAsync(folder, paths, new() { Targets = [target], Runs = [run], Parameters = parameters });
        var window = new MainWindow(vm);
        app.MainWindow = window;
        window.Show();
        await app.Dispatcher.InvokeAsync(() => window.UpdateLayout());
        if (window.Icon is null || window.ActualWidth <= 0) throw new InvalidOperationException("Window or icon did not load.");
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var searchClose = WindowFrameVerification.CheckDialogClose(new SearchCriteriaDialog(parameters, 600, PrimerSearchKind.Pairs, "", ""));
        var advancedSearchClose = WindowFrameVerification.CheckDialogClose(new SearchCriteriaDialog(parameters, 600, PrimerSearchKind.Pairs, "", "", true));
        var hairpinClose = WindowFrameVerification.CheckDialogClose(new HairpinSettingsDialog(new()));
        var smartClose = WindowFrameVerification.CheckDialogClose(new SmartExportDialog(smart));
        var backupClose = WindowFrameVerification.CheckDialogClose(new BackupWindow(vm.ProjectPath));
        var frame = WindowFrameVerification.Check(window);
        foreach (var topic in HelpWindow.Topics) MarkdownHelp.Render(MarkdownHelp.Load(topic.Id));
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            Success = true, BaseDirectory = AppContext.BaseDirectory, Candidates = run.Candidates.Count,
            Thermodynamics = true, Blast = blast.Output.Trim(), Parser = parser.Output.Trim(), SQLite = true,
            Window = true, Icon = true, Frame = frame, DialogClose = new[] { searchClose, advancedSearchClose, hairpinClose, smartClose, backupClose },
            SelectionAutoSave = true, FastaImport = true, GenBankImport = true,
            ParameterRecommendation = true, ProjectParameterSharing = true,
            SmartExport = true, SmartBindingFilters = true, TargetNavigation = true, CheckedOnlyDefault = true,
            AuditRepairs = true,
            IndependentReauditRepairs = true,
            RuntimeBundled = File.Exists(Path.Combine(AppContext.BaseDirectory, "System.Private.CoreLib.dll")) && File.Exists(Path.Combine(AppContext.BaseDirectory, "PresentationFramework.dll")),
            HelpTopics = HelpWindow.Topics.Length
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
