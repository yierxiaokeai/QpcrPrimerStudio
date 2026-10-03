using System.IO;
using System.Text.Json;
using QpcrPrimerStudio.Core;
using QpcrPrimerStudio.Desktop;

internal static class SelectionAutoSaveChecks
{
    internal static async Task<int> RunAsync(string root, string work, ProjectDocument seed)
    {
        var count = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            count++; Console.WriteLine("PASS: " + label);
        }
        var folder = Path.Combine(work, "selection-autosave");
        Directory.CreateDirectory(folder);
        var engines = Path.Combine(root, "src", "QpcrPrimerStudio.Desktop", "bin", "Debug", "net10.0-windows", "tools");
        MainViewModel ViewModel(string storage) => new(engines, storage);
        ProjectDocument CopySeed()
        {
            var project = JsonSerializer.Deserialize<ProjectDocument>(JsonSerializer.Serialize(seed, ProjectStore.JsonOptions), ProjectStore.JsonOptions)!;
            foreach (var candidate in project.Runs.SelectMany(r => r.Candidates)) candidate.Selected = false;
            return project;
        }
        static PrimerCandidate First(ProjectDocument project) => project.Runs.SelectMany(r => r.Candidates).First();
        static PrimerCandidate Current(MainViewModel vm) => vm.Runs.SelectMany(r => r.Candidates).First();
        static bool SamePath(string first, string second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

        var draftStorage = Path.Combine(folder, "draft-profile");
        var vm = ViewModel(draftStorage);
        Check(!await vm.RecoverLastAutoSaveAsync(), "An empty profile has no recoverable selection draft");
        vm.Restore(CopySeed());
        var candidate = Current(vm);
        Check(await vm.AutoSaveSelectionAsync(candidate, true), "Selecting a candidate writes the first project draft");
        await vm.FlushSelectionSavesAsync();
        var firstDraftPath = vm.ProjectPath;
        var firstDraft = await ProjectStore.LoadAsync(firstDraftPath);
        Check(File.Exists(firstDraftPath) && First(firstDraft).Selected && First(firstDraft).Id == candidate.Id && !vm.HasUnsavedChanges,
            "The first draft persists the selected candidate and acknowledges completed disk storage");
        Check(!string.IsNullOrWhiteSpace(vm.AutoSaveStatus), "Selection autosave exposes a visible save status");

        var recovered = ViewModel(draftStorage);
        Check(await recovered.RecoverLastAutoSaveAsync(), "A new application session can discover the first saved draft");
        Check(SamePath(recovered.ProjectPath, firstDraftPath) && Current(recovered).Selected && Current(recovered).Id == candidate.Id,
            "Draft recovery restores the project identity and candidate selection");
        Check(await recovered.AutoSaveSelectionAsync(Current(recovered), false), "Clearing a candidate selection is saved automatically");
        await recovered.FlushSelectionSavesAsync();
        Check(!First(await ProjectStore.LoadAsync(firstDraftPath)).Selected,
            "The project on disk retains the cleared selection");

        var changes = new List<Task<bool>>();
        for (var i = 0; i < 21; i++) changes.Add(recovered.AutoSaveSelectionAsync(Current(recovered), i % 2 == 0));
        var outcomes = await Task.WhenAll(changes);
        await recovered.FlushSelectionSavesAsync();
        Check(outcomes.All(success => success) && Current(recovered).Selected && First(await ProjectStore.LoadAsync(firstDraftPath)).Selected
            && !recovered.HasUnsavedChanges, "Rapid selection changes preserve the final state without stale disk writes");

        var existingPath = Path.Combine(folder, "existing-project.qpcrproject");
        var existingProject = CopySeed();
        await ProjectStore.SaveAsync(existingPath, existingProject);
        var existingVm = ViewModel(Path.Combine(folder, "existing-profile"));
        existingVm.Restore(existingProject); existingVm.ProjectPath = existingPath;
        Check(await existingVm.AutoSaveSelectionAsync(Current(existingVm), true), "Selection autosave updates an existing named project");
        await existingVm.FlushSelectionSavesAsync();
        Check(SamePath(existingVm.ProjectPath, existingPath) && First(await ProjectStore.LoadAsync(existingPath)).Selected,
            "An existing project keeps its chosen file location during autosave");

        const string unfinishedName = "  next_gene_unfinished  ";
        const string unfinishedTemplate = ">next_gene\r\nACGTACGT\r\n待核对的文字";
        var previousTarget = existingVm.SelectedTarget!;
        existingVm.TargetName = unfinishedName; existingVm.TemplateText = unfinishedTemplate;
        Check(await existingVm.AutoSaveSelectionAsync(Current(existingVm), false),
            "Unapplied invalid template text does not prevent saving a candidate selection");
        await existingVm.FlushSelectionSavesAsync();
        var draftVm = ViewModel(Path.Combine(folder, "existing-profile"));
        Check(await draftVm.RecoverLastAutoSaveAsync() && draftVm.TargetName == unfinishedName && draftVm.TemplateText == unfinishedTemplate,
            "Recovery preserves the exact unfinished template and target name");
        Check(draftVm.Targets.Any(t => t.Id == previousTarget.Id && t.Sequence == previousTarget.Sequence) && !Current(draftVm).Selected,
            "Saving an editor draft preserves the last applied target and the latest selection");

        var projectA = CopySeed(); var projectB = CopySeed();
        First(projectA).Id = "autosave-project-a"; First(projectB).Id = "autosave-project-b";
        var pathA = Path.Combine(folder, "project-a.qpcrproject");
        var pathB = Path.Combine(folder, "project-b.qpcrproject");
        await ProjectStore.SaveAsync(pathA, projectA);
        await ProjectStore.SaveAsync(pathB, projectB);
        var switchingVm = ViewModel(Path.Combine(folder, "switching-profile"));
        switchingVm.Restore(projectA); switchingVm.ProjectPath = pathA;
        var savingA = switchingVm.AutoSaveSelectionAsync(Current(switchingVm), true);
        switchingVm.Restore(projectB); switchingVm.ProjectPath = pathB;
        var savingB = switchingVm.AutoSaveSelectionAsync(Current(switchingVm), true);
        var switchingOutcomes = await Task.WhenAll(savingA, savingB);
        await switchingVm.FlushSelectionSavesAsync();
        var savedA = await ProjectStore.LoadAsync(pathA); var savedB = await ProjectStore.LoadAsync(pathB);
        Check(switchingOutcomes.All(success => success) && First(savedA).Id == "autosave-project-a" && First(savedA).Selected
            && First(savedB).Id == "autosave-project-b" && First(savedB).Selected,
            "Queued saves retain their own project contents and destination across a project switch");
        Check(SamePath(switchingVm.ProjectPath, pathB) && Current(switchingVm).Id == "autosave-project-b" && !switchingVm.HasUnsavedChanges,
            "Completion of an earlier save cannot replace the current project identity or save state");

        var newSessionProject = CopySeed(); First(newSessionProject).Id = "independent-new-session";
        var newSessionVm = ViewModel(draftStorage); newSessionVm.Restore(newSessionProject);
        Check(await newSessionVm.AutoSaveSelectionAsync(Current(newSessionVm), true), "A fresh session can save an independent project draft");
        await newSessionVm.FlushSelectionSavesAsync();
        Check(!SamePath(newSessionVm.ProjectPath, firstDraftPath)
            && First(await ProjectStore.LoadAsync(firstDraftPath)).Id == candidate.Id
            && First(await ProjectStore.LoadAsync(newSessionVm.ProjectPath)).Id == "independent-new-session",
            "A new project draft never overwrites a previous session's draft");

        var blockedPath = Path.Combine(folder, "directory-at-project-path.qpcrproject");
        Directory.CreateDirectory(blockedPath);
        var failedVm = ViewModel(Path.Combine(folder, "failure-profile"));
        failedVm.Restore(CopySeed()); failedVm.ProjectPath = blockedPath;
        Check(!await failedVm.AutoSaveSelectionAsync(Current(failedVm), true), "A real filesystem write failure is reported as an unsuccessful save");
        await failedVm.FlushSelectionSavesAsync();
        Check(Current(failedVm).Selected && failedVm.HasUnsavedChanges && Directory.Exists(blockedPath)
            && !string.IsNullOrWhiteSpace(failedVm.AutoSaveStatus),
            "A failed write preserves the in-memory selection and reports unsaved changes");
        var retryPath = Path.Combine(folder, "recovered-write.qpcrproject");
        failedVm.ProjectPath = retryPath;
        Check(await failedVm.AutoSaveSelectionAsync(Current(failedVm), true), "The selected candidate can be saved after correcting the file location");
        await failedVm.FlushSelectionSavesAsync();
        Check(First(await ProjectStore.LoadAsync(retryPath)).Selected && !failedVm.HasUnsavedChanges,
            "A successful retry persists the retained selection and clears unsaved state");
        return count;
    }
}
