using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Altium_Library_Manager
{
    /// <summary>
    /// Lets a button close the menu or popup it opened. Pressing the mouse outside an open menu closes it, and the click
    /// then still reaches the button, which would open it again. So closing notes a press over its button
    /// (<see cref="NoteClosed"/>), and the button's click, seeing it, does nothing (<see cref="WasJustClosed"/>).
    /// </summary>
    internal static class DropDown
    {
        // A press that ended elsewhere leaves no click to use the note up: it doesn't count for long
        private const long ValidMs = 1500;

        private static UIElement? _button;
        private static long _closedAt;

        /// <summary>
        /// For a menu opened by a button. By IsOpen, not Closed: a menu's Closed comes after its fade out, later than the
        /// click (IsOpen changes on the press itself).
        /// </summary>
        public static void Track(ContextMenu menu, UIElement button)
        {
            var isOpen = DependencyPropertyDescriptor.FromProperty(ContextMenu.IsOpenProperty, typeof(ContextMenu));
            EventHandler? changed = null;
            changed = (_, _) =>
            {
                if (menu.IsOpen)
                    return;
                isOpen.RemoveValueChanged(menu, changed);
                NoteClosed(button);
            };
            isOpen.AddValueChanged(menu, changed);
        }

        public static void NoteClosed(UIElement button)
        {
            var position = Mouse.GetPosition(button);
            var over = new Rect(button.RenderSize).Contains(position);
            _button = Mouse.LeftButton == MouseButtonState.Pressed && over ? button : null;
            _closedAt = Environment.TickCount64;
        }

        /// <summary>This click on the button is the one that closed its menu: it should stay closed.</summary>
        public static bool WasJustClosed(UIElement button)
        {
            var closed = _button == button && Environment.TickCount64 - _closedAt < ValidMs;
            _button = null;
            return closed;
        }
    }
}
