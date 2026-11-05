using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Altium_Library_Manager
{
    /// <summary>
    /// Keeps a window inside the work area (screen minus taskbar) of the monitor it is on. A size and position saved on
    /// a large 4K screen would otherwise open partly off a Full HD or QHD one. Works in physical pixels on the window's
    /// own monitor, so mixed resolutions and per-monitor scaling don't matter.
    /// </summary>
    internal static class ScreenFit
    {
        public static void FitToWorkArea(Window window)
        {
            if (window.WindowState != WindowState.Normal)
                return;

            var handle = new WindowInteropHelper(window).Handle;
            var monitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) || !GetWindowRect(handle, out var rect))
                return;

            var work = info.rcWork;
            int workWidth = work.Right - work.Left, workHeight = work.Bottom - work.Top;
            var width = Math.Min(rect.Right - rect.Left, workWidth);
            var height = Math.Min(rect.Bottom - rect.Top, workHeight);
            var x = Math.Clamp(rect.Left, work.Left, work.Right - width);
            var y = Math.Clamp(rect.Top, work.Top, work.Bottom - height);

            if (x != rect.Left || y != rect.Top || width != rect.Right - rect.Left || height != rect.Bottom - rect.Top)
                SetWindowPos(handle, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        /// <summary>
        /// How far a maximized window reaches past the work area on each side (in DIPs): Windows extends it by its
        /// resize frame plus a padded border, so content laid out to the window's edge would be cut off (the title bar
        /// lost a few pixels at the top). SystemParameters.WindowResizeBorderThickness leaves out the padded border,
        /// so it is measured instead; null if it can't be.
        /// </summary>
        public static Thickness? MaximizedOverhang(Window window)
        {
            var handle = new WindowInteropHelper(window).Handle;
            var monitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (handle == IntPtr.Zero || monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) || !GetWindowRect(handle, out var rect))
                return null;

            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
            var work = info.rcWork;
            static double Over(int pixels, double scale) => Math.Max(pixels, 0) / scale;
            var overhang = new Thickness(
                Over(work.Left - rect.Left, dpi.DpiScaleX), Over(work.Top - rect.Top, dpi.DpiScaleY),
                Over(rect.Right - work.Right, dpi.DpiScaleX), Over(rect.Bottom - work.Bottom, dpi.DpiScaleY));
            // Not (yet) laid out as maximized: nothing past the work area. More than a frame's width (measured while
            // moving between monitors): not believed, the caller falls back to the system value.
            const double MaxFrame = 32;
            var sides = new[] { overhang.Left, overhang.Top, overhang.Right, overhang.Bottom };
            return sides.Any(s => s > 0) && sides.All(s => s <= MaxFrame) ? overhang : null;
        }

        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    }
}
