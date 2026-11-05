using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Altium_Library_Manager
{
    /// <summary>Where a panel sits in the work area (see the layout presets in MainWindow).</summary>
    public enum LayoutSlot
    {
        /// <summary>The large area (2); can't be collapsed.</summary>
        Center,
        /// <summary>The area on the right (4); collapses into a vertical strip.</summary>
        Right,
        /// <summary>The area at the bottom (3); collapses down to its header.</summary>
        Bottom
    }

    /// <summary>
    /// Frame of a work area panel: a title bar with a collapse button around its content.
    /// How it collapses depends on the <see cref="Slot"/> it currently occupies; the window owns the slot sizes and
    /// reacts to <see cref="CollapseToggled"/>.
    /// A lookless ContentControl (template in App.xaml) rather than a UserControl, so the panels' content can keep
    /// x:Name'd elements of the window.
    /// </summary>
    [TemplatePart(Name = "PART_Expanded", Type = typeof(FrameworkElement))]
    [TemplatePart(Name = "PART_Header", Type = typeof(FrameworkElement))]
    [TemplatePart(Name = "PART_Title", Type = typeof(TextBlock))]
    [TemplatePart(Name = "PART_Collapse", Type = typeof(Button))]
    [TemplatePart(Name = "PART_Body", Type = typeof(FrameworkElement))]
    [TemplatePart(Name = "PART_Strip", Type = typeof(Button))]
    [TemplatePart(Name = "PART_StripText", Type = typeof(TextBlock))]
    public class PanelFrame : ContentControl
    {
        private string _title = "";
        private LayoutSlot _slot;
        private bool _isCollapsed;

        private FrameworkElement? _expanded, _header, _body;
        private TextBlock? _titleText, _stripText;
        private Button? _collapseButton, _strip;

        static PanelFrame()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(PanelFrame), new FrameworkPropertyMetadata(typeof(PanelFrame)));
        }

        public string Title
        {
            get => _title;
            set
            {
                _title = value;
                UpdateVisuals();
            }
        }

        /// <summary>The tables panel has its own tab strip, so it needs no title bar in the center.</summary>
        public bool ShowHeaderInCenter { get; set; } = true;

        public LayoutSlot Slot
        {
            get => _slot;
            set
            {
                _slot = value;
                UpdateVisuals();
            }
        }

        public bool IsCollapsed
        {
            get => _isCollapsed;
            set
            {
                _isCollapsed = value;
                UpdateVisuals();
            }
        }

        public event EventHandler? CollapseToggled;

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            if (_collapseButton != null)
                _collapseButton.Click -= Toggle_Click;
            if (_strip != null)
                _strip.Click -= Toggle_Click;
            if (_header != null)
                _header.MouseLeftButtonUp -= Header_MouseLeftButtonUp;

            _expanded = GetTemplateChild("PART_Expanded") as FrameworkElement;
            _header = GetTemplateChild("PART_Header") as FrameworkElement;
            _titleText = GetTemplateChild("PART_Title") as TextBlock;
            _collapseButton = GetTemplateChild("PART_Collapse") as Button;
            _body = GetTemplateChild("PART_Body") as FrameworkElement;
            _strip = GetTemplateChild("PART_Strip") as Button;
            _stripText = GetTemplateChild("PART_StripText") as TextBlock;

            if (_collapseButton != null)
                _collapseButton.Click += Toggle_Click;
            if (_strip != null)
                _strip.Click += Toggle_Click;
            if (_header != null)
                _header.MouseLeftButtonUp += Header_MouseLeftButtonUp;

            UpdateVisuals();
        }

        private void Toggle_Click(object sender, RoutedEventArgs e) => CollapseToggled?.Invoke(this, EventArgs.Empty);

        private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Clicking the title bar of the bottom panel toggles it too (a large, easy target)
            if (_slot == LayoutSlot.Bottom && e.OriginalSource is not Button)
                CollapseToggled?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateVisuals()
        {
            if (_expanded == null || _header == null || _body == null || _titleText == null ||
                _collapseButton == null || _strip == null || _stripText == null)
                return;

            var collapsed = _isCollapsed && _slot != LayoutSlot.Center;
            var strip = collapsed && _slot == LayoutSlot.Right;

            _titleText.Text = _title;
            _titleText.ToolTip = _title;
            _stripText.Text = _title;
            _strip.ToolTip = $"Expand {_title}";

            _expanded.Visibility = strip ? Visibility.Collapsed : Visibility.Visible;
            _strip.Visibility = strip ? Visibility.Visible : Visibility.Collapsed;
            _body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            _header.Visibility = _slot == LayoutSlot.Center && !ShowHeaderInCenter ? Visibility.Collapsed : Visibility.Visible;
            _header.Cursor = _slot == LayoutSlot.Bottom ? Cursors.Hand : null;
            // The line under the header separates it from the content; a collapsed card is only its header
            if (_header is Border headerBorder)
                headerBorder.BorderThickness = collapsed ? new Thickness(0) : new Thickness(0, 0, 0, 1);

            _collapseButton.Visibility = _slot == LayoutSlot.Center ? Visibility.Collapsed : Visibility.Visible;
            (_collapseButton.Content, _collapseButton.ToolTip) = _slot switch
            {
                LayoutSlot.Right => ("", "Collapse"),               // chevron right
                LayoutSlot.Bottom when collapsed => ("", "Expand"), // chevron up
                LayoutSlot.Bottom => ("", "Collapse"),              // chevron down
                _ => ("", "")
            };
        }
    }
}
