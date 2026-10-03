using System.IO;
using System.Text.Json;
using QpcrPrimerStudio.Core;
using QpcrPrimerStudio.Desktop;

internal static class ReauditInteractionChecks
{
    internal static async Task<int> RunAsync(string root, string work, ProjectDocument seed)
    {
        var count = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); count++; Console.WriteLine("PASS: " + label); }
        static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ProjectStore.JsonOptions), ProjectStore.JsonOptions)!;
        var folder = Path.Combine(work, "reaudit-interaction"); Directory.CreateDirectory(folder);
        var tools = Path.Combine(root, "src/QpcrPrimerStudio.Desktop/bin/Debug/net10.0-windows/tools");
        MainViewModel Vm(string name) => new(tools, Path.Combine(folder, name));
        var a = Copy(seed.Targets[0]); var b = a with { Id = "reaudit-draft-B", Sequence = a.Sequence + "ACGT" };
        var run = Copy(seed.Runs[0]); run.Target = a; run.HairpinScreening = null;
        var project = new ProjectDocument { Targets = [a, b], Runs = [run], Parameters = seed.Parameters, AutoSpecificity = false };
        var vm = Vm("drafts"); vm.Restore(Copy(project));
        vm.AddTargetCommand.Execute(null);
        vm.TargetName = "name-only-draft"; vm.SelectedTarget = vm.Targets[0]; vm.AddTargetCommand.Execute(null);
        Check(vm.TargetName == "name-only-draft" && vm.TemplateText == "", "An unassigned gene name survives navigation before any sequence is pasted");
        vm.TargetName = "new-raw-C"; vm.TemplateText = "invalid raw 新基因"; vm.ExpectedText = "draft-expected";
        vm.ManualForward = "invalid F"; vm.InputTabIndex = 1;
        vm.SelectedTarget = vm.Targets[0];
        Check(vm.Snapshot().UnassignedDraft is { TemplateText: "invalid raw 新基因", ManualForward: "invalid F" }, "An unassigned raw template and primer draft survive selecting an existing gene");
        var path = Path.Combine(folder, "unassigned.qpcrproject"); await ProjectStore.SaveAsync(path, vm.Snapshot());
        var recovered = Vm("recovered"); recovered.Restore(await ProjectStore.LoadAsync(path));
        recovered.AddTargetCommand.Execute(null);
        Check(recovered.SelectedTarget is null && recovered.TargetName == "new-raw-C" && recovered.TemplateText == "invalid raw 新基因" &&
            recovered.ExpectedText == "draft-expected" && recovered.ManualForward == "invalid F" && recovered.InputTabIndex == 1,
            "Add sequence restores the independent raw draft after disk save and load");
        recovered.SelectedTarget = recovered.Targets[0]; recovered.AddTargetCommand.Execute(null);
        Check(recovered.TemplateText == "invalid raw 新基因", "Repeated add and gene navigation retain the same unassigned draft");

        vm = Vm("manual"); vm.Restore(Copy(project));
        vm.LoadManualCommand.Execute(null);
        var parent = vm.SelectedCandidate!;
        vm.ManualForward = parent.Forward.Sequence + "N"; vm.ManualReverse = parent.Reverse.Sequence;
        var firstDraft = vm.Snapshot().EditorDraft!;
        Check(vm.HasUnsavedChanges && firstDraft.ParentCandidateId == parent.Id && firstDraft.EditTemplateSha256 == a.Sha256 && firstDraft.PrimerDraftModified,
            "Manual edits mark the project dirty and retain candidate lineage with its template hash");
        vm.SelectedTarget = vm.Targets[1]; vm.ManualForward = "B raw F"; vm.ManualReverse = "B raw R";
        vm.SelectedTarget = vm.Targets[0];
        Check(vm.ManualForward == parent.Forward.Sequence + "N" && vm.ManualReverse == parent.Reverse.Sequence && vm.Snapshot().EditorDraft?.ParentCandidateId == parent.Id,
            "Returning to a gene restores its own manual sequences and edit parent");
        vm.SelectedTarget = vm.Targets[1];
        Check(vm.ManualForward == "B raw F" && vm.ManualReverse == "B raw R", "A second gene retains a separate pair of manual raw inputs");
        path = Path.Combine(folder,"manual.qpcrproject"); await ProjectStore.SaveAsync(path, vm.Snapshot());
        recovered = Vm("manual-restored"); recovered.Restore(await ProjectStore.LoadAsync(path));
        Check(recovered.ManualForward == "B raw F" && recovered.ManualReverse == "B raw R", "Disk recovery restores the current gene's manual draft without normalization");
        recovered.SelectedTarget = recovered.Targets[0];
        Check(recovered.Snapshot().EditorDraft?.ParentCandidateId == parent.Id && recovered.ManualForward == parent.Forward.Sequence + "N",
            "Disk recovery restores another gene's saved primer lineage and raw edit");

        vm = Vm("entry-guards"); vm.Restore(Copy(project)); vm.TemplateText = a.Sequence + "RAW invalid draft";
        vm.LoadManualCommand.Execute(null);
        Check(vm.TemplateText.EndsWith("RAW invalid draft") && vm.Status.Contains("草稿") && vm.ManualForward == "",
            "Loading a candidate preserves and reports an uncommitted same-gene template draft");
        vm.TemplateText = a.Sequence;
        var single = new SinglePrimerSearch { Target=a, Primers=[parent.Forward], Parameters=seed.Parameters };
        vm.SinglePrimerSearches.Add(single); vm.SelectedSingleSearch=single;
        vm.TemplateText=a.Sequence + "RAW single draft"; vm.UseSinglePrimerCommand.Execute(null);
        Check(vm.TemplateText.EndsWith("RAW single draft") && vm.Status.Contains("草稿") && vm.ManualForward == "",
            "Loading a single primer preserves and reports an uncommitted template draft");
        vm.TemplateText = a.Sequence; vm.ManualForward="unapplied raw F";
        vm.LoadManualCommand.Execute(null);
        Check(vm.ManualForward == "unapplied raw F" && vm.Status.Contains("未应用"), "Candidate loading preserves unapplied manual primer edits");
        vm.SelectedSingleSearch = null; vm.SelectedSingleSearch = single;
        Check(vm.ManualForward == "unapplied raw F", "Viewing same-gene single-primer history preserves the current manual input");

        vm = Vm("collision"); vm.Restore(Copy(project)); vm.TargetName=b.Id;
        vm.ApplyTemplateCommand.Execute(null);
        Check(vm.Targets.Single(t=>t.Id==b.Id).Sequence == b.Sequence && vm.SelectedTarget?.Id == a.Id && vm.TargetName == b.Id && vm.Status.Contains("其他序列"),
            "An existing other gene ID cannot overwrite that gene when applying the current template");
        vm.AddTargetCommand.Execute(null); vm.TargetName=b.Id; vm.TemplateText=a.Sequence;
        vm.ApplyTemplateCommand.Execute(null);
        Check(vm.Targets.Single(t=>t.Id==b.Id).Sequence == b.Sequence && vm.SelectedTarget is null && vm.TemplateText==a.Sequence,
            "An unassigned draft cannot silently replace an existing gene ID");

        vm = Vm("non-consuming-search"); vm.Restore(Copy(project));
        vm.ManualForward="raw manual F before Sense"; vm.ManualReverse="raw manual R before Sense";
        await vm.ExecuteSearchRequestAsync(new(PrimerSearchKind.Sense, seed.Parameters, vm.ManualForward, vm.ManualReverse));
        Check(vm.SinglePrimerSearches.Count==1 && vm.SinglePrimerSearches[0].Primers.Count>0 && vm.Snapshot().EditorDraft?.PrimerDraftModified==true,
            "A real single-primer search keeps the protection of pre-existing manual inputs that it did not consume");
        vm.UseSinglePrimerCommand.Execute(null);
        Check(vm.ManualForward=="raw manual F before Sense" && vm.ManualReverse=="raw manual R before Sense" && vm.Status.Contains("未应用"),
            "A successful unrelated single search cannot enable later silent replacement of manual raw drafts");

        vm=Vm("edit-during-evaluation"); vm.Restore(Copy(project)); vm.LoadManualCommand.Execute(null);
        var evaluation=vm.EvaluateCommand.ExecuteAsync(null);
        Check(!evaluation.IsCompleted && vm.IsBusy,"A real Primer3 evaluation is in flight before a newer manual input is entered");
        vm.ManualForward="new raw F entered during real evaluation";
        await evaluation;
        Check(vm.Runs.Count==2 && vm.SelectedRun?.Candidates.Count>0 && vm.ManualForward=="new raw F entered during real evaluation" && vm.Snapshot().EditorDraft?.PrimerDraftModified==true,
            "Finishing actual Primer3 evaluation consumes only its starting inputs and preserves protection for later edits");
        vm.LoadManualCommand.Execute(null);
        Check(vm.ManualForward=="new raw F entered during real evaluation" && vm.Status.Contains("未应用"),
            "Loading the completed evaluation candidate cannot overwrite a newer in-flight manual draft");

        vm=Vm("edit-during-compatible-search"); vm.Restore(Copy(project)); vm.LoadManualCommand.Execute(null);
        var compatible=vm.SearchWithForwardCommand.ExecuteAsync(null);
        Check(!compatible.IsCompleted && vm.IsBusy,"A real fixed-primer pairing search is in flight before a newer manual input is entered");
        vm.ManualReverse="new raw R entered during real pairing search";
        await compatible;
        Check(vm.Runs.Count==2 && vm.SelectedRun?.Candidates.Count>0 && vm.ManualReverse=="new raw R entered during real pairing search" && vm.Snapshot().EditorDraft?.PrimerDraftModified==true,
            "Actual pairing-search completion retains protection of a later raw reverse-primer edit");
        vm.LoadManualCommand.Execute(null);
        Check(vm.ManualReverse=="new raw R entered during real pairing search" && vm.Status.Contains("未应用"),
            "Loading a pairing-search result cannot overwrite its later manual draft");
        return count;
    }
}
