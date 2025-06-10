using IrisBotManager.Core.Models;
using IrisBotManager.Core.Services;
using Microsoft.VisualBasic;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace IrisBotManager.GUI;

public partial class MainWindow : Window
{
    private readonly ConfigService _configService;
    private readonly AdminService _adminService;
    private readonly AuthService _authService;
    private readonly WebSocketService _webSocketService;
    private readonly PluginManager _pluginManager;
    private readonly PluginUIService _pluginUIService;
    private readonly RoomSettingsService _roomSettingsService;

    // 🔧 추가: 오류 로깅 시스템
    private readonly PluginStateManager _stateManager;
    private readonly ErrorLogger _errorLogger;

    private bool _isShuttingDown = false;

    // 필터링 관련 필드
    private List<PluginDisplayInfo> _allPlugins = new();
    private string _currentSearchText = string.Empty;
    private string _currentCategoryFilter = "전체 카테고리";
    private bool _showEnabledOnly = false;

    // 자동 스캔 타이머
    private DispatcherTimer? _autoScanTimer;

    // 실시간 사용자 수집
    private Dictionary<string, UserInfo> _realtimeUsers = new();

    public class UserInfo
    {
        public string UserId { get; set; } = "";
        public string Nickname { get; set; } = "";
        public string RoomId { get; set; } = "";
        public DateTime LastSeen { get; set; }
    }

    public MainWindow(ConfigService configService, AdminService adminService,
                     AuthService authService, WebSocketService webSocketService,
                     PluginManager pluginManager, PluginUIService pluginUIService,
                     RoomSettingsService roomSettingsService,
                     PluginStateManager stateManager) // 🔧 추가: 매개변수
    {
        InitializeComponent();

        _configService = configService;
        _adminService = adminService;
        _authService = authService;
        _webSocketService = webSocketService;
        _pluginManager = pluginManager;
        _pluginUIService = pluginUIService;
        _roomSettingsService = roomSettingsService;
        _stateManager = stateManager; // 🔧 추가: 초기화

        // 🔧 추가: ErrorLogger 초기화
        _errorLogger = new ErrorLogger(_configService.DataPath);

        InitializeServices();
        InitializeUI();
        InitializeAutoScan();
        _ = LoadPluginsAsync(); // 🔧 수정: 비동기 호출

        // 키보드 이벤트 등록 (Escape로 종료)
        this.KeyDown += MainWindow_KeyDown;

        // 창 닫기 이벤트 등록
        this.Closing += MainWindow_Closing;
    }

    private void InitializeServices()
    {
        _authService.PinChanged += OnPinChanged;
        _webSocketService.ConnectionChanged += OnConnectionChanged;
        _webSocketService.LogMessage += OnLogMessage;
        _webSocketService.MessageReceived += OnMessageReceived;

        // 플러그인 매니저 이벤트 설정
        _pluginManager.TabAddRequested += OnPluginTabAddRequested;
        _pluginManager.NotificationRequested += OnPluginNotificationRequested;

        // 플러그인 UI 서비스 이벤트 설정
        _pluginUIService.PluginListChanged += OnPluginListChanged;
        _pluginUIService.PluginStateChanged += OnPluginStateChanged;
        _pluginUIService.PluginError += OnPluginError;
    }

    private void InitializeUI()
    {
        // 연결 설정 초기화
        HostBox.Text = _configService.Host;
        PortBox.Text = _configService.Port;

        // PIN 표시 초기화
        OnPinChanged(_authService.CurrentPin);

        // 카테고리 필터 초기화
        InitializeCategoryFilter();

        // 자동 스캔 체크박스 이벤트 등록 (XAML에 없는 경우를 대비)
        if (AutoScanCheckBox != null)
        {
            AutoScanCheckBox.Checked += AutoScanCheckBox_Changed;
            AutoScanCheckBox.Unchecked += AutoScanCheckBox_Changed;
        }

        // 플러그인 목록 초기화
        RefreshPluginList();

        // 스캔 결과 초기화
        UpdateScanResultDisplay();
    }

    private void InitializeAutoScan()
    {
        // 자동 스캔 타이머 설정 (30초마다)
        _autoScanTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _autoScanTimer.Tick += AutoScanTimer_Tick;

        // 자동 스캔 체크박스 상태에 따라 타이머 시작/중지
        UpdateAutoScan();
    }

    private void InitializeCategoryFilter()
    {
        try
        {
            // CategoryFilterComboBox가 초기화되었는지 확인
            if (CategoryFilterComboBox == null)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    InitializeCategoryFilter();
                });
                return;
            }

            // 🔧 수정: 직접 카테고리 추출
            var categories = _allPlugins.Select(p => p.Category).Distinct().ToList();
            var categoryItems = new List<ComboBoxItem> { new ComboBoxItem { Content = "전체 카테고리" } };

            foreach (var category in categories)
            {
                categoryItems.Add(new ComboBoxItem { Content = category });
            }

            CategoryFilterComboBox.ItemsSource = categoryItems;
            CategoryFilterComboBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            AppendPluginLog($"❌ 카테고리 필터 초기화 실패: {ex.Message}");
            _ = _errorLogger.LogErrorAsync(LogCategories.UI, "카테고리 필터 초기화 실패", ex.StackTrace, ex.Message);
        }
    }

    #region 🔧 플러그인 관리 메서드들

    // MainWindow.xaml.cs의 수정된 RefreshPluginList 메서드

    private bool _isRefreshingPluginList = false;
    private int _refreshRetryCount = 0;
    private const int MAX_REFRESH_RETRIES = 5;

    private void RefreshPluginList()
    {
        try
        {
            // 🔧 수정: 재귀 방지 플래그 확인
            if (_isRefreshingPluginList)
            {
                AppendPluginLog("⚠️ RefreshPluginList가 이미 실행 중입니다 - 건너뜀");
                return;
            }

            _isRefreshingPluginList = true;
            AppendPluginLog("🔄 RefreshPluginList 시작");

            // 🔧 수정: UI 컨트롤 초기화 확인 및 재시도 제한
            if (PluginListPanel == null || CategoryFilterComboBox == null)
            {
                _refreshRetryCount++;

                if (_refreshRetryCount > MAX_REFRESH_RETRIES)
                {
                    AppendPluginLog($"❌ UI 컨트롤 초기화 실패 - 최대 재시도 횟수({MAX_REFRESH_RETRIES}) 초과");
                    _isRefreshingPluginList = false;
                    _refreshRetryCount = 0;
                    return;
                }

                AppendPluginLog($"⚠️ UI 컨트롤이 아직 초기화되지 않음 - 재시도 {_refreshRetryCount}/{MAX_REFRESH_RETRIES}");

                // 🔧 수정: 지연 후 재시도 (즉시 재귀하지 않고)
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    _isRefreshingPluginList = false; // 플래그 해제 후 재시도
                    RefreshPluginList();
                };
                timer.Start();
                return;
            }

            // 성공적으로 UI 컨트롤을 찾았으면 재시도 카운터 리셋
            _refreshRetryCount = 0;

            AppendPluginLog("✅ UI 컨트롤들이 초기화됨");

            // 🔧 기존 이벤트 핸들러 완전 제거
            foreach (var child in PluginListPanel.Children.OfType<Controls.PluginItemControl>())
            {
                try
                {
                    child.EnabledChanged -= OnPluginEnabledChanged;
                    child.HelpRequested -= OnPluginHelpRequested;
                    child.SettingsRequested -= OnPluginSettingsRequested;
                    child.RoomSettingsRequested -= OnPluginRoomSettingsRequested;
                }
                catch (Exception ex)
                {
                    AppendPluginLog($"⚠️ 이벤트 핸들러 제거 실패: {ex.Message}");
                }
            }

            // 모든 플러그인 정보 새로고침
            _allPlugins = _pluginUIService.GetPluginDisplayInfos();
            AppendPluginLog($"📊 _allPlugins 업데이트 완료: {_allPlugins.Count}개");

            // 카테고리 필터 업데이트
            AppendPluginLog("🏷️ 카테고리 필터 초기화 시작");
            InitializeCategoryFilter();

            // 필터 적용하여 표시
            AppendPluginLog("🔍 필터 적용 시작");
            ApplyPluginFilters();

            // 통계 업데이트
            AppendPluginLog("📈 통계 업데이트 시작");
            UpdatePluginStatistics();

            // 스캔 결과 업데이트
            AppendPluginLog("📋 스캔 결과 업데이트 시작");
            UpdateScanResultDisplay();

            AppendPluginLog($"✅ 플러그인 목록 새로고침 완료 ({_allPlugins.Count}개)");
        }
        catch (Exception ex)
        {
            var errorMessage = $"플러그인 목록 새로고침 실패: {ex.Message}";

            if (StatusText != null)
            {
                StatusText.Text = errorMessage;
            }

            AppendPluginLog($"❌ {errorMessage}");
            AppendPluginLog($"❌ 스택 트레이스: {ex.StackTrace}");

            // 🔧 추가: 오류 로그 파일 저장
            _ = _errorLogger.LogErrorAsync(LogCategories.PLUGIN, errorMessage, ex.StackTrace);
        }
        finally
        {
            // 🔧 수정: 반드시 플래그 해제
            _isRefreshingPluginList = false;
        }
    }

    private void RefreshPluginList_back()
    {
        try
        {
            AppendPluginLog("🔄 RefreshPluginList 시작");

            // UI 컨트롤들이 초기화되었는지 확인
            if (PluginListPanel == null || CategoryFilterComboBox == null)
            {
                AppendPluginLog("⚠️ UI 컨트롤이 아직 초기화되지 않음 - 지연 재시도");
                Dispatcher.BeginInvoke(() =>
                {
                    RefreshPluginList();
                });
                return;
            }

            AppendPluginLog("✅ UI 컨트롤들이 초기화됨");

            // 🔧 기존 이벤트 핸들러 완전 제거
            foreach (var child in PluginListPanel.Children.OfType<Controls.PluginItemControl>())
            {
                try
                {
                    child.EnabledChanged -= OnPluginEnabledChanged;
                    child.HelpRequested -= OnPluginHelpRequested;
                    child.SettingsRequested -= OnPluginSettingsRequested;
                    child.RoomSettingsRequested -= OnPluginRoomSettingsRequested;
                }
                catch (Exception ex)
                {
                    AppendPluginLog($"⚠️ 이벤트 핸들러 제거 실패: {ex.Message}");
                }
            }

            // 모든 플러그인 정보 새로고침
            _allPlugins = _pluginUIService.GetPluginDisplayInfos();
            AppendPluginLog($"📊 _allPlugins 업데이트 완료: {_allPlugins.Count}개");

            // 카테고리 필터 업데이트
            AppendPluginLog("🏷️ 카테고리 필터 초기화 시작");
            InitializeCategoryFilter();

            // 필터 적용하여 표시
            AppendPluginLog("🔍 필터 적용 시작");
            ApplyPluginFilters();

            // 통계 업데이트
            AppendPluginLog("📈 통계 업데이트 시작");
            UpdatePluginStatistics();

            // 스캔 결과 업데이트
            AppendPluginLog("📋 스캔 결과 업데이트 시작");
            UpdateScanResultDisplay();

            AppendPluginLog($"✅ 플러그인 목록 새로고침 완료 ({_allPlugins.Count}개)");
        }
        catch (Exception ex)
        {
            var errorMessage = $"플러그인 목록 새로고침 실패: {ex.Message}";

            if (StatusText != null)
            {
                StatusText.Text = errorMessage;
            }

            AppendPluginLog($"❌ {errorMessage}");
            AppendPluginLog($"❌ 스택 트레이스: {ex.StackTrace}");

            // 🔧 추가: 오류 로그 파일 저장
            _ = _errorLogger.LogErrorAsync(LogCategories.PLUGIN, errorMessage, ex.StackTrace);
        }
    }

    private void DisplayFilteredPlugins(List<PluginDisplayInfo> plugins)
    {
        if (PluginListPanel == null)
        {
            AppendPluginLog("❌ PluginListPanel이 null입니다!");
            Dispatcher.BeginInvoke(() => DisplayFilteredPlugins(plugins));
            return;
        }

        try
        {
            AppendPluginLog($"🔄 플러그인 목록 업데이트 시작 - {plugins.Count}개 플러그인");

            // 🔧 강화된 UI 클리어 프로세스
            var existingControls = PluginListPanel.Children.OfType<Controls.PluginItemControl>().ToList();
            foreach (var control in existingControls)
            {
                try
                {
                    control.EnabledChanged -= OnPluginEnabledChanged;
                    control.HelpRequested -= OnPluginHelpRequested;
                    control.SettingsRequested -= OnPluginSettingsRequested;
                    control.RoomSettingsRequested -= OnPluginRoomSettingsRequested;
                }
                catch (Exception ex)
                {
                    AppendPluginLog($"⚠️ 컨트롤 이벤트 해제 실패: {ex.Message}");
                }
            }

            PluginListPanel.Children.Clear();
            PluginListPanel.UpdateLayout();
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            if (plugins.Count == 0)
            {
                var noResultsLabel = new TextBlock
                {
                    Text = "조건에 맞는 플러그인이 없습니다.",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontStyle = FontStyles.Italic,
                    Foreground = System.Windows.Media.Brushes.Gray,
                    Margin = new Thickness(20)
                };
                PluginListPanel.Children.Add(noResultsLabel);
                AppendPluginLog($"✅ '플러그인 없음' 라벨 추가됨");
                return;
            }

            foreach (var pluginInfo in plugins)
            {
                try
                {
                    var pluginControl = new Controls.PluginItemControl();
                    pluginControl.SetPluginInfo(pluginInfo);

                    // 🔧 새로운 이벤트 핸들러 등록
                    pluginControl.EnabledChanged += OnPluginEnabledChanged;
                    pluginControl.HelpRequested += OnPluginHelpRequested;
                    pluginControl.SettingsRequested += OnPluginSettingsRequested;
                    pluginControl.RoomSettingsRequested += OnPluginRoomSettingsRequested;

                    PluginListPanel.Children.Add(pluginControl);
                }
                catch (Exception ex)
                {
                    AppendPluginLog($"❌ 플러그인 '{pluginInfo.DisplayName}' UI 생성 실패: {ex.Message}");
                    _ = _errorLogger.LogErrorAsync(LogCategories.UI, $"플러그인 컨트롤 생성 실패: {pluginInfo.DisplayName}", ex.StackTrace);
                }
            }

            AppendPluginLog($"✅ 플러그인 목록 UI 업데이트 완료 - 총 {PluginListPanel.Children.Count}개 컨트롤 추가됨");
            PluginListPanel.UpdateLayout();
        }
        catch (Exception ex)
        {
            AppendPluginLog($"❌ 플러그인 목록 UI 업데이트 실패: {ex.Message}");
            _ = _errorLogger.LogErrorAsync(LogCategories.UI, "플러그인 목록 UI 업데이트 실패", ex.StackTrace);

            if (StatusText != null)
            {
                StatusText.Text = $"플러그인 목록 표시 실패: {ex.Message}";
            }
        }
    }

    private void ApplyPluginFilters()
    {
        try
        {
            if (PluginListPanel == null || StatusText == null)
            {
                Dispatcher.BeginInvoke(() => ApplyPluginFilters());
                return;
            }

            var filteredPlugins = _allPlugins.AsEnumerable();

            // 검색 텍스트 필터
            if (!string.IsNullOrEmpty(_currentSearchText))
            {
                filteredPlugins = filteredPlugins.Where(p =>
                    p.DisplayName.Contains(_currentSearchText, StringComparison.OrdinalIgnoreCase) ||
                    p.Description.Contains(_currentSearchText, StringComparison.OrdinalIgnoreCase) ||
                    p.Category.Contains(_currentSearchText, StringComparison.OrdinalIgnoreCase));
            }

            // 카테고리 필터
            if (_currentCategoryFilter != "전체 카테고리")
            {
                filteredPlugins = filteredPlugins.Where(p => p.Category == _currentCategoryFilter);
            }

            // 활성화 상태 필터
            if (_showEnabledOnly)
            {
                filteredPlugins = filteredPlugins.Where(p => p.IsGloballyEnabled);
            }

            // UI 업데이트
            DisplayFilteredPlugins(filteredPlugins.ToList());

            // 필터 결과 상태 표시
            var totalCount = _allPlugins.Count;
            var filteredCount = filteredPlugins.Count();
            StatusText.Text = $"플러그인: {filteredCount}/{totalCount}개 표시됨";
        }
        catch (Exception ex)
        {
            AppendPluginLog($"❌ 플러그인 필터링 오류: {ex.Message}");
            if (StatusText != null)
            {
                StatusText.Text = $"필터링 실패: {ex.Message}";
            }
        }
    }

    private void UpdatePluginStatistics()
    {
        try
        {
            if (TotalPluginsText == null || EnabledPluginsText == null || DisabledPluginsText == null)
            {
                Dispatcher.BeginInvoke(() => UpdatePluginStatistics());
                return;
            }

            var totalCount = _allPlugins.Count;
            var enabledCount = _allPlugins.Count(p => p.IsGloballyEnabled);
            var disabledCount = totalCount - enabledCount;

            TotalPluginsText.Text = totalCount.ToString();
            EnabledPluginsText.Text = enabledCount.ToString();
            DisabledPluginsText.Text = disabledCount.ToString();
        }
        catch (Exception ex)
        {
            AppendPluginLog($"❌ 통계 업데이트 실패: {ex.Message}");
        }
    }

    private void UpdateScanResultDisplay()
    {
        try
        {
            if (ScanResultTextBox == null)
            {
                Dispatcher.BeginInvoke(() => UpdateScanResultDisplay());
                return;
            }

            try
            {
                var scanResult = _pluginManager.GetLastScanResult();
                var resultText = FormatScanResult(scanResult);
                ScanResultTextBox.Text = resultText;
            }
            catch (Exception ex)
            {
                ScanResultTextBox.Text = $"스캔 결과 표시 중 오류 발생: {ex.Message}";
                AppendPluginLog($"❌ 스캔 결과 처리 실패: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            AppendPluginLog($"❌ 스캔 결과 표시 실패: {ex.Message}");
        }
    }

    private string FormatScanResult(PluginScanResult? scanResult)
    {
        var result = new System.Text.StringBuilder();

        if (scanResult == null)
        {
            result.AppendLine("❌ 스캔 결과를 사용할 수 없습니다.");
            return result.ToString();
        }

        result.AppendLine($"🕐 스캔 시간: {scanResult.ScanTime:yyyy-MM-dd HH:mm:ss}");
        result.AppendLine();

        result.AppendLine("📊 스캔 통계:");
        result.AppendLine($"  • 발견된 DLL 파일: {scanResult.FoundFiles?.Count ?? 0}개");
        result.AppendLine($"  • 로드된 플러그인: {scanResult.LoadedPlugins?.Count ?? 0}개");
        result.AppendLine($"  • 제외된 파일: {scanResult.ExcludedFiles?.Count ?? 0}개");
        result.AppendLine($"  • 유효하지 않은 파일: {scanResult.InvalidFiles?.Count ?? 0}개");
        result.AppendLine($"  • 플러그인 없는 DLL: {scanResult.NoPluginFiles?.Count ?? 0}개");
        result.AppendLine($"  • 스캔된 서브폴더: {scanResult.SubfoldersScanned}개");
        result.AppendLine($"  • 오류: {scanResult.Errors?.Count ?? 0}개");
        result.AppendLine();

        if (scanResult.LoadedPlugins?.Any() == true)
        {
            result.AppendLine("✅ 로드된 플러그인:");
            foreach (var plugin in scanResult.LoadedPlugins)
            {
                if (plugin != null)
                {
                    result.AppendLine($"  • {plugin.DisplayName} v{plugin.Version}");
                    result.AppendLine($"    파일: {Path.GetFileName(plugin.FilePath)}");
                    result.AppendLine();
                }
            }
        }

        if (scanResult.Errors?.Any() == true)
        {
            result.AppendLine("❌ 오류 목록:");
            foreach (var error in scanResult.Errors)
            {
                if (!string.IsNullOrEmpty(error))
                {
                    result.AppendLine($"  • {error}");
                }
            }
            result.AppendLine();
        }

        return result.ToString();
    }

    #endregion

    #region 🔧 수정된 관리자 명령어 처리

    private async Task ProcessAdminCommands(string rawMessage)
    {
        try
        {
            var messageData = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(rawMessage);

            if (!messageData.TryGetProperty("json", out var jsonElement))
                return;

            JsonElement innerJson;
            if (jsonElement.ValueKind == JsonValueKind.String)
            {
                var jsonString = jsonElement.GetString();
                if (string.IsNullOrEmpty(jsonString)) return;
                innerJson = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonString);
            }
            else
            {
                innerJson = jsonElement;
            }

            if (!innerJson.TryGetProperty("message", out var messageElement) ||
                !innerJson.TryGetProperty("user_id", out var userIdElement) ||
                !innerJson.TryGetProperty("chat_id", out var roomIdElement) ||
                !innerJson.TryGetProperty("type", out var typeElement))
                return;

            var message = messageElement.GetString() ?? "";
            var userId = userIdElement.GetString() ?? "";
            var roomId = roomIdElement.GetString() ?? "";

            // 🔧 수정: 안전한 타입 변환
            int messageType;
            if (typeElement.ValueKind == JsonValueKind.Number)
            {
                messageType = typeElement.GetInt32();
            }
            else if (typeElement.ValueKind == JsonValueKind.String)
            {
                if (!int.TryParse(typeElement.GetString(), out messageType))
                {
                    AppendConnectionLog($"⚠️ 메시지 타입 변환 실패: {typeElement.GetString()}");
                    return;
                }
            }
            else
            {
                AppendConnectionLog($"⚠️ 지원하지 않는 메시지 타입 형식: {typeElement.ValueKind}");
                return;
            }

            if (messageType != 1 || string.IsNullOrEmpty(message))
                return;

            // 실시간 사용자 정보 수집
            if (!string.IsNullOrEmpty(userId))
            {
                var nickname = "";
                if (innerJson.TryGetProperty("sender_name", out var senderElement))
                {
                    nickname = senderElement.GetString() ?? "";
                }

                if (!string.IsNullOrEmpty(nickname))
                {
                    _realtimeUsers[userId] = new UserInfo
                    {
                        UserId = userId,
                        Nickname = nickname,
                        RoomId = roomId,
                        LastSeen = DateTime.Now
                    };
                }
            }

            // 관리자 명령어 처리
            string? response = null;

            if (message.StartsWith("!관리자등록 "))
            {
                response = await HandleAdminRegister(userId, message);
            }
            else if (message.StartsWith("!관리자삭제 "))
            {
                response = await HandleAdminRemove(userId, message);
            }
            else if (message.StartsWith("!관리자목록 "))
            {
                response = await HandleAdminList(userId, message);
            }
            else if (message == "!관리자도움말" || message == "!관리자명령어")
            {
                response = GetAdminHelpMessage();
            }

            if (!string.IsNullOrEmpty(response))
            {
                await _webSocketService.SendMessageAsync(roomId, response);
                AppendConnectionLog($"📤 관리자 명령어 응답 전송: {roomId}");
            }
        }
        catch (Exception ex)
        {
            var errorMsg = $"관리자 명령어 파싱 실패: {ex.Message}";
            AppendConnectionLog($"❌ {errorMsg}");
            _ = _errorLogger.LogErrorAsync(LogCategories.ADMIN_COMMAND, errorMsg, ex.StackTrace, rawMessage);
        }
    }

    // 🔧 수정: 비동기 메서드들 올바른 반환 타입 사용
    private async Task<string> HandleAdminRegister(string userId, string message)
    {
        await Task.CompletedTask; // 🔧 수정: await 추가

        try
        {
            var parts = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return "⚠️ 사용법: !관리자등록 [PIN번호]";
            }

            var inputPin = parts[1];

            if (inputPin != _authService.CurrentPin)
            {
                _authService.GenerateNewPin();
                AppendConnectionLog($"❌ 잘못된 PIN 시도: {userId}");
                return "❌ 잘못된 PIN입니다.\n새로운 PIN이 생성되었습니다.\n관리자에게 새 PIN을 확인하세요.";
            }

            var result = _authService.AddAdminDirect(userId);

            if (result.Contains("✅"))
            {
                _authService.GenerateNewPin();
                AppendConnectionLog($"✅ 관리자 등록 성공: {userId}");
                return result + "\n🔐 보안을 위해 새 PIN이 생성되었습니다.";
            }

            return result;
        }
        catch (Exception ex)
        {
            AppendConnectionLog($"❌ 관리자 등록 처리 실패: {ex.Message}");
            _ = _errorLogger.LogErrorAsync(LogCategories.ADMIN_COMMAND, "관리자 등록 처리 실패", ex.StackTrace);
            return "❌ 관리자 등록 중 오류가 발생했습니다.";
        }
    }

    private async Task<string> HandleAdminRemove(string userId, string message)
    {
        await Task.CompletedTask; // 🔧 수정: await 추가

        try
        {
            var parts = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                return "⚠️ 사용법: !관리자삭제 [대상자ID] [PIN번호]";
            }

            var targetUserId = parts[1];
            var inputPin = parts[2];

            if (inputPin != _authService.CurrentPin)
            {
                _authService.GenerateNewPin();
                AppendConnectionLog($"❌ 관리자 삭제 시 잘못된 PIN 시도: {userId}");
                return "❌ 잘못된 PIN입니다.\n새로운 PIN이 생성되었습니다.";
            }

            var result = _authService.RemoveAdminDirect(targetUserId);

            if (result.Contains("✅"))
            {
                _authService.GenerateNewPin();
                AppendConnectionLog($"✅ 관리자 삭제 성공: {userId} -> {targetUserId}");
                return result + "\n🔐 보안을 위해 새 PIN이 생성되었습니다.";
            }

            return result;
        }
        catch (Exception ex)
        {
            AppendConnectionLog($"❌ 관리자 삭제 처리 실패: {ex.Message}");
            _ = _errorLogger.LogErrorAsync(LogCategories.ADMIN_COMMAND, "관리자 삭제 처리 실패", ex.StackTrace);
            return "❌ 관리자 삭제 중 오류가 발생했습니다.";
        }
    }

    private async Task<string> HandleAdminList(string userId, string message)
    {
        await Task.CompletedTask; // 🔧 수정: await 추가

        try
        {
            var parts = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return "⚠️ 사용법: !관리자목록 [PIN번호]";
            }

            var inputPin = parts[1];

            if (inputPin != _authService.CurrentPin)
            {
                _authService.GenerateNewPin();
                AppendConnectionLog($"❌ 관리자 목록 조회 시 잘못된 PIN 시도: {userId}");
                return "❌ 잘못된 PIN입니다.\n새로운 PIN이 생성되었습니다.";
            }

            var adminList = _authService.GetAdminListDirect();

            _authService.GenerateNewPin();
            AppendConnectionLog($"✅ 관리자 목록 조회: {userId}");

            return adminList + "\n🔐 보안을 위해 새 PIN이 생성되었습니다.";
        }
        catch (Exception ex)
        {
            AppendConnectionLog($"❌ 관리자 목록 조회 실패: {ex.Message}");
            _ = _errorLogger.LogErrorAsync(LogCategories.ADMIN_COMMAND, "관리자 목록 조회 실패", ex.StackTrace);
            return "❌ 관리자 목록 조회 중 오류가 발생했습니다.";
        }
    }

    private string GetAdminHelpMessage()
    {
        return @"👑 관리자 명령어 도움말

📝 사용 가능한 명령어:
• !관리자등록 [PIN] - 자신을 관리자로 등록
• !관리자삭제 [대상자ID] [PIN] - 다른 관리자 삭제 (관리자만)
• !관리자목록 [PIN] - 관리자 목록 확인 (관리자만)
• !관리자도움말 - 이 도움말 표시

🔐 보안 사항:
• PIN은 관리자 프로그램에서 확인 가능
• 명령어 사용 후 자동으로 새 PIN 생성
• 잘못된 PIN 입력 시 새 PIN 생성

💡 사용 예시:
!관리자등록 123456

⚠️ 주의: PIN 번호는 관리자에게 문의하세요.";
    }

    #endregion

    #region 🔧 향상된 로깅 메서드들

    private void AppendConnectionLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var logMessage = $"[{timestamp}] {message}";
            ConnectionLogBox.AppendText($"{logMessage}\n");
            ConnectionLogBox.ScrollToEnd();

            if (message.Contains("❌") || message.Contains("실패") || message.Contains("오류"))
            {
                _ = _errorLogger.LogErrorAsync(LogCategories.CONNECTION, message);
            }
            else if (message.Contains("⚠️") || message.Contains("경고"))
            {
                _ = _errorLogger.LogWarningAsync(LogCategories.CONNECTION, message);
            }
        });
    }

    private void AppendPluginLog(string message)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                if (PluginLogTextBox != null)
                {
                    var timestamp = DateTime.Now.ToString("HH:mm:ss");
                    var logMessage = $"[{timestamp}] {message}";
                    PluginLogTextBox.AppendText($"{logMessage}\n");
                    PluginLogTextBox.ScrollToEnd();
                }

                if (message.Contains("❌") || message.Contains("실패") || message.Contains("오류"))
                {
                    _ = _errorLogger.LogErrorAsync(LogCategories.PLUGIN, message);
                }
                else if (message.Contains("⚠️") || message.Contains("경고"))
                {
                    _ = _errorLogger.LogWarningAsync(LogCategories.PLUGIN, message);
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"플러그인 로그 출력 실패: {ex.Message}");
        }
    }

    private void AppendAdminResult(string message)
    {
        Dispatcher.Invoke(() =>
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var logMessage = $"[{timestamp}] {message}";
            AdminResultBox.AppendText($"{logMessage}\n");
            AdminResultBox.ScrollToEnd();

            if (message.Contains("❌") || message.Contains("실패") || message.Contains("오류"))
            {
                _ = _errorLogger.LogErrorAsync(LogCategories.ADMIN_COMMAND, message);
            }
            else if (message.Contains("⚠️") || message.Contains("경고"))
            {
                _ = _errorLogger.LogWarningAsync(LogCategories.ADMIN_COMMAND, message);
            }
        });
    }

    #endregion

    #region 🔧 이벤트 핸들러들

    // 플러그인 이벤트 핸들러들
    private void OnPluginEnabledChanged(string pluginName, bool enabled)
    {
        try
        {
            _pluginUIService.TogglePluginGlobalState(pluginName, enabled);
            StatusText.Text = $"플러그인 '{pluginName}' {(enabled ? "활성화" : "비활성화")}됨";

            var plugin = _allPlugins.FirstOrDefault(p => p.Name == pluginName);
            if (plugin != null)
            {
                plugin.IsGloballyEnabled = enabled;
            }

            UpdatePluginStatistics();
            ApplyPluginFilters();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"플러그인 상태 변경 실패: {ex.Message}";
            AppendPluginLog($"❌ 플러그인 상태 변경 실패: {ex.Message}");
        }
    }

    private void OnPluginHelpRequested(string pluginName)
    {
        try
        {
            var helpText = _pluginUIService.GetPluginHelpText(pluginName);
            System.Windows.MessageBox.Show(helpText, $"{pluginName} 도움말", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"도움말 표시 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnPluginSettingsRequested(string pluginName)
    {
        try
        {
            var detailInfo = _pluginUIService.GetPluginDetailInfo(pluginName);
            if (detailInfo != null)
            {
                var settingsWindow = new Windows.PluginSettingsWindow(_pluginUIService, null, pluginName, detailInfo);
                settingsWindow.Owner = this;
                settingsWindow.ShowDialog();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"설정 창 열기 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnPluginRoomSettingsRequested(string pluginName)
    {
        try
        {
            var roomSettingsWindow = new Windows.RoomSettingsWindow(_roomSettingsService, _pluginUIService);
            roomSettingsWindow.Owner = this;
            roomSettingsWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"방별 설정 창 열기 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // 자동 스캔 관련 핸들러들
    private void AutoScanCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateAutoScan();
    }

    private void UpdateAutoScan()
    {
        if (_autoScanTimer == null) return;

        if (AutoScanCheckBox?.IsChecked == true)
        {
            _autoScanTimer.Start();
            AppendPluginLog("🔄 자동 스캔이 활성화되었습니다. (30초 간격)");
        }
        else
        {
            _autoScanTimer.Stop();
            AppendPluginLog("⏸️ 자동 스캔이 비활성화되었습니다.");
        }
    }

    private async void AutoScanTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            var pluginFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");
            if (Directory.Exists(pluginFolder))
            {
                var dllFiles = Directory.GetFiles(pluginFolder, "*.dll", SearchOption.AllDirectories);
                var lastScanResult = _pluginManager.GetLastScanResult();

                // null 안전 검사 추가
                var foundFilesCount = lastScanResult?.FoundFiles?.Count ?? 0;

                if (dllFiles.Length != foundFilesCount)
                {
                    AppendPluginLog($"🔍 자동 스캔: 파일 변경 감지 ({dllFiles.Length}개 DLL)");
                    await _pluginManager.LoadPluginsAsync();
                    RefreshPluginList();
                }
            }
        }
        catch (Exception ex)
        {
            AppendPluginLog($"⚠️ 자동 스캔 오류: {ex.Message}");
        }
    }

    // 서비스 이벤트 핸들러들
    private void OnPluginTabAddRequested(string header, object content, UserRole requiredRole)
    {
        if (_isShuttingDown) return;

        Dispatcher.Invoke(() =>
        {
            if (content is UserControl userControl)
            {
                AddPluginTab(header, userControl, requiredRole);
            }
        });
    }

    private void OnPluginNotificationRequested(string message)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = message;
            AppendPluginLog(message);

            if (message.Contains("플러그인 스캔 완료") ||
                message.Contains("플러그인 로드 완료") ||
                message.Contains("로드된 플러그인"))
            {
                RefreshPluginList();
            }
        });
    }

    private void OnPluginListChanged()
    {
        Dispatcher.Invoke(() =>
        {
            RefreshPluginList();
        });
    }

    private void OnPluginStateChanged(string pluginName, bool enabled)
    {
        Dispatcher.Invoke(() =>
        {
            RefreshPluginList();
        });
    }

    private void OnPluginError(string errorMessage)
    {
        Dispatcher.Invoke(() =>
        {
            AppendPluginLog($"❌ {errorMessage}");
            StatusText.Text = errorMessage;
        });
    }

    // 기존 이벤트 핸들러들
    private void OnPinChanged(string newPin)
    {
        Dispatcher.Invoke(() =>
        {
            CurrentPin.Text = newPin;
            PinDisplay.Text = newPin;
            AppendAdminResult($"🔐 새 PIN 생성: {newPin}");
        });
    }

    private void OnConnectionChanged(bool isConnected)
    {
        Dispatcher.Invoke(() =>
        {
            ConnectionStatus.Text = isConnected ? "연결됨" : "연결 안됨";
        });
    }

    private void OnLogMessage(string message)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = message;
        });
    }

    private async void OnMessageReceived(string rawMessage)
    {
        if (_isShuttingDown) return;

        try
        {
            // UI에 메시지 표시
            Dispatcher.Invoke(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                MessageLogBox.AppendText($"[{timestamp}] {rawMessage}\n");

                if (AutoScrollCheckBox?.IsChecked == true)
                {
                    MessageLogBox.ScrollToEnd();
                }
            });

            // 🔧 추가: 메시지 처리 로깅
            AppendConnectionLog($"📨 메시지 수신: {rawMessage.Substring(0, Math.Min(100, rawMessage.Length))}...");

            // 관리자 명령어 처리 (기존 유지)
            try
            {
                await ProcessAdminCommands(rawMessage);
            }
            catch (Exception ex)
            {
                AppendConnectionLog($"❌ 관리자 명령어 처리 실패: {ex.Message}");
                _ = _errorLogger.LogErrorAsync(LogCategories.MESSAGE_PROCESSING, "관리자 명령어 처리 실패", ex.StackTrace);
            }

            // 🔧 핵심 수정: 플러그인 메시지 처리 강화
            try
            {
                var (message, roomId, userId) = ParseMessage(rawMessage);

                if (!string.IsNullOrEmpty(message) && !string.IsNullOrEmpty(roomId))
                {
                    AppendConnectionLog($"🔍 파싱된 메시지: '{message}' (방: {roomId}, 사용자: {userId})");

                    // 🔧 추가: 플러그인 매니저로 메시지 전달 전 상태 확인
                    var loadedPluginCount = _pluginManager.GetLoadedPlugins().Count;
                    AppendConnectionLog($"📦 로드된 플러그인 수: {loadedPluginCount}개");

                    if (loadedPluginCount == 0)
                    {
                        AppendConnectionLog("⚠️ 로드된 플러그인이 없어 메시지 처리를 건너뜁니다.");
                    }
                    else
                    {
                        AppendConnectionLog($"🚀 플러그인 매니저로 메시지 전달 시작...");

                        // 플러그인 매니저에 메시지 전달
                        await _pluginManager.ProcessMessageAsync(message, roomId);

                        AppendConnectionLog($"✅ 플러그인 메시지 처리 완료");
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(message))
                        AppendConnectionLog("⚠️ 메시지 내용이 비어있음");
                    if (string.IsNullOrEmpty(roomId))
                        AppendConnectionLog("⚠️ 방 ID가 비어있음");
                }
            }
            catch (Exception ex)
            {
                AppendConnectionLog($"❌ 플러그인 메시지 처리 실패: {ex.Message}");
                _ = _errorLogger.LogErrorAsync(LogCategories.MESSAGE_PROCESSING, "플러그인 메시지 처리 실패", ex.StackTrace);
            }
        }
        catch (Exception ex)
        {
            AppendConnectionLog($"❌ 메시지 수신 처리 전체 실패: {ex.Message}");
            _ = _errorLogger.LogErrorAsync(LogCategories.MESSAGE_PROCESSING, "메시지 수신 처리 전체 실패", ex.StackTrace, rawMessage);
        }
    }
    #endregion

    #region 메시지 파싱
    // MainWindow.xaml.cs - 간소화된 ParseMessage (chat_id 중심)

    /// <summary>
    /// WebSocket 메시지를 파싱하여 메시지 내용, 방 ID(chat_id), 사용자 ID를 추출합니다.
    /// </summary>
    /// <param name="rawMessage">원본 WebSocket 메시지</param>
    /// <returns>(메시지 내용, 방 ID, 사용자 ID)</returns>
    private (string message, string roomId, string userId) ParseMessage(string rawMessage)
    {
        try
        {
            AppendConnectionLog($"🔍 메시지 파싱 시작: {rawMessage.Length}자");

            var messageData = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(rawMessage);

            // 최상위 레벨에서 기본 정보 추출
            var message = messageData.TryGetProperty("msg", out var msgElement)
                ? msgElement.GetString() ?? "" : "";
            var roomName = messageData.TryGetProperty("room", out var roomElement)
                ? roomElement.GetString() ?? "" : "";
            var sender = messageData.TryGetProperty("sender", out var senderElement)
                ? senderElement.GetString() ?? "" : "";

            AppendConnectionLog($"📄 기본 정보: 메시지='{message}', 방이름='{roomName}', 발신자='{sender}'");

            // 기본값
            var actualRoomId = roomName;
            var actualUserId = sender;

            // json 객체에서 실제 chat_id 추출
            if (messageData.TryGetProperty("json", out var jsonElement))
            {
                JsonElement innerJson;

                // json이 문자열인 경우 파싱
                if (jsonElement.ValueKind == JsonValueKind.String)
                {
                    var jsonString = jsonElement.GetString();
                    if (!string.IsNullOrEmpty(jsonString))
                    {
                        try
                        {
                            innerJson = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonString);
                        }
                        catch (Exception parseEx)
                        {
                            AppendConnectionLog($"⚠️ JSON 문자열 파싱 실패: {parseEx.Message}");
                            return (message, actualRoomId, actualUserId);
                        }
                    }
                    else
                    {
                        return (message, actualRoomId, actualUserId);
                    }
                }
                else
                {
                    innerJson = jsonElement;
                }

                // 메시지 타입 확인 - 일반 메시지(타입 1)만 처리
                var messageType = 1;
                if (innerJson.TryGetProperty("type", out var typeElement))
                {
                    if (typeElement.ValueKind == JsonValueKind.Number)
                    {
                        messageType = typeElement.GetInt32();
                    }
                    else if (typeElement.ValueKind == JsonValueKind.String &&
                             int.TryParse(typeElement.GetString(), out var parsedType))
                    {
                        messageType = parsedType;
                    }
                }

                if (messageType != 1)
                {
                    AppendConnectionLog($"⚠️ 일반 메시지가 아님 (타입: {messageType}), 건너뜀");
                    return ("", "", "");
                }

                // 🔧 핵심: chat_id 추출 (채팅방 고유 번호)
                if (innerJson.TryGetProperty("chat_id", out var chatIdElement))
                {
                    var chatId = chatIdElement.GetString();
                    if (!string.IsNullOrEmpty(chatId))
                    {
                        actualRoomId = chatId;
                        AppendConnectionLog($"✅ chat_id 추출 성공: '{actualRoomId}' (방이름: '{roomName}')");
                    }
                    else
                    {
                        AppendConnectionLog($"⚠️ chat_id가 비어있음, 방이름 사용: '{actualRoomId}'");
                    }
                }
                else
                {
                    AppendConnectionLog($"⚠️ chat_id 필드 없음, 방이름 사용: '{actualRoomId}'");
                }

                // 사용자 ID 추출
                if (innerJson.TryGetProperty("user_id", out var userIdElement))
                {
                    var userId = userIdElement.GetString();
                    if (!string.IsNullOrEmpty(userId))
                    {
                        actualUserId = userId;
                        AppendConnectionLog($"✅ user_id 추출 성공: '{actualUserId}'");
                    }
                }

                // 메시지 내용 재확인
                if (innerJson.TryGetProperty("message", out var innerMessageElement))
                {
                    var innerMessage = innerMessageElement.GetString();
                    if (!string.IsNullOrEmpty(innerMessage))
                    {
                        message = innerMessage;
                        AppendConnectionLog($"🔄 메시지 내용 업데이트: '{message}'");
                    }
                }
            }

            // 최종 검증
            if (string.IsNullOrEmpty(message))
            {
                AppendConnectionLog($"⚠️ 메시지 내용이 없음");
                return ("", "", "");
            }

            AppendConnectionLog($"✅ 최종 파싱 결과: 메시지='{message}', chat_id='{actualRoomId}', user_id='{actualUserId}'");
            return (message, actualRoomId, actualUserId);
        }
        catch (Exception ex)
        {
            AppendConnectionLog($"❌ 메시지 파싱 실패: {ex.Message}");
            _ = _errorLogger.LogWarningAsync(LogCategories.MESSAGE_PROCESSING, "메시지 파싱 실패", ex.Message);
            return ("", "", "");
        }
    }


    #endregion

    #region 🔧 진단 UI 버튼 이벤트 핸들러들

    // MainWindow.xaml에 버튼들을 추가한 후 사용할 이벤트 핸들러들

    /// <summary>
    /// 플러그인 강제 활성화 버튼 클릭
    /// </summary>
    private void ForceEnableAllButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppendPluginLog("🔧 모든 플러그인 강제 활성화 시작...");

            var loadedPlugins = _pluginManager.GetLoadedPlugins();
            var pluginNames = loadedPlugins.Select(p => p.Name).ToList();

            if (pluginNames.Count == 0)
            {
                AppendPluginLog("❌ 로드된 플러그인이 없습니다!");
                System.Windows.MessageBox.Show("로드된 플러그인이 없습니다.\n먼저 플러그인을 로드하세요.", "플러그인 없음",
                              MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // StateManager에서 모든 플러그인 강제 활성화
            _stateManager.ForceEnableAllPlugins(pluginNames);

            // UI 새로고침
            RefreshPluginList();

            AppendPluginLog($"✅ {pluginNames.Count}개 플러그인 강제 활성화 완료!");
            System.Windows.MessageBox.Show($"{pluginNames.Count}개 플러그인이 전역 및 방별로 활성화되었습니다.", "강제 활성화 완료",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            var errorMsg = $"플러그인 강제 활성화 실패: {ex.Message}";
            AppendPluginLog($"❌ {errorMsg}");
            System.Windows.MessageBox.Show(errorMsg, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// StateManager 상태 리포트 버튼 클릭
    /// </summary>
    private void ShowStateReportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var report = _stateManager.GetFullStateReport();

            var window = new Window
            {
                Title = "StateManager 상태 리포트",
                Width = 700,
                Height = 500,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };

            var textBox = new TextBox
            {
                Text = report,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Margin = new Thickness(10)
            };

            window.Content = textBox;
            window.Show();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"상태 리포트 생성 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 특정 방 기본 설정 생성 버튼 클릭
    /// </summary>
    private void CreateRoomDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var roomId = Microsoft.VisualBasic.Interaction.InputBox(
                "기본 설정을 생성할 방 ID를 입력하세요:",
                "방 ID 입력",
                "18447954271650616");

            if (string.IsNullOrEmpty(roomId))
                return;

            var loadedPlugins = _pluginManager.GetLoadedPlugins();
            var pluginNames = loadedPlugins.Select(p => p.Name).ToList();

            if (pluginNames.Count == 0)
            {
                System.Windows.MessageBox.Show("로드된 플러그인이 없습니다.", "플러그인 없음",
                              MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 방에 기본 설정 생성
            _stateManager.CreateDefaultRoomSettings(roomId, pluginNames);

            AppendPluginLog($"✅ 방 {roomId}에 {pluginNames.Count}개 플러그인 기본 설정 생성 완료!");
            System.Windows.MessageBox.Show($"방 {roomId}에 {pluginNames.Count}개 플러그인의 기본 설정이 생성되었습니다.",
                          "기본 설정 생성 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            var errorMsg = $"방 기본 설정 생성 실패: {ex.Message}";
            AppendPluginLog($"❌ {errorMsg}");
            System.Windows.MessageBox.Show(errorMsg, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 테스트 메시지 전송 버튼 클릭
    /// </summary>
    private async void SendTestMessageButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var roomId = Microsoft.VisualBasic.Interaction.InputBox(
                "테스트 메시지를 보낼 방 ID를 입력하세요:",
                "방 ID 입력",
                "18447954271650616");

            if (string.IsNullOrEmpty(roomId))
                return;

            var message = Microsoft.VisualBasic.Interaction.InputBox(
                "테스트 메시지 내용을 입력하세요:",
                "메시지 입력",
                "안녕하세요! 플러그인 테스트입니다.");

            if (string.IsNullOrEmpty(message))
                return;

            AppendConnectionLog($"🧪 테스트 메시지 처리 시작: '{message}' → 방 {roomId}");

            // 플러그인 매니저로 직접 메시지 전달
            await _pluginManager.ProcessMessageAsync(message, roomId);

            AppendConnectionLog($"✅ 테스트 메시지 처리 완료");
            System.Windows.MessageBox.Show("테스트 메시지가 처리되었습니다.\n로그를 확인하세요.", "테스트 완료",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            var errorMsg = $"테스트 메시지 전송 실패: {ex.Message}";
            AppendConnectionLog($"❌ {errorMsg}");
            System.Windows.MessageBox.Show(errorMsg, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    #region 🔧 추가된 유틸리티 메서드들

    /// <summary>
    /// 플러그인 실행 상태 요약 표시
    /// </summary>
    private void ShowPluginExecutionSummary()
    {
        try
        {
            var roomId = Microsoft.VisualBasic.Interaction.InputBox(
                "확인할 방 ID를 입력하세요:",
                "방 ID 입력",
                "18447954271650616");

            if (string.IsNullOrEmpty(roomId))
                return;

            var summary = _pluginManager.GetPluginExecutionSummary(roomId);

            var window = new Window
            {
                Title = $"방 {roomId} 플러그인 실행 상태",
                Width = 600,
                Height = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };

            var textBox = new TextBox
            {
                Text = summary,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Margin = new Thickness(10)
            };

            window.Content = textBox;
            window.Show();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"실행 상태 요약 생성 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion





    #region 🔧 추가: 누락된 이벤트 핸들러들

    // 타이틀바 관련
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            this.DragMove();
        }
        catch (Exception ex)
        {
            AppendConnectionLog($"⚠️ 창 드래그 중 오류: {ex.Message}");
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        this.WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        RequestApplicationClose();
    }

    // 연결 관련
    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var host = HostBox.Text.Trim();
            var port = PortBox.Text.Trim();

            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(port))
            {
                AppendConnectionLog("⚠️ Host 또는 Port 값이 비어 있습니다.");
                return;
            }

            _configService.Host = host;
            _configService.Port = port;

            AppendConnectionLog($"✅ 설정 저장 완료: {host}:{port}");

            // 연결 시도
            await _webSocketService.ConnectAsync();
        }
        catch (Exception ex)
        {
            AppendConnectionLog($"❌ 연결 실패: {ex.Message}");
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppendConnectionLog("📋 채팅방 목록 조회 중...");

            var rooms = await _webSocketService.GetChatRoomsAsync();

            if (rooms.Any())
            {
                var roomItems = rooms.Select(room => new ComboBoxItem
                {
                    Content = room.DisplayName,
                    Tag = room.Id
                }).ToList();

                RoomDropdown.ItemsSource = roomItems;
                RoomDropdown.SelectedIndex = 0;

                AppendConnectionLog($"✅ 채팅방 {rooms.Count}개 조회됨");
            }
            else
            {
                AppendConnectionLog("⚠️ 조회된 채팅방이 없습니다.");
            }
        }
        catch (Exception ex)
        {
            AppendConnectionLog($"❌ 채팅방 조회 실패: {ex.Message}");
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var message = MessageBox.Text.Trim();
            if (string.IsNullOrEmpty(message))
            {
                AppendConnectionLog("⚠️ 메시지를 입력하세요.");
                return;
            }

            var selectedRoom = RoomDropdown.SelectedItem as ComboBoxItem;
            if (selectedRoom?.Tag == null)
            {
                AppendConnectionLog("⚠️ 채팅방을 선택하세요.");
                return;
            }

            var roomId = selectedRoom.Tag.ToString()!;

            await _webSocketService.SendMessageAsync(roomId, message);
            MessageBox.Clear();
            AppendConnectionLog($"📤 메시지 전송: {message}");
        }
        catch (Exception ex)
        {
            AppendConnectionLog($"❌ 메시지 전송 실패: {ex.Message}");
        }
    }

    // 메시지 관련
    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        MessageLogBox.Clear();
        AppendConnectionLog("🗑️ 메시지 로그가 지워졌습니다.");
    }

    // 플러그인 필터링 관련
    private void PluginSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            _currentSearchText = PluginSearchBox.Text?.Trim() ?? "";
            ApplyPluginFilters();
        }
        catch (Exception ex)
        {
            AppendPluginLog($"⚠️ 검색 필터 적용 실패: {ex.Message}");
        }
    }

    private void CategoryFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            var selectedItem = CategoryFilterComboBox.SelectedItem as ComboBoxItem;
            _currentCategoryFilter = selectedItem?.Content?.ToString() ?? "전체 카테고리";
            ApplyPluginFilters();
        }
        catch (Exception ex)
        {
            AppendPluginLog($"⚠️ 카테고리 필터 적용 실패: {ex.Message}");
        }
    }

    private void ShowEnabledOnlyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        try
        {
            _showEnabledOnly = ShowEnabledOnlyCheckBox.IsChecked == true;
            ApplyPluginFilters();
        }
        catch (Exception ex)
        {
            AppendPluginLog($"⚠️ 활성화 필터 적용 실패: {ex.Message}");
        }
    }

    // 플러그인 대량 작업
    private void EnableAllButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var enabledCount = _pluginUIService.EnableAllPluginsGlobally();
            StatusText.Text = $"{enabledCount}개 플러그인이 활성화되었습니다.";
            AppendPluginLog($"✅ 전체 활성화: {enabledCount}개 플러그인");
        }
        catch (Exception ex)
        {
            AppendPluginLog($"❌ 전체 활성화 실패: {ex.Message}");
        }
    }

    private void DisableAllButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = System.Windows.MessageBox.Show(
                "모든 플러그인을 비활성화하시겠습니까?",
                "전체 비활성화 확인",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                var disabledCount = _pluginUIService.DisableAllPluginsGlobally();
                StatusText.Text = $"{disabledCount}개 플러그인이 비활성화되었습니다.";
                AppendPluginLog($"✅ 전체 비활성화: {disabledCount}개 플러그인");
            }
        }
        catch (Exception ex)
        {
            AppendPluginLog($"❌ 전체 비활성화 실패: {ex.Message}");
        }
    }

    private async void PluginRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppendPluginLog("🔄 플러그인 새로고침 시작...");
            await LoadPluginsAsync();
            AppendPluginLog("✅ 플러그인 새로고침 완료");
        }
        catch (Exception ex)
        {
            AppendPluginLog($"❌ 플러그인 새로고침 실패: {ex.Message}");
        }
    }

    // 플러그인 폴더 및 설정
    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var pluginFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");
            Directory.CreateDirectory(pluginFolder);
            Process.Start("explorer.exe", pluginFolder);
            AppendPluginLog($"📁 플러그인 폴더 열기: {pluginFolder}");
        }
        catch (Exception ex)
        {
            AppendPluginLog($"❌ 플러그인 폴더 열기 실패: {ex.Message}");
        }
    }

    private void RoomSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var roomSettingsWindow = new Windows.RoomSettingsWindow(_roomSettingsService, _pluginUIService, _webSocketService);
            roomSettingsWindow.Owner = this;
            roomSettingsWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            AppendPluginLog($"❌ 방별 설정 창 열기 실패: {ex.Message}");
        }
    }

    private void ExportSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var saveDialog = new SaveFileDialog
            {
                Title = "플러그인 설정 내보내기",
                Filter = "JSON 파일 (*.json)|*.json|모든 파일 (*.*)|*.*",
                DefaultExt = "json",
                FileName = $"PluginSettings_{DateTime.Now:yyyyMMdd_HHmmss}.json"
            };

            if (saveDialog.ShowDialog() == true)
            {
                // TODO: 실제 설정 내보내기 구현
                System.Windows.MessageBox.Show("설정 내보내기 기능은 준비 중입니다.", "알림",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            AppendPluginLog($"❌ 설정 내보내기 실패: {ex.Message}");
        }
    }

    private void ImportSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var openDialog = new OpenFileDialog
            {
                Title = "플러그인 설정 가져오기",
                Filter = "JSON 파일 (*.json)|*.json|모든 파일 (*.*)|*.*",
                Multiselect = false
            };

            if (openDialog.ShowDialog() == true)
            {
                // TODO: 실제 설정 가져오기 구현
                System.Windows.MessageBox.Show("설정 가져오기 기능은 준비 중입니다.", "알림",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            AppendPluginLog($"❌ 설정 가져오기 실패: {ex.Message}");
        }
    }

    // 관리자 관련
    private void NewPinButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _authService.GenerateNewPin();
            AppendAdminResult("🔄 새로운 PIN이 생성되었습니다.");
        }
        catch (Exception ex)
        {
            AppendAdminResult($"❌ PIN 생성 실패: {ex.Message}");
        }
    }

    private void UsageGuideButton_Click(object sender, RoutedEventArgs e)
    {
        var helpMessage = @"📚 IrisBotManager 사용법

        🔐 PIN 시스템:
        • PIN은 관리자 등록/삭제 시 필요한 보안 번호입니다
        • 사용 후 자동으로 새 PIN이 생성됩니다

        👑 관리자 등록 방법:
        1. 채팅방에서 '!관리자등록 [PIN번호]' 입력
        2. 봇이 자동으로 관리자로 등록
        3. 보안을 위해 새 PIN 자동 생성

        📝 기타 명령어:
        • !관리자목록 [PIN] - 관리자 목록 확인
        • !관리자삭제 [대상ID] [PIN] - 관리자 삭제
        • !관리자도움말 - 도움말 표시

        ⚠️ 주의사항:
        • PIN 번호는 타인에게 노출되지 않도록 주의
        • 명령어는 채팅방에서만 사용 가능";

        System.Windows.MessageBox.Show(helpMessage, "사용법 안내", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CheckAdminButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var pin = Interaction.InputBox("PIN 번호를 입력하세요:", "PIN 확인", "");
            if (string.IsNullOrEmpty(pin)) return;

            var result = _authService.ShowAdminList(pin);
            AppendAdminResult(result);
        }
        catch (Exception ex)
        {
            AppendAdminResult($"❌ 관리자 목록 확인 실패: {ex.Message}");
        }
    }

    private void DetailedHelpButton_Click(object sender, RoutedEventArgs e)
    {
        var detailedHelp = @"📖 상세 도움말

        🚀 프로그램 시작:
        1. Host/Port 설정 후 '저장 및 연결' 클릭
        2. 연결 성공 시 '새로고침'으로 채팅방 목록 조회
        3. 플러그인 탭에서 필요한 플러그인 활성화

        🧩 플러그인 관리:
        • 플러그인 폴더에 .dll 파일 복사
        • '새로고침' 버튼으로 새 플러그인 스캔
        • 체크박스로 개별 활성화/비활성화
        • '방별 설정'으로 특정 방에서만 동작하도록 설정

        👑 관리자 시스템:
        • PIN 기반 보안 시스템
        • 채팅방에서 직접 관리자 등록 가능
        • 명령어 사용 후 자동 PIN 재생성

        🔧 고급 기능:
        • 자동 스캔: 플러그인 파일 변경 자동 감지
        • 필터링: 카테고리별/상태별 플러그인 조회
        • 통계: 플러그인 사용 현황 모니터링

        ❓ 문제 해결:
        • 연결 안됨: Host/Port 확인
        • 플러그인 미동작: 전역 활성화 및 방별 설정 확인
        • 오류 발생: 로그 탭에서 상세 정보 확인";

        System.Windows.MessageBox.Show(detailedHelp, "상세 도움말", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CopyPinButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_authService.CurrentPin);
            AppendAdminResult($"📋 PIN 복사됨: {_authService.CurrentPin}");
            StatusText.Text = "PIN이 클립보드에 복사되었습니다.";
        }
        catch (Exception ex)
        {
            AppendAdminResult($"❌ PIN 복사 실패: {ex.Message}");
        }
    }

    #endregion

    #region 기타 메서드들

    // 🔧 수정: 비동기 플러그인 로딩
    private async Task LoadPluginsAsync()
    {
        if (_isShuttingDown) return;

        try
        {
            StatusText.Text = "플러그인 로드 시작...";
            AppendPluginLog("🚀 플러그인 로드 프로세스 시작");

            await _pluginManager.LoadPluginsAsync();

            AppendPluginLog("✅ PluginManager.LoadPluginsAsync() 완료");
            StatusText.Text = "플러그인 로드 완료";

            AppendPluginLog("🔄 RefreshPluginList() 호출 시작");
            RefreshPluginList();
            AppendPluginLog("✅ RefreshPluginList() 호출 완료");
        }
        catch (Exception ex)
        {
            var errorMsg = $"플러그인 로드 실패: {ex.Message}";
            StatusText.Text = errorMsg;
            AppendPluginLog($"❌ {errorMsg}");
            AppendPluginLog($"❌ 스택 트레이스: {ex.StackTrace}");
        }
    }

    public void AddPluginTab(string header, UserControl content, UserRole requiredRole = UserRole.User)
    {
        var tab = new TabItem { Header = header, Content = content };
        MainTabControl.Items.Add(tab);
    }

    #endregion

    #region 종료 관련 메서드들

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            RequestApplicationClose();
        }
        else if (e.Key == Key.F4 &&
                (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            ForceExit();
        }
    }

    private void MainWindow_Closing(object sender, CancelEventArgs e)
    {
        if (!_isShuttingDown)
        {
            e.Cancel = true;
            RequestApplicationClose();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _autoScanTimer?.Stop();
            _webSocketService.Dispose();
            _pluginManager.Dispose();
            _errorLogger.CleanupOldLogs();
        }
        catch
        {
            // 정리 작업 실패 시 무시
        }
        finally
        {
            base.OnClosed(e);
        }
    }

    private void RequestApplicationClose()
    {
        _isShuttingDown = true;
        Close();
    }

    private void ForceExit()
    {
        Environment.Exit(0);
    }

    #endregion

    // MainWindow.xaml.cs에 추가할 진단 메서드들

    #region 🔧 플러그인 진단 도구

    /// <summary>
    /// 플러그인 상태 전체 진단
    /// </summary>
    private void DiagnosePluginStatus()
    {
        var diagnostic = new StringBuilder();
        diagnostic.AppendLine("=== 플러그인 진단 시작 ===");
        diagnostic.AppendLine($"진단 시간: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        diagnostic.AppendLine();

        try
        {
            // 1. 로드된 플러그인 확인
            var loadedPlugins = _pluginManager.GetLoadedPlugins();
            diagnostic.AppendLine($"📦 로드된 플러그인 수: {loadedPlugins.Count}개");

            if (loadedPlugins.Count == 0)
            {
                diagnostic.AppendLine("❌ 로드된 플러그인이 없습니다!");
                diagnostic.AppendLine("   - plugins 폴더에 DLL 파일이 있는지 확인하세요");
                diagnostic.AppendLine("   - 플러그인 스캔 로그를 확인하세요");
            }
            else
            {
                foreach (var plugin in loadedPlugins)
                {
                    diagnostic.AppendLine($"   • {plugin.DisplayName} v{plugin.Version} ({plugin.Category})");
                }
            }
            diagnostic.AppendLine();

            // 2. 전역 상태 확인
            var allPluginInfos = _pluginUIService.GetPluginDisplayInfos();
            var globallyEnabled = allPluginInfos.Count(p => p.IsGloballyEnabled);
            diagnostic.AppendLine($"🌍 전역 활성화된 플러그인: {globallyEnabled}/{allPluginInfos.Count}개");

            foreach (var plugin in allPluginInfos)
            {
                var status = plugin.IsGloballyEnabled ? "✅ 활성화" : "❌ 비활성화";
                diagnostic.AppendLine($"   • {plugin.DisplayName}: {status}");
            }
            diagnostic.AppendLine();

            // 3. 방 설정 상태 확인
            DiagnoseRoomSettings(diagnostic);

            // 4. 메시지 처리 파이프라인 확인
            DiagnoseMessagePipeline(diagnostic);

            // 5. StateManager 상태 확인
            DiagnoseStateManager(diagnostic);

        }
        catch (Exception ex)
        {
            diagnostic.AppendLine($"❌ 진단 중 오류 발생: {ex.Message}");
            diagnostic.AppendLine($"스택 트레이스: {ex.StackTrace}");
        }

        diagnostic.AppendLine("=== 플러그인 진단 완료 ===");

        // 진단 결과를 로그에 출력
        var result = diagnostic.ToString();
        AppendPluginLog(result);

        // 진단 결과를 별도 창으로 표시
        ShowDiagnosticResult(result);
    }

    /// <summary>
    /// 방 설정 진단
    /// </summary>
    private void DiagnoseRoomSettings(StringBuilder diagnostic)
    {
        try
        {
            diagnostic.AppendLine("🏠 방 설정 진단:");

            // 사용 가능한 방 목록 확인
            var availableRooms = _roomSettingsService.GetAvailableRooms();
            diagnostic.AppendLine($"   사용 가능한 방: {availableRooms.Count}개");

            if (availableRooms.Count == 0)
            {
                diagnostic.AppendLine("   ❌ 설정된 방이 없습니다!");
                diagnostic.AppendLine("   - WebSocket 연결 확인");
                diagnostic.AppendLine("   - 채팅방 목록 새로고침 시도");
            }
            else
            {
                foreach (var roomId in availableRooms.Take(5)) // 처음 5개만 표시
                {
                    try
                    {
                        var roomSettings = _roomSettingsService.GetRoomPluginSettings(roomId);
                        var enabledInRoom = roomSettings.PluginSettings.Count(p => p.IsEnabled);
                        var totalInRoom = roomSettings.PluginSettings.Count;

                        diagnostic.AppendLine($"   • 방 {roomId}: {enabledInRoom}/{totalInRoom}개 플러그인 활성화");

                        // 활성화된 플러그인 세부 정보
                        var enabledPlugins = roomSettings.PluginSettings.Where(p => p.IsEnabled).ToList();
                        if (enabledPlugins.Any())
                        {
                            diagnostic.AppendLine($"     활성화된 플러그인: {string.Join(", ", enabledPlugins.Select(p => p.DisplayName))}");
                        }
                    }
                    catch (Exception ex)
                    {
                        diagnostic.AppendLine($"   ❌ 방 {roomId} 설정 조회 실패: {ex.Message}");
                    }
                }

                if (availableRooms.Count > 5)
                {
                    diagnostic.AppendLine($"   ... 외 {availableRooms.Count - 5}개 방 더 있음");
                }
            }
            diagnostic.AppendLine();
        }
        catch (Exception ex)
        {
            diagnostic.AppendLine($"   ❌ 방 설정 진단 실패: {ex.Message}");
            diagnostic.AppendLine();
        }
    }

    /// <summary>
    /// 메시지 처리 파이프라인 진단
    /// </summary>
    private void DiagnoseMessagePipeline(StringBuilder diagnostic)
    {
        try
        {
            diagnostic.AppendLine("💬 메시지 처리 파이프라인 진단:");

            // WebSocket 연결 상태
            var isConnected = _webSocketService.IsConnected;
            diagnostic.AppendLine($"   WebSocket 연결: {(isConnected ? "✅ 연결됨" : "❌ 연결 안됨")}");

            if (!isConnected)
            {
                diagnostic.AppendLine("   ❌ WebSocket이 연결되지 않아 메시지를 받을 수 없습니다!");
                diagnostic.AppendLine("   - 서버 주소와 포트 확인");
                diagnostic.AppendLine("   - 네트워크 연결 확인");
            }

            // 🔧 수정: 이벤트 직접 접근 대신 다른 방법 사용
            // 이벤트 구독 상태는 WebSocketService 내부에서 추적하도록 수정
            //diagnostic.AppendLine($"   메시지 수신 이벤트: {(isConnected ? "✅ 구독됨" : "❌ 구독 안됨")}");
            diagnostic.AppendLine($"   메시지 수신 이벤트: {(_webSocketService.HasMessageReceivedSubscribers ? "✅ 구독됨" : "❌ 구독 안됨")}");

            diagnostic.AppendLine();
        }
        catch (Exception ex)
        {
            diagnostic.AppendLine($"   ❌ 메시지 파이프라인 진단 실패: {ex.Message}");
            diagnostic.AppendLine();
        }
    }

    /// <summary>
    /// StateManager 상태 진단
    /// </summary>
    private void DiagnoseStateManager(StringBuilder diagnostic)
    {
        try
        {
            diagnostic.AppendLine("🔧 StateManager 진단:");

            // 전역 상태 조회
            var globalStates = _stateManager.GetAllGlobalStates();
            diagnostic.AppendLine($"   전역 플러그인 상태: {globalStates.Count}개 등록됨");

            foreach (var kvp in globalStates)
            {
                var status = kvp.Value ? "활성화" : "비활성화";
                diagnostic.AppendLine($"   • {kvp.Key}: {status}");
            }

            diagnostic.AppendLine();
        }
        catch (Exception ex)
        {
            diagnostic.AppendLine($"   ❌ StateManager 진단 실패: {ex.Message}");
            diagnostic.AppendLine();
        }
    }

    /// <summary>
    /// 테스트 메시지 처리 시뮬레이션
    /// </summary>
    private async Task TestMessageProcessing(string testRoomId = "test_room")
    {
        try
        {
            AppendPluginLog("🧪 메시지 처리 테스트 시작...");

            // 테스트 메시지 생성
            var testMessages = new[]
            {
            "안녕하세요",
            "!도움말",
            "테스트 메시지",
            "자동응답 테스트"
        };

            foreach (var message in testMessages)
            {
                AppendPluginLog($"📤 테스트 메시지 처리: '{message}'");

                try
                {
                    // 플러그인 매니저를 통해 직접 메시지 처리 테스트
                    await _pluginManager.ProcessMessageAsync(message, testRoomId);
                    AppendPluginLog($"✅ 메시지 처리 완료: '{message}'");
                }
                catch (Exception ex)
                {
                    AppendPluginLog($"❌ 메시지 처리 실패 '{message}': {ex.Message}");
                }

                // 잠시 대기
                await Task.Delay(100);
            }

            AppendPluginLog("🧪 메시지 처리 테스트 완료");
        }
        catch (Exception ex)
        {
            AppendPluginLog($"❌ 메시지 처리 테스트 실패: {ex.Message}");
        }
    }

    /// <summary>
    /// 진단 결과를 별도 창으로 표시
    /// </summary>
    private void ShowDiagnosticResult(string diagnosticResult)
    {
        var window = new Window
        {
            Title = "플러그인 진단 결과",
            Width = 800,
            Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this
        };

        var textBox = new TextBox
        {
            Text = diagnosticResult,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Margin = new Thickness(10)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(textBox);
        Grid.SetRow(textBox, 0);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10)
        };

        var testButton = new Button
        {
            Content = "메시지 처리 테스트",
            Width = 120,
            Height = 30,
            Margin = new Thickness(5)
        };
        testButton.Click += async (s, e) => await TestMessageProcessing();

        var closeButton = new Button
        {
            Content = "닫기",
            Width = 80,
            Height = 30,
            Margin = new Thickness(5)
        };
        closeButton.Click += (s, e) => window.Close();

        buttonPanel.Children.Add(testButton);
        buttonPanel.Children.Add(closeButton);

        grid.Children.Add(buttonPanel);
        Grid.SetRow(buttonPanel, 1);

        window.Content = grid;
        window.Show();
    }

    #endregion

    // MainWindow.xaml에 진단 버튼 추가를 위한 이벤트 핸들러
    private void DiagnoseButton_Click(object sender, RoutedEventArgs e)
    {
        DiagnosePluginStatus();
    }

}

public class PluginViewModel
{
    public string DisplayName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
}