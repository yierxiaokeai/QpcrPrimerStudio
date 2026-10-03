using System.IO;
using QpcrPrimerStudio.Core;
using QpcrPrimerStudio.Desktop;

internal static class TutorialWorkflowChecks
{
    internal static async Task<int> RunAsync(string root, string work, SequenceTarget target, TargetRun original, string executable)
    {
        var count = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            count++; Console.WriteLine("PASS: " + label);
        }
        var p = original.UsedParameters!; var pair = original.Candidates[0];
        Check(SequenceFiles.Normalize("acgt\r\n ryswkm\tbdhvn") == "ACGTRYSWKMBDHVN",
            "Lowercase DNA, IUPAC bases and formatting whitespace normalize correctly");
        foreach (var bad in new[] { "将 Debian 13 的 APT 软件源切换为中科大（USTC）", ">target_1", "123", "ACGT-", "ACGT\u200b" })
        {
            SequenceInputException? error = null;
            try { SequenceFiles.Normalize("ACGT\r\nAC" + bad); } catch (SequenceInputException ex) { error = ex; }
            Check(error is not null && error.Line == 2 && error.Offset >= 8 && error.Message.Contains("无效字符"),
                "Contaminated input is rejected with original coordinates: " + bad);
        }
        var draft = new SearchCriteriaModel(p, PrimerSearchKind.Pairs, "", "")
        {
            ForwardStart = pair.Forward.Start.ToString(), ForwardEnd = pair.Forward.End.ToString(),
            ReverseStart = pair.Reverse.Start.ToString(), ReverseEnd = pair.Reverse.End.ToString()
        };
        var request = draft.Read(target.Sequence.Length);
        var engine = new Primer3Engine(executable);
        var bounded = await engine.DesignAsync(target, request.Parameters, default);
        Check(bounded.Candidates.Count > 0 && bounded.Candidates.All(c => c.Forward.Start >= pair.Forward.Start &&
            c.Forward.End <= pair.Forward.End && c.Reverse.Start >= pair.Reverse.Start && c.Reverse.End <= pair.Reverse.End),
            "Inclusive start/end search ranges constrain real F/R primer coordinates");
        draft.ForwardEnd = (pair.Forward.Start - 1).ToString();
        var invalid = false; try { draft.Read(target.Sequence.Length); } catch (ArgumentException) { invalid = true; }
        Check(invalid, "Reversed search range rejected before engine execution");
        draft.Kind = PrimerSearchKind.Sense; draft.ForwardStart = "1"; draft.ForwardEnd = "";
        draft.ReverseStart = "unused"; draft.MinProduct = "unused";
        var singleRequest = draft.Read(target.Sequence.Length);
        Check(!draft.IsPairSearch && !draft.UsesReverseRange && singleRequest.Parameters.ReverseRegionLength == 0,
            "Single search ignores disabled product and opposite-strand range fields");
        var thermo = new Thermodynamics(Path.Combine(Path.GetDirectoryName(executable)!, "ntthal.exe"));
        foreach (var reverse in new[] { false, true })
        {
            var f = reverse ? "" : pair.Forward.Sequence; var r = reverse ? pair.Reverse.Sequence : "";
            var result = await engine.InspectOligosAsync(f, r, p, default);
            var structure = await thermo.AnalyzeAsync(f, r, p, default);
            Check(result.Forward == f && result.Reverse == r && result.Report.Contains("Tm") && structure.Contains("发卡") &&
                !structure.Contains("异二聚体"), "Single oligo analysis returns real metrics without inferred pair structure: " + (reverse ? "R" : "F"));
        }
        var vm = new MainViewModel(Path.Combine(root, "src/QpcrPrimerStudio.Desktop/bin/Debug/net10.0-windows/tools"), Path.Combine(work, "direct-select-profile"));
        vm.Restore(new ProjectDocument { Targets = [target], Parameters = p, AutoSpecificity = false });
        await vm.SelectTemplatePrimerAsync(false, pair.Forward.Start, pair.Forward.Sequence.Length);
        Check(vm.ManualForward == pair.Forward.Sequence && vm.ManualReverse == "" && vm.EditorAnalysisText.Contains("Tm"),
            "Direct selection analyzes a Forward oligo without requiring a Reverse oligo");
        await vm.SelectTemplatePrimerAsync(true, pair.Reverse.Start, pair.Reverse.Sequence.Length);
        Check(vm.ManualReverse == pair.Reverse.Sequence && vm.EditorAnalysisText.Contains("异二聚体") && vm.OligoInspections.Count == 2,
            "Direct Reverse selection takes reverse complement and updates pair analysis");
        var previous = vm.ManualForward;
        await vm.SelectTemplatePrimerAsync(false, 0, 20);
        Check(vm.ManualForward == previous && vm.Status.Contains("请选择"), "Invalid direct selection leaves editor sequences intact");
        vm.ManualForward = previous[1..];
        Check(vm.EditorAnalysisText == "" && vm.EditSummary.Contains("Analyze"), "Editing clears current analysis until recalculated");
        await vm.AnalyzeManualCommand.ExecuteAsync(null);
        Check(vm.EditorAnalysisText.Contains(previous[1..]), "Analyze refreshes editor output for the current sequence");
        return count;
    }
}
