using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Shell;

namespace QpcrPrimerStudio.Desktop;

public static class WindowFrameVerification
{
    public sealed record DialogCloseReport(string Title, long CloseHitTest, bool CloseAction);

    public static DialogCloseReport CheckDialogClose(Window window)
    {
        Exception? failure = null;
        long hit = 0;
        window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            try
            {
                window.UpdateLayout();
                var button = (Button)window.FindName("CloseDialogButton");
                hit = HitTest(window, button, new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                if (hit != 1) throw new InvalidOperationException($"Dialog close button is intercepted by the title bar: {window.Title}, hit={hit}.");
                Invoke(button);
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (window.IsVisible) throw new InvalidOperationException($"Dialog close button did not close: {window.Title}.");
            }
            catch (Exception ex) { failure = ex; if (window.IsVisible) window.Close(); }
        });
        var result = window.ShowDialog();
        if (failure is not null) throw new InvalidOperationException("Dialog close verification failed.", failure);
        if (result != false) throw new InvalidOperationException("Closing the dialog must cancel its draft.");
        return new(window.Title, hit, true);
    }

    public static object Check(Window window)
    {
        if (window.WindowStyle != WindowStyle.None || WindowChrome.GetWindowChrome(window) is null)
            throw new InvalidOperationException("The integrated title bar is missing.");
        var minimize = (Button)window.FindName("MinimizeButton");
        var maximize = (Button)window.FindName("MaximizeButton");
        var close = (Button)window.FindName("CloseButton");
        if (!minimize.IsVisible || !maximize.IsVisible || !close.IsVisible || minimize.ActualWidth <= 0 || maximize.ActualWidth <= 0 || close.ActualWidth <= 0)
            throw new InvalidOperationException("Caption buttons are invisible.");
        var drag = (FrameworkElement)window.FindName("HeaderDragArea");
        var dragHit = HitTest(window, drag, new Point(drag.ActualWidth * 0.75, drag.ActualHeight / 2));
        var closeHit = HitTest(window, close, new Point(close.ActualWidth / 2, close.ActualHeight / 2));
        var minimizeHit = HitTest(window, minimize, new Point(minimize.ActualWidth / 2, minimize.ActualHeight / 2));
        if (dragHit != 2 || closeHit != 1 || minimizeHit != 1)
            throw new InvalidOperationException($"Caption hit tests failed: drag={dragHit}, close={closeHit}, minimize={minimizeHit}.");
        Invoke(minimize);
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (window.WindowState != WindowState.Minimized) throw new InvalidOperationException("Minimize button did not minimize the window.");
        SystemCommands.RestoreWindow(window);
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (window.WindowState != WindowState.Normal) throw new InvalidOperationException("Window restoration failed.");
        var normalSize = new Size(window.ActualWidth, window.ActualHeight);
        Invoke(maximize);
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (window.WindowState != WindowState.Maximized || !Equals(maximize.ToolTip, "还原"))
            throw new InvalidOperationException("Maximize button or restore label failed.");
        var monitor = new NativeWindowPresentation.MonitorInfo { Size = Marshal.SizeOf<NativeWindowPresentation.MonitorInfo>() };
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!NativeWindowPresentation.GetMonitorInfo(NativeWindowPresentation.MonitorFromWindow(hwnd, 2), ref monitor) || !GetWindowRect(hwnd, out var bounds))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (bounds.Left < monitor.Work.Left || bounds.Top < monitor.Work.Top || bounds.Right > monitor.Work.Right || bounds.Bottom > monitor.Work.Bottom)
            throw new InvalidOperationException($"Maximized window exceeds work area: {bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}.");
        var acrylic = ((MainWindow)window).Presentation.AcrylicEnabled;
        var neutral = ((MainWindow)window).Presentation.NeutralFrameEnabled;
        // CAPTION_COLOR is a setter-only attribute on this host; DwmGetWindowAttribute returns E_INVALIDARG.
        var header = (System.Windows.Controls.Border)window.FindName("HeaderBar");
        var opaqueHeader = header.Background is System.Windows.Media.SolidColorBrush brush && brush.Color.A == 255;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && (!neutral || !opaqueHeader))
            throw new InvalidOperationException("Neutral frame setting or opaque custom header is missing.");
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && !acrylic)
            throw new InvalidOperationException("Desktop Acrylic was not accepted by DWM.");
        if (HitTest(window, close, new Point(close.ActualWidth / 2, close.ActualHeight / 2)) != 1)
            throw new InvalidOperationException("Close button is inaccessible when maximized.");
        Invoke(maximize);
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (window.WindowState != WindowState.Normal || !Equals(maximize.ToolTip, "最大化"))
            throw new InvalidOperationException("Maximize button did not restore the window.");
        window.UpdateLayout();
        if (Math.Abs(window.ActualWidth - normalSize.Width) > 1 || Math.Abs(window.ActualHeight - normalSize.Height) > 1)
            throw new InvalidOperationException("Restore did not recover the original window size.");
        if (!GetWindowRect(hwnd, out var restoredBounds))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if ((bounds.Right - bounds.Left) <= (restoredBounds.Right - restoredBounds.Left) &&
            (bounds.Bottom - bounds.Top) <= (restoredBounds.Bottom - restoredBounds.Top))
            throw new InvalidOperationException("Restore did not reduce the maximized window size.");
        Invoke(close);
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (window.IsVisible) throw new InvalidOperationException("Close button did not close the verification window.");
        return new { CustomTitleBar = true, DragHitTest = dragHit, CloseHitTest = closeHit,
            MinimizeHitTest = minimizeHit, MinimizeAction = true, MaximizeAction = true, MaximizeRestoreAction = true, RestoreAction = true, CloseAction = true,
            MaximizedWithinWorkArea = true, RestoreRecoversSize = true, AcrylicEnabled = acrylic, NeutralFrameEnabled = neutral, OpaqueHeader = opaqueHeader };
    }

    private static void Invoke(Button button)
    {
        var provider = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke);
        provider.Invoke();
    }
    private static long HitTest(Window window, FrameworkElement element, Point point)
    {
        var screen = element.PointToScreen(point);
        var packed = (nint)(((int)screen.Y << 16) | ((int)screen.X & 0xffff));
        return SendMessage(new WindowInteropHelper(window).Handle, 0x0084, 0, packed).ToInt64();
    }
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeWindowPresentation.NativeRect rect);
}
