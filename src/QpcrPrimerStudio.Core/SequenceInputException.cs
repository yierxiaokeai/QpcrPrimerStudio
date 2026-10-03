namespace QpcrPrimerStudio.Core;

public sealed class SequenceInputException(int offset, int line, int column, char character)
    : ArgumentException($"序列第 {line} 行、第 {column} 列含无效字符“{character}”（U+{(int)character:X4}）。请删除误粘贴的文字或符号，只保留 DNA IUPAC 字母。")
{
    public int Offset { get; } = offset;
    public int Line { get; } = line;
    public int Column { get; } = column;
}
