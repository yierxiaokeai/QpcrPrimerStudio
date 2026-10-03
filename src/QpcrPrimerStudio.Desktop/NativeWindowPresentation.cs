using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace QpcrPrimerStudio.Desktop;

internal sealed class NativeWindowPresentation
{
    private readonly MainWindow window;
    private HwndSource? source;
    private bool darkTheme;
    internal bool AcrylicEnabled { get; private set; }
    internal bool NeutralFrameEnabled { get; private set; }
    internal NativeWindowPresentation(MainWindow window)
    {
        this.window = window;
        window.SourceInitialized += Initialize;
        window.Loaded += (_, _) => RefreshFrame();
        window.Activated += (_, _) => RefreshFrame();
        window.Deactivated += (_, _) => RefreshFrame();
        window.StateChanged += (_, _) => RefreshFrame();
        window.Closed += (_, _) => source?.RemoveHook(WindowProc);
    }
    private void Initialize(object? sender, EventArgs e)
    {
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source.AddHook(WindowProc);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            var acrylic = 3;
            AcrylicEnabled = DwmSetWindowAttribute(source.Handle, 38, ref acrylic, sizeof(int)) == 0;
        }
        if (AcrylicEnabled)
        {
            WindowChrome.GetWindowChrome(window).GlassFrameThickness = new Thickness(-1);
            window.Background = Brushes.Transparent;
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }
        else window.WindowSurface.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "CanvasBrush");
        UpdateTheme(false);
    }
    internal void UpdateTheme(bool dark)
    {
        darkTheme = dark;
        if (source is null) return;
        var value = dark ? 1 : 0;
        var result = DwmSetWindowAttribute(source.Handle, 20, ref value, sizeof(int));
        if (result != 0) System.Diagnostics.Trace.WriteLine($"DWM theme unavailable: {result:X8}");
        SetFrameColors();
        RefreshFrame();
    }
    private void RefreshFrame() => window.Dispatcher.InvokeAsync(SetFrameColors, System.Windows.Threading.DispatcherPriority.ContextIdle);
    private void SetFrameColors()
    {
        if (source is null || source.IsDisposed) return;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var border = unchecked((int)0xfffffffe);
            var caption = darkTheme ? 0x382819 : 0xffffff;
            NeutralFrameEnabled = DwmSetWindowAttribute(source.Handle, 34, ref border, sizeof(int)) == 0
                && DwmSetWindowAttribute(source.Handle, 35, ref caption, sizeof(int)) == 0;
        }
    }
    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0024) return 0;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MaxPosition = new NativePoint(monitor.Work.Left - monitor.Bounds.Left, monitor.Work.Top - monitor.Bounds.Top);
        info.MaxSize = new NativePoint(monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top);
        var dpi = VisualTreeHelper.GetDpi(window);
        info.MinTrackSize = new NativePoint((int)Math.Ceiling(window.MinWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.MinHeight * dpi.DpiScaleY));
        Marshal.StructureToPtr(info, lParam, false);
        handled = true;
        return 0;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct NativePoint(int x, int y) { public int X = x; public int Y = y; }
    [StructLayout(LayoutKind.Sequential)] internal struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public int Size; public NativeRect Bounds, Work; public int Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
