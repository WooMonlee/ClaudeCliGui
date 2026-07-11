using System.Windows;
using System.Windows.Controls;

namespace ClaudeGui;

public partial class NewProjectDialog : Window
{
    private readonly ConfigService _config;
    public ProjectEntry? Result { get; private set; }
    private static string? _lastParentDir; // 记忆上次使用的父目录

    public NewProjectDialog(ConfigService config)
    {
        InitializeComponent();
        _config = config;

        // 默认父目录：上次输入的值 > 第一个项目路径 > D:\
        if (!string.IsNullOrWhiteSpace(_lastParentDir))
            TxtParentDir.Text = _lastParentDir;
        else if (config.GetProjects().Count > 0)
            TxtParentDir.Text = Path.GetDirectoryName(config.GetProjects()[0].Path.TrimEnd(Path.DirectorySeparatorChar));
        else
            TxtParentDir.Text = @"D:\";
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var name = TxtName.Text.Trim();
        var dir = TxtParentDir.Text.Trim();
        TxtPreview.Text = (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(dir))
            ? $"将创建：{Path.Combine(dir, name)}" : "输入项目名称和父目录后，此处显示完整路径";
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { Create(); e.Handled = true; }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择父目录",
            SelectedPath = TxtParentDir.Text.Trim(),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            TxtParentDir.Text = dialog.SelectedPath;
    }

    private void Create()
    {
        var name = TxtName.Text.Trim();
        var parentDir = TxtParentDir.Text.Trim();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parentDir))
        {
            MessageBox.Show("请填写项目名称和父目录", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!Directory.Exists(parentDir))
        {
            MessageBox.Show("父目录不存在", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            var fullPath = Path.Combine(parentDir, name);
            if (!Directory.Exists(fullPath)) Directory.CreateDirectory(fullPath);
            Result = _config.AddProject(name, fullPath);
            _lastParentDir = parentDir; // 记忆
            DialogResult = true;
            Close();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void CreateBtn_Click(object sender, RoutedEventArgs e) => Create();
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
