using System.Windows;
using System.Windows.Controls;
using IrisBotManager.Core.Services;
using IrisBotManager.Core.Plugin;
using System.Globalization;

namespace IrisBotManager.GUI.Windows;

public partial class PluginSettingsWindow : Window
{
    private readonly PluginUIService _pluginUIService;
    private readonly PluginStateManager _stateManager;
    private readonly string _pluginName;
    private readonly PluginDetailInfo _pluginDetailInfo;
    private readonly Dictionary<string, FrameworkElement> _settingControls = new();
    private Dictionary<string, object> _originalSettings = new();
    private bool _hasUnsavedChanges = false;

    public PluginSettingsWindow(PluginUIService pluginUIService, PluginStateManager stateManager,
                               string pluginName, PluginDetailInfo pluginDetailInfo)
    {
        InitializeComponent();

        _pluginUIService = pluginUIService;
        _stateManager = stateManager;
        _pluginName = pluginName;
        _pluginDetailInfo = pluginDetailInfo;

        InitializePluginInfo();
        InitializeSettings();

        this.Closing += OnClosing;
    }

    #region 초기화

    private void InitializePluginInfo()
    {
        if (_pluginDetailInfo.Plugin == null) return;

        var plugin = _pluginDetailInfo.Plugin;

        PluginNameLabel.Text = plugin.DisplayName;
        PluginVersionLabel.Text = plugin.Version;
        PluginCategoryLabel.Text = plugin.Category;
        PluginDescriptionLabel.Text = plugin.Description;

        // 의존성 표시
        if (plugin.Dependencies?.Any() == true)
        {
            PluginDependenciesLabel.Text = string.Join(", ", plugin.Dependencies);
        }
        else
        {
            PluginDependenciesLabel.Text = "없음";
        }

        // 상태 표시
        PluginEnabledCheckBox.IsChecked = _pluginDetailInfo.IsGloballyEnabled;
        UpdateStatusLabel();

        // 창 제목 설정
        this.Title = $"{plugin.DisplayName} 설정";
    }

    private void InitializeSettings()
    {
        try
        {
            var schema = _pluginDetailInfo.ConfigSchema;
            if (schema == null || !schema.Fields.Any())
            {
                NoSettingsLabel.Visibility = Visibility.Visible;
                return;
            }

            NoSettingsLabel.Visibility = Visibility.Collapsed;

            // 현재 설정값 로드
            LoadCurrentSettings();

            // 설정 UI 생성
            CreateSettingsUI(schema);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"설정 초기화 실패: {ex.Message}", "오류",
                          MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadCurrentSettings()
    {
        try
        {
            var currentConfig = _stateManager.GetGlobalConfig<Dictionary<string, object>>(_pluginName);
            _originalSettings = currentConfig ?? new Dictionary<string, object>();
        }
        catch (Exception ex)
        {
            _originalSettings = new Dictionary<string, object>();
            Console.WriteLine($"설정 로드 실패: {ex.Message}");
        }
    }

    private void CreateSettingsUI(PluginConfigSchema schema)
    {
        SettingsPanel.Children.Clear();
        _settingControls.Clear();

        foreach (var field in schema.Fields)
        {
            try
            {
                var settingControl = CreateSettingControl(field);
                if (settingControl != null)
                {
                    SettingsPanel.Children.Add(settingControl);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"설정 컨트롤 생성 실패 [{field.Name}]: {ex.Message}");
            }
        }
    }

    private FrameworkElement? CreateSettingControl(ConfigField field)
    {
        var container = new StackPanel { Margin = new Thickness(0, 5, 0, 15) }; // 수정: left, top, right, bottom

        // 라벨
        var label = new TextBlock
        {
            Text = field.DisplayName + (field.IsRequired ? " *" : ""),
            FontWeight = field.IsRequired ? FontWeights.Bold : FontWeights.Normal,
            Margin = new Thickness(0, 0, 0, 5) // 수정: left, top, right, bottom
        };
        container.Children.Add(label);

        // 설명
        if (!string.IsNullOrEmpty(field.Description))
        {
            var description = new TextBlock
            {
                Text = field.Description,
                FontSize = 11,
                Foreground = System.Windows.Media.Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5) // 수정: left, top, right, bottom
            };
            container.Children.Add(description);
        }

        // 입력 컨트롤
        FrameworkElement? inputControl = field.Type switch
        {
            ConfigFieldType.Text => CreateTextControl(field),
            ConfigFieldType.Number => CreateNumberControl(field),
            ConfigFieldType.Boolean => CreateBooleanControl(field),
            ConfigFieldType.Dropdown => CreateDropdownControl(field),
            ConfigFieldType.Radio => CreateRadioControl(field),
            ConfigFieldType.TextArea => CreateTextAreaControl(field),
            _ => null
        };

        if (inputControl != null)
        {
            container.Children.Add(inputControl);
            _settingControls[field.Name] = inputControl;

            // 변경 이벤트 등록
            RegisterChangeEvent(inputControl, field);
        }

        return container;
    }

    #endregion

    #region 컨트롤 생성

    private FrameworkElement CreateTextControl(ConfigField field)
    {
        var textBox = new TextBox
        {
            Height = 25,
            Padding = new Thickness(5), // 수정: 단일 값으로 모든 면에 적용
            Text = GetCurrentValue(field)?.ToString() ?? field.DefaultValue?.ToString() ?? ""
        };

        return textBox;
    }

    private FrameworkElement CreateNumberControl(ConfigField field)
    {
        var textBox = new TextBox
        {
            Height = 25,
            Padding = new Thickness(5), // 수정: 단일 값으로 모든 면에 적용
            Text = GetCurrentValue(field)?.ToString() ?? field.DefaultValue?.ToString() ?? "0"
        };

        // 숫자만 입력 가능하도록 검증
        textBox.PreviewTextInput += (s, e) =>
        {
            e.Handled = !IsNumeric(e.Text);
        };

        return textBox;
    }

    private FrameworkElement CreateBooleanControl(ConfigField field)
    {
        var checkBox = new CheckBox
        {
            Content = field.DisplayName,
            IsChecked = GetCurrentValue(field) as bool? ?? field.DefaultValue as bool? ?? false
        };

        return checkBox;
    }

    private FrameworkElement CreateDropdownControl(ConfigField field)
    {
        var comboBox = new ComboBox
        {
            Height = 25,
            Padding = new Thickness(5) // 수정: 단일 값으로 모든 면에 적용
        };

        if (field.Options != null)
        {
            foreach (var option in field.Options)
            {
                comboBox.Items.Add(new ComboBoxItem { Content = option, Tag = option });
            }
        }

        // 현재 값 선택
        var currentValue = GetCurrentValue(field)?.ToString() ?? field.DefaultValue?.ToString();
        if (!string.IsNullOrEmpty(currentValue))
        {
            foreach (ComboBoxItem item in comboBox.Items)
            {
                if (item.Tag?.ToString() == currentValue)
                {
                    comboBox.SelectedItem = item;
                    break;
                }
            }
        }

        return comboBox;
    }

    private FrameworkElement CreateRadioControl(ConfigField field)
    {
        var panel = new StackPanel();
        var groupName = $"radio_{field.Name}";
        var currentValue = GetCurrentValue(field)?.ToString() ?? field.DefaultValue?.ToString();

        if (field.Options != null)
        {
            foreach (var option in field.Options)
            {
                var radioButton = new RadioButton
                {
                    Content = option,
                    GroupName = groupName,
                    Tag = option,
                    IsChecked = option == currentValue,
                    Margin = new Thickness(0, 2, 0, 0) // 수정: left, top, right, bottom
                };
                panel.Children.Add(radioButton);
            }
        }

        return panel;
    }

    private FrameworkElement CreateTextAreaControl(ConfigField field)
    {
        var textBox = new TextBox
        {
            Height = 80,
            Padding = new Thickness(5), // 수정: 단일 값으로 모든 면에 적용
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = GetCurrentValue(field)?.ToString() ?? field.DefaultValue?.ToString() ?? ""
        };

        return textBox;
    }

    #endregion

    #region 이벤트 처리

    private void RegisterChangeEvent(FrameworkElement control, ConfigField field)
    {
        switch (control)
        {
            case TextBox textBox:
                textBox.TextChanged += (s, e) => OnSettingChanged();
                break;
            case CheckBox checkBox:
                checkBox.Checked += (s, e) => OnSettingChanged();
                checkBox.Unchecked += (s, e) => OnSettingChanged();
                break;
            case ComboBox comboBox:
                comboBox.SelectionChanged += (s, e) => OnSettingChanged();
                break;
            case StackPanel panel when field.Type == ConfigFieldType.Radio:
                foreach (RadioButton radio in panel.Children.OfType<RadioButton>())
                {
                    radio.Checked += (s, e) => OnSettingChanged();
                }
                break;
        }
    }

    private void OnSettingChanged()
    {
        _hasUnsavedChanges = true;
        UpdateStatusLabel();
    }

    private void PluginEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        try
        {
            var enabled = PluginEnabledCheckBox.IsChecked == true;
            _pluginUIService.TogglePluginGlobalState(_pluginName, enabled);
            UpdateStatusLabel();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"플러그인 상태 변경 실패: {ex.Message}", "오류",
                          MessageBoxButton.OK, MessageBoxImage.Error);

            // 상태 복원
            PluginEnabledCheckBox.IsChecked = _pluginDetailInfo.IsGloballyEnabled;
        }
    }

    #endregion

    #region 버튼 이벤트

    private void ResetToDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "모든 설정을 기본값으로 되돌리시겠습니까?",
            "기본값 복원",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                ResetToDefaults();
                _hasUnsavedChanges = true;
                UpdateStatusLabel();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"기본값 복원 실패: {ex.Message}", "오류",
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = GetCurrentSettingsFromUI();
            var isValid = await _pluginDetailInfo.Plugin!.ValidateConfigAsync(settings);

            if (isValid)
            {
                MessageBox.Show("설정이 유효합니다!", "테스트 성공",
                              MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("설정이 유효하지 않습니다. 값을 확인해주세요.", "테스트 실패",
                              MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"설정 테스트 실패: {ex.Message}", "오류",
                          MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveSettings();
    }

    private async void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (await SaveSettings())
        {
            this.DialogResult = true;
            this.Close();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_hasUnsavedChanges)
        {
            var result = MessageBox.Show(
                "저장하지 않은 변경사항이 있습니다. 정말 취소하시겠습니까?",
                "변경사항 확인",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.No) return;
        }

        this.DialogResult = false;
        this.Close();
    }

    #endregion

    #region 유틸리티 메서드

    private object? GetCurrentValue(ConfigField field)
    {
        return _originalSettings.TryGetValue(field.Name, out var value) ? value : field.DefaultValue;
    }

    private Dictionary<string, object> GetCurrentSettingsFromUI()
    {
        var settings = new Dictionary<string, object>();
        var schema = _pluginDetailInfo.ConfigSchema;

        if (schema == null) return settings;

        foreach (var field in schema.Fields)
        {
            if (!_settingControls.TryGetValue(field.Name, out var control)) continue;

            object? value = control switch
            {
                TextBox textBox => field.Type == ConfigFieldType.Number ?
                    (int.TryParse(textBox.Text, out var intValue) ? intValue : 0) :
                    textBox.Text,
                CheckBox checkBox => checkBox.IsChecked == true,
                ComboBox comboBox => (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "",
                StackPanel panel when field.Type == ConfigFieldType.Radio =>
                    panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag?.ToString() ?? "",
                _ => null
            };

            if (value != null)
            {
                settings[field.Name] = value;
            }
        }

        return settings;
    }

    private void ResetToDefaults()
    {
        var schema = _pluginDetailInfo.ConfigSchema;
        if (schema == null) return;

        foreach (var field in schema.Fields)
        {
            if (!_settingControls.TryGetValue(field.Name, out var control)) continue;

            switch (control)
            {
                case TextBox textBox:
                    textBox.Text = field.DefaultValue?.ToString() ?? "";
                    break;
                case CheckBox checkBox:
                    checkBox.IsChecked = field.DefaultValue as bool? ?? false;
                    break;
                case ComboBox comboBox:
                    var defaultValue = field.DefaultValue?.ToString();
                    foreach (ComboBoxItem item in comboBox.Items)
                    {
                        if (item.Tag?.ToString() == defaultValue)
                        {
                            comboBox.SelectedItem = item;
                            break;
                        }
                    }
                    break;
                case StackPanel panel when field.Type == ConfigFieldType.Radio:
                    var defaultRadioValue = field.DefaultValue?.ToString();
                    foreach (RadioButton radio in panel.Children.OfType<RadioButton>())
                    {
                        radio.IsChecked = radio.Tag?.ToString() == defaultRadioValue;
                    }
                    break;
            }
        }
    }

    private async Task<bool> SaveSettings()
    {
        try
        {
            var settings = GetCurrentSettingsFromUI();

            // 유효성 검증
            var isValid = await _pluginDetailInfo.Plugin!.ValidateConfigAsync(settings);
            if (!isValid)
            {
                MessageBox.Show("설정이 유효하지 않습니다. 값을 확인해주세요.", "저장 실패",
                              MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // 설정 저장
            _stateManager.SetGlobalConfig(_pluginName, settings);
            _originalSettings = new Dictionary<string, object>(settings);
            _hasUnsavedChanges = false;

            UpdateStatusLabel();

            MessageBox.Show("설정이 저장되었습니다.", "저장 완료",
                          MessageBoxButton.OK, MessageBoxImage.Information);

            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"설정 저장 실패: {ex.Message}", "오류",
                          MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void UpdateStatusLabel()
    {
        var isEnabled = PluginEnabledCheckBox.IsChecked == true;
        var statusText = isEnabled ? "활성화" : "비활성화";

        if (_hasUnsavedChanges)
        {
            statusText += " (변경됨)";
            PluginStatusLabel.Foreground = System.Windows.Media.Brushes.Orange;
        }
        else
        {
            PluginStatusLabel.Foreground = isEnabled ?
                System.Windows.Media.Brushes.Green :
                System.Windows.Media.Brushes.Red;
        }

        PluginStatusLabel.Text = statusText;
    }

    private bool IsNumeric(string text)
    {
        return text.All(c => char.IsDigit(c) || c == '.' || c == '-');
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_hasUnsavedChanges)
        {
            var result = MessageBox.Show(
                "저장하지 않은 변경사항이 있습니다. 정말 닫으시겠습니까?",
                "변경사항 확인",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
            }
        }
    }

    #endregion
}