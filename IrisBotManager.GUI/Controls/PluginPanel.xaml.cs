using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using IrisBotManager.Core.Services;

namespace IrisBotManager.GUI.Controls;

public partial class PluginPanel : UserControl
{
    private AuthService? _authService;
    private PluginManager? _pluginManager;

    public PluginPanel()
    {
        InitializeComponent();
    }

    public void Initialize(AuthService authService, PluginManager pluginManager)
    {
        _authService = authService;
        _pluginManager = pluginManager;
        RefreshPluginList();
    }

    private void RefreshPluginList()
    {
        if (_pluginManager == null)
        {
            // 플러그인 매니저가 없을 때 더미 데이터 표시
            var dummyPlugins = new List<PluginViewModel>
            {
                new PluginViewModel { DisplayName = "플러그인 로딩 중...", Version = "", IsEnabled = false }
            };
            PluginListBox.ItemsSource = dummyPlugins;
            return;
        }

        var plugins = _pluginManager.GetLoadedPlugins();
        var pluginViewModels = plugins.Select(p => new PluginViewModel
        {
            DisplayName = p.DisplayName,
            Version = p.Version,
            IsEnabled = true // 로드된 플러그인은 모두 활성화된 상태
        }).ToList();

        if (pluginViewModels.Count == 0)
        {
            pluginViewModels.Add(new PluginViewModel
            {
                DisplayName = "설치된 플러그인이 없습니다",
                Version = "",
                IsEnabled = false
            });
        }

        PluginListBox.ItemsSource = pluginViewModels;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshPluginList();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var pluginFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");

        try
        {
            Directory.CreateDirectory(pluginFolder);
            Process.Start("explorer.exe", pluginFolder);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"플러그인 폴더 열기 실패: {ex.Message}", "오류",
                          MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public class PluginViewModel
{
    public string DisplayName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
}