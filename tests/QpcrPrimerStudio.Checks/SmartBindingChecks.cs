using System.IO;
using System.Text.Json;
using QpcrPrimerStudio.Core;

internal static class SmartBindingChecks
{
    internal static async Task<int> RunAsync(string root, string work, SequenceTarget target, TargetRun run)
    {
        var count = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            count++; Console.WriteLine("PASS: " + label);
        }
        static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ProjectStore.JsonOptions), ProjectStore.JsonOptions)!;
        var candidate = Copy(run.Candidates[0]); candidate.Specificity.Clear();
        List<string> Screen(PrimerCandidate c, SequenceTarget? t = null, List<DatabaseManifest>? dbs = null) =>
            SmartBindingScreen.Rejections(c, t ?? target, dbs ?? []);
        Check(Screen(candidate).Count == 0, "Exact local F/R binding passes without claiming database verification");
        var forwardMismatch = Copy(candidate);
        var f = forwardMismatch.Forward.Sequence.ToCharArray(); f[^1] = f[^1] == 'A' ? 'C' : 'A';
        forwardMismatch.Forward = forwardMismatch.Forward with { Sequence = new string(f) };
        Check(Screen(forwardMismatch).Any(r => r.Contains("F 与目标模板错配 1") && r.Contains("5 nt 内 1")),
            "Forward terminal mismatch reports total and three-prime counts");
        var reverseMismatch = Copy(candidate);
        var r = reverseMismatch.Reverse.Sequence.ToCharArray(); r[0] = r[0] == 'A' ? 'C' : 'A';
        reverseMismatch.Reverse = reverseMismatch.Reverse with { Sequence = new string(r) };
        Check(Screen(reverseMismatch).Any(s => s.Contains("R 与目标模板错配 1") && s.Contains("5 nt 内 0")),
            "Reverse mismatch uses primer orientation rather than positive-strand orientation");
        var ambiguous = target.Sequence.ToCharArray(); ambiguous[candidate.Forward.Start - 1] = 'N';
        Check(Screen(candidate, target with { Sequence = new string(ambiguous) }).Any(s => s.Contains("歧义碱基")),
            "Ambiguous template binding cannot establish zero mismatches");
        var db = new DatabaseManifest("fixture", "转录本", "fixture", "fixture", "fixture", 1, DateTimeOffset.Now, "fixture");
        Check(Screen(candidate, dbs: [db]).Any(s => s.Contains("尚未完成")), "A bound database requires specificity evidence before smart selection");
        BindingSite Site(string query, string subject) => query == "F"
            ? new(query, subject, candidate.Forward.Start, candidate.Forward.End, true, 0, 0)
            : new(query, subject, candidate.Reverse.Start, candidate.Reverse.End, false, 0, 0);
        PotentialProduct Product(string subject) => new(subject, candidate.Forward.Start, candidate.Reverse.End, "F", "R", 0, true)
        { ForwardSite = Site("F", subject), ReverseSite = Site("R", subject) };
        candidate.Specificity = [new SpecificityReport { Kind = "转录本", Status = "完成", Database = db,
            QueryPairSha256 = CandidateEvidenceOwnership.QueryPairSha256(candidate.Forward.Sequence, candidate.Reverse.Sequence),
            Products = [Product(target.Id)] }];
        Check(Screen(candidate, dbs: [db]).Count == 0, "A complete exact expected F/R product passes the binding screen");
        var indel = Copy(candidate); var product = indel.Specificity[0].Products[0];
        indel.Specificity[0].Products[0] = product with { ForwardSite = product.ForwardSite! with { IndelBases = 1 } };
        Check(Screen(indel).Any(s => s.Contains("无插缺")), "Zero substitution count cannot hide a binding indel");
        var missingSite = Copy(candidate); missingSite.Specificity[0].Products[0] = product with { ReverseSite = null };
        Check(Screen(missingSite).Count > 0, "An expected product without complete binding sites cannot pass");
        var mismatch = Copy(candidate); mismatch.Specificity[0].Products[0] = product with
        { Mismatches = 1, ForwardSite = product.ForwardSite! with { Mismatches = 1, ThreePrimeMismatches = 1 } };
        Check(Screen(mismatch).Any(s => s.Contains("最少总错配 1")), "Expected database products with mismatches are excluded");
        var additional = Copy(candidate); additional.Specificity[0].Products.Add(mismatch.Specificity[0].Products[0]);
        Check(Screen(additional).Count == 0, "An additional imperfect site does not invalidate a verified exact expected pair");
        var multiple = target with { ExpectedSubjects = [target.Id, "second-transcript"] };
        Check(Screen(candidate, multiple).Any(s => s.Contains("second-transcript")), "Each requested expected transcript must have a verified exact pair");
        additional.Specificity[0].Products.Add(Product("second-transcript"));
        Check(Screen(additional, multiple).Count == 0, "All requested expected transcripts can be verified independently");
        var offTarget = Copy(candidate); offTarget.Specificity[0].Products.Add(Product("other-gene") with { Expected = false });
        Check(Screen(offTarget).Any(s => s.Contains("非靶向产物")), "Potential non-target products are rejected separately from intended-target mismatches");
        var incomplete = Copy(candidate); incomplete.Specificity[0].Status = "搜索不完整";
        Check(Screen(incomplete).Any(s => s.Contains("尚未核对完成")), "Incomplete searches cannot establish smart-export eligibility");
        var stale = Copy(candidate); stale.Specificity[0].QueryPairSha256 = "previous-query";
        Check(Screen(stale).Count > 0, "Evidence from a different query pair cannot confirm zero mismatches");
        var genome = Copy(candidate); genome.Specificity[0].Kind = "基因组"; genome.Specificity[0].Products.Clear();
        Check(Screen(genome).Count == 0, "A verified genome search may have no genomic product for junction primers");

        var folder = Path.Combine(work, "smart-binding"); Directory.CreateDirectory(folder);
        var paths = EnginePaths.FromFolder(Path.Combine(root, "src/QpcrPrimerStudio.Desktop/bin/Debug/net10.0-windows/tools"));
        var checker = new SpecificityEngine(paths);
        var altered = target.Sequence.ToCharArray(); var position = candidate.Forward.End - 1;
        altered[position] = altered[position] == 'A' ? 'C' : 'A';
        var fasta = Path.Combine(folder, "terminal-mismatch.fasta");
        await File.WriteAllTextAsync(fasta, $">{target.Id}\n{new string(altered)}\n");
        var actualDb = await checker.BuildDatabaseAsync(fasta, "intended terminal mismatch", "转录本", Path.Combine(folder, "db"), default);
        var actual = Copy(candidate);
        actual.Specificity = [await checker.CheckAsync(actual, target, actualDb, new(), folder, default)];
        Check(actual.Specificity[0].Products.Any(p => p.Expected && p.Mismatches == 1) &&
            Screen(actual, dbs: [actualDb]).Any(s => s.Contains("零错配")),
            "Real BLAST detects the expected terminal mismatch and smart binding excludes it");
        return count;
    }
}
