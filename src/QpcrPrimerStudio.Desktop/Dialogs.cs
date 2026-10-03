using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;
public sealed class FieldDialog : Window
{
    private readonly List<TextBox> fields = [];
    public string[] Values => fields.Select(t => t.Text).ToArray();
    public FieldDialog(string title, string[] labels)
    {
        Title = title; Width = 560; Height = 690; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        foreach (var label in labels) { panel.Children.Add(new TextBlock { Text = label }); var field = new TextBox(); fields.Add(field); panel.Children.Add(field); }
        var save = new Button { Content = "保存记录", IsDefault = true }; save.Click += (_, _) => DialogResult = true; panel.Children.Add(save);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
public sealed class LibraryWindow : Window
{
    public LibraryWindow(List<PrimerCandidate> candidates)
    {
        Title = "本地引物库"; Width = 1000; Height = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(24) };
        var search = new TextBox { ToolTip = "按靶标名称搜索" }; DockPanel.SetDock(search, Dock.Top); panel.Children.Add(search);
        var copy = new Button { Content = "复制选中引物（5′→3′）" }; DockPanel.SetDock(copy, Dock.Bottom); panel.Children.Add(copy);
        var grid = new DataGrid { ItemsSource = candidates, IsReadOnly = true };
        grid.Columns.Add(new DataGridTextColumn { Header = "靶标", Binding = new Binding("TargetId"), Width = 150 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Forward 5′→3′", Binding = new Binding("Forward.Sequence"), Width = 240 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Reverse 5′→3′", Binding = new Binding("Reverse.Sequence"), Width = 240 });
        grid.Columns.Add(new DataGridTextColumn { Header = "bp", Binding = new Binding("ProductLength"), Width = 60 });
        grid.Columns.Add(new DataGridTextColumn { Header = "特异性", Binding = new Binding("SpecificitySummary"), Width = 220 });
        search.TextChanged += (_, _) => grid.ItemsSource = candidates.Where(c => c.TargetId.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        copy.Click += (_, _) => { if (grid.SelectedItem is PrimerCandidate c) Clipboard.SetText($"{c.TargetId}\t{c.Forward.Sequence}\t{c.Reverse.Sequence}\t{c.ProductLength}"); };
        panel.Children.Add(grid); Content = panel;
    }
}
