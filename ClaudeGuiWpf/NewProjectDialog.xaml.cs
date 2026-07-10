using System.Windows;
using System.Windows.Controls;

namespace ClaudeGui;

public partial class NewProjectDialog : Window
{
    private readonly ConfigService _config;
    public ProjectEntry? Result { get; private set; }

    public NewProjectDialog(ConfigService config)
    {
        InitializeComponent();
        _config = config;

        // 默认使用第一个项目路径作为父目录
        var projects = config.GetProjects();
        if (projects.Count > 0)
        {
            var parent = Path.GetDirectoryName(projects[0].Path.TrimEnd(Path.DirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(parent))
                TxtParentDir.Text = parent;
        }
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var name = TxtName.Text.Trim();
        var dir = TxtParentDir.Text.Trim();
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(dir))
            TxtPreview.Text = $"将创建：{Path.Combine(dir, name)}";
        else
            TxtPreview.Text = "";
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
        {
            TxtParentDir.Text = dialog.SelectedPath;
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
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
            if (!Directory.Exists(fullPath))
                Directory.CreateDirectory(fullPath);

            Result = _config.AddProject(name, fullPath);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
