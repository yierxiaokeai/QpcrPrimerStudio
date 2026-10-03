using System.IO;
using System.Text.Json;
using QpcrPrimerStudio.Core;
using QpcrPrimerStudio.Desktop;

internal static class ParameterRecommendationChecks
{
    internal static async Task<int> RunAsync(string root, string work, SequenceTarget target, TargetRun original, string executable)
    {
        var count = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            count++; Console.WriteLine("PASS: " + label);
        }
        var service = new ParameterRecommendationEngine(executable);
        var parameters = new DesignParameters { MinProduct = 250, MaxProduct = 400, Count = 3 };
        var untouched = JsonSerializer.Serialize(parameters);
        var recommendation = await service.RecommendAsync(target, parameters, RecommendationSearchKind.Pairs, "", "", default);
        Check(recommendation.Parameters is { MinProduct: 80, MaxProduct: 200, Count: 10 } && recommendation.Trials[0].Retained > 0,
            "Smart recommendation runs real Primer3 and chooses standard qPCR conditions when retained pairs exist");
        Check(recommendation.Trials.Count == 1 && recommendation.EngineSha256 == Hashing.File(executable) &&
            recommendation.TemplateSha256 == target.Sha256 && recommendation.Trials[0].RawOutput.Contains("PRIMER_PAIR_NUM_RETURNED"),
            "Recommendation preserves exact template and engine fingerprints with original trial input/output");
        Check(JsonSerializer.Serialize(parameters) == untouched && target.ParameterOverride is null,
            "Recommendation previews leave original parameters and target unchanged");
        var offered = recommendation.Parameters!;
        Check(offered.MinTm == 58 && offered.OptTm == 60 && offered.MaxTm == 62 && offered.MaxTmDifference == 2 &&
            offered.MinGc == 40 && offered.MaxGc == 60, "Standard recommendations retain a common Tm window and explicit pair difference");
        var reproduced = await new Primer3Engine(executable).DesignAsync(target, offered, default);
        Check(reproduced.AcceptedCount == recommendation.Trials[0].Retained,
            "Applying recommended parameters reproduces the real retained pair count");
        var pair = original.Candidates[0];
        var bounded = parameters with { MonovalentMm = 60, MagnesiumMm = 2,
            ForwardRegionStart = pair.Forward.Start, ForwardRegionLength = pair.Forward.Sequence.Length,
            ReverseRegionStart = pair.Reverse.Start, ReverseRegionLength = pair.Reverse.Sequence.Length,
            ExcludedRegions = [new(1, 10)] };
        var regionRecommendation = await service.RecommendAsync(target, bounded, RecommendationSearchKind.Pairs, "", "", default);
        Check(regionRecommendation.Trials.All(t => t.Parameters.MonovalentMm == 60 && t.Parameters.MagnesiumMm == 2 &&
            t.Parameters.ForwardRegionStart == bounded.ForwardRegionStart && t.Parameters.ExcludedRegions.SequenceEqual(bounded.ExcludedRegions)),
            "Recommendation trials preserve user reaction concentrations and custom search regions");
        var impossible = await service.RecommendAsync(target with { Id = "polyA", Sequence = new string('A', 600) }, parameters,
            RecommendationSearchKind.Pairs, "", "", default);
        Check(impossible.Parameters is null && impossible.Trials.Count == 3 && impossible.Trials.All(t => t.Retained == 0),
            "Impossible templates return a documented no-recommendation result after all real profiles");
        Check(impossible.Trials[1].Parameters.MaxLength == 30 && impossible.Trials[2].Parameters.MinGc == 30 &&
            impossible.Trials.All(t => t.Parameters.MinTm == 58 && t.Parameters.MaxSelfEndTm == parameters.MaxSelfEndTm),
            "Fallback profiles expand length and GC while retaining Tm and structural safety limits");
        var ambiguous = await service.RecommendAsync(target with { Id = "unknown", Sequence = new string('N', 600) }, parameters,
            RecommendationSearchKind.Pairs, "", "", default);
        Check(ambiguous.Parameters is null && ambiguous.Report.Contains("歧义碱基 600"),
            "All-ambiguous templates report sequence quality without inventing a usable recommendation");
        var shortTarget = target with { Id = "short", Sequence = target.Sequence[..75] };
        var noEndAvoidance = new TargetRegionSettings { AvoidFivePrime = 0, AvoidThreePrime = 0 };
        var shortResult = await service.RecommendAsync(shortTarget, parameters, RecommendationSearchKind.Pairs, "", "", default, noEndAvoidance);
        Check(shortResult.Trials.All(t => t.Parameters.MinProduct == 60 && t.Parameters.MaxProduct == 75),
            "Short-template trials constrain their entire recommended product range to the actual template");
        var rejected = false;
        try { await service.RecommendAsync(target with { Sequence = target.Sequence[..50] }, parameters, RecommendationSearchKind.Pairs, "", "", default); }
        catch (ArgumentException ex) { rejected = ex.Message.Contains("不足 60"); }
        Check(rejected, "Templates too short for this qPCR recommendation receive actionable input guidance");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); var cancelled = false;
            try { await service.RecommendAsync(target, parameters, RecommendationSearchKind.Pairs, "", "", cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Cancelled recommendations stop before changing any parameters");
        }
        foreach (var kind in new[] { RecommendationSearchKind.Sense, RecommendationSearchKind.Antisense, RecommendationSearchKind.Both,
            RecommendationSearchKind.CompatibleWithSense, RecommendationSearchKind.CompatibleWithAntisense })
        {
            var result = await service.RecommendAsync(target, parameters, kind, pair.Forward.Sequence, pair.Reverse.Sequence, default);
            Check(result.Parameters is not null && result.Trials[^1].Retained > 0,
                "Real recommendation supports search mode " + kind);
            if (kind == RecommendationSearchKind.Both)
                Check(result.Trials[^1].RawOutput.Contains("PRIMER_LEFT_NUM_RETURNED") && result.Trials[^1].RawOutput.Contains("PRIMER_RIGHT_NUM_RETURNED"),
                    "Both-mode recommendation checks both independent directions");
            if (kind is RecommendationSearchKind.CompatibleWithSense or RecommendationSearchKind.CompatibleWithAntisense)
                Check(result.Trials[^1].RawInput.Contains(kind == RecommendationSearchKind.CompatibleWithSense ?
                    "SEQUENCE_PRIMER=" + pair.Forward.Sequence : "SEQUENCE_PRIMER_REVCOMP=" + pair.Reverse.Sequence),
                    "Fixed-primer recommendations use the unchanged input oligo " + kind);
        }
        var invalidRegions = parameters with { IncludedStart = 1000, IncludedLength = 10, ExcludedRegions = [new(599, 10)] };
        var model = new SearchCriteriaModel(invalidRegions, PrimerSearchKind.Pairs, "", "");
        rejected = false; try { model.Read(600); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Hidden out-of-template regions remain visible validation failures until explicitly cleared");
        model.ClearCustomRegions = true; model.ForwardStart = "bad";
        var cleaned = model.Read(600);
        Check(cleaned.Parameters.ExcludedRegions.Count == 0 && cleaned.Parameters.IncludedLength == 0 &&
            cleaned.Parameters.ForwardRegionLength == 0 && cleaned.ClearCustomRegions,
            "Explicit whole-template selection clears all manual region constraints without parsing unused coordinates");
        var own = new DesignParameters { MinTm = 55, OptTm = 57, MaxTm = 59, MinGc = 35, MaxGc = 65, MinProduct = 120, MaxProduct = 250,
            ForwardRegionStart = 60, ForwardRegionLength = 100, ExcludedRegions = [new(20, 10)], MagnesiumMm = 2.5 };
        var engines = Path.Combine(root, "src/QpcrPrimerStudio.Desktop/bin/Debug/net10.0-windows/tools");
        var vm = new MainViewModel(engines, Path.Combine(work, "recommend-profile"));
        vm.Restore(new ProjectDocument { Parameters = parameters, Targets = [target, target with { Id = "geneB", ParameterOverride = own }, target with { Id = "geneC" }] });
        vm.ApplySearchParameters(new(PrimerSearchKind.Pairs, offered, "", "") { ApplyToWholeProject = true, Recommendation = recommendation });
        var snapshot = vm.Snapshot();
        Check(snapshot.Parameters.MinTm == offered.MinTm && snapshot.Targets.All(t =>
            (t.ParameterOverride ?? snapshot.Parameters).MinTm == offered.MinTm && (t.ParameterOverride ?? snapshot.Parameters).MinProduct == offered.MinProduct),
            "Project sharing synchronizes normal targets and existing target overrides");
        var retainedOwn = snapshot.Targets[1].ParameterOverride!;
        Check(retainedOwn.ForwardRegionStart == own.ForwardRegionStart && retainedOwn.ExcludedRegions.SequenceEqual(own.ExcludedRegions) &&
            retainedOwn.MagnesiumMm == own.MagnesiumMm,
            "Project sharing retains each gene's custom coordinates and reaction concentrations");
        var file = Path.Combine(work, "recommended-project.qpcrproject");
        await ProjectStore.SaveAsync(file, snapshot);
        var restored = await ProjectStore.LoadAsync(file);
        Check(restored.UseSharedSearchParameters && restored.ParameterRecommendations.Single().Trials[0].RawOutput == recommendation.Trials[0].RawOutput,
            "Project roundtrip retains shared-search preference and complete recommendation evidence");
        var recovered = new MainViewModel(engines, Path.Combine(work, "recommend-recovery")); recovered.Restore(restored);
        Check(recovered.UseSharedSearchParameters && recovered.Snapshot().ParameterRecommendations.Count == 1,
            "Restored workbench preserves recommendation history and shared-search preference");
        await vm.DesignTargetsAsync(vm.Targets.ToList(), vm.ReadParameters(), default);
        Check(vm.Runs.Count == 3 && vm.Runs.All(r => r.UsedParameters?.MinProduct == offered.MinProduct && r.UsedParameters?.MinTm == offered.MinTm),
            "Real batch design uses the newly shared parameters for all genes");
        await File.WriteAllTextAsync(Path.Combine(work, "parameter-recommendation.json"), JsonSerializer.Serialize(recommendation, ProjectStore.JsonOptions));
        return count;
    }
}
