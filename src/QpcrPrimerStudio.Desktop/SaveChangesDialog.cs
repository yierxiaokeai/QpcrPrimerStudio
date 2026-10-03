using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace QpcrPrimerStudio.Desktop;

public sealed class SaveChangesDialog : Window
{
    private bool saving;
    public SaveChangesDialog(Func<Task<bool>> save, Func<string> status)
    {
        Title = "保存项目"; Width = 500; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "CardBrush");
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 42, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "保存对项目的修改？", FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "当前项目有未保存修改。保存后可继续当前操作，放弃修改将离开当前项目。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 12) };
        feedback.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); panel.Children.Add(feedback);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var discard = new Button { Content = "放弃修改", Name = "DiscardButton" };
        var cancel = new Button { Content = "取消", IsCancel = true, Name = "CancelButton" };
        var accept = new Button { Content = "保存并继续", IsDefault = true, Name = "SaveButton", Style = (Style)FindResource("Primary") };
        discard.Click += (_, _) => DialogResult = true;
        accept.Click += async (_, _) =>
        {
            saving = true; accept.IsEnabled = discard.IsEnabled = cancel.IsEnabled = false;
            try
            {
                if (await save()) { saving = false; DialogResult = true; return; }
                feedback.Text = "保存未完成，请选择文件位置或检查项目内容。\n" + status(); feedback.Visibility = Visibility.Visible;
            }
            finally { saving = false; accept.IsEnabled = discard.IsEnabled = cancel.IsEnabled = true; }
        };
        buttons.Children.Add(discard); buttons.Children.Add(cancel); buttons.Children.Add(accept); panel.Children.Add(buttons);
        Content = new Border { Child = panel, BorderThickness = new Thickness(1), BorderBrush = (Brush)FindResource("LineBrush"), CornerRadius = new CornerRadius(10) };
        Closing += (_, e) => { if (saving) e.Cancel = true; };
    }
}
