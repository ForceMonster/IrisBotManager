using IrisBotManager.Core.Models;
using IrisBotManager.Core.Services;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IrisBotManager.GUI.Windows;

public partial class RoomSettingsWindow : Window
{
    private readonly RoomSettingsService _roomSettingsService;
    private readonly PluginUIService _pluginUIService;
    private readonly WebSocketService? _webSocketService;
    private string? _currentRoomId;
    private readonly List<RoomPluginControlInfo> _pluginControls = new();
    private bool _isLoading = false;

    public RoomSettingsWindow(RoomSettingsService roomSettingsService, PluginUIService pluginUIService, WebSocketService? webSocketService = null)
    {
        InitializeComponent();

        _roomSettingsService = roomSettingsService;
        _pluginUIService = pluginUIService;
        _webSocketService = webSocketService;

        // 이벤트 구독
        _roomSettingsService.RoomSettingsChanged += OnRoomSettingsChanged;
        _roomSettingsService.RoomSettingsError += OnRoomSettingsError;
        _pluginUIService.PluginStateChanged += OnPluginStateChanged;

        // 비동기 초기화
        Loaded += async (s, e) => await InitializeAsync();

        // 키보드 단축키 지원 (올바른 이벤트 핸들러 등록)
        this.KeyDown += OnKeyDownHandler;
    }

    #region 초기화

    private async Task InitializeAsync()
    {
        try
        {
            _isLoading = true;
            UpdateLoadingState("초기화 중...");

            InitializeCategoryFilter();
            UpdateTotalPluginCount();
            ShowLoadingMessage(false);
            ShowNoPluginsMessage(false);

            await LoadAvailableRoomsAsync();
        }
        catch (Exception ex)
        {
            ShowError($"초기화 실패: {ex.Message}");
        }
        finally
        {
            _isLoading = false;
            UpdateLoadingState(null);
        }
    }

    private void InitializeCategoryFilter()
    {
        try
        {
            CategoryFilterComboBox.Items.Clear();
            CategoryFilterComboBox.Items.Add(new ComboBoxItem { Content = "모든 카테고리", Tag = "" });

            var categories = _pluginUIService.GetPluginCategories();
            foreach (var category in categories)
            {
                CategoryFilterComboBox.Items.Add(new ComboBoxItem { Content = category, Tag = category });
            }

            CategoryFilterComboBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            ShowError($"카테고리 필터 초기화 실패: {ex.Message}");
        }
    }

    private void UpdateTotalPluginCount()
    {
        try
        {
            var globalPlugins = _pluginUIService.GetPluginDisplayInfos();
            TotalPluginsCountLabel.Text = globalPlugins.Count.ToString();
        }
        catch (Exception)
        {
            TotalPluginsCountLabel.Text = "?";
        }
    }

    private async Task LoadAvailableRoomsAsync()
    {
        try
        {
            UpdateLoadingState("방 목록 로드 중...");

            // UI 컨트롤 초기화 (UI 스레드에서 실행)
            RoomSelectionComboBox.Items.Clear();
            RoomSelectionComboBox.Items.Add(new ComboBoxItem { Content = "방을 선택하세요...", Tag = null });

            List<RoomInfo> rooms;

            // WebSocketService를 통해 방 목록 조회
            if (_webSocketService != null && _webSocketService.IsConnected)
            {
                // 비동기 작업이지만 UI 컨텍스트 유지
                rooms = await _webSocketService.GetChatRoomsAsync().ConfigureAwait(true);
            }
            else
            {
                // 폴백: RoomSettingsService를 통해 조회 (동기 작업)
                var roomIds = _roomSettingsService.GetAvailableRooms();
                rooms = roomIds.Select(id => new RoomInfo
                {
                    Id = id,
                    Name = $"방 {id}",
                    DisplayName = $"방 {id} ({id})"
                }).ToList();
            }

            // UI 업데이트 (이미 UI 스레드에 있으므로 직접 접근 가능)
            foreach (var room in rooms)
            {
                var item = new ComboBoxItem
                {
                    Content = room.DisplayName,
                    Tag = room.Id
                };
                RoomSelectionComboBox.Items.Add(item);
            }

            if (rooms.Count > 0)
            {
                RoomSelectionComboBox.SelectedIndex = 1; // 첫 번째 실제 방 선택
                CurrentRoomNameLabel.Text = $"총 {rooms.Count}개 방 발견";
            }
            else
            {
                CurrentRoomNameLabel.Text = "사용 가능한 방이 없습니다";
            }
        }
        catch (Exception ex)
        {
            ShowError($"방 목록 로드 실패: {ex.Message}");
            CurrentRoomNameLabel.Text = "방 목록 로드 실패";
        }
    }

    private async Task LoadRoomPluginSettingsAsync(string roomId)
    {
        try
        {
            _isLoading = true;
            _currentRoomId = roomId;
            UpdateLoadingState("방 설정 로드 중...");
            ShowLoadingMessage(true);

            // 전역 플러그인 목록 가져오기
            var allPlugins = _pluginUIService.GetPluginDisplayInfos();

            // 방 설정을 전역 플러그인과 동기화하여 로드
            var roomSettings = _roomSettingsService.GetRoomPluginSettingsWithGlobalSync(roomId, allPlugins);

            // UI 업데이트
            var selectedItem = RoomSelectionComboBox.SelectedItem as ComboBoxItem;
            var roomDisplayName = selectedItem?.Content?.ToString() ?? $"방 {roomId}";

            // 방 이름 정리
            var cleanRoomName = roomDisplayName;
            if (roomDisplayName.Contains(" (") && roomDisplayName.EndsWith(")"))
            {
                cleanRoomName = roomDisplayName.Substring(0, roomDisplayName.LastIndexOf(" ("));
            }

            CurrentRoomNameLabel.Text = cleanRoomName;

            // 플러그인 목록 생성
            await CreateRoomPluginListAsync(allPlugins, roomSettings);

            // 통계 업데이트
            UpdateAllStatistics();

            ShowLoadingMessage(false);
        }
        catch (Exception ex)
        {
            ShowError($"방 설정 로드 실패: {ex.Message}");
            CurrentRoomNameLabel.Text = "방 설정 로드 실패";
            ShowLoadingMessage(false);
        }
        finally
        {
            _isLoading = false;
            UpdateLoadingState(null);
        }
    }

    private async Task CreateRoomPluginListAsync(List<PluginDisplayInfo> allPlugins, RoomSettingsInfo roomSettings)
    {
        try
        {
            // 기존 컨트롤 정리
            RoomPluginSettingsPanel.Children.Clear();
            _pluginControls.Clear();

            if (allPlugins.Count == 0)
            {
                ShowNoPluginsMessage(true);
                return;
            }

            ShowNoPluginsMessage(false);

            // 각 플러그인에 대한 컨트롤 생성
            foreach (var plugin in allPlugins)
            {
                try
                {
                    var pluginControl = await CreatePluginControlAsync(plugin, roomSettings);
                    RoomPluginSettingsPanel.Children.Add(pluginControl);
                }
                catch (Exception ex)
                {
                    // 개별 플러그인 실패 시 오류 표시
                    var errorControl = new TextBlock
                    {
                        Text = $"❌ {plugin.DisplayName}: 컨트롤 생성 실패 ({ex.Message})",
                        Foreground = System.Windows.Media.Brushes.Red,
                        Margin = new Thickness(5),
                        TextWrapping = TextWrapping.Wrap
                    };
                    RoomPluginSettingsPanel.Children.Add(errorControl);
                }
            }

            // 필터 적용
            ApplyFilters();
        }
        catch (Exception ex)
        {
            ShowError($"플러그인 목록 생성 실패: {ex.Message}");
        }
    }

    private async Task<FrameworkElement> CreatePluginControlAsync(PluginDisplayInfo plugin, RoomSettingsInfo roomSettings)
    {
        await Task.CompletedTask; // 비동기 메서드로 만들기 위한 더미

        // 방별 설정에서 해당 플러그인 찾기
        var roomPluginSetting = roomSettings.PluginSettings.FirstOrDefault(p => p.PluginName == plugin.Name);

        // 방별 설정이 없으면 전역 설정 기반으로 생성
        if (roomPluginSetting == null)
        {
            roomPluginSetting = new RoomPluginDisplayInfo
            {
                PluginName = plugin.Name,
                DisplayName = plugin.DisplayName,
                Description = plugin.Description,
                Category = plugin.Category,
                IsEnabled = false, // 기본 비활성화
                HasCustomSettings = false,
                IsGloballyEnabled = plugin.IsGloballyEnabled
            };
        }
        else
        {
            roomPluginSetting.IsGloballyEnabled = plugin.IsGloballyEnabled;
        }

        var border = new Border
        {
            BorderBrush = System.Windows.Media.Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(5),
            Padding = new Thickness(10),
            Background = GetPluginBackgroundBrush(plugin, roomPluginSetting)
        };

        var mainPanel = new StackPanel();

        // 헤더 패널 (이름, 상태, 전역 상태 표시)
        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };

        // 플러그인 이름
        var nameLabel = new TextBlock
        {
            Text = plugin.DisplayName,
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerPanel.Children.Add(nameLabel);

        // 카테고리 배지
        var categoryBadge = new Border
        {
            Background = System.Windows.Media.Brushes.LightBlue,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 2, 5, 2),
            Margin = new Thickness(10, 0, 5, 0)
        };
        categoryBadge.Child = new TextBlock
        {
            Text = plugin.Category,
            FontSize = 10,
            Foreground = System.Windows.Media.Brushes.DarkBlue
        };
        headerPanel.Children.Add(categoryBadge);

        // 전역 상태 표시
        var globalStatusLabel = new TextBlock
        {
            Text = $"전역: {(plugin.IsGloballyEnabled ? "활성" : "비활성")}",
            FontSize = 11,
            Foreground = plugin.IsGloballyEnabled ?
                System.Windows.Media.Brushes.Green :
                System.Windows.Media.Brushes.Red,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        headerPanel.Children.Add(globalStatusLabel);

        mainPanel.Children.Add(headerPanel);

        // 설명
        if (!string.IsNullOrEmpty(plugin.Description))
        {
            var descriptionLabel = new TextBlock
            {
                Text = plugin.Description,
                FontSize = 11,
                Foreground = System.Windows.Media.Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 0)
            };
            mainPanel.Children.Add(descriptionLabel);
        }

        // 컨트롤 패널
        var controlPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0)
        };

        // 방별 활성화 체크박스
        var enableCheckBox = new CheckBox
        {
            Content = "이 방에서 활성화",
            IsChecked = roomPluginSetting.IsEnabled,
            IsEnabled = plugin.IsGloballyEnabled, // 전역 비활성화 시 비활성화
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        // 체크박스 이벤트 처리
        enableCheckBox.Checked += (s, e) => OnPluginEnabledChanged(plugin.Name, true);
        enableCheckBox.Unchecked += (s, e) => OnPluginEnabledChanged(plugin.Name, false);

        controlPanel.Children.Add(enableCheckBox);

        // 설정 버튼 (나중에 구현)
        var settingsButton = new Button
        {
            Content = "설정",
            Width = 60,
            Height = 25,
            IsEnabled = false, // 임시로 비활성화
            Margin = new Thickness(0, 0, 5, 0)
        };
        controlPanel.Children.Add(settingsButton);

        // 초기화 버튼
        var resetButton = new Button
        {
            Content = "초기화",
            Width = 60,
            Height = 25
        };
        resetButton.Click += (s, e) => OnPluginReset(plugin.Name);
        controlPanel.Children.Add(resetButton);

        mainPanel.Children.Add(controlPanel);

        // 전역 비활성화 시 알림
        if (!plugin.IsGloballyEnabled)
        {
            var warningLabel = new TextBlock
            {
                Text = "⚠️ 전역적으로 비활성화되어 있습니다. 먼저 플러그인 탭에서 활성화하세요.",
                FontSize = 10,
                Foreground = System.Windows.Media.Brushes.Orange,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 5, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            mainPanel.Children.Add(warningLabel);
        }

        border.Child = mainPanel;

        // 컨트롤 정보 저장
        var controlInfo = new RoomPluginControlInfo
        {
            PluginName = plugin.Name,
            EnableCheckBox = enableCheckBox,
            Container = border,
            Category = plugin.Category
        };
        _pluginControls.Add(controlInfo);

        return border;
    }

    #endregion

    #region 이벤트 핸들러

    private async void RoomSelectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;

        var selectedItem = RoomSelectionComboBox.SelectedItem as ComboBoxItem;
        var roomId = selectedItem?.Tag?.ToString();

        if (!string.IsNullOrEmpty(roomId))
        {
            await LoadRoomPluginSettingsAsync(roomId);
        }
        else
        {
            _currentRoomId = null;
            CurrentRoomNameLabel.Text = "방을 선택하세요";
            ConfiguredPluginsCountLabel.Text = "0";
            TotalPluginsCountLabel.Text = "0";
            RoomPluginSettingsPanel.Children.Clear();
            _pluginControls.Clear();
            ShowNoPluginsMessage(false);
        }
    }

    private async void RefreshRoomsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        try
        {
            var currentSelection = _currentRoomId;

            await LoadAvailableRoomsAsync();

            // 이전 선택 복원 시도
            if (!string.IsNullOrEmpty(currentSelection))
            {
                foreach (ComboBoxItem item in RoomSelectionComboBox.Items)
                {
                    if (item.Tag?.ToString() == currentSelection)
                    {
                        RoomSelectionComboBox.SelectedItem = item;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ShowError($"새로고침 실패: {ex.Message}");
        }
    }

    private void OnPluginEnabledChanged(string pluginName, bool enabled)
    {
        if (string.IsNullOrEmpty(_currentRoomId) || _isLoading) return;

        try
        {
            _roomSettingsService.SetRoomPluginEnabled(_currentRoomId, pluginName, enabled);

            // UI 업데이트
            UpdateRoomStatistics();

            // 상태 피드백
            ShowStatusMessage($"플러그인 '{pluginName}' {(enabled ? "활성화" : "비활성화")}됨");
        }
        catch (Exception ex)
        {
            HandlePluginOperationError(enabled ? "활성화" : "비활성화", pluginName, ex);

            // 체크박스 상태 복원
            var control = _pluginControls.FirstOrDefault(c => c.PluginName == pluginName);
            if (control != null)
            {
                control.EnableCheckBox.IsChecked = !enabled;
            }
        }
    }

    private void OnPluginReset(string pluginName)
    {
        if (string.IsNullOrEmpty(_currentRoomId)) return;

        var result = MessageBox.Show(
            $"플러그인 '{pluginName}'의 방별 설정을 초기화하시겠습니까?",
            "설정 초기화 확인",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                _roomSettingsService.ResetRoomPluginSettings(_currentRoomId, pluginName);
                _ = LoadRoomPluginSettingsAsync(_currentRoomId);
                ShowStatusMessage($"플러그인 '{pluginName}' 설정이 초기화되었습니다.");
            }
            catch (Exception ex)
            {
                HandlePluginOperationError("초기화", pluginName, ex);
            }
        }
    }

    private void CopyFromRoomButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateRoomOperation("방 설정 복사")) return;

        try
        {
            var copyWindow = new RoomCopyWindow(_roomSettingsService, _currentRoomId!, _webSocketService);
            copyWindow.Owner = this;

            if (copyWindow.ShowDialog() == true)
            {
                _ = LoadRoomPluginSettingsAsync(_currentRoomId!);
                ShowStatusMessage("방 설정이 복사되었습니다.");
            }
        }
        catch (Exception ex)
        {
            ShowError($"방 설정 복사 실패: {ex.Message}");
        }
    }

    private async void ResetSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateRoomOperation("설정 초기화")) return;

        var result = MessageBox.Show(
            $"방 '{CurrentRoomNameLabel.Text}'의 모든 플러그인 설정을 초기화하시겠습니까?\n이 작업은 되돌릴 수 없습니다.",
            "설정 초기화 확인",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                _roomSettingsService.ResetAllRoomSettings(_currentRoomId!);
                await LoadRoomPluginSettingsAsync(_currentRoomId!);
                ShowStatusMessage("모든 설정이 초기화되었습니다.");
            }
            catch (Exception ex)
            {
                ShowError($"설정 초기화 실패: {ex.Message}");
            }
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ShowStatusMessage("설정이 실시간으로 저장되고 있습니다.");
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }

    // 외부 이벤트 핸들러
    private void OnRoomSettingsChanged(string roomId)
    {
        if (roomId == _currentRoomId)
        {
            Dispatcher.BeginInvoke(async () =>
            {
                if (!_isLoading && !string.IsNullOrEmpty(_currentRoomId))
                {
                    await LoadRoomPluginSettingsAsync(_currentRoomId);
                }
            });
        }
    }

    private void OnRoomSettingsError(string errorMessage)
    {
        Dispatcher.BeginInvoke(() => ShowError(errorMessage));
    }

    private void OnPluginStateChanged(string pluginName, bool enabled)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // 전역 상태 변경 시 해당 플러그인 컨트롤 업데이트
            var control = _pluginControls.FirstOrDefault(c => c.PluginName == pluginName);
            if (control != null)
            {
                control.EnableCheckBox.IsEnabled = enabled;

                // 전역 비활성화 시 방별 설정도 비활성화
                if (!enabled)
                {
                    control.EnableCheckBox.IsChecked = false;
                }

                // 배경색 업데이트
                UpdatePluginControlBackground(control);
            }
        });
    }

    #endregion

    #region 필터링 및 검색 기능

    private void CategoryFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || CategoryFilterComboBox.SelectedItem == null) return;

        try
        {
            ApplyFilters();
        }
        catch (Exception ex)
        {
            ShowError($"카테고리 필터 적용 실패: {ex.Message}");
        }
    }

    private void ShowEnabledOnlyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        try
        {
            ApplyFilters();
        }
        catch (Exception ex)
        {
            ShowError($"활성화 필터 적용 실패: {ex.Message}");
        }
    }

    private void ApplyFilters()
    {
        if (_isLoading || string.IsNullOrEmpty(_currentRoomId)) return;

        try
        {
            var selectedCategory = (CategoryFilterComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
            var showEnabledOnly = ShowEnabledOnlyCheckBox.IsChecked == true;

            // 현재 표시된 모든 플러그인 컨트롤을 가져와서 필터링
            var allControls = _pluginControls.ToList();
            var filteredControls = allControls.AsEnumerable();

            // 카테고리 필터 적용
            if (!string.IsNullOrEmpty(selectedCategory) && selectedCategory != "모든 카테고리")
            {
                filteredControls = filteredControls.Where(c => c.Category == selectedCategory);
            }

            // 활성화 상태 필터 적용
            if (showEnabledOnly)
            {
                filteredControls = filteredControls.Where(c => c.EnableCheckBox.IsChecked == true);
            }

            var filteredList = filteredControls.ToList();

            // UI 업데이트
            foreach (var control in allControls)
            {
                control.Container.Visibility = filteredList.Contains(control) ?
                    Visibility.Visible : Visibility.Collapsed;
            }

            // 메시지 표시 업데이트
            var visibleCount = filteredList.Count;
            ShowNoPluginsMessage(visibleCount == 0);

            // 통계 업데이트
            UpdateFilteredStatistics(filteredList);
        }
        catch (Exception ex)
        {
            ShowError($"필터 적용 실패: {ex.Message}");
        }
    }

    private void UpdateFilteredStatistics(List<RoomPluginControlInfo> filteredControls)
    {
        try
        {
            var enabledCount = filteredControls.Count(c => c.EnableCheckBox.IsChecked == true);
            ConfiguredPluginsCountLabel.Text = enabledCount.ToString();
        }
        catch (Exception)
        {
            ConfiguredPluginsCountLabel.Text = "?";
        }
    }

    #endregion

    #region 대량 작업 기능

    private async void EnableAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateRoomOperation("전체 활성화")) return;

        try
        {
            var result = MessageBox.Show(
                "이 방의 모든 플러그인을 활성화하시겠습니까?\n전역적으로 비활성화된 플러그인은 제외됩니다.",
                "전체 활성화 확인",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _isLoading = true;
                UpdateLoadingState("모든 플러그인 활성화 중...");

                var globalPlugins = _pluginUIService.GetPluginDisplayInfos();
                var pluginNames = globalPlugins.Where(p => p.IsGloballyEnabled).Select(p => p.Name).ToList();

                _roomSettingsService.EnableAllPluginsInRoom(_currentRoomId!, pluginNames);

                ShowStatusMessage($"{pluginNames.Count}개 플러그인이 활성화되었습니다.");
                await LoadRoomPluginSettingsAsync(_currentRoomId!);
            }
        }
        catch (Exception ex)
        {
            ShowError($"전체 활성화 실패: {ex.Message}");
        }
        finally
        {
            _isLoading = false;
            UpdateLoadingState(null);
        }
    }

    private async void DisableAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateRoomOperation("전체 비활성화")) return;

        try
        {
            var result = MessageBox.Show(
                "이 방의 모든 플러그인을 비활성화하시겠습니까?\n이 작업은 모든 플러그인 기능을 중지시킵니다.",
                "전체 비활성화 확인",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                _isLoading = true;
                UpdateLoadingState("모든 플러그인 비활성화 중...");

                var roomSettings = _roomSettingsService.GetRoomPluginSettings(_currentRoomId!);
                var pluginNames = roomSettings.PluginSettings.Select(p => p.PluginName).ToList();

                _roomSettingsService.DisableAllPluginsInRoom(_currentRoomId!, pluginNames);

                ShowStatusMessage($"{pluginNames.Count}개 플러그인이 비활성화되었습니다.");
                await LoadRoomPluginSettingsAsync(_currentRoomId!);
            }
        }
        catch (Exception ex)
        {
            ShowError($"전체 비활성화 실패: {ex.Message}");
        }
        finally
        {
            _isLoading = false;
            UpdateLoadingState(null);
        }
    }

    #endregion

    #region 설정 내보내기/가져오기 기능

    private void ExportSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateRoomOperation("설정 내보내기")) return;

        try
        {
            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "방별 플러그인 설정 내보내기",
                Filter = "JSON 파일 (*.json)|*.json|모든 파일 (*.*)|*.*",
                DefaultExt = "json",
                FileName = $"Room_{_currentRoomId}_Settings_{DateTime.Now:yyyyMMdd_HHmmss}.json"
            };

            if (saveDialog.ShowDialog() == true)
            {
                var result = _roomSettingsService.ExportRoomSettings(_currentRoomId!, saveDialog.FileName);

                MessageBox.Show(result, "내보내기 결과",
                              MessageBoxButton.OK,
                              result.StartsWith("✅") ? MessageBoxImage.Information : MessageBoxImage.Error);

                if (result.StartsWith("✅"))
                {
                    ShowStatusMessage("설정이 성공적으로 내보내졌습니다.");
                }
            }
        }
        catch (Exception ex)
        {
            ShowError($"설정 내보내기 실패: {ex.Message}");
        }
    }

    private async void ImportSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateRoomOperation("설정 가져오기")) return;

        try
        {
            var openDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "방별 플러그인 설정 가져오기",
                Filter = "JSON 파일 (*.json)|*.json|모든 파일 (*.*)|*.*",
                Multiselect = false
            };

            if (openDialog.ShowDialog() == true)
            {
                var confirmResult = MessageBox.Show(
                    $"설정 파일을 가져오면 현재 설정이 변경될 수 있습니다.\n계속하시겠습니까?\n\n파일: {Path.GetFileName(openDialog.FileName)}",
                    "설정 가져오기 확인",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirmResult == MessageBoxResult.Yes)
                {
                    // 덮어쓰기 옵션 선택
                    var overwriteResult = MessageBox.Show(
                        "기존 설정을 덮어쓰시겠습니까?\n\n'예': 기존 설정 덮어쓰기\n'아니오': 새로운 설정만 추가",
                        "덮어쓰기 옵션",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);

                    if (overwriteResult == MessageBoxResult.Cancel) return;

                    var overwriteExisting = overwriteResult == MessageBoxResult.Yes;
                    var result = _roomSettingsService.ImportRoomSettings(_currentRoomId!, openDialog.FileName, overwriteExisting);

                    MessageBox.Show(result, "가져오기 결과",
                                  MessageBoxButton.OK,
                                  result.StartsWith("✅") ? MessageBoxImage.Information : MessageBoxImage.Error);

                    if (result.StartsWith("✅"))
                    {
                        ShowStatusMessage("설정이 성공적으로 가져와졌습니다.");
                        await LoadRoomPluginSettingsAsync(_currentRoomId!);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ShowError($"설정 가져오기 실패: {ex.Message}");
        }
    }

    #endregion

    #region 유틸리티 메서드

    private void UpdateLoadingState(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            // 로딩 완료
            this.IsEnabled = true;
            this.Cursor = Cursors.Arrow;
        }
        else
        {
            // 로딩 중
            this.IsEnabled = false;
            this.Cursor = Cursors.Wait;
            CurrentRoomNameLabel.Text = message;
        }
    }

    private void UpdateRoomStatistics()
    {
        try
        {
            var enabledCount = _pluginControls.Count(c => c.EnableCheckBox.IsChecked == true);
            ConfiguredPluginsCountLabel.Text = enabledCount.ToString();
        }
        catch (Exception)
        {
            ConfiguredPluginsCountLabel.Text = "?";
        }
    }

    private void UpdateAllStatistics()
    {
        try
        {
            UpdateRoomStatistics();
            UpdateTotalPluginCount();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"통계 업데이트 실패: {ex.Message}");
        }
    }

    private System.Windows.Media.Brush GetPluginBackgroundBrush(PluginDisplayInfo plugin, RoomPluginDisplayInfo roomSetting)
    {
        if (!plugin.IsGloballyEnabled)
        {
            return System.Windows.Media.Brushes.LightGray; // 전역 비활성화
        }
        else if (roomSetting.IsEnabled)
        {
            return System.Windows.Media.Brushes.LightGreen; // 방별 활성화
        }
        else
        {
            return System.Windows.Media.Brushes.LightYellow; // 방별 비활성화
        }
    }

    private void UpdatePluginControlBackground(RoomPluginControlInfo control)
    {
        try
        {
            var plugin = _pluginUIService.GetPluginDisplayInfos()
                .FirstOrDefault(p => p.Name == control.PluginName);

            if (plugin != null)
            {
                var roomSetting = new RoomPluginDisplayInfo
                {
                    IsEnabled = control.EnableCheckBox.IsChecked == true
                };

                control.Container.Background = GetPluginBackgroundBrush(plugin, roomSetting);
            }
        }
        catch
        {
            // 업데이트 실패 시 무시
        }
    }

    private void ShowError(string message)
    {
        MessageBox.Show(message, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void ShowStatusMessage(string message)
    {
        // 상태바에 메시지 표시 (3초 후 자동 사라짐)
        CurrentRoomNameLabel.Text = message;

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        timer.Tick += (s, e) =>
        {
            if (!_isLoading && !string.IsNullOrEmpty(_currentRoomId))
            {
                var selectedItem = RoomSelectionComboBox.SelectedItem as ComboBoxItem;
                var roomDisplayName = selectedItem?.Content?.ToString() ?? $"방 {_currentRoomId}";
                CurrentRoomNameLabel.Text = roomDisplayName.Contains(" (") ?
                    roomDisplayName.Substring(0, roomDisplayName.LastIndexOf(" (")) :
                    roomDisplayName;
            }
            timer.Stop();
        };
        timer.Start();
    }

    private void ShowLoadingMessage(bool show)
    {
        if (LoadingMessage != null)
        {
            LoadingMessage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        if (NoPluginsMessage != null && show)
        {
            NoPluginsMessage.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowNoPluginsMessage(bool show)
    {
        if (NoPluginsMessage != null)
        {
            NoPluginsMessage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        if (LoadingMessage != null && show)
        {
            LoadingMessage.Visibility = Visibility.Collapsed;
        }
    }

    private void HandlePluginOperationError(string operation, string pluginName, Exception ex)
    {
        var errorMessage = $"{operation} 실패 [{pluginName}]: {ex.Message}";
        Console.WriteLine($"ERROR: {errorMessage}");

        // 사용자에게 친화적인 메시지 표시
        var userMessage = operation switch
        {
            "활성화" => $"플러그인 '{pluginName}'을(를) 활성화할 수 없습니다.",
            "비활성화" => $"플러그인 '{pluginName}'을(를) 비활성화할 수 없습니다.",
            "설정" => $"플러그인 '{pluginName}'의 설정을 변경할 수 없습니다.",
            "초기화" => $"플러그인 '{pluginName}'의 설정을 초기화할 수 없습니다.",
            _ => $"플러그인 '{pluginName}' 작업이 실패했습니다."
        };

        ShowStatusMessage($"❌ {userMessage}");
    }

    private bool ValidateRoomOperation(string operationName)
    {
        if (string.IsNullOrEmpty(_currentRoomId))
        {
            ShowError($"{operationName}을(를) 수행하려면 먼저 방을 선택하세요.");
            return false;
        }

        if (_isLoading)
        {
            ShowStatusMessage($"{operationName}은(는) 현재 처리 중인 작업이 완료된 후 수행할 수 있습니다.");
            return false;
        }

        return true;
    }

    #endregion

    #region 키보드 단축키 지원 (수정된 버전)

    private void OnKeyDownHandler(object sender, KeyEventArgs e)
    {
        try
        {
            // Ctrl+S: 저장
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SaveButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            // Ctrl+R: 새로고침
            else if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control)
            {
                RefreshRoomsButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            // Ctrl+E: 전체 활성화
            else if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.Control)
            {
                EnableAllButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            // Ctrl+D: 전체 비활성화
            else if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control)
            {
                DisableAllButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            // F5: 새로고침
            else if (e.Key == Key.F5)
            {
                RefreshRoomsButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            // Escape: 닫기
            else if (e.Key == Key.Escape)
            {
                CloseButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"키보드 단축키 처리 실패: {ex.Message}");
        }
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        // 이벤트 구독 해제
        _roomSettingsService.RoomSettingsChanged -= OnRoomSettingsChanged;
        _roomSettingsService.RoomSettingsError -= OnRoomSettingsError;
        _pluginUIService.PluginStateChanged -= OnPluginStateChanged;

        base.OnClosed(e);
    }
}

// 플러그인 컨트롤 정보 클래스
public class RoomPluginControlInfo
{
    public string PluginName { get; set; } = "";
    public string Category { get; set; } = "";
    public CheckBox EnableCheckBox { get; set; } = null!;
    public Border Container { get; set; } = null!;
}

// 간단한 방 복사 창
public class RoomCopyWindow : Window
{
    private readonly RoomSettingsService _roomSettingsService;
    private readonly string _targetRoomId;
    private readonly WebSocketService? _webSocketService;
    private ComboBox? _sourceRoomComboBox;

    public RoomCopyWindow(RoomSettingsService roomSettingsService, string targetRoomId, WebSocketService? webSocketService = null)
    {
        _roomSettingsService = roomSettingsService;
        _targetRoomId = targetRoomId;
        _webSocketService = webSocketService;

        InitializeUI();
        _ = LoadSourceRoomsAsync();
    }

    private void InitializeUI()
    {
        Title = "방 설정 복사";
        Width = 400;
        Height = 200;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 설명
        var descLabel = new TextBlock
        {
            Text = "복사할 원본 방을 선택하세요:",
            Margin = new Thickness(10),
            FontWeight = FontWeights.Bold
        };
        Grid.SetRow(descLabel, 0);
        grid.Children.Add(descLabel);

        // 방 선택 콤보박스
        _sourceRoomComboBox = new ComboBox
        {
            Margin = new Thickness(10),
            Height = 25
        };
        Grid.SetRow(_sourceRoomComboBox, 1);
        grid.Children.Add(_sourceRoomComboBox);

        // 버튼 패널
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10)
        };

        var copyButton = new Button
        {
            Content = "복사",
            Width = 80,
            Height = 30,
            Margin = new Thickness(5, 0, 5, 0)
        };
        copyButton.Click += CopyButton_Click;

        var cancelButton = new Button
        {
            Content = "취소",
            Width = 80,
            Height = 30,
            Margin = new Thickness(5, 0, 5, 0)
        };
        cancelButton.Click += (s, e) => this.Close();

        buttonPanel.Children.Add(copyButton);
        buttonPanel.Children.Add(cancelButton);

        Grid.SetRow(buttonPanel, 2);
        grid.Children.Add(buttonPanel);

        Content = grid;
    }

    private async Task LoadSourceRoomsAsync()
    {
        try
        {
            if (_sourceRoomComboBox == null) return;

            _sourceRoomComboBox.Items.Clear();
            _sourceRoomComboBox.Items.Add(new ComboBoxItem { Content = "방을 선택하세요...", Tag = null });

            List<RoomInfo> rooms;

            if (_webSocketService != null && _webSocketService.IsConnected)
            {
                rooms = await _webSocketService.GetChatRoomsAsync();
            }
            else
            {
                var roomIds = _roomSettingsService.GetAvailableRooms();
                rooms = roomIds.Select(id => new RoomInfo
                {
                    Id = id,
                    Name = $"방 {id}",
                    DisplayName = $"방 {id} ({id})"
                }).ToList();
            }

            foreach (var room in rooms.Where(r => r.Id != _targetRoomId))
            {
                var item = new ComboBoxItem
                {
                    Content = room.DisplayName,
                    Tag = room.Id
                };
                _sourceRoomComboBox.Items.Add(item);
            }

            _sourceRoomComboBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"방 목록 로드 실패: {ex.Message}", "오류",
                          MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_sourceRoomComboBox?.SelectedItem is ComboBoxItem selectedItem &&
                selectedItem.Tag is string sourceRoomId)
            {
                // 실제 복사 실행 - 이제 CopyRoomSettings 메서드가 존재합니다
                _roomSettingsService.CopyRoomSettings(sourceRoomId, _targetRoomId);

                this.DialogResult = true;
                this.Close();
            }
            else
            {
                MessageBox.Show("복사할 원본 방을 선택하세요.", "알림",
                              MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"복사 실패: {ex.Message}", "오류",
                          MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}