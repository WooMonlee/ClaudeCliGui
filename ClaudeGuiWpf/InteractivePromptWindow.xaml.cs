using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClaudeGui;

/// <summary>
/// AI 交互提问弹窗（AskUserQuestion / question / confirm 等）。
/// 非模态 Topmost，用户选择后通过回调返回结果。
/// </summary>
public partial class InteractivePromptWindow : Window
{
    private readonly string _toolName;
    private readonly bool _multiSelect;
    private readonly List<OptionItem> _options = new();
    private readonly Action<List<string>> _onConfirm;

    public InteractivePromptWindow(string projectName, string toolName, string question,
        List<(string label, string? desc)> options, bool multiSelect,
        Action<List<string>> onConfirm)
    {
        InitializeComponent();
        _toolName = toolName;
        _multiSelect = multiSelect;
        _onConfirm = onConfirm;

        Title = $"AI 提问 — {projectName}";
        TxtProject.Text = $"📁 {projectName}";
        TxtQuestion.Text = question;
        TxtMultiHint.Visibility = multiSelect ? Visibility.Visible : Visibility.Collapsed;

        foreach (var (label, desc) in options)
            _options.Add(new OptionItem { Label = label, Description = desc ?? "" });

        BuildOptions();
    }

    private void BuildOptions()
    {
        OptionsPanel.Children.Clear();
        foreach (var opt in _options)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x2e)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x35, 0x54)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = Cursors.Hand,
                Tag = opt,
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 选中圆点
            var dot = new Ellipse
            {
                Width = 10, Height = 10,
                Margin = new Thickness(0, 0, 10, 0),
                Fill = new SolidColorBrush(Color.FromRgb(0x23, 0x35, 0x54)),
                Stroke = new SolidColorBrush(Color.FromRgb(0x64, 0xff, 0xda)),
                StrokeThickness = 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(dot, 0);
            grid.Children.Add(dot);
            border.Tag = dot; // 临时存 dot 引用以便 UpdateDot

            var textPanel = new StackPanel();
            Grid.SetColumn(textPanel, 1);
            textPanel.Children.Add(new TextBlock
            {
                Text = opt.Label,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0)),
                FontWeight = FontWeights.SemiBold,
            });
            if (!string.IsNullOrWhiteSpace(opt.Description))
            {
                textPanel.Children.Add(new TextBlock
                {
                    Text = opt.Description,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x92, 0xb0)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                });
            }
            grid.Children.Add(textPanel);

            border.MouseEnter += (_, _) =>
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x64, 0xff, 0xda));
            border.MouseLeave += (_, _) =>
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x35, 0x54));

            border.MouseLeftButtonDown += (_, _) =>
            {
                if (_multiSelect)
                {
                    opt.IsSelected = !opt.IsSelected;
                }
                else
                {
                    foreach (var o in _options) o.IsSelected = false;
                    opt.IsSelected = true;
                }
                UpdateAllDots();
            };

            border.Child = grid;
            OptionsPanel.Children.Add(border);
        }

        UpdateAllDots();
    }

    private void UpdateAllDots()
    {
        foreach (Border border in OptionsPanel.Children)
        {
            if (border.Tag is Ellipse dot && border.Tag is OptionItem)
            {
                // Actually border.Tag is the option, and dot is stored separately...
            }
        }
        // 简化：重新遍历，通过 border.Child 找 dot
        foreach (Border border in OptionsPanel.Children)
        {
            var opt = border.Tag as OptionItem;
            if (opt == null) continue;
            if (border.Child is Grid grid && grid.Children.Count > 0 && grid.Children[0] is Ellipse dot)
            {
                dot.Fill = opt.IsSelected
                    ? new SolidColorBrush(Color.FromRgb(0x64, 0xff, 0xda))
                    : new SolidColorBrush(Color.FromRgb(0x23, 0x35, 0x54));
            }
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Activate();
        // 任务栏闪烁
        if (new WindowInteropHelper(this).Handle != IntPtr.Zero)
            FlashWindowEx();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var selected = _options.Where(o => o.IsSelected).Select(o => o.Label).ToList();
        _onConfirm(selected);
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _onConfirm(new List<string>());
        Close();
    }

    // P/Invoke 任务栏闪烁
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private void FlashWindowEx()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var fi = new FLASHWINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = 0x00000003, // FLASHW_TRAY | FLASHW_CAPTION
                uCount = 4,
                dwTimeout = 0,
            };
            FlashWindowEx(ref fi);
        }
        catch { }
    }

    private class OptionItem
    {
        public string Label { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsSelected { get; set; }
    }
}
