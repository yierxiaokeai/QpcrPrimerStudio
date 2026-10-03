using Bio.IO.FastA;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace QpcrPrimerStudio.Core;

public sealed class ReferenceImporter(string parserFolder)
{
    public async Task<ReferenceWorkspace> ImportAsync(ReferenceImportRequest request, string outputFolder, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Version))
            throw new ArgumentException("参考项目需要物种名称和版本标识。");
        if (request.Vcf is not null && (request.Gff is null || request.Genome is null))
            throw new ArgumentException("基因组 VCF 的坐标映射和 REF 校验需要 genome 与 GFF3。");
        Directory.CreateDirectory(outputFolder);
        var result = new ReferenceWorkspace { Name = request.Name, Version = request.Version, ImportedAt = DateTimeOffset.Now };
        foreach (var asset in new (string Role, string? Path)[] { ("Transcriptome", request.Transcripts), ("Genome", request.Genome),
            ("Annotation", request.Gff), ("Variants", request.Vcf), ("GeneMap", request.GeneMap), ("CDS", request.Cds), ("Protein", request.Protein) })
        {
            if (asset.Path is null) continue;
            if (!File.Exists(asset.Path)) throw new FileNotFoundException($"参考文件缺失：{asset.Role}", asset.Path);
            result.Assets.Add(new(asset.Role, Path.GetFullPath(asset.Path), await Task.Run(() => Hashing.File(asset.Path), token)));
        }
        var sequences = await Task.Run(() => SequenceFiles.Read(request.Transcripts), token);
        var mapping = request.GeneMap is null ? new Dictionary<string, string>(StringComparer.Ordinal) : ReadGeneMap(request.GeneMap);
        var genome = request.Genome is null ? null : await Task.Run(() => ReadGenome(request.Genome), token);
        var features = request.Gff is null ? [] : await ParseAsync<GffFeature>("gff", request.Gff, outputFolder, token);
        var transcriptFeatures = features.Where(f => f.Type is "mRNA" or "transcript" or "lnc_RNA" or "ncRNA" or "pseudogenic_transcript")
            .ToDictionary(f => f.Id, StringComparer.Ordinal);
        var childrenByParent = features.SelectMany(f => f.Parents.Select(parent => (parent, feature: f))).ToLookup(item => item.parent, item => item.feature, StringComparer.Ordinal);
        foreach (var sequence in sequences)
        {
            token.ThrowIfCancellationRequested();
            transcriptFeatures.TryGetValue(sequence.Id, out var definition);
            if (request.Gff is not null && definition is null) throw new InvalidDataException($"GFF3 中缺少对应 Transcript ID：{sequence.Id}。");
            var children = childrenByParent[sequence.Id].ToList();
            var exons = children.Where(f => f.Type == "exon").ToList();
            if (definition is not null && exons.Count == 0) throw new InvalidDataException($"{sequence.Id} 缺少 exon 注释，无法可靠映射坐标。");
            if (definition is not null && (definition.Parents.Count != 1 || definition.Strand is not ("+" or "-")))
                throw new InvalidDataException($"{sequence.Id} 的 gene Parent 或链方向不明确。");
            var blocks = new List<ExonBlock>(); var position = 1;
            foreach (var exon in definition?.Strand == "-" ? exons.OrderByDescending(e => e.Start) : exons.OrderBy(e => e.Start))
            {
                if (exon.SeqId != definition!.SeqId || exon.Strand != definition.Strand || exon.End < exon.Start || exon.Start < 1)
                    throw new InvalidDataException($"{sequence.Id} 的外显子坐标或链不一致。");
                var length = checked((int)(exon.End - exon.Start + 1));
                if (blocks.Any(b => b.GenomicStart <= exon.End && b.GenomicEnd >= exon.Start))
                    throw new InvalidDataException($"{sequence.Id} 有重叠 exon。");
                blocks.Add(new(exon.SeqId, exon.Start, exon.End, exon.Strand == "+", position, position + length - 1)); position += length;
            }
            if (blocks.Count > 0 && position - 1 != sequence.Sequence.Length)
                throw new InvalidDataException($"{sequence.Id}：exon 长度 {position - 1} 与转录本 {sequence.Sequence.Length} 不符。");
            if (genome is not null && blocks.Count > 0)
            {
                var reconstructed = string.Concat(blocks.Select(b =>
                {
                    if (!genome.TryGetValue(b.SeqId, out var chromosome) || b.GenomicEnd > chromosome.Length)
                        throw new InvalidDataException($"{sequence.Id} 的 exon 超出参考基因组。");
                    var segment = chromosome.Substring(checked((int)b.GenomicStart - 1), b.End - b.Start + 1);
                    return b.Plus ? segment : SequenceFiles.ReverseComplement(segment);
                }));
                if (reconstructed != sequence.Sequence) throw new InvalidDataException($"{sequence.Id}：参考基因组重建序列与转录本不一致。");
            }
            var cds = new List<SequenceRegion>();
            foreach (var feature in children.Where(f => f.Type == "CDS"))
            {
                var block = blocks.FirstOrDefault(b => b.SeqId == feature.SeqId && b.GenomicStart <= feature.Start && b.GenomicEnd >= feature.End);
                if (block is null || feature.Strand != definition!.Strand) throw new InvalidDataException($"{sequence.Id} 的 CDS 不在 exon 内。");
                var start = block.Plus ? block.Start + feature.Start - block.GenomicStart : block.Start + block.GenomicEnd - feature.End;
                cds.Add(new(checked((int)start), checked((int)(feature.End - feature.Start + 1))));
            }
            var gene = definition?.Parents.Single() ?? mapping.GetValueOrDefault(sequence.Id, "");
            if (mapping.TryGetValue(sequence.Id, out var explicitGene) && gene.Length > 0 && gene != explicitGene)
                throw new InvalidDataException($"{sequence.Id} 的 GFF3 与 gene mapping 冲突。");
            result.Transcripts.Add(new() { Id = sequence.Id, GeneId = gene, Sequence = sequence.Sequence, Exons = blocks, Cds = cds.OrderBy(c => c.Start).ToList() });
        }
        if (request.Cds is not null)
        foreach (var cdsSequence in SequenceFiles.Read(request.Cds))
        {
            var transcript = result.Find(cdsSequence.Id) ?? throw new InvalidDataException($"CDS ID 无对应转录本：{cdsSequence.Id}");
            if (transcript.Cds.Count > 0 && string.Concat(transcript.Cds.Select(c => transcript.Sequence.Substring(c.Start - 1, c.Length))) != cdsSequence.Sequence)
                throw new InvalidDataException($"{transcript.Id} 的 CDS FASTA 与注释不一致。");
            if (transcript.Cds.Count == 0)
            {
                var start = transcript.Sequence.IndexOf(cdsSequence.Sequence, StringComparison.Ordinal);
                if (start < 0 || transcript.Sequence.IndexOf(cdsSequence.Sequence, start + 1, StringComparison.Ordinal) >= 0)
                    throw new InvalidDataException($"{transcript.Id} 的 CDS 无法唯一定位。");
                var index = result.Transcripts.IndexOf(transcript);
                result.Transcripts[index] = transcript with { Cds = [new(start + 1, cdsSequence.Sequence.Length)] };
            }
        }
        if (request.Vcf is not null)
        {
            result.Variants = await ParseAsync<VariantSite>("vcf", request.Vcf, outputFolder, token);
            foreach (var v in result.Variants)
            {
                if (v.Position < 1 || v.End < v.Position || !genome!.TryGetValue(v.SeqId, out var chr) || v.Position + v.Ref.Length - 1 > chr.Length ||
                    chr.Substring(checked((int)v.Position - 1), v.Ref.Length) != v.Ref.ToUpperInvariant())
                    throw new InvalidDataException($"VCF REF 与参考基因组不匹配：{v.SeqId}:{v.Position}。");
            }
        }
        if (!result.HasGenome) result.Warnings.Add("Genome specificity：Not available；gDNA discrimination：Unknown。");
        if (features.Count == 0) result.Warnings.Add("无 exon 注释；Exon-junction design：Not available。");
        if (result.Transcripts.Any(t => t.GeneId.Length == 0)) result.Warnings.Add("部分转录本缺少 gene 映射；这些靶标不能进行 gene-level 或 isoform-specific 设计。");
        if (result.Transcripts.Any(t => t.Cds.Count == 0)) result.Warnings.Add("部分转录本没有 CDS 注释；Prefer CDS 将记录全转录本回退，严格 CDS/UTR 模式会拒绝缺失注释。");
        ProjectValidation.Validate(new ProjectDocument { Reference = result });
        BuildIndex(result, Path.Combine(outputFolder, "reference.sqlite"));
        await File.WriteAllTextAsync(Path.Combine(outputFolder, "reference.qpcrreference"), JsonSerializer.Serialize(result, ProjectStore.JsonOptions), token);
        return result;
    }
    private async Task<List<T>> ParseAsync<T>(string mode, string input, string folder, CancellationToken token)
    {
        var output = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".jsonl");
        await ProcessRunner.RunAsync(Path.Combine(parserFolder, "node.exe"), [Path.Combine(parserFolder, "import.mjs"), mode, input, output], null, token, TimeSpan.FromHours(1));
        var items = new List<T>();
        using var reader = new StreamReader(output);
        while (await reader.ReadLineAsync(token) is { } line)
            items.Add(JsonSerializer.Deserialize<T>(line) ?? throw new InvalidDataException("解析器返回空记录。"));
        return items;
    }
    public static ReferenceWorkspace Load(string path)
    {
        var reference = JsonSerializer.Deserialize<ReferenceWorkspace>(File.ReadAllText(path), ProjectStore.JsonOptions)
            ?? throw new InvalidDataException("参考项目格式无效。");
        ProjectValidation.Validate(new ProjectDocument { Reference = reference });
        return reference;
    }
    private static Dictionary<string, string> ReadGenome(string path)
    {
        using var stream = File.OpenRead(path);
        return new FastAParser().Parse(stream).ToDictionary(s => s.ID.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0],
            s => new string(s.Select(b => (char)b).ToArray()).ToUpperInvariant(), StringComparer.Ordinal);
    }
    private static Dictionary<string, string> ReadGeneMap(string path)
    {
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = "\t" });
        return csv.GetRecords<GeneMapping>().ToDictionary(m => m.Transcript, m => m.Gene, StringComparer.Ordinal);
    }
    private static void BuildIndex(ReferenceWorkspace reference, string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}"); connection.Open();
        using var transaction = connection.BeginTransaction();
        using var create = connection.CreateCommand(); create.Transaction = transaction;
        create.CommandText = "CREATE TABLE transcripts(id TEXT PRIMARY KEY,gene TEXT,sequence TEXT,annotation TEXT); CREATE INDEX genes ON transcripts(gene); CREATE TABLE variants(seqid TEXT,start INTEGER,end INTEGER,record TEXT); CREATE INDEX variant_position ON variants(seqid,start,end);";
        create.ExecuteNonQuery();
        foreach (var t in reference.Transcripts)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO transcripts VALUES($id,$gene,$sequence,$annotation)";
            command.Parameters.AddWithValue("$id", t.Id); command.Parameters.AddWithValue("$gene", t.GeneId);
            command.Parameters.AddWithValue("$sequence", t.Sequence); command.Parameters.AddWithValue("$annotation", JsonSerializer.Serialize(t)); command.ExecuteNonQuery();
        }
        foreach (var v in reference.Variants)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO variants VALUES($seq,$start,$end,$record)";
            command.Parameters.AddWithValue("$seq", v.SeqId); command.Parameters.AddWithValue("$start", v.Position);
            command.Parameters.AddWithValue("$end", v.End); command.Parameters.AddWithValue("$record", JsonSerializer.Serialize(v)); command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    private sealed record GffFeature(string SeqId, string Type, long Start, long End, string Strand, string Id, List<string> Parents, object? Phase);
    private sealed record GeneMapping(string Transcript, string Gene);
}
