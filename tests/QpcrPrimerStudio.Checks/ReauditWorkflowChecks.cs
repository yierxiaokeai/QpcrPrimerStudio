using System.IO;
using System.Text.Json;
using CsvHelper;
using QpcrPrimerStudio.Core;
using QpcrPrimerStudio.Desktop;

internal static class ReauditWorkflowChecks
{
    internal static int Run(string root, string work, ProjectDocument seed)
    {
        int count = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); count++; Console.WriteLine("PASS: " + label); }
        var project = JsonSerializer.Deserialize<ProjectDocument>(JsonSerializer.Serialize(seed, ProjectStore.JsonOptions), ProjectStore.JsonOptions)!;
        var badReference = new ReferenceWorkspace { Transcripts = [new() { Id = seed.Targets[0].Id, Sequence = seed.Targets[0].Sequence,
            Exons = [new("chr", 1, long.MaxValue, false, 1, seed.Targets[0].Sequence.Length)] }] };
        var referencePath = Path.Combine(work, "reaudit-invalid.qpcrreference");
        File.WriteAllText(referencePath, JsonSerializer.Serialize(badReference, ProjectStore.JsonOptions));
        bool rejected = false; try { ReferenceImporter.Load(referencePath); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Standalone reference loading validates the complete coordinate graph before binding it");
        var tools = Path.Combine(root, "src/QpcrPrimerStudio.Desktop/bin/Debug/net10.0-windows/tools");
        var vm = new MainViewModel(tools, Path.Combine(work, "reaudit-workflow")); vm.Restore(project);
        var original = vm.SelectedCandidate!; original.Locked = true;
        vm.CandidateNotes = "new revision notes"; vm.NotesCommand.Execute(null);
        var revision = vm.SelectedCandidate!;
        Check(revision != original && revision.ParentCandidateId == original.Id && vm.Candidates.Contains(revision) && revision.Notes == "new revision notes" && original.Notes != revision.Notes,
            "Locked candidate edits select a visible revision and preserve the original notes");
        var candidates = vm.Runs[0].Candidates.Count;
        vm.CandidateNotes = "same revision updated"; vm.NotesCommand.Execute(null);
        Check(vm.Runs[0].Candidates.Count == candidates && vm.SelectedCandidate == revision && revision.Notes == "same revision updated",
            "Subsequent note edits update the visible unlocked revision");
        revision.Selected = true; revision.Assessment.Rejections.Add("observed off-target risk");
        var path = Path.Combine(work, "reaudit-manual-risk.csv"); ResultExporter.Export(path, vm.Snapshot(), true);
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, System.Globalization.CultureInfo.InvariantCulture);
        csv.Read(); csv.ReadHeader();
        var rowCount = 0; var evidenceMatches = true;
        while (csv.Read())
        {
            rowCount++;
            evidenceMatches &= csv.GetField("CandidateId") == revision.Id && !csv.GetField<bool>("Accepted") &&
                csv.GetField("Rejections")!.Contains("off-target") && csv.GetField("TemplateSha256") == vm.Runs[0].Target.Sha256;
        }
        Check(rowCount == 1 && evidenceMatches,
            "Manual checked export includes explicit acceptance, rejection and template identity evidence");
        return count;
    }
}
