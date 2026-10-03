using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace QpcrPrimerStudio.Desktop;

public static class SequenceInputFocus
{
    public static readonly DependencyProperty ErrorOffsetProperty = DependencyProperty.RegisterAttached(
        "ErrorOffset", typeof(int), typeof(SequenceInputFocus), new PropertyMetadata(-1, Changed));
    public static int GetErrorOffset(DependencyObject target) => (int)target.GetValue(ErrorOffsetProperty);
    public static void SetErrorOffset(DependencyObject target, int value) => target.SetValue(ErrorOffsetProperty, value);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not TextBox input || (int)e.NewValue < 0) return;
        input.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            var offset = GetErrorOffset(input);
            if (!input.IsVisible || offset < 0 || offset >= input.Text.Length) return;
            input.Focus(); input.Select(offset, char.IsSurrogatePair(input.Text, offset) ? 2 : 1);
            var line = input.GetLineIndexFromCharacterIndex(offset);
            if (line >= 0) input.ScrollToLine(line);
        });
    }
}
