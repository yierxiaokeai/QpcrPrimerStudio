using System.Globalization;
using System.Windows;
using System.Windows.Media;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;
public sealed class SequenceTrack : FrameworkElement
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(nameof(Target), typeof(SequenceTarget), typeof(SequenceTrack), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CandidateProperty = DependencyProperty.Register(nameof(Candidate), typeof(PrimerCandidate), typeof(SequenceTrack), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AnnotationProperty = DependencyProperty.Register(nameof(Annotation), typeof(TranscriptAnnotation), typeof(SequenceTrack), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty VariantsProperty = DependencyProperty.Register(nameof(Variants), typeof(List<TranscriptVariant>), typeof(SequenceTrack), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(SequenceTrack), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(SequenceTrack), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public SequenceTarget? Target { get => (SequenceTarget?)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }
    public PrimerCandidate? Candidate { get => (PrimerCandidate?)GetValue(CandidateProperty); set => SetValue(CandidateProperty, value); }
    public TranscriptAnnotation? Annotation { get => (TranscriptAnnotation?)GetValue(AnnotationProperty); set => SetValue(AnnotationProperty, value); }
    public List<TranscriptVariant>? Variants { get => (List<TranscriptVariant>?)GetValue(VariantsProperty); set => SetValue(VariantsProperty, value); }
    public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }
    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsPositiveInfinity(availableSize.Width) ? 650 * Zoom : Math.Min(650 * Zoom, availableSize.Width), Compact ? 80 : 120);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (Target is null || Target.Sequence.Length == 0 || ActualWidth < 80) return;
        var width = ActualWidth - 40;
        double X(double position) => 20 + width * position / Target.Sequence.Length;
        dc.DrawLine(new Pen(Brushes.SlateGray, 2), new Point(20, 44), new Point(ActualWidth - 20, 44));
        for (var i = 0; i <= 4; i++) Text(dc, Math.Max(1, Target.Sequence.Length * i / 4).ToString(), X(Target.Sequence.Length * i / 4d) - 8, 60);
        foreach (var junction in Target.Junctions) dc.DrawLine(new Pen(Brushes.Orange, 2), new Point(X(junction), 24), new Point(X(junction), 52));
        if (Annotation is not null && !Compact)
        {
            foreach (var exon in Annotation.Exons)
                dc.DrawRectangle(Brushes.SlateGray, null, new Rect(X(exon.Start - 1), 82, Math.Max(2, X(exon.End) - X(exon.Start - 1)), 8));
            foreach (var cds in Annotation.Cds)
                dc.DrawRectangle(Brushes.SteelBlue, null, new Rect(X(cds.Start - 1), 80, Math.Max(2, X(cds.Start + cds.Length - 1) - X(cds.Start - 1)), 12));
            Text(dc, "灰：exon / UTR   蓝：CDS   红：已知变异   橙：junction", 20, 99);
        }
        foreach (var variant in Variants ?? []) dc.DrawLine(new Pen(Brushes.Crimson, 2), new Point(X(variant.Start), 25), new Point(X(variant.End), Compact ? 52 : 95));
        if (Candidate is null) return;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(40, 0, 125, 134)), null,
            new Rect(X(Candidate.Forward.Start - 1), 34, Math.Max(2, X(Candidate.Reverse.End) - X(Candidate.Forward.Start - 1)), 20), 3, 3);
        Arrow(dc, X(Candidate.Forward.Start - 1), X(Candidate.Forward.End), 20, Brushes.Teal, true);
        Arrow(dc, X(Candidate.Reverse.Start - 1), X(Candidate.Reverse.End), 44, Brushes.MediumPurple, false);
        Text(dc, $"{Candidate.ProductLength} bp", (X(Candidate.Forward.Start - 1) + X(Candidate.Reverse.End)) / 2 - 20, 2);
    }
    private void Text(DrawingContext dc, string text, double x, double y) => dc.DrawText(new FormattedText(text,
        CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11,
        (Brush)(TryFindResource("InkBrush") ?? Brushes.Black), VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
    private static void Arrow(DrawingContext dc, double start, double end, double y, Brush brush, bool plus)
    {
        dc.DrawLine(new Pen(brush, 5), new Point(start, y), new Point(end, y));
        var tip = plus ? end : start; var tail = tip + (plus ? -7 : 7);
        var geometry = new StreamGeometry(); using (var g = geometry.Open()) { g.BeginFigure(new Point(tip, y), true, true); g.LineTo(new Point(tail, y - 5), true, false); g.LineTo(new Point(tail, y + 5), true, false); }
        dc.DrawGeometry(brush, null, geometry);
    }
}
