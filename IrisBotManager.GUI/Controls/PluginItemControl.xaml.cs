using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IrisBotManager.Core.Services;

namespace IrisBotManager.GUI.Controls;

public partial class PluginItemControl : UserControl
{
    internal PluginDisplayInfo? _pluginInfo;

    // 이벤트
    public event Action<string, bool>? EnabledChanged;
    public event Action<string>? HelpRequested;
    public event Action<string>? SettingsRequested;
    public event Action<string>? RoomSettingsRequested;

    public PluginItemControl()
    {
        InitializeComponent();
    }

    public void SetPluginInfo(PluginDisplayInfo pluginInfo)
    {
        _pluginInfo = pluginInfo;
        UpdateUI();
    }

    private void UpdateUI()
    {
        if (_pluginInfo == null) return;

        try
        {
            // 기본 정보
            PluginNameLabel.Text = _pluginInfo.DisplayName;
            DescriptionLabel.Text = _pluginInfo.Description;
            VersionLabel.Text = $"v{_pluginInfo.Version}";
            CategoryLabel.Text = _pluginInfo.Category;
            RequiredRoleLabel.Text = _pluginInfo.RequiredRole;
            FilePathLabel.Text = _pluginInfo.FilePath;
            UsageLabel.Text = _pluginInfo.UsageText;

            // 활성화 상태
            EnabledCheckBox.IsChecked = _pluginInfo.IsGloballyEnabled;
            EnabledCheckBox.Content = _pluginInfo.IsGloballyEnabled ? "활성화" : "비활성화";

            // 권한 레벨에 따른 색상
            RequiredRoleBorder.Background = _pluginInfo.RequiredRole switch
            {
                "사용자" => new SolidColorBrush(Colors.LightGreen),
                "관리자" => new SolidColorBrush(Colors.Orange),
                "최고관리자" => new SolidColorBrush(Colors.Red),
                _ => new SolidColorBrush(Colors.Gray)
            };

            // 방별 설정 지원 여부에 따른 버튼 활성화
            RoomSettingsButton.IsEnabled = _pluginInfo.SupportsRoomSettings;
            RoomSettingsButton.Opacity = _pluginInfo.SupportsRoomSettings ? 1.0 : 0.5;

            // 전체 컨트롤 상태 (비활성화된 플러그인은 흐리게)
            this.Opacity = _pluginInfo.IsGloballyEnabled ? 1.0 : 0.7;
        }
        catch (Exception ex)
        {
            // UI 업데이트 실패 시 기본값으로 설정
            PluginNameLabel.Text = "플러그인 정보 오류";
            DescriptionLabel.Text = $"UI 업데이트 실패: {ex.Message}";
        }
    }

    #region 이벤트 핸들러

    private void EnabledCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (_pluginInfo == null) return;

        try
        {
            var isEnabled = EnabledCheckBox.IsChecked == true;
            _pluginInfo.IsGloballyEnabled = isEnabled;

            EnabledChanged?.Invoke(_pluginInfo.Name, isEnabled);
            UpdateUI(); // UI 상태 갱신
        }
        catch (Exception ex)
        {
            // 체크박스 상태 복원
            EnabledCheckBox.IsChecked = _pluginInfo.IsGloballyEnabled;
            MessageBox.Show($"플러그인 상태 변경 실패: {ex.Message}", "오류",
                          MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pluginInfo == null) return;
        HelpRequested?.Invoke(_pluginInfo.Name);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pluginInfo == null) return;
        SettingsRequested?.Invoke(_pluginInfo.Name);
    }

    private void RoomSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pluginInfo == null) return;

        if (!_pluginInfo.SupportsRoomSettings)
        {
            MessageBox.Show("이 플러그인은 방별 설정을 지원하지 않습니다.", "알림",
                          MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RoomSettingsRequested?.Invoke(_pluginInfo.Name);
    }

    #endregion

    #region 외부에서 호출 가능한 메서드

    public void RefreshPluginInfo(PluginDisplayInfo updatedInfo)
    {
        SetPluginInfo(updatedInfo);
    }

    public void SetEnabled(bool enabled)
    {
        if (_pluginInfo != null)
        {
            _pluginInfo.IsGloballyEnabled = enabled;
            UpdateUI();
        }
    }

    public void HighlightPlugin(bool highlight)
    {
        if (highlight)
        {
            this.Background = new SolidColorBrush(Color.FromRgb(255, 255, 200)); // 연한 노란색
        }
        else
        {
            this.Background = new SolidColorBrush(Colors.Transparent);
        }
    }

    #endregion
}