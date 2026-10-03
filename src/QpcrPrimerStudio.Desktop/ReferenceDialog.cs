using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public sealed class ReferenceDialog : Window
{
    private readonly Dictionary<string, TextBox> fields = [];
    public ReferenceImportRequest Request => new(fields["物种 / 项目名称"].Text.Trim(), fields["参考版本"].Text.Trim(),
        fields["Transcriptome FASTA（必填）"].Text.Trim(), Value("Genome FASTA"), Value("Annotation GFF3"), Value("Variants VCF"),
        Value("Transcript–gene TSV"), Value("CDS FASTA"), Value("Protein FASTA"));
    private string? Value(string key) => string.IsNullOrWhiteSpace(fields[key].Text) ? null : fields[key].Text.Trim();
    public ReferenceDialog()
    {
        Title = "建立本地参考项目"; Width = 760; Height = 770; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "核对序列、exon/CDS、链方向与 VCF REF 后建立索引和特异性数据库。", TextWrapping = TextWrapping.Wrap });
        foreach (var key in new[] { "物种 / 项目名称", "参考版本", "Transcriptome FASTA（必填）", "Genome FASTA", "Annotation GFF3", "Variants VCF", "Transcript–gene TSV", "CDS FASTA", "Protein FASTA" })
        {
            panel.Children.Add(new TextBlock { Text = key, Margin = new Thickness(0, 10, 0, 0) });
            var row = new DockPanel(); var text = new TextBox(); fields[key] = text;
            if (key.Contains("FASTA") || key.Contains("GFF3") || key.Contains("VCF") || key.Contains("TSV"))
            {
                var button = new Button { Content = "选择文件", Margin = new Thickness(8, 0, 0, 0) }; DockPanel.SetDock(button, Dock.Right);
                button.Click += (_, _) => { var dialog = new OpenFileDialog { Filter = "参考文件|*.fa;*.fasta;*.fna;*.fas;*.gff;*.gff3;*.vcf;*.gz;*.tsv|所有文件|*.*" }; if (dialog.ShowDialog() == true) text.Text = Path.GetFullPath(dialog.FileName); };
                row.Children.Add(button);
            }
            row.Children.Add(text); panel.Children.Add(row);
        }
        panel.Children.Add(new TextBlock { Text = "无基因组时可只提供转录组。TSV 表头：Transcript、Gene。ID 需与 FASTA/GFF3 完全一致。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        var save = new Button { Content = "校验并建立参考项目", IsDefault = true }; save.Click += (_, _) => DialogResult = true; panel.Children.Add(save);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
