using System.IO;
using QpcrPrimerStudio.Core;
using QpcrPrimerStudio.Desktop;

internal static class PrimerWorkflowChecks
{
    internal static async Task<int> RunAsync(string root, string work, SequenceTarget target, TargetRun original, string executable)
    {
        var count = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            count++; Console.WriteLine("PASS: " + label);
        }
        var engine = new Primer3Engine(executable);
        var primer = original.Candidates[0];
        var p = original.UsedParameters!;
        count += await TutorialWorkflowChecks.RunAsync(root, work, target, original, executable);
        var flowVm = new MainViewModel(Path.Combine(root, "src/QpcrPrimerStudio.Desktop/bin/Debug/net10.0-windows/tools"), Path.Combine(work, "pp5-flow-profile"));
        foreach (var kind in Enum.GetValues<PrimerSearchKind>())
        {
            flowVm.Restore(new ProjectDocument { Targets = [target], Parameters = p, AutoSpecificity = false });
            await flowVm.ExecuteSearchRequestAsync(new(kind, p, primer.Forward.Sequence, primer.Reverse.Sequence));
            Check(kind is PrimerSearchKind.Sense or PrimerSearchKind.Antisense or PrimerSearchKind.Both
                ? flowVm.SinglePrimerSearches.Count > 0 && flowVm.SinglePrimerSearches.All(s => s.Primers.Count > 0)
                : flowVm.Runs.Count == 1 && flowVm.Runs[0].Candidates.Count > 0, "Search Criteria dispatch completes: " + kind);
            flowVm.ShowResultsCommand.Execute(null);
            flowVm.EditSelectedCommand.Execute(null);
            Check(flowVm.InputExpanded && flowVm.InputTabIndex == 1 && flowVm.ManualForward.Length + flowVm.ManualReverse.Length > 0,
                "Results selection enters editor: " + kind);
        }
        var singleF = await engine.SearchSingleAsync(target, p, false, default);
        var singleR = await engine.SearchSingleAsync(target, p, true, default);
        Check(singleF.Primers.Count > 0 && singleR.Primers.Count > 0 && singleR.Primers.All(r =>
            SequenceFiles.ReverseComplement(target.Sequence.Substring(r.Start - 1, r.Sequence.Length)) == r.Sequence),
            "Independent sense and antisense searches return mapped real primers");
        var longProductSingle = await engine.SearchSingleAsync(target, p with { MinProduct = 1000, MaxProduct = 1100 }, false, default);
        Check(longProductSingle.Primers.Count == singleF.Primers.Count, "Single primer search ignores paired product size limits");
        var mutated = (primer.Forward.Sequence[0] == 'A' ? "C" : "A") + primer.Forward.Sequence[1..];
        var oligos = await engine.InspectOligosAsync(mutated, primer.Reverse.Sequence, p, default);
        Check(oligos.Forward == mutated && oligos.Report.Contains("Tm") && !oligos.RawInput.Contains("SEQUENCE_TEMPLATE"),
            "Substituted primer retains real Tm and hairpin analysis without invented template coordinates");
        var fixedF = await engine.DesignAsync(target, p, default, primer.Forward.Sequence);
        Check(fixedF.Candidates.Count > 0 && fixedF.Candidates.All(c => c.Forward.Sequence == primer.Forward.Sequence), "Fixed forward finds real compatible reverse primers");
        var fixedR = await engine.DesignAsync(target, p, default, reverse: primer.Reverse.Sequence);
        Check(fixedR.Candidates.Count > 0 && fixedR.Candidates.All(c => c.Reverse.Sequence == primer.Reverse.Sequence), "Fixed reverse keeps 5-prime orientation and finds compatible forward primers");
        var bounded = p with { ForwardRegionStart = primer.Forward.Start, ForwardRegionLength = primer.Forward.Sequence.Length,
            ReverseRegionStart = primer.Reverse.Start, ReverseRegionLength = primer.Reverse.Sequence.Length };
        var boundedSingle = await engine.SearchSingleAsync(target, bounded, false, default);
        Check(boundedSingle.Primers.Count > 0 && boundedSingle.Parameters.IncludedLength == p.IncludedLength && boundedSingle.Primers.All(f =>
            f.Start >= bounded.ForwardRegionStart && f.End < bounded.ForwardRegionStart + bounded.ForwardRegionLength),
            "Single-primer range does not shrink subsequent paired-search template");
        var restricted = await engine.DesignAsync(target, bounded, default);
        Check(restricted.Candidates.Count > 0 && restricted.Candidates.All(c =>
            c.Forward.Start >= bounded.ForwardRegionStart && c.Forward.End < bounded.ForwardRegionStart + bounded.ForwardRegionLength &&
            c.Reverse.Start >= bounded.ReverseRegionStart && c.Reverse.End < bounded.ReverseRegionStart + bounded.ReverseRegionLength), "Separate F and R search ranges constrain real engine coordinates");
        var rejected = await engine.DesignAsync(target, p with { MinTm = 20, OptTm = 25, MaxTm = 30 }, default,
            primer.Forward.Sequence, primer.Reverse.Sequence);
        Check(rejected.Candidates.Count == 1 && rejected.Candidates[0].ConstraintWarnings.Count > 0 && !rejected.Candidates[0].Assessment.Accepted,
            "Edited primers outside Tm limits retain metrics with explicit rejection");
        var rechecked = rejected.Candidates[0];
        CandidateQualityEngine.Evaluate(rechecked, target, rejected.UsedParameters!, rejected.RegionSettings,
            TargetRegionEngine.Plan(target, rejected.RegionSettings, null), null);
        Check(!rechecked.Assessment.Accepted, "Quality recheck preserves engine constraint failures");
        var vm = new MainViewModel(Path.Combine(root, "src/QpcrPrimerStudio.Desktop/bin/Debug/net10.0-windows/tools"), Path.Combine(work, "edit-profile"));
        vm.Restore(new ProjectDocument { Targets = [target], Runs = [original], Parameters = p, AutoSpecificity = false });
        vm.LoadManualCommand.Execute(null);
        var priorSequence = primer.Forward.Sequence;
        vm.TransformPrimerCommand.Execute("F:ReverseComplement");
        vm.TransformPrimerCommand.Execute("F:ReverseComplement");
        Check(vm.ManualForward == priorSequence, "Editor reverse complement preserves sequence on round trip");
        vm.ManualForward = priorSequence[1..];
        await vm.EvaluateCommand.ExecuteAsync(null);
        Check(vm.Runs.Count == 2 && vm.SelectedRun!.Candidates.Count == 1 && primer.Forward.Sequence == priorSequence,
            "Primer edit recomputes without overwriting original candidate");
        var child = vm.SelectedRun!.Candidates[0];
        Check(child.Id != primer.Id && child.ParentCandidateId == primer.Id && child.Revision == primer.Revision + 1 && child.Specificity.Count == 0,
            "Edited revision has lineage and no stale specificity");
        vm.ManualForward = "";
        await vm.EvaluateCommand.ExecuteAsync(null);
        Check(vm.Runs.Count == 2 && vm.Status.StartsWith("操作失败"), "Empty evaluation cannot start an unconstrained search");
        var path = Path.Combine(work, "edit-revisions.qpcrproject");
        await ProjectStore.SaveAsync(path, vm.Snapshot());
        var loaded = await ProjectStore.LoadAsync(path);
        Check(loaded.Runs[1].Candidates[0].ParentCandidateId == primer.Id, "Edited lineage survives project round trip");
        loaded.SinglePrimerSearches = [boundedSingle]; loaded.OligoInspections = [oligos];
        vm.Restore(loaded);
        Check(vm.ManualForward == "" && vm.ManualReverse == child.Reverse.Sequence,
            "An unapplied manual edit survives project recovery before loading a different primer");
        vm.ManualReverse = "";
        vm.UseSinglePrimerCommand.Execute(null);
        await vm.SearchWithForwardCommand.ExecuteAsync(null);
        Check(vm.SelectedRun!.Candidates.Count > 0 && vm.SelectedRun.Candidates.All(c => c.Forward.Sequence == vm.ManualForward),
            "Loaded single-primer search completes compatible pairing through view model");
        vm.SelectedOligoInspection = vm.OligoInspections[0];
        Check(vm.StructureText == oligos.Report && vm.Snapshot().SinglePrimerSearches.Count == 1, "Saved sequence analysis and single searches restore in UI");
        await vm.SearchBothCommand.ExecuteAsync(null);
        Check(vm.SinglePrimerSearches.Count == 3 && vm.SinglePrimerSearches[^2].Reverse == false && vm.SinglePrimerSearches[^1].Reverse,
            "Both mode retains independent forward and reverse search histories");
        vm.LoadManualCommand.Execute(null);
        vm.Restore(new ProjectDocument { Targets = [target], Parameters = p, AutoSpecificity = false });
        vm.ManualForward = primer.Forward.Sequence; vm.ManualReverse = primer.Reverse.Sequence;
        await vm.EvaluateCommand.ExecuteAsync(null);
        Check(vm.Runs.Count == 1 && vm.Runs[0].Candidates[0].ParentCandidateId is null,
            "Opening another project clears prior editor lineage");
        var invalidRangeRejected = false;
        try { (p with { ReverseRegionStart = target.Sequence.Length, ReverseRegionLength = 2 }).Validate(target.Sequence.Length); }
        catch (ArgumentException) { invalidRangeRejected = true; }
        Check(invalidRangeRejected, "Out-of-bounds reverse region rejected before engine execution");
        var userFasta = Path.Combine(root, "artifacts/primer-premier-research/user-cds.fasta");
        if (File.Exists(userFasta))
        {
            var targets = SequenceFiles.Read(userFasta);
            var comparison = p with { OptLength = 21, MinGc = 30, MaxGc = 70, Count = 100 };
            var regions = new TargetRegionSettings { Mode = TargetRegionMode.EntireTranscript, AvoidFivePrime = 0, AvoidThreePrime = 0 };
            var runs = new List<TargetRun>();
            foreach (var userTarget in targets)
            {
                var run = await engine.DesignAsync(userTarget, comparison, default, regions: regions);
                Check(run.Candidates.Count > 0 && run.Candidates.All(c => c.ProductLength is >= 80 and <= 200 &&
                    c.Forward.Sequence.Length is >= 18 and <= 24 && c.Reverse.Sequence.Length is >= 18 and <= 24 &&
                    c.Forward.Tm is >= 58 and <= 62 && c.Reverse.Tm is >= 58 and <= 62), "User CDS Primer3 candidates meet length, Tm and product limits: " + userTarget.Id);
                runs.Add(run);
            }
            await ProjectStore.SaveAsync(Path.Combine(work, "user-cds-designed.qpcrproject"), new ProjectDocument
            { Name = "两段 CDS · 搜索与编辑示例", Targets = targets, Runs = runs, Parameters = comparison, RegionSettings = regions, AutoSpecificity = false });
        }
        return count;
    }
}
