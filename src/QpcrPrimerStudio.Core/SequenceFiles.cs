using Bio;
using Bio.IO;
using Bio.IO.FastA;
using Bio.IO.GenBank;

namespace QpcrPrimerStudio.Core;

public static class SequenceFiles
{
    static SequenceFiles() => BioRuntimeCompatibility.Initialize();
    public static List<SequenceTarget> Read(string path)
    {
        using var stream = File.OpenRead(path);
        ISequenceParser parser = Path.GetExtension(path).ToLowerInvariant() is ".gb" or ".gbk" or ".genbank"
            ? new GenBankParser() : new FastAParser();
        var targets = parser.Parse(stream).Select(s => new SequenceTarget
        {
            Id = s.ID.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "sequence",
            Sequence = new string(s.Select(b => (char)b).ToArray()).ToUpperInvariant(), Source = Path.GetFullPath(path)
        }).ToList();
        if (targets.Count == 0) throw new InvalidDataException("文件中没有序列。");
        if (targets.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != targets.Count)
            throw new InvalidDataException("序列 ID 重复，请为每个靶标指定唯一名称。");
        foreach (var target in targets) target.Validate();
        return targets;
    }
    public static string Normalize(string sequence)
    {
        var symbols = Alphabets.AmbiguousDNA.GetValidSymbols();
        var line = 1; var column = 1;
        for (var offset = 0; offset < sequence.Length; offset++)
        {
            var character = sequence[offset]; var upper = char.ToUpperInvariant(character);
            if (!char.IsWhiteSpace(character) && (upper > byte.MaxValue || upper == '-' || !symbols.Contains((byte)upper)))
                throw new SequenceInputException(offset, line, column, character);
            if (character == '\r') { line++; column = 1; }
            else if (character == '\n') { if (offset == 0 || sequence[offset - 1] != '\r') line++; column = 1; }
            else column++;
        }
        var normalized = new string(sequence.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        _ = new Sequence(Alphabets.AmbiguousDNA, normalized);
        return normalized;
    }
    public static string NormalizePrimer(string sequence)
    {
        var normalized = Normalize(sequence);
        if (normalized.Length < 15 || normalized.Length > 36 || normalized.Any(c => !"ACGT".Contains(c)))
            throw new ArgumentException("引物须为 15–36 nt，且只包含 A/C/G/T。");
        return normalized;
    }
    public static string ReverseComplement(string sequence) => new string(new Sequence(Alphabets.AmbiguousDNA, sequence)
        .GetReverseComplementedSequence().Select(b => (char)b).ToArray());
    public static int CountFasta(string path)
    {
        using var stream = File.OpenRead(path);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sequence in new FastAParser().Parse(stream))
        {
            var id = sequence.ID.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).First();
            if (!ids.Add(id)) throw new InvalidDataException($"数据库序列 ID 重复：{id}");
        }
        if (ids.Count == 0) throw new InvalidDataException("数据库 FASTA 为空。");
        return ids.Count;
    }
}
