using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public static class AdaptiveCandidateColumns
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(AdaptiveCandidateColumns), new PropertyMetadata(false, OnEnabledChanged));
    public static readonly DependencyProperty ProfessionalModeProperty = DependencyProperty.RegisterAttached(
        "ProfessionalMode", typeof(bool), typeof(AdaptiveCandidateColumns), new PropertyMetadata(false, OnModeChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(AdaptiveCandidateColumns));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetProfessionalMode(DependencyObject target) => (bool)target.GetValue(ProfessionalModeProperty);
    public static void SetProfessionalMode(DependencyObject target, bool value) => target.SetValue(ProfessionalModeProperty, value);
    private static void OnModeChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target.GetValue(StateProperty) is State state) state.UpdateMode();
    }
    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not DataGrid grid) return;
        (grid.GetValue(StateProperty) as State)?.Dispose();
        grid.SetValue(StateProperty, (bool)e.NewValue ? new State(grid) : null);
    }
    private sealed class State : IDisposable
    {
        private readonly DataGrid grid;
        private readonly INotifyCollectionChanged items;
        private bool subscribed;
        private bool pending;
        private bool contentDirty = true;
        private double previousBudget = double.NaN;
        public State(DataGrid grid)
        {
            this.grid = grid; items = grid.Items;
            grid.Loaded += Loaded; grid.Unloaded += Unloaded;
            grid.SizeChanged += Resized; grid.LayoutUpdated += LayoutUpdated;
            if (grid.IsLoaded) Subscribe();
        }
        private void Loaded(object sender, RoutedEventArgs e) { UpdateMode(); Subscribe(); }
        public void UpdateMode()
        {
            foreach (var column in grid.Columns.Where(c => Path(c) is "Assessment.EvidenceCoverage" or "Assessment.Accepted" or "Assessment.Gdna.Level" or "TargetId" or "Penalty"))
                column.Visibility = GetProfessionalMode(grid) ? Visibility.Visible : Visibility.Collapsed;
            Schedule();
        }
        private void Unloaded(object sender, RoutedEventArgs e)
        {
            if (subscribed) items.CollectionChanged -= Changed;
            subscribed = false;
        }
        private void Subscribe()
        {
            if (!subscribed) items.CollectionChanged += Changed;
            subscribed = true; contentDirty = true; Schedule();
        }
        private void Changed(object? sender, NotifyCollectionChangedEventArgs e) { contentDirty = true; Schedule(); }
        private void Resized(object sender, SizeChangedEventArgs e) => Schedule();
        private static string? Path(DataGridColumn column) => (column as DataGridTextColumn)?.Binding is Binding binding ? binding.Path?.Path : column.SortMemberPath;
        private static double Weight(DataGridColumn column) => Path(column) switch { "Forward.Sequence" or "Reverse.Sequence" => 2, "SpecificitySummary" or "TargetId" => 1, _ => 0 };
        private double Budget() => grid.ActualWidth - 16 - grid.Columns.Where(c => c.Visibility == Visibility.Visible && Weight(c) == 0).Sum(c => c.ActualWidth);
        private void LayoutUpdated(object? sender, EventArgs e)
        {
            if (grid.IsLoaded && (double.IsNaN(previousBudget) || Math.Abs(Budget() - previousBudget) > 0.5)) Schedule();
        }
        private void Schedule()
        {
            if (pending) return;
            pending = true;
            grid.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                pending = false;
                if (!grid.IsLoaded) return;
                var typeface = new Typeface(grid.FontFamily, grid.FontStyle, FontWeights.Normal, grid.FontStretch);
                foreach (var column in grid.Columns.Where(_ => contentDirty))
                {
                    var path = Path(column);
                    if (path is not ("Forward.Sequence" or "Reverse.Sequence")) continue;
                    var sequences = grid.Items.OfType<PrimerCandidate>().Select(c => path == "Forward.Sequence" ? c.Forward.Sequence : c.Reverse.Sequence);
                    column.MinWidth = Math.Max(210, sequences.Select(s => new FormattedText(s, CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight, typeface, grid.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(grid).PixelsPerDip)
                        .WidthIncludingTrailingWhitespace + 22).DefaultIfEmpty(170).Max());
                }
                contentDirty = false;
                previousBudget = Budget();
                var flexible = grid.Columns.Where(c => c.Visibility == Visibility.Visible && Weight(c) > 0).ToList();
                var extra = Math.Max(0, previousBudget - flexible.Sum(c => c.MinWidth));
                var weights = flexible.Sum(Weight);
                foreach (var column in flexible)
                {
                    var width = column.MinWidth + extra * Weight(column) / weights;
                    if (!column.Width.IsAbsolute || Math.Abs(column.Width.Value - width) > 0.5)
                        column.Width = new DataGridLength(width);
                }
            });
        }
        public void Dispose()
        {
            grid.Loaded -= Loaded; grid.Unloaded -= Unloaded;
            grid.SizeChanged -= Resized; grid.LayoutUpdated -= LayoutUpdated;
            if (subscribed) items.CollectionChanged -= Changed;
            subscribed = false;
        }
    }
}
