using System.Windows;
using System.Windows.Input;

namespace ClaudeGui;

/// <summary>
/// 项目重命名对话框。
/// 从 MainWindow 提取的内联窗口 → 独立 WPF Window。
/// </summary>
public partial class RenameProjectDialog : Window
{
    private readonly string _oldName;
    private readonly Func<string, string?> _renameFunc;
    private readonly Action _onRenamed;

    /// <param name="oldName">当前项目名</param>
    /// <param name="renameFunc">重命名回调，返回新 ProjectEntry 的 Name（失败抛异常或返回 null）</param>
    /// <param name="onRenamed">重命名成功后的刷新回调</param>
    public RenameProjectDialog(string oldName, Func<string, string?> renameFunc, Action onRenamed)
    {
        InitializeComponent();
        _oldName = oldName;
        _renameFunc = renameFunc;
        _onRenamed = onRenamed;
        NameBox.Text = oldName;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DoRename();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void OkBtn_Click(object sender, RoutedEventArgs e) => DoRename();

    private void DoRename()
    {
        var newName = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName)) { NameBox.Focus(); return; }
        try
        {
            var updated = _renameFunc(newName);
            if (updated != null) _onRenamed();
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
