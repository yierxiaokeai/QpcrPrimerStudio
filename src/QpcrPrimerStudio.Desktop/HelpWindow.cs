using System.Windows;
using System.Windows.Controls;

namespace QpcrPrimerStudio.Desktop;

public sealed class HelpWindow : Window
{
    public static readonly (string Id, string Title)[] Topics = [("quickstart", "快速开始"), ("parameters", "设计参数"), ("results", "结果与实验记录"), ("references", "参考与特异性")];
    public HelpWindow()
    {
        Title = "qPCR Primer Studio · 使用帮助"; Width = 1000; Height = 760; MinWidth = 720; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; SetResourceReference(BackgroundProperty, "CanvasBrush");
        var root = new Grid { Margin = new Thickness(16) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) }); root.ColumnDefinitions.Add(new ColumnDefinition());
        var navigation = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        navigation.Children.Add(new TextBlock { Text = "使用帮助", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        navigation.Children.Add(new TextBlock { Text = "搜索关键词", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        var search = new TextBox { ToolTip = "搜索帮助内容", Margin = new Thickness(0, 0, 0, 12) }; navigation.Children.Add(search);
        var chapters = new ListBox { DisplayMemberPath = "Title", ItemContainerStyle = (Style)FindResource("HelpNavigationItem"), BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent }; navigation.Children.Add(chapters);
        var reader = new FlowDocumentScrollViewer { IsToolBarVisible = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        reader.SetResourceReference(BackgroundProperty, "CardBrush"); Grid.SetColumn(reader, 1); root.Children.Add(navigation); root.Children.Add(reader); Content = root;
        var entries = Topics.Select(t => new HelpTopic(t.Id, t.Title, MarkdownHelp.Load(t.Id))).ToArray();
        chapters.SelectionChanged += (_, _) => { if (chapters.SelectedItem is HelpTopic topic) reader.Document = MarkdownHelp.Render(topic.Markdown); };
        search.TextChanged += (_, _) =>
        {
            chapters.ItemsSource = entries.Where(t => t.Markdown.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray(); chapters.SelectedIndex = 0;
            if (chapters.Items.Count == 0) reader.Document = MarkdownHelp.Render("# 没有匹配内容\n请缩短关键词，或搜索参数名称。");
        };
        chapters.ItemsSource = entries; chapters.SelectedIndex = 0;
    }
    private sealed record HelpTopic(string Id, string Title, string Markdown);
}
