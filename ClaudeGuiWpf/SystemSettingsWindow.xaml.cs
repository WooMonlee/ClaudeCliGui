using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClaudeGui;

public partial class SystemSettingsWindow : Window
{
    private readonly ConfigService _config;
    private ApiProviderConfig? _selected;

    public SystemSettingsWindow(ConfigService config)
    {
        InitializeComponent();
        _config = config;
        RefreshProviderList();
        LoadGeneralSettings();
    }

    // ===== 通用设置 =====

    private void LoadGeneralSettings()
    {
        var strategy = _config.GetConfigValue("preloadStrategy", "lastN");
        var enabled = strategy != "off";
        ChkPreloadEnabled.IsChecked = enabled;

        foreach (ComboBoxItem item in CmbPreloadStrategy.Items)
        {
            if (item.Tag is string tag && tag == strategy)
            {
                item.IsSelected = true;
                break;
            }
        }
        TxtPreloadCount.Text = _config.GetConfigValue("preloadCount", "3");
        var showCount = CmbPreloadStrategy.SelectedItem is ComboBoxItem sel && sel.Tag is "lastN";
        PanelPreloadCount.Visibility = showCount ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreloadHint();
        PanelPreloadOptions.IsEnabled = enabled;
    }

    private void PreloadEnabled_Changed(object sender, RoutedEventArgs e)
    { if (PanelPreloadOptions != null) PanelPreloadOptions.IsEnabled = ChkPreloadEnabled.IsChecked == true; }

    private void PreloadStrategy_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PanelPreloadCount == null) return; // 初始化期间未就绪
        var showCount = CmbPreloadStrategy.SelectedItem is ComboBoxItem sel && sel.Tag is "lastN";
        PanelPreloadCount.Visibility = showCount ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreloadHint();
    }

    private void UpdatePreloadHint()
    {
        var total = _config.GetProjects().Count;
        int.TryParse(TxtPreloadCount.Text, out var n);
        if (total <= 3 && n >= total)
            TxtPreloadHint.Text = $"（当前共 {total} 个，将全部打开）";
        else
            TxtPreloadHint.Text = $"（当前共 {total} 个项目）";
    }

    private void SaveGeneral_Click(object sender, RoutedEventArgs e)
    {
        var enabled = ChkPreloadEnabled.IsChecked == true;
        if (enabled && CmbPreloadStrategy.SelectedItem is ComboBoxItem item && item.Tag is string strategy)
        {
            _config.SetConfigValue("preloadStrategy", strategy);
        }
        else if (!enabled)
        {
            _config.SetConfigValue("preloadStrategy", "off");
        }
        _config.SetConfigValue("preloadCount", int.TryParse(TxtPreloadCount.Text, out var n) ? Math.Clamp(n, 1, 10).ToString() : "3");
        TxtGeneralStatus.Text = "已保存 ✓";
        _ = Task.Run(async () => { await Task.Delay(1500); Dispatcher.Invoke(() => TxtGeneralStatus.Text = ""); });
    }

    private void ShowProvidersTab(object sender, RoutedEventArgs e)
    {
        PanelProviders.Visibility = Visibility.Visible;
        PanelGeneral.Visibility = Visibility.Collapsed;
        BtnTabProviders.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0xff, 0xda));
        BtnTabProviders.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0xff, 0xda));
        BtnTabGeneral.BorderBrush = Brushes.Transparent;
        BtnTabGeneral.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x92, 0xb0));
    }

    private void ShowGeneralTab(object sender, RoutedEventArgs e)
    {
        PanelProviders.Visibility = Visibility.Collapsed;
        PanelGeneral.Visibility = Visibility.Visible;
        BtnTabGeneral.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0xff, 0xda));
        BtnTabGeneral.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0xff, 0xda));
        BtnTabProviders.BorderBrush = Brushes.Transparent;
        BtnTabProviders.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x92, 0xb0));
    }

    private void RefreshProviderList()
    {
        ProviderList.ItemsSource = null;
        ProviderList.ItemsSource = _config.GetProviders();
    }

    private void ProviderList_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderList.SelectedItem is ApiProviderConfig p)
        {
            _selected = p;
            TxtProvName.Text = p.Name;
            TxtProvUrl.Text = p.BaseUrl;
            TxtProvKey.Text = p.ApiKey;
            TxtModel.Text = p.Model;
            TxtSmall.Text = p.SmallFastModel;
        }
    }

    private void SaveProvider_Click(object sender, RoutedEventArgs e)
    {
        var name = TxtProvName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        // 修复 L5：重名冲突检查（排除自身改名）
        var existing = _config.GetProvider(name);
        if (existing != null && existing != _selected)
        {
            MessageBox.Show($"提供商 [{name}] 已存在", "名称冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 修复 L4：保存时做深拷贝，避免直接修改列表引用
        var saved = new ApiProviderConfig();
        if (_selected != null) { saved.Name = _selected.Name; saved.Priority = _selected.Priority; }
        saved.Name = name;
        saved.BaseUrl = TxtProvUrl.Text.Trim();
        saved.ApiKey = TxtProvKey.Text.Trim();
        saved.Model = TxtModel.Text.Trim();
        saved.SmallFastModel = TxtSmall.Text.Trim();
        // 修复 L9：始终同步子模型
        saved.DefaultOpusModel = _selected?.DefaultOpusModel ?? saved.Model;
        saved.DefaultSonnetModel = _selected?.DefaultSonnetModel ?? saved.Model;
        saved.DefaultHaikuModel = _selected?.DefaultHaikuModel ?? saved.SmallFastModel;
        saved.DefaultFableModel = _selected?.DefaultFableModel ?? saved.Model;
        if (string.IsNullOrWhiteSpace(saved.DefaultOpusModel)) saved.DefaultOpusModel = saved.Model;
        if (string.IsNullOrWhiteSpace(saved.DefaultSonnetModel)) saved.DefaultSonnetModel = saved.Model;
        if (string.IsNullOrWhiteSpace(saved.DefaultHaikuModel)) saved.DefaultHaikuModel = saved.SmallFastModel;
        if (string.IsNullOrWhiteSpace(saved.DefaultFableModel)) saved.DefaultFableModel = saved.Model;

        _config.SaveProvider(saved);
        _selected = saved;
        RefreshProviderList();

        // 保存反馈（修复 A5：窗口关闭时安全检查）
        TxtSaveStatus.Text = "已保存 ✓";
        var status = TxtSaveStatus;
        _ = Task.Run(async () => { await Task.Delay(1500); Dispatcher.Invoke(() => { if (IsLoaded) status.Text = ""; }); });
    }

    private void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || string.IsNullOrWhiteSpace(_selected.Name)) return;
        // 将所有主提供商降级，然后设为默认
        foreach (var p in _config.GetProviders().Where(x => x.Priority == 0))
            p.Priority = 1;
        _selected.Priority = 0;
        // 修复 S2：同时保存 Priority 变更和活跃名称
        _config.SetActiveProvider(_selected.Name);
        _config.Save(); // 持久化 Priority 变更
        RefreshProviderList();
        TxtSaveStatus.Text = "已设为默认 ✓";
        _ = Task.Run(async () => { await Task.Delay(1500); Dispatcher.Invoke(() => { if (IsLoaded) TxtSaveStatus.Text = ""; }); });
    }

    private void DeleteProvider_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        if (MessageBox.Show($"删除提供商 [{_selected.Name}]？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            _config.DeleteProvider(_selected.Name);
            _selected = null;
            TxtProvName.Text = TxtProvUrl.Text = TxtProvKey.Text = TxtModel.Text = "";
            RefreshProviderList();
        }
    }

    private void AddCustom_Click(object sender, RoutedEventArgs e)
    {
        _selected = null;
        TxtProvName.Text = "";
        TxtProvUrl.Text = "";
        TxtProvKey.Text = "";
        TxtModel.Text = "";
        TxtSmall.Text = "";
        TxtProvName.Focus();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
