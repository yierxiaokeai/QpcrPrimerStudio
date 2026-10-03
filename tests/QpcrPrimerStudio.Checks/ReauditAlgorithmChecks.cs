using System.IO;
using System.Text.Json;
using QpcrPrimerStudio.Core;
using QpcrPrimerStudio.Desktop;

internal static class ReauditAlgorithmChecks
{
    internal static async Task<int> RunAsync(string root, string work, ProjectDocument seed, string primer3)
    {
        var count = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            count++; Console.WriteLine("PASS: " + label);
        }
        static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ProjectStore.JsonOptions), ProjectStore.JsonOptions)!;
        var folder = Path.Combine(work, "reaudit-algorithms"); Directory.CreateDirectory(folder);
        var paths = EnginePaths.FromFolder(Path.Combine(root, "src/QpcrPrimerStudio.Desktop/bin/Debug/net10.0-windows/tools"));
        var engine = new Primer3Engine(primer3);
        var checker = new SpecificityEngine(paths);
        var smart = new SmartExportEngine(Path.Combine(Path.GetDirectoryName(primer3)!, "ntthal.exe"));
        var run = Copy(seed.Runs[0]); run.Candidates = [run.Candidates.First(c => c.Assessment.Accepted)];
        var target = run.Target; var parameters = run.UsedParameters!;
        var cleanFasta = Path.Combine(folder, "clean.fasta"); var dirtyFasta = Path.Combine(folder, "dirty.fasta");
        await File.WriteAllTextAsync(cleanFasta, $">{target.Id}\n{target.Sequence}\n");
        await File.WriteAllTextAsync(dirtyFasta, $">{target.Id}\n{target.Sequence}\n>offtarget\n{target.Sequence}\n");
        var clean = await checker.BuildDatabaseAsync(cleanFasta, "clean", "转录本", Path.Combine(folder, "databases"), default);
        var dirty = await checker.BuildDatabaseAsync(dirtyFasta, "dirty", "转录本", Path.Combine(folder, "databases"), default);
        var candidate = run.Candidates[0];
        candidate.Specificity = [await checker.CheckAsync(candidate, target, clean, new(), folder, default)];
        CandidateQualityEngine.Refresh(candidate, run, parameters, null);
        var project = new ProjectDocument { Targets = [target], Runs = [run], Parameters = parameters, Databases = [clean] };
        var baseline = await smart.PrepareAsync(project, new(), default);
        Check(baseline.Choices.Count == 1, "Unchanged real BLAST and ntthal evidence remains eligible for intelligent export");
        foreach (var corruption in new[] { "primer", "product", "amplicon" })
        {
            var inconsistent = Copy(project); var broken = inconsistent.Runs[0].Candidates[0];
            if (corruption == "primer") broken.Forward = broken.Forward with { Sequence = new string('A', broken.Forward.Sequence.Length) };
            if (corruption == "product") broken.ProductLength++;
            if (corruption == "amplicon") broken.Amplicon = "ACGT";
            var refused = false;
            try { await smart.PrepareAsync(inconsistent, new(), default); } catch (InvalidDataException) { refused = true; }
            Check(refused, "Direct intelligent preparation validates the real engine project graph: " + corruption);
        }
        var sourceBytes = JsonSerializer.Serialize(project, ProjectStore.JsonOptions);
        var switched = Copy(project); switched.Databases = [dirty]; switched.Runs[0].Candidates[0].Locked = true;
        var originalReport = JsonSerializer.Serialize(switched.Runs[0].Candidates[0].Specificity, ProjectStore.JsonOptions);
        var dbPlan = await smart.PrepareAsync(switched, new(), default);
        Check(dbPlan.Choices.Count == 0 && dbPlan.Report.Genes[0].Reason.Contains("数据库已变化") && dbPlan.Report.Genes[0].Reason.Contains("重新检查"),
            "A changed current database invalidates old passing evidence with a recheck instruction");
        Check(dbPlan.Project.Runs[0].Candidates[0].Assessment.Score is null &&
            JsonSerializer.Serialize(dbPlan.Project.Runs[0].Candidates[0].Specificity, ProjectStore.JsonOptions) == originalReport &&
            switched.Runs[0].Candidates[0].Assessment.Score.HasValue && switched.Runs[0].Candidates[0].Locked,
            "Invalidation clears live ranking while preserving locked historical BLAST evidence and source assessment");
        var realRisk = await checker.CheckAsync(candidate, target, dirty, new(), folder, default);
        Check(realRisk.Products.Any(p => !p.Expected), "The replacement real BLAST database confirms the previously hidden off-target product");
        var expandedFasta = Path.Combine(folder, "expanded.fasta");
        await File.WriteAllTextAsync(expandedFasta, $">{target.Id}\n{target.Sequence}\n>unrelated\n{new string('A', 600)}\n");
        var expanded = await checker.BuildDatabaseAsync(expandedFasta, "expanded", "转录本", Path.Combine(folder, "databases"), default);
        var rechecked = Copy(project); rechecked.Databases = [expanded];
        var beforeRecheck = await smart.PrepareAsync(rechecked, new(), default);
        Check(beforeRecheck.Choices.Count == 0, "A changed database requires a fresh check even when its added transcript is unrelated");
        var currentCandidate = beforeRecheck.Project.Runs[0].Candidates[0];
        currentCandidate.Specificity = [await checker.CheckAsync(currentCandidate, target, expanded, new(), folder, default)];
        var afterRecheck = await smart.PrepareAsync(beforeRecheck.Project, new(), default);
        Check(afterRecheck.Choices.Count == 1 && afterRecheck.Project.Runs[0].Candidates[0].Assessment.Score.HasValue,
            "Fresh safe BLAST evidence from the current changed database recovers the invalidated score and eligibility");
        var retainedHistory = Copy(afterRecheck.Project);
        retainedHistory.Runs[0].Candidates[0].Specificity.Insert(0, Copy(candidate.Specificity[0]));
        var historyPlan = await smart.PrepareAsync(retainedHistory, new(), default);
        Check(historyPlan.Choices.Count == 1 && historyPlan.Project.Runs[0].Candidates[0].Specificity.Count == 2,
            "Latest current-database evidence governs eligibility while older same-kind reports remain historical");
        dbPlan.Project.Databases = [clean];
        var restoredBinding = await smart.PrepareAsync(dbPlan.Project, new(), default);
        Check(restoredBinding.Choices.Count == 1, "Restoring the original database recovers its valid preserved evidence and score");
        var checkedReplacement = Copy(project); checkedReplacement.Databases = [dirty];
        checkedReplacement.Runs[0].Candidates[0].Specificity = [realRisk];
        var riskPlan = await smart.PrepareAsync(checkedReplacement, new(), default);
        Check(riskPlan.Choices.Count == 0 && riskPlan.Project.Runs[0].Candidates[0].Assessment.Rejections.Any(r => r.Contains("非靶向")),
            "Fresh evidence from the current database retains actual risk exclusion");
        var changedExpected = Copy(project); changedExpected.Targets[0] = target with { ExpectedSubjects = ["other-accession"] };
        var expectedPlan = await smart.PrepareAsync(changedExpected, new(), default);
        Check(expectedPlan.Choices.Count == 0 && expectedPlan.Report.Genes[0].Reason.Contains("预期数据库 ID 已变化"),
            "Current expected IDs cannot reuse a historical whitelist even when template SHA is unchanged");
        var changedJunction = Copy(project); changedJunction.Targets[0] = target with { Junctions = [50] };
        var junctionPlan = await smart.PrepareAsync(changedJunction, new(), default);
        Check(junctionPlan.Choices.Count == 0 && junctionPlan.Report.Genes[0].Reason.Contains("连接位点已变化"),
            "Saved current junction metadata changes invalidate the historical junction assessment");
        var equivalentExpected = Copy(project); equivalentExpected.Targets[0] = target with { ExpectedSubjects = [target.Id, target.Id] };
        var equivalentPlan = await smart.PrepareAsync(equivalentExpected, new(), default);
        Check(equivalentPlan.Choices.Count == 1, "Expected ID ordering and duplicate entries do not create false evidence invalidation");
        var regions = new TargetRegionSettings { AvoidFivePrime = 0, AvoidThreePrime = 0,
            Expression = ExpressionMode.SelectedTranscripts, RequiredTranscripts = [target.Id] };
        var reference = new ReferenceWorkspace { Version = "reaudit", Transcripts =
            [new() { Id = target.Id, GeneId = "A", Sequence = target.Sequence }, new() { Id = "geneA.t2", GeneId = "A", Sequence = new string('A', target.Sequence.Length) }] };
        var external = target with { ExpectedSubjects = ["external-accession"], CoverageTranscripts = [target.Id] };
        foreach (var mode in new[] { ExpressionMode.SelectedTranscripts, ExpressionMode.TotalGene, ExpressionMode.IsoformSpecific })
        {
            var designed = await engine.DesignAsync(external, parameters, default, regions: regions with { Expression = mode }, reference: reference);
            Check(designed.Target.ExpectedSubjects.SequenceEqual(external.ExpectedSubjects), "Explicit external database IDs survive real multi-transcript design: " + mode);
        }
        var selectedRun = await engine.DesignAsync(external, parameters, default, regions: regions, reference: reference);
        var coverageProject = new ProjectDocument { Targets = [external with { CoverageTranscripts = ["geneA.t2"] }], Runs = [selectedRun],
            Parameters = parameters, RegionSettings = regions, Reference = reference };
        var coveragePlan = await smart.PrepareAsync(coverageProject, new(), default);
        Check(coveragePlan.Choices.Count == 0 && coveragePlan.Report.Genes[0].Reason.Contains("覆盖的转录本已变化"),
            "Current coverage changes require redesign and cannot pass using old t1 coverage scores");
        var defaults = external with { ExpectedSubjects = [] };
        var defaultRun = await engine.DesignAsync(defaults, parameters, default, regions: regions, reference: reference);
        Check(defaultRun.Target.ExpectedSubjects.SequenceEqual([target.Id]), "Empty expected IDs obtain the reference expression target default");
        var defaultPlan = await smart.PrepareAsync(new() { Targets = [defaults], Runs = [defaultRun], Parameters = parameters, Reference = reference }, new(), default);
        Check(defaultPlan.Choices.Count == 1, "An unchanged inferred expression whitelist remains compatible with the current target");
        var changedReference = Copy(reference); changedReference.Version = "new-version";
        var refPlan = await smart.PrepareAsync(new() { Targets = [defaults], Runs = [defaultRun], Parameters = parameters, Reference = changedReference }, new(), default);
        Check(refPlan.Choices.Count == 0 && refPlan.Report.Genes[0].Reason.Contains("当前参考版本"),
            "Reference replacement cannot rank evidence attributed to a different design reference");
        var genomeDb = await checker.BuildDatabaseAsync(cleanFasta, "genome provenance", "基因组", Path.Combine(folder, "databases"), default);
        var mismatched = Copy(project); mismatched.Databases = [genomeDb];
        mismatched.Runs[0].Candidates[0].Specificity = [await checker.CheckAsync(mismatched.Runs[0].Candidates[0], target, genomeDb, new(), folder, default)];
        mismatched.Reference = new() { Assets = [new("Genome", dirtyFasta, dirty.SourceSha256)],
            Transcripts = [new() { Id = target.Id, Sequence = target.Sequence }] };
        var mismatchPlan = await smart.PrepareAsync(mismatched, new(), default);
        Check(mismatchPlan.Choices.Count == 0 && mismatchPlan.Report.Genes[0].Reason.Contains("当前参考文件指纹不匹配"),
            "Current reference genome hashes are checked against preserved genome specificity database provenance");
        var externalFasta = Path.Combine(folder, "external-accessions.fasta");
        await File.WriteAllTextAsync(externalFasta, $">external-accession\n{target.Sequence}\n");
        var externalDb = await checker.BuildDatabaseAsync(externalFasta, "external accession database", "转录本", Path.Combine(folder, "databases"), default);
        var externalReference = Copy(reference); externalReference.Assets = [new("Transcriptome", cleanFasta, clean.SourceSha256)];
        var externalDesign = await engine.DesignAsync(external, parameters, default, regions: regions, reference: externalReference);
        externalDesign.Candidates = [externalDesign.Candidates.First(c => c.Assessment.Accepted)];
        var externalCandidate = externalDesign.Candidates[0];
        externalCandidate.Specificity = [await checker.CheckAsync(externalCandidate, externalDesign.Target, externalDb, new(), folder, default)];
        CandidateQualityEngine.Refresh(externalCandidate, externalDesign, parameters, externalReference);
        var externalPlan = await smart.PrepareAsync(new() { Targets = [external], Runs = [externalDesign], Parameters = parameters,
            Reference = externalReference, Databases = [externalDb] }, new(), default);
        Check(externalDb.SourceSha256 != clean.SourceSha256 && externalPlan.Choices.Count == 1 &&
            externalCandidate.Specificity[0].Products.Any(p => p.Expected) && externalCandidate.Assessment.Components.Single(c => c.Metric == "转录本特异性").Quality == 1,
            "A current explicitly bound external accession database remains valid alongside differently named reference transcripts");
        var legacy = Copy(project); legacy.Runs[0].Candidates[0].Specificity[0].QueryPairSha256 = "";
        CandidateQualityEngine.Refresh(legacy.Runs[0].Candidates[0], legacy.Runs[0], parameters, null);
        Check(legacy.Runs[0].Candidates[0].Assessment.Components.Single(c => c.Metric == "转录本特异性").Quality is null,
            "A legacy real BLAST report without a query hash cannot score specificity as passed");
        var legacyXml = legacy.Runs[0].Candidates[0].Specificity[0].RawXml;
        var legacyPlan = await smart.PrepareAsync(legacy, new(), default);
        Check(legacyPlan.Choices.Count == 0 && legacyPlan.Report.Genes[0].Reason.Contains("缺少查询引物指纹") &&
            legacyPlan.Project.Runs[0].Candidates[0].Specificity[0].RawXml == legacyXml,
            "Legacy query-unbound XML is preserved and intelligent export explicitly requires a fresh check");
        var random = new Random(827364);
        var alternate = target with { Sequence = new string(Enumerable.Range(0, 600).Select(_ => "ACGT"[random.Next(4)]).ToArray()) };
        var alternateRun = await engine.DesignAsync(alternate, parameters, default);
        alternateRun.Candidates = [alternateRun.Candidates.First(c => c.Assessment.Accepted)];
        var pairedFasta = Path.Combine(folder, "query-pairs.fasta");
        await File.WriteAllTextAsync(pairedFasta, $">{target.Id}\n{target.Sequence}\n>offtarget\n{alternate.Sequence}\n");
        var pairedDb = await checker.BuildDatabaseAsync(pairedFasta, "query pairs", "转录本", Path.Combine(folder, "databases"), default);
        var originalQuery = await checker.CheckAsync(candidate, target, pairedDb, new(), folder, default);
        var alternateQuery = await checker.CheckAsync(alternateRun.Candidates[0], alternate, pairedDb, new(), folder, default);
        Check(!originalQuery.Products.Any(p => !p.Expected) && alternateQuery.Products.Any(p => !p.Expected),
            "Actual BLAST proves distinct safe and off-target queries for two valid real Primer3 candidate graphs");
        alternateRun.Candidates[0].Specificity = [originalQuery];
        CandidateQualityEngine.Refresh(alternateRun.Candidates[0], alternateRun, parameters, null);
        Check(alternateRun.Candidates[0].Assessment.Components.Single(c => c.Metric == "转录本特异性").Quality is null,
            "A report from another real primer query cannot score the current legitimate candidate as passed");
        var mixedQuery = new ProjectDocument { Targets = [alternate], Runs = [alternateRun], Parameters = parameters, Databases = [pairedDb] };
        ProjectValidation.Validate(mixedQuery);
        var mixedPlan = await smart.PrepareAsync(mixedQuery, new(), default);
        Check(mixedPlan.Choices.Count == 0 && mixedPlan.Report.Genes[0].Reason.Contains("引物序列与当前候选不匹配"),
            "A coordinate-valid graph carrying another query's real BLAST report is rejected before intelligent ranking");
        var safeAlternateFasta = Path.Combine(folder, "safe-alternate.fasta");
        await File.WriteAllTextAsync(safeAlternateFasta, $">{alternate.Id}\n{alternate.Sequence}\n");
        var safeAlternate = await checker.BuildDatabaseAsync(safeAlternateFasta, "safe alternate", "转录本", Path.Combine(folder, "databases"), default);
        mixedPlan.Project.Databases = [safeAlternate];
        var boundCandidate = mixedPlan.Project.Runs[0].Candidates[0];
        boundCandidate.Specificity = [await checker.CheckAsync(boundCandidate, alternate, safeAlternate, new(), folder, default)];
        var reboundPlan = await smart.PrepareAsync(mixedPlan.Project, new(), default);
        Check(reboundPlan.Choices.Count == 1 && reboundPlan.Project.Runs[0].Candidates[0].Assessment.Components.Single(c => c.Metric == "转录本特异性").Quality == 1,
            "A fresh correct real query and current safe database restore specificity scoring and intelligent eligibility");
        Check(JsonSerializer.Serialize(project, ProjectStore.JsonOptions) == sourceBytes,
            "All ownership checks preserve the original source project and its historical evidence");
        var vm = new MainViewModel(Path.Combine(root, "src/QpcrPrimerStudio.Desktop/bin/Debug/net10.0-windows/tools"), Path.Combine(folder, "draft-profile"));
        foreach (var field in new[] { "expected", "coverage", "junction" })
        {
            vm.Restore(Copy(project));
            if (field == "expected") vm.ExpectedText = "unsaved-accession";
            if (field == "coverage") vm.CoverageText = "unsaved-coverage";
            if (field == "junction") vm.JunctionText = "50";
            var pending = await vm.PrepareSmartExportAsync();
            Check(pending is null && vm.Status.Contains("尚未保存") && vm.Snapshot().SmartExportReports.Count == 0,
                "Unsaved current target metadata blocks desktop intelligent preparation: " + field);
        }
        return count;
    }
}
