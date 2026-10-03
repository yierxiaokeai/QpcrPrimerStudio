using System.Windows;

namespace QpcrPrimerStudio.Desktop;

internal static class ThemeSupport
{
    // Fluent ThemeMode is opt-in experimental in the pinned .NET 10 WPF runtime.
    // Keep this API behind one adapter so SDK upgrades can replace it independently.
    public static void SetDark(bool dark)
    {
#pragma warning disable WPF0001
        Application.Current.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
    }
}
