using System.Globalization;
using System.Windows;
using System.Windows.Shell;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public partial class HairpinSettingsDialog : Window
{
    public HairpinScreenSettings? Settings { get; private set; }
    public HairpinSettingsDialog(HairpinScreenSettings settings)
    {
        InitializeComponent();
        static string N(double value) => value.ToString(CultureInfo.InvariantCulture);
        AnnealingInput.Text = N(settings.AnnealingC); TerminalMarginInput.Text = N(settings.TerminalMarginC);
        DeltaGInput.Text = N(settings.TerminalDeltaGKcal); CheckTerminalInput.IsChecked = settings.CheckTerminalHairpins;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 42, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
    }
    private void ApplySettings(object sender, RoutedEventArgs e)
    {
        try
        {
            static double D(string text) => double.Parse(text, CultureInfo.InvariantCulture);
            var settings = new HairpinScreenSettings(D(AnnealingInput.Text), D(TerminalMarginInput.Text), D(DeltaGInput.Text), CheckTerminalInput.IsChecked == true);
            settings.Validate(); Settings = settings; DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        { Feedback.Text = "请检查筛选条件：" + ex.Message; }
    }
}
