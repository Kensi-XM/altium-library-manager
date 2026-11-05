using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Altium_Library_Manager
{
    /// <summary>
    /// Horizontal scrolling with the mouse's horizontal wheel (tilt wheel or thumb wheel) and with Shift + wheel.
    /// WPF has no horizontal wheel event, so WM_MOUSEHWHEEL is taken from the window's message loop and applied to
    /// the nearest horizontally scrollable area under the mouse: a table, the tab strip, a list, …
    /// </summary>
    public static class HorizontalWheel
    {
        private const int WM_MOUSEHWHEEL = 0x020E;
        private const double WheelDelta = 120;          // one notch
        private const double PixelsPerNotch = 60;

        public static void Attach(Window window)
        {
            window.SourceInitialized += (_, _) =>
                HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(WndProc);
            window.PreviewMouseWheel += (_, e) =>
            {
                // Wheel down scrolls right, as in Explorer and browsers
                if (Keyboard.Modifiers == ModifierKeys.Shift && Scroll(e.OriginalSource as DependencyObject, -e.Delta))
                    e.Handled = true;
            };
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_MOUSEHWHEEL)
                return IntPtr.Zero;
            // The high word of wParam is the distance; positive is a tilt to the right
            var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            if (Scroll(Mouse.DirectlyOver as DependencyObject, delta))
                handled = true;
            return IntPtr.Zero;
        }

        private static bool Scroll(DependencyObject? element, int delta)
        {
            for (var current = element; current != null; current = Parent(current))
            {
                if (current is ScrollViewer { ScrollableWidth: > 0 } viewer &&
                    viewer.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled)
                {
                    viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset + delta / WheelDelta * PixelsPerNotch);
                    return true;
                }
            }
            return false;
        }

        // Text elements (Run, …) are not visuals; go up through their logical parent
        private static DependencyObject? Parent(DependencyObject element) =>
            element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
    }
}
