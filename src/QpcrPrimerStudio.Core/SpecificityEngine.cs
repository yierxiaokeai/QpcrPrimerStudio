using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace QpcrPrimerStudio.Core;

public sealed class SpecificityEngine(EnginePaths paths)
{
    public async Task<DatabaseManifest> BuildDatabaseAsync(string fasta, string name, string kind,
        string storageRoot, CancellationToken token)
    {
        if (kind is not ("转录本" or "基因组")) throw new ArgumentException("数据库类型需为转录本或基因组。");
        var count = await Task.Run(() => SequenceFiles.CountFasta(fasta), token);
        var hash = await Task.Run(() => Hashing.File(fasta), token);
        var folder = Path.Combine(storageRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, "reference.fasta");
        File.Copy(fasta, input);
        var prefix = Path.Combine(folder, "reference");
        await ProcessRunner.RunAsync(paths.MakeBlastDb, ["-in", "reference.fasta", "-dbtype", "nucl", "-blastdb_version", "4", "-parse_seqids", "-out", "reference"],
            null, token, TimeSpan.FromHours(1), folder);
        var version = await ProcessRunner.RunAsync(paths.BlastN, ["-version"], null, token);
        var manifest = new DatabaseManifest(name, kind, prefix, hash, Path.GetFullPath(fasta), count,
            DateTimeOffset.Now, version.Output.Trim());
        await File.WriteAllTextAsync(Path.Combine(folder, "database.json"), JsonSerializer.Serialize(manifest, ProjectStore.JsonOptions), token);
        return manifest;
    }

    public static DatabaseManifest ReadDatabase(string manifestPath) => JsonSerializer.Deserialize<DatabaseManifest>(File.ReadAllText(manifestPath))
        ?? throw new InvalidDataException("数据库清单无效。");

    public async Task<SpecificityReport> CheckAsync(PrimerCandidate candidate, SequenceTarget target,
        DatabaseManifest db, SpecificitySettings settings, string workRoot, CancellationToken token)
    {
        if (settings.MaxMismatches < 0 || settings.MaxThreePrimeMismatches < 0 || settings.ThreePrimeBases < 1 ||
            settings.MaxProduct < 40 || settings.MaxSubjects < 1)
            throw new ArgumentException("特异性参数无效。");
        // BLAST 2.14's Windows file APIs do not reliably accept Unicode absolute paths.
        // Execute inside the database volume and pass ASCII relative filenames throughout.
        var folder = Path.Combine(Path.GetDirectoryName(db.Prefix)!, "jobs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var queries = new Dictionary<string, string> { ["F"] = candidate.Forward.Sequence, ["R"] = candidate.Reverse.Sequence };
        var queryPairSha256 = CandidateEvidenceOwnership.QueryPairSha256(queries["F"], queries["R"]);
        var queryPath = Path.Combine(folder, "query.fasta");
        await File.WriteAllTextAsync(queryPath, $">F\n{queries["F"]}\n>R\n{queries["R"]}\n", token);
        var xmlPath = Path.Combine(folder, "hits.xml");
        var execution = await ProcessRunner.RunAsync(paths.BlastN,
            ["-task", "blastn-short", "-query", "query.fasta", "-db", Path.GetRelativePath(folder, db.Prefix), "-out", "hits.xml",
                "-outfmt", "5", "-word_size", "7", "-evalue", "1000", "-dust", "no", "-soft_masking", "false",
                "-reward", "1", "-penalty", "-1", "-gapopen", "2", "-gapextend", "2",
                "-max_target_seqs", settings.MaxSubjects.ToString(CultureInfo.InvariantCulture)],
            null, token, TimeSpan.FromMinutes(20), folder);
        var raw = await File.ReadAllTextAsync(xmlPath, token);
        using var reader = XmlReader.Create(new StringReader(raw), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        var xml = XDocument.Load(reader);
        var sites = new List<BindingSite>();
        var requests = new List<(string Query, string Subject, long Start, long End, bool Plus)>();
        var seen = new HashSet<string>();
        var incomplete = !string.IsNullOrWhiteSpace(execution.Error);
        foreach (var iteration in xml.Descendants("Iteration"))
        {
            var queryId = Value(iteration, "Iteration_query-def").Split(' ')[0];
            var sequence = queries[queryId];
            var hits = iteration.Descendants("Hit").ToList();
            if (hits.Count >= settings.MaxSubjects) incomplete = true;
            foreach (var hit in hits)
            {
                var subject = Value(hit, "Hit_accession");
                var subjectLength = Long(hit, "Hit_len");
                foreach (var hsp in hit.Descendants("Hsp"))
                {
                    token.ThrowIfCancellationRequested();
                    var qFrom = Long(hsp, "Hsp_query-from"); var qTo = Long(hsp, "Hsp_query-to");
                    var from = Long(hsp, "Hsp_hit-from"); var to = Long(hsp, "Hsp_hit-to");
                    var plus = from <= to;
                    if (Value(hsp, "Hsp_qseq").Contains('-') || Value(hsp, "Hsp_hseq").Contains('-'))
                    {
                        if (qFrom != 1 || qTo != sequence.Length) { incomplete = true; continue; }
                        var alignedQuery = Value(hsp, "Hsp_qseq"); var alignedSubject = Value(hsp, "Hsp_hseq");
                        if (alignedQuery.Length != alignedSubject.Length) throw new InvalidDataException("BLAST 有缺口比对长度不一致。");
                        var evidence = new List<MismatchEvidence>(); var queryPosition = 0; var indels = 0;
                        for (var column = 0; column < alignedQuery.Length; column++)
                        {
                            if (alignedQuery[column] != '-') queryPosition++;
                            if (alignedQuery[column] == '-' || alignedSubject[column] == '-') indels++;
                            if (alignedQuery[column] == alignedSubject[column]) continue;
                            var pos = Math.Clamp(queryPosition, 1, sequence.Length); var distance = sequence.Length - pos + 1;
                            evidence.Add(new(pos, distance, alignedQuery[column], alignedSubject[column], distance == 1 ? 4 : distance <= 5 ? 2 : 0.5));
                        }
                        var terminal = evidence.Count(e => e.PositionFromThreePrime <= settings.ThreePrimeBases);
                        var gapKey = $"{queryId}/{subject}/{Math.Min(from, to)}/{Math.Max(from, to)}/{plus}";
                        if (evidence.Count <= settings.MaxMismatches && terminal <= settings.MaxThreePrimeMismatches && seen.Add(gapKey))
                            sites.Add(new(queryId, subject, Math.Min(from, to), Math.Max(from, to), plus, evidence.Count, terminal)
                            { IndelBases = indels, MismatchPositions = evidence });
                        continue;
                    }
                    var start = plus ? from - qFrom + 1 : to - sequence.Length + qTo;
                    var end = plus ? to + sequence.Length - qTo : from + qFrom - 1;
                    if (start < 1 || end > subjectLength || end - start + 1 != sequence.Length) continue;
                    var key = $"{queryId}/{subject}/{start}/{end}/{plus}";
                    if (!seen.Add(key)) continue;
                    requests.Add((queryId, subject, start, end, plus));
                }
            }
        }
        if (requests.Count > 0)
        {
            var batchPath = Path.Combine(folder, "binding-ranges.txt");
            await File.WriteAllLinesAsync(batchPath, requests.Select(r => $"{r.Subject} {r.Start}-{r.End} {(r.Plus ? "plus" : "minus")}"), token);
            var fetched = await ProcessRunner.RunAsync(paths.BlastDbCmd, ["-db", Path.GetRelativePath(folder, db.Prefix), "-entry_batch", "binding-ranges.txt", "-outfmt", "%s"], null, token, workingDirectory: folder);
            if (!string.IsNullOrWhiteSpace(fetched.Error)) throw new InvalidDataException("结合位点提取出现警告：" + fetched.Error);
            var sequences = fetched.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (sequences.Length != requests.Count) throw new InvalidDataException("批量结合位点数量不一致。");
            for (var index = 0; index < requests.Count; index++)
            {
                var request = requests[index]; var sequence = queries[request.Query]; var bindingSequence = SequenceFiles.Normalize(sequences[index]);
                if (bindingSequence.Length != sequence.Length) throw new InvalidDataException("数据库返回的完整结合位点长度不一致。");
                var mismatches = Enumerable.Range(0, sequence.Length).Where(i => sequence[i] != bindingSequence[i]).ToList();
                var threePrime = mismatches.Count(i => i >= sequence.Length - settings.ThreePrimeBases);
                if (mismatches.Count <= settings.MaxMismatches && threePrime <= settings.MaxThreePrimeMismatches)
                    sites.Add(new(request.Query, request.Subject, request.Start, request.End, request.Plus, mismatches.Count, threePrime)
                    { MismatchPositions = mismatches.Select(i => new MismatchEvidence(i + 1, sequence.Length - i, sequence[i], bindingSequence[i],
                        sequence.Length - i == 1 ? 4 : sequence.Length - i <= 5 ? 2 : 0.5)).ToList() });
            }
        }
        var expected = target.ExpectedSubjects.Count > 0 ? target.ExpectedSubjects : [target.Id];
        var products = PairSites(sites, expected, db.Kind, settings.MaxProduct);
        if (db.Kind == "基因组")
        {
            var fExon = target.ExonBlocks.FirstOrDefault(e => e.Contains(candidate.Forward.Start) && e.Contains(candidate.Forward.End));
            var rExon = target.ExonBlocks.FirstOrDefault(e => e.Contains(candidate.Reverse.Start) && e.Contains(candidate.Reverse.End));
            if (fExon is not null && rExon is not null && fExon.SeqId == rExon.SeqId && fExon.Plus == rExon.Plus)
            {
                var start = Math.Min(fExon.GenomicPosition(candidate.Forward.Start), rExon.GenomicPosition(candidate.Reverse.End));
                var end = Math.Max(fExon.GenomicPosition(candidate.Forward.Start), rExon.GenomicPosition(candidate.Reverse.End));
                products = products.Select(p => p.SubjectId == fExon.SeqId && p.Start == start && p.End == end && p.FirstPrimer != p.SecondPrimer ? p with { Expected = true } : p).ToList();
            }
        }
        var expectedFound = products.Any(p => p.Expected);
        var risks = products.Any(p => !p.Expected);
        var status = incomplete ? "搜索不完整，需复核" : risks ? "检出非靶向产物风险" :
            db.Kind == "转录本" && !expectedFound ? "未检出预期产物，需核对数据库与 ID" : "所选范围内未检出非靶向产物";
        if (db.Kind == "基因组" && target.ReferenceGenomeSha256.Length > 0 && db.SourceSha256 != target.ReferenceGenomeSha256)
            status = "数据库与参考基因组不匹配，需复核；" + status;
        return new SpecificityReport
        {
            Kind = db.Kind, Database = db, Settings = settings, Sites = sites, Products = products,
            Status = status, RawXml = raw, QueryPairSha256 = queryPairSha256,
            Evidence = $"BLASTn-short，种子 7 nt；完整位点延伸复核；允许总错配 ≤{settings.MaxMismatches}，末端 {settings.ThreePrimeBases} nt 错配 ≤{settings.MaxThreePrimeMismatches}；" +
                $"成对产物 ≤{settings.MaxProduct} bp；包含 F/R、F/F、R/R 组合；完整引物的有缺口 HSP 记录插缺，部分有缺口 HSP 标记搜索不完整。" +
                $"\n数据库 {db.Name}：{db.SequenceCount:N0} 条；SHA256 {db.SourceSha256}。" +
                $"\n预期转录本 ID：{string.Join(", ", expected)}；潜在产物 {products.Count} 个。" +
                "\n短序列启发式搜索存在覆盖限制；错配类型保存原始碱基，位置权重是启发式，未据此估计 PCR 成功概率或错配结合 Tm；结果只适用于所选数据库及参数。" +
                (string.IsNullOrWhiteSpace(execution.Error) ? "" : "\n引擎警告：" + execution.Error)
        };
    }

    public static List<PotentialProduct> PairSites(List<BindingSite> sites, List<string> expected, string kind, int maxProduct)
    {
        var products = new List<PotentialProduct>();
        foreach (var group in sites.GroupBy(s => s.SubjectId))
        {
            var right = group.Where(s => !s.Plus).OrderBy(s => s.Start).ToList();
            foreach (var left in group.Where(s => s.Plus))
            foreach (var r in right)
            {
                if (r.Start <= left.End || r.End - left.Start + 1 > maxProduct) continue;
                var expectedProduct = kind == "转录本" && expected.Contains(group.Key, StringComparer.Ordinal) && left.QueryId != r.QueryId;
                var terminalDisruption = left.MismatchPositions.Sum(m => m.Weight) + r.MismatchPositions.Sum(m => m.Weight);
                var terminalMismatch = left.MismatchPositions.Any(m => m.PositionFromThreePrime == 1) || r.MismatchPositions.Any(m => m.PositionFromThreePrime == 1);
                products.Add(new(group.Key, left.Start, r.End, left.QueryId, r.QueryId, left.Mismatches + r.Mismatches, expectedProduct)
                { Risk = terminalMismatch ? "Lower (3′ terminal mismatch)" : terminalDisruption >= 4 ? "Moderate" : "High",
                    ForwardSite = left, ReverseSite = r });
            }
        }
        return products.Distinct().OrderBy(p => p.SubjectId).ThenBy(p => p.Start).ToList();
    }
    private static string Value(XElement element, string name) => element.Element(name)?.Value
        ?? throw new InvalidDataException($"BLAST XML 缺少 {name}。");
    private static long Long(XElement element, string name) => long.Parse(Value(element, name), CultureInfo.InvariantCulture);
}
