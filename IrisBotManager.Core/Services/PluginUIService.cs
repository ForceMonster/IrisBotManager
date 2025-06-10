using IrisBotManager.Core.Plugin;
using IrisBotManager.Core.Models; // UserRoleExtensions를 위한 using 추가

namespace IrisBotManager.Core.Services;

public class PluginUIService
{
    private readonly PluginManager _pluginManager;
    private readonly PluginStateManager _stateManager;

    // UI 업데이트 이벤트
    public event Action? PluginListChanged;
    public event Action<string, bool>? PluginStateChanged;
    public event Action<string>? PluginError;

    public PluginUIService(PluginManager pluginManager, PluginStateManager stateManager)
    {
        _pluginManager = pluginManager;
        _stateManager = stateManager;

        // 🔧 수정: 이제 시그니처가 맞으므로 정상 동작
        _stateManager.GlobalStateChanged += OnGlobalStateChanged;
        _stateManager.RoomStateChanged += OnRoomStateChanged; // 이제 3개 매개변수 이벤트와 매칭됨
        _stateManager.PluginConfigChanged += OnPluginConfigChanged;
    }

    // 🔧 추가: 쿼리 실행 추적 (순환 방지)
    private readonly HashSet<string> _executingQueries = new();
    private readonly object _queryLock = new object();

    #region 🔧 수정: 안전한 플러그인 목록 조회

    public List<PluginDisplayInfo> GetPluginDisplayInfos()
    {
        const string queryKey = "GetPluginDisplayInfos";

        lock (_queryLock)
        {
            if (_executingQueries.Contains(queryKey))
            {
                Console.WriteLine($"순환 쿼리 방지: {queryKey}");
                return new List<PluginDisplayInfo>(); // 빈 리스트 반환
            }

            _executingQueries.Add(queryKey);
        }

        try
        {
            var plugins = _pluginManager.GetLoadedPlugins();

            // 🔧 수정: null 체크 및 안전한 변환
            if (plugins == null || plugins.Count == 0)
            {
                return new List<PluginDisplayInfo>();
            }

            var pluginInfos = new List<PluginDisplayInfo>();

            // 🔧 수정: foreach 사용 (LINQ 대신)으로 순환 참조 위험 감소
            foreach (var plugin in plugins)
            {
                try
                {
                    if (plugin == null) continue;

                    var pluginInfo = CreatePluginDisplayInfo(plugin);
                    if (pluginInfo != null)
                    {
                        pluginInfos.Add(pluginInfo);
                    }
                }
                catch (Exception ex)
                {
                    PluginError?.Invoke($"플러그인 정보 생성 실패 [{plugin?.Name ?? "Unknown"}]: {ex.Message}");
                    Console.WriteLine($"플러그인 정보 생성 실패: {ex.Message}");
                }
            }

            return pluginInfos;
        }
        catch (Exception ex)
        {
            PluginError?.Invoke($"플러그인 목록 조회 실패: {ex.Message}");
            return new List<PluginDisplayInfo>();
        }
        finally
        {
            lock (_queryLock)
            {
                _executingQueries.Remove(queryKey);
            }
        }
    }

    // 🔧 추가: 안전한 PluginDisplayInfo 생성
    private PluginDisplayInfo? CreatePluginDisplayInfo(IPlugin plugin)
    {
        try
        {
            if (plugin == null) return null;

            var roomUsageCount = GetPluginRoomUsageCountSafe(plugin.Name);
            var totalRoomCount = GetTotalRoomCountSafe();
            var filePath = GetPluginFilePathSafe(plugin);
            var roleDisplayName = GetRoleDisplayNameSafe(plugin.RequiredRole);

            return new PluginDisplayInfo
            {
                Name = plugin.Name ?? "",
                DisplayName = plugin.DisplayName ?? "",
                Version = plugin.Version ?? "",
                Description = plugin.Description ?? "",
                Category = plugin.Category ?? "",
                IsGloballyEnabled = _stateManager.IsGloballyEnabled(plugin.Name),
                RoomUsageCount = roomUsageCount,
                TotalRoomCount = totalRoomCount,
                FilePath = filePath,
                RequiredRole = roleDisplayName,
                SupportsRoomSettings = plugin.SupportsRoomSettings,
                Dependencies = plugin.Dependencies?.ToList() ?? new List<string>(),
                LoadTime = DateTime.Now
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PluginDisplayInfo 생성 실패 [{plugin?.Name}]: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 카테고리별로 필터링된 플러그인 목록 조회
    /// </summary>
    public List<PluginDisplayInfo> GetPluginDisplayInfosByCategory(string category)
    {
        var allPlugins = GetPluginDisplayInfos();

        if (string.IsNullOrEmpty(category) || category == "모든 카테고리" || category == "전체 카테고리")
        {
            return allPlugins;
        }

        return allPlugins.Where(p => p.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// 활성화 상태별로 필터링된 플러그인 목록 조회
    /// </summary>
    public List<PluginDisplayInfo> GetPluginDisplayInfosByEnabledState(bool enabledOnly)
    {
        var allPlugins = GetPluginDisplayInfos();

        if (!enabledOnly)
        {
            return allPlugins;
        }

        return allPlugins.Where(p => p.IsGloballyEnabled).ToList();
    }

    
    public void TogglePluginGlobalState(string pluginName, bool enabled)
    {
        try
        {
            // 🔧 추가: 현재 상태와 같으면 변경하지 않음
            var currentState = _stateManager.IsGloballyEnabled(pluginName);
            if (currentState == enabled)
            {
                Console.WriteLine($"플러그인 '{pluginName}' 상태가 이미 {(enabled ? "활성화" : "비활성화")}되어 있음");
                return;
            }

            _stateManager.SetGlobalEnabled(pluginName, enabled);
        }
        catch (Exception ex)
        {
            PluginError?.Invoke($"플러그인 상태 변경 실패 [{pluginName}]: {ex.Message}");
        }
    }


    public bool IsPluginGloballyEnabled(string pluginName)
    {
        return _stateManager.IsGloballyEnabled(pluginName);
    }

    #endregion


    #region 🔧 수정: 안전한 헬퍼 메서드들

    private int GetPluginRoomUsageCountSafe(string pluginName)
    {
        try
        {
            if (string.IsNullOrEmpty(pluginName))
                return 0;

            var enabledRooms = GetEnabledRoomsForPluginSafe(pluginName);
            return enabledRooms?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private int GetTotalRoomCountSafe()
    {
        try
        {
            var allKnownRooms = GetAllKnownRoomsSafe();
            return allKnownRooms?.Count ?? 10; // 폴백 값
        }
        catch
        {
            return 10; // 폴백 값
        }
    }

    private string GetPluginFilePathSafe(IPlugin plugin)
    {
        try
        {
            if (plugin == null) return "Unknown";

            var assembly = plugin.GetType().Assembly;
            var location = assembly.Location;
            return !string.IsNullOrEmpty(location) ? Path.GetFileName(location) : "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private string GetRoleDisplayNameSafe(UserRole role)
    {
        try
        {
            return role.GetDisplayName();
        }
        catch
        {
            return "사용자"; // 기본값
        }
    }

    private List<string>? GetEnabledRoomsForPluginSafe(string pluginName)
    {
        try
        {
            if (string.IsNullOrEmpty(pluginName))
                return new List<string>();

            var enabledRooms = new List<string>();
            var allRooms = GetAllKnownRoomsSafe();

            if (allRooms == null || allRooms.Count == 0)
                return enabledRooms;

            // 🔧 수정: foreach 사용 (LINQ 대신)
            foreach (var roomId in allRooms)
            {
                try
                {
                    if (string.IsNullOrEmpty(roomId)) continue;

                    if (_stateManager.IsRoomEnabled(roomId, pluginName))
                    {
                        enabledRooms.Add(roomId);
                    }
                }
                catch
                {
                    // 개별 방 확인 실패 시 무시
                    continue;
                }
            }

            return enabledRooms;
        }
        catch
        {
            return new List<string>();
        }
    }

    private List<string>? GetAllKnownRoomsSafe()
    {
        try
        {
            // 🔧 수정: 간단한 기본 방 목록 반환 (복잡한 쿼리 회피)
            var basicRooms = new List<string> { "18447954271650616" }; // 예시 방 ID

            // 추가 방들이 있다면 안전하게 추가
            try
            {
                // 실제 구현에서는 안전한 방법으로 방 목록을 가져와야 함
                // 복잡한 LINQ 쿼리나 순환 참조 가능성이 있는 코드 피하기
            }
            catch
            {
                // 추가 방 조회 실패 시 기본 방만 사용
            }

            return basicRooms;
        }
        catch
        {
            return new List<string> { "18447954271650616" };
        }
    }

    public List<PluginDisplayInfo> GetFilteredPluginDisplayInfos(string? category = null, bool? enabledOnly = null, string? searchText = null)
    {
        string queryKey = $"GetFilteredPlugins_{category}_{enabledOnly}_{searchText}"; // 🔧 수정: const 제거

        lock (_queryLock)
        {
            if (_executingQueries.Contains(queryKey))
            {
                Console.WriteLine($"순환 쿼리 방지: {queryKey}");
                return new List<PluginDisplayInfo>();
            }

            _executingQueries.Add(queryKey);
        }

        try
        {
            var plugins = GetPluginDisplayInfos();
            if (plugins == null || plugins.Count == 0)
                return new List<PluginDisplayInfo>();

            var filteredPlugins = new List<PluginDisplayInfo>();

            foreach (var plugin in plugins)
            {
                try
                {
                    if (plugin == null) continue;

                    bool shouldInclude = true;

                    if (!string.IsNullOrEmpty(category) &&
                        category != "모든 카테고리" &&
                        category != "전체 카테고리")
                    {
                        if (!string.Equals(plugin.Category, category, StringComparison.OrdinalIgnoreCase))
                        {
                            shouldInclude = false;
                        }
                    }

                    if (enabledOnly == true && !plugin.IsGloballyEnabled)
                    {
                        shouldInclude = false;
                    }

                    if (!string.IsNullOrEmpty(searchText) && shouldInclude)
                    {
                        var searchLower = searchText.ToLowerInvariant();
                        var matchesSearch =
                            (plugin.DisplayName?.ToLowerInvariant().Contains(searchLower) ?? false) ||
                            (plugin.Description?.ToLowerInvariant().Contains(searchLower) ?? false) ||
                            (plugin.Category?.ToLowerInvariant().Contains(searchLower) ?? false) ||
                            (plugin.Name?.ToLowerInvariant().Contains(searchLower) ?? false);

                        if (!matchesSearch)
                        {
                            shouldInclude = false;
                        }
                    }

                    if (shouldInclude)
                    {
                        filteredPlugins.Add(plugin);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"플러그인 필터링 실패 [{plugin?.Name}]: {ex.Message}");
                    continue;
                }
            }

            return filteredPlugins;
        }
        catch (Exception ex)
        {
            PluginError?.Invoke($"플러그인 필터링 실패: {ex.Message}");
            return new List<PluginDisplayInfo>();
        }
        finally
        {
            lock (_queryLock)
            {
                _executingQueries.Remove(queryKey);
            }
        }
    }

    #endregion


    #region 방별 플러그인 관리

    /// <summary>
    /// 특정 방에서의 플러그인 상태 조회
    /// </summary>
    public bool IsPluginEnabledInRoom(string pluginName, string roomId)
    {
        return _stateManager.IsRoomEnabled(roomId, pluginName);
    }

    /// <summary>
    /// 방별 플러그인 활성화/비활성화
    /// </summary>
    public void TogglePluginRoomState(string pluginName, string roomId, bool enabled)
    {
        try
        {
            // 🔧 추가: 현재 상태와 같으면 변경하지 않음
            var currentState = _stateManager.IsRoomEnabled(roomId, pluginName);
            if (currentState == enabled)
            {
                Console.WriteLine($"플러그인 '{pluginName}'의 방별 상태가 이미 {(enabled ? "활성화" : "비활성화")}되어 있음");
                return;
            }

            _stateManager.SetRoomEnabled(roomId, pluginName, enabled);
        }
        catch (Exception ex)
        {
            PluginError?.Invoke($"방별 플러그인 상태 변경 실패 [{pluginName}@{roomId}]: {ex.Message}");
        }
    }

    /// <summary>
    /// 방별 플러그인 설정 조회
    /// </summary>
    public PluginRoomSettings? GetPluginRoomSettings(string pluginName, string roomId)
    {
        return _stateManager.GetRoomSettings(roomId, pluginName);
    }

    /// <summary>
    /// 방에서 사용 중인 플러그인 목록 조회
    /// </summary>
    public List<PluginDisplayInfo> GetEnabledPluginsInRoom(string roomId)
    {
        var allPlugins = GetPluginDisplayInfos();
        return allPlugins.Where(p => _stateManager.IsRoomEnabled(roomId, p.Name)).ToList();
    }

    /// <summary>
    /// 플러그인이 사용되는 방 목록 조회 (대체 구현)
    /// </summary>
    public List<string> GetRoomsUsingPlugin(string pluginName)
    {
        return GetEnabledRoomsForPlugin(pluginName);
    }

    #endregion

    #region 플러그인 정보 조회

    public PluginDetailInfo? GetPluginDetailInfo(string pluginName)
    {
        try
        {
            var plugin = _pluginManager.GetLoadedPlugins().FirstOrDefault(p => p.Name == pluginName);
            if (plugin == null) return null;

            var configSchema = plugin.GetConfigSchema();
            var globalConfig = _stateManager.GetGlobalConfig<object>(pluginName);
            var enabledRooms = GetEnabledRoomsForPlugin(pluginName);

            return new PluginDetailInfo
            {
                Plugin = plugin,
                ConfigSchema = configSchema,
                GlobalConfig = globalConfig,
                IsGloballyEnabled = _stateManager.IsGloballyEnabled(pluginName),
                EnabledRooms = enabledRooms,
                RoomUsageCount = enabledRooms.Count,
                FilePath = GetPluginFilePath(plugin)
            };
        }
        catch (Exception ex)
        {
            PluginError?.Invoke($"플러그인 상세 정보 조회 실패 [{pluginName}]: {ex.Message}");
            return null;
        }
    }

    public List<string> GetPluginCategories()
    {
        try
        {
            var plugins = _pluginManager.GetLoadedPlugins();
            var categories = plugins.Select(p => p.Category)
                                   .Distinct()
                                   .Where(c => !string.IsNullOrEmpty(c))
                                   .OrderBy(c => c)
                                   .ToList();

            // 기본 카테고리들 추가 (없으면)
            var defaultCategories = new[] { "자동응답", "모니터링", "관리", "유틸리티", "게임", "기타" };
            foreach (var defaultCategory in defaultCategories)
            {
                if (!categories.Contains(defaultCategory))
                {
                    categories.Add(defaultCategory);
                }
            }

            return categories;
        }
        catch
        {
            return new List<string> { "자동응답", "모니터링", "관리", "유틸리티", "게임", "기타" };
        }
    }

    public List<PluginDisplayInfo> GetPluginsByCategory(string category)
    {
        return GetPluginDisplayInfos().Where(p => p.Category == category).ToList();
    }

    /// <summary>
    /// 플러그인 검색
    /// </summary>
    public List<PluginDisplayInfo> SearchPlugins(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return GetPluginDisplayInfos();
        }

        return GetPluginDisplayInfos().Where(p =>
            p.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
            p.Description.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
            p.Category.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase)
        ).ToList();
    }

    #endregion

    #region 플러그인 상태 통계

    public PluginStatistics GetPluginStatistics()
    {
        try
        {
            var plugins = _pluginManager.GetLoadedPlugins();
            var totalPlugins = plugins.Count;
            var enabledPlugins = plugins.Count(p => _stateManager.IsGloballyEnabled(p.Name));
            var categoryCounts = plugins.GroupBy(p => p.Category)
                                      .ToDictionary(g => g.Key, g => g.Count());
            var roomsWithSettings = GetRoomsWithCustomSettings();

            return new PluginStatistics
            {
                TotalPlugins = totalPlugins,
                EnabledPlugins = enabledPlugins,
                DisabledPlugins = totalPlugins - enabledPlugins,
                CategoryCounts = categoryCounts,
                RoomsWithCustomSettings = roomsWithSettings.Count,
                TotalRooms = GetTotalRoomCount()
            };
        }
        catch (Exception ex)
        {
            PluginError?.Invoke($"플러그인 통계 조회 실패: {ex.Message}");
            return new PluginStatistics();
        }
    }

    /// <summary>
    /// 상세 플러그인 통계 조회
    /// </summary>
    public DetailedPluginStatistics GetDetailedPluginStatistics()
    {
        try
        {
            var plugins = _pluginManager.GetLoadedPlugins();
            var allRooms = GetAllKnownRooms();

            var stats = new DetailedPluginStatistics
            {
                TotalPlugins = plugins.Count,
                LoadedPlugins = plugins.Count,
                EnabledPlugins = plugins.Count(p => _stateManager.IsGloballyEnabled(p.Name)),
                CategoryBreakdown = plugins.GroupBy(p => p.Category)
                                         .ToDictionary(g => g.Key, g => g.Count()),
                RoleBreakdown = plugins.GroupBy(p => p.RequiredRole)
                                     .ToDictionary(g => g.Key.GetDisplayName(), g => g.Count()),
                RoomUsageStats = new Dictionary<string, int>()
            };

            // 방별 사용 통계
            foreach (var plugin in plugins)
            {
                var roomCount = GetPluginRoomUsageCount(plugin.Name);
                stats.RoomUsageStats[plugin.Name] = roomCount;
            }

            // 가장 많이 사용되는 플러그인 순으로 정렬
            stats.MostUsedPlugins = stats.RoomUsageStats
                .OrderByDescending(kvp => kvp.Value)
                .Take(10)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            return stats;
        }
        catch (Exception ex)
        {
            PluginError?.Invoke($"상세 플러그인 통계 조회 실패: {ex.Message}");
            return new DetailedPluginStatistics();
        }
    }

    #endregion

    #region 대량 작업

    /// <summary>
    /// 모든 플러그인 전역 활성화
    /// </summary>
    public int EnableAllPluginsGlobally()
    {
        try
        {
            var plugins = _pluginManager.GetLoadedPlugins();
            var enabledCount = 0;

            foreach (var plugin in plugins.Where(p => !_stateManager.IsGloballyEnabled(p.Name)))
            {
                try
                {
                    _stateManager.SetGlobalEnabled(plugin.Name, true);
                    enabledCount++;
                }
                catch (Exception ex)
                {
                    PluginError?.Invoke($"플러그인 '{plugin.DisplayName}' 활성화 실패: {ex.Message}");
                }
            }

            if (enabledCount > 0)
            {
                PluginListChanged?.Invoke();
            }

            return enabledCount;
        }
        catch (Exception ex)
        {
            PluginError?.Invoke($"전체 활성화 실패: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 모든 플러그인 전역 비활성화
    /// </summary>
    public int DisableAllPluginsGlobally()
    {
        try
        {
            var plugins = _pluginManager.GetLoadedPlugins();
            var disabledCount = 0;

            foreach (var plugin in plugins.Where(p => _stateManager.IsGloballyEnabled(p.Name)))
            {
                try
                {
                    _stateManager.SetGlobalEnabled(plugin.Name, false);
                    disabledCount++;
                }
                catch (Exception ex)
                {
                    PluginError?.Invoke($"플러그인 '{plugin.DisplayName}' 비활성화 실패: {ex.Message}");
                }
            }

            if (disabledCount > 0)
            {
                PluginListChanged?.Invoke();
            }

            return disabledCount;
        }
        catch (Exception ex)
        {
            PluginError?.Invoke($"전체 비활성화 실패: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 카테고리별 플러그인 활성화/비활성화
    /// </summary>
    public int TogglePluginsByCategory(string category, bool enabled)
    {
        try
        {
            var plugins = GetPluginsByCategory(category);
            var changedCount = 0;

            foreach (var plugin in plugins)
            {
                if (_stateManager.IsGloballyEnabled(plugin.Name) != enabled)
                {
                    try
                    {
                        _stateManager.SetGlobalEnabled(plugin.Name, enabled);
                        changedCount++;
                    }
                    catch (Exception ex)
                    {
                        PluginError?.Invoke($"플러그인 '{plugin.DisplayName}' 상태 변경 실패: {ex.Message}");
                    }
                }
            }

            if (changedCount > 0)
            {
                PluginListChanged?.Invoke();
            }

            return changedCount;
        }
        catch (Exception ex)
        {
            PluginError?.Invoke($"카테고리별 상태 변경 실패: {ex.Message}");
            return 0;
        }
    }

    #endregion

    #region 도움말 시스템

    public string GetPluginHelpText(string pluginName)
    {
        var plugin = _pluginManager.GetLoadedPlugins().FirstOrDefault(p => p.Name == pluginName);
        if (plugin == null) return $"'{pluginName}' 플러그인을 찾을 수 없습니다.";

        var helpText = $"📦 {plugin.DisplayName} v{plugin.Version}\n\n";
        helpText += $"📝 설명:\n{plugin.Description}\n\n";
        helpText += $"🏷️ 카테고리: {plugin.Category}\n";
        helpText += $"👤 필요 권한: {plugin.RequiredRole.GetDisplayName()}\n"; // GetDisplayName 확장 메서드 사용

        if (plugin.Dependencies?.Any() == true)
        {
            helpText += $"🔗 의존성: {string.Join(", ", plugin.Dependencies)}\n";
        }

        if (plugin.SupportsRoomSettings)
        {
            helpText += "⚙️ 방별 설정 지원\n";
        }

        // 사용 통계
        var roomCount = GetPluginRoomUsageCount(plugin.Name);
        var totalRooms = GetTotalRoomCount();
        helpText += $"📊 사용 현황: {roomCount}/{totalRooms}개 방에서 사용 중\n";

        // 설정 스키마 정보
        try
        {
            var schema = plugin.GetConfigSchema();
            if (schema.Fields.Any())
            {
                helpText += "\n🛠️ 설정 옵션:\n";
                foreach (var field in schema.Fields)
                {
                    helpText += $"• {field.DisplayName}: {field.Description}\n";
                }
            }
        }
        catch
        {
            // 스키마 조회 실패 시 무시
        }

        return helpText;
    }

    /// <summary>
    /// 모든 플러그인의 요약 도움말
    /// </summary>
    public string GetAllPluginsHelpSummary()
    {
        try
        {
            var plugins = _pluginManager.GetLoadedPlugins();
            var summary = $"📚 로드된 플러그인 목록 ({plugins.Count}개)\n\n";

            var categorizedPlugins = plugins.GroupBy(p => p.Category)
                                           .OrderBy(g => g.Key);

            foreach (var categoryGroup in categorizedPlugins)
            {
                summary += $"🏷️ {categoryGroup.Key}:\n";

                foreach (var plugin in categoryGroup.OrderBy(p => p.DisplayName))
                {
                    var enabledStatus = _stateManager.IsGloballyEnabled(plugin.Name) ? "✅" : "❌";
                    var roomCount = GetPluginRoomUsageCount(plugin.Name);

                    summary += $"  {enabledStatus} {plugin.DisplayName} v{plugin.Version}";
                    if (roomCount > 0)
                    {
                        summary += $" ({roomCount}개 방)";
                    }
                    summary += "\n";
                }
                summary += "\n";
            }

            return summary;
        }
        catch (Exception ex)
        {
            return $"도움말 생성 실패: {ex.Message}";
        }
    }

    #endregion


    // 🔧 추가: 이벤트 순환 방지 플래그들
    private bool _isProcessingGlobalStateChange = false;
    private bool _isProcessingRoomStateChange = false;
    private bool _isProcessingConfigChange = false;

    // 🔧 추가: 최근 처리된 이벤트 추적 (중복 방지)
    private readonly Dictionary<string, DateTime> _recentEvents = new();
    private const int EVENT_DEBOUNCE_MS = 100; // 100ms 내 중복 이벤트 무시


    #region 이벤트 핸들러 (순환 방지 적용)

    private void OnGlobalStateChanged(string pluginName, bool enabled)
    {
        // 🔧 추가: 순환 방지 및 디바운싱
        if (_isProcessingGlobalStateChange)
        {
            Console.WriteLine($"GlobalStateChanged 이벤트 순환 방지: {pluginName}");
            return;
        }

        var eventKey = $"global_{pluginName}_{enabled}";
        if (IsRecentEvent(eventKey))
        {
            Console.WriteLine($"GlobalStateChanged 중복 이벤트 무시: {pluginName}");
            return;
        }

        try
        {
            _isProcessingGlobalStateChange = true;
            RecordEvent(eventKey);

            PluginStateChanged?.Invoke(pluginName, enabled);

            // 🔧 수정: PluginListChanged 이벤트 발생 조건 제한
            if (!_isProcessingRoomStateChange && !_isProcessingConfigChange)
            {
                PluginListChanged?.Invoke();
            }
        }
        finally
        {
            _isProcessingGlobalStateChange = false;
        }
    }

    // 🔧 수정: 3개 매개변수 버전으로 변경
    private void OnRoomStateChanged(string roomId, string pluginName, bool enabled)
    {
        if (_isProcessingRoomStateChange)
        {
            Console.WriteLine($"RoomStateChanged 이벤트 순환 방지: {roomId}-{pluginName}");
            return;
        }

        var eventKey = $"room_{roomId}_{pluginName}_{enabled}";
        if (IsRecentEvent(eventKey))
        {
            Console.WriteLine($"RoomStateChanged 중복 이벤트 무시: {roomId}-{pluginName}");
            return;
        }

        try
        {
            _isProcessingRoomStateChange = true;
            RecordEvent(eventKey);

            // 🔧 수정: 전역 상태 변경 중이 아닐 때만 UI 업데이트
            if (!_isProcessingGlobalStateChange && !_isProcessingConfigChange)
            {
                PluginListChanged?.Invoke();
            }
        }
        finally
        {
            _isProcessingRoomStateChange = false;
        }
    }

    private void OnPluginConfigChanged(string pluginName)
    {
        if (_isProcessingConfigChange)
        {
            Console.WriteLine($"ConfigChanged 이벤트 순환 방지: {pluginName}");
            return;
        }

        var eventKey = $"config_{pluginName}";
        if (IsRecentEvent(eventKey))
        {
            Console.WriteLine($"ConfigChanged 중복 이벤트 무시: {pluginName}");
            return;
        }

        try
        {
            _isProcessingConfigChange = true;
            RecordEvent(eventKey);

            if (!_isProcessingGlobalStateChange && !_isProcessingRoomStateChange)
            {
                PluginListChanged?.Invoke();
            }
        }
        finally
        {
            _isProcessingConfigChange = false;
        }
    }

    #endregion

    #region 🔧 추가: 이벤트 디바운싱 및 중복 방지

    private bool IsRecentEvent(string eventKey)
    {
        if (_recentEvents.TryGetValue(eventKey, out var lastTime))
        {
            var timeSinceLastEvent = DateTime.Now - lastTime;
            return timeSinceLastEvent.TotalMilliseconds < EVENT_DEBOUNCE_MS;
        }
        return false;
    }

    private void RecordEvent(string eventKey)
    {
        _recentEvents[eventKey] = DateTime.Now;

        // 🔧 추가: 오래된 이벤트 기록 정리 (메모리 누수 방지)
        if (_recentEvents.Count > 100)
        {
            var cutoffTime = DateTime.Now.AddMinutes(-1);
            var keysToRemove = _recentEvents
                .Where(kvp => kvp.Value < cutoffTime)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _recentEvents.Remove(key);
            }
        }
    }

    #endregion



    #region 대체 구현 메서드들 (PluginStateManager에 없는 메서드들)

    /// <summary>
    /// 플러그인을 사용하는 방의 수 조회 (대체 구현)
    /// </summary>
    private int GetPluginRoomUsageCount(string pluginName)
    {
        try
        {
            // PluginStateManager에 메서드가 있다면 그것을 사용하고, 없다면 대체 구현
            var enabledRooms = GetEnabledRoomsForPlugin(pluginName);
            return enabledRooms.Count;
        }
        catch
        {
            return 0; // 오류 시 0 반환
        }
    }

    /// <summary>
    /// 플러그인이 활성화된 방 목록 조회 (대체 구현)
    /// </summary>
    private List<string> GetEnabledRoomsForPlugin(string pluginName)
    {
        try
        {
            // 임시 구현: 실제로는 PluginStateManager에서 방별 상태를 조회해야 함
            // 여기서는 빈 리스트를 반환하거나 기본값을 제공
            var enabledRooms = new List<string>();

            // 알려진 방 목록을 가져와서 각 방에서 플러그인이 활성화되었는지 확인
            var allRooms = GetAllKnownRooms();
            foreach (var roomId in allRooms)
            {
                try
                {
                    if (_stateManager.IsRoomEnabled(roomId, pluginName))
                    {
                        enabledRooms.Add(roomId);
                    }
                }
                catch
                {
                    // 개별 방 확인 실패 시 무시
                }
            }

            return enabledRooms;
        }
        catch
        {
            return new List<string>(); // 오류 시 빈 리스트 반환
        }
    }

    #endregion

    #region 유틸리티 메서드

    private string GetPluginFilePath(IPlugin plugin)
    {
        try
        {
            var assembly = plugin.GetType().Assembly;
            var location = assembly.Location;
            return !string.IsNullOrEmpty(location) ? Path.GetFileName(location) : "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private int GetTotalRoomCount()
    {
        // 실제로는 WebSocketService나 다른 서비스에서 방 목록을 가져와야 함
        // 여기서는 StateManager에서 추정값 계산
        try
        {
            var allKnownRooms = GetAllKnownRooms();
            return allKnownRooms.Count;
        }
        catch
        {
            return 10; // 폴백 값
        }
    }

    private List<string> GetRoomsWithCustomSettings()
    {
        try
        {
            // StateManager에서 방별 설정이 있는 방 목록 조회
            var roomsWithSettings = new HashSet<string>();
            var plugins = _pluginManager.GetLoadedPlugins();

            foreach (var plugin in plugins)
            {
                var enabledRooms = GetEnabledRoomsForPlugin(plugin.Name);
                foreach (var room in enabledRooms)
                {
                    roomsWithSettings.Add(room);
                }
            }

            return roomsWithSettings.ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private List<string> GetAllKnownRooms()
    {
        try
        {
            var allRooms = new HashSet<string>();
            var plugins = _pluginManager.GetLoadedPlugins();

            foreach (var plugin in plugins)
            {
                var rooms = GetEnabledRoomsForPlugin(plugin.Name);
                foreach (var room in rooms)
                {
                    allRooms.Add(room);
                }
            }

            // 최소 기본값 보장
            if (allRooms.Count == 0)
            {
                allRooms.Add("18447954271650616"); // 예시 방 ID
            }

            return allRooms.ToList();
        }
        catch
        {
            return new List<string> { "18447954271650616" };
        }
    }

    #endregion
}

// UI 표시용 플러그인 정보
public class PluginDisplayInfo
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool IsGloballyEnabled { get; set; }
    public int RoomUsageCount { get; set; }
    public int TotalRoomCount { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string RequiredRole { get; set; } = string.Empty;
    public bool SupportsRoomSettings { get; set; }
    public List<string> Dependencies { get; set; } = new();
    public DateTime LoadTime { get; set; }

    // UI 편의 속성
    public string UsageText => $"{RoomUsageCount}/{TotalRoomCount}개 방에서 사용중";
    public string StatusText => IsGloballyEnabled ? "활성화" : "비활성화";
}

// 플러그인 상세 정보
public class PluginDetailInfo
{
    public IPlugin? Plugin { get; set; }
    public PluginConfigSchema? ConfigSchema { get; set; }
    public object? GlobalConfig { get; set; }
    public bool IsGloballyEnabled { get; set; }
    public List<string> EnabledRooms { get; set; } = new();
    public int RoomUsageCount { get; set; }
    public string FilePath { get; set; } = string.Empty;
}

// 플러그인 통계
public class PluginStatistics
{
    public int TotalPlugins { get; set; }
    public int EnabledPlugins { get; set; }
    public int DisabledPlugins { get; set; }
    public Dictionary<string, int> CategoryCounts { get; set; } = new();
    public int RoomsWithCustomSettings { get; set; }
    public int TotalRooms { get; set; }
}

// 상세 플러그인 통계
public class DetailedPluginStatistics
{
    public int TotalPlugins { get; set; }
    public int LoadedPlugins { get; set; }
    public int EnabledPlugins { get; set; }
    public Dictionary<string, int> CategoryBreakdown { get; set; } = new();
    public Dictionary<string, int> RoleBreakdown { get; set; } = new();
    public Dictionary<string, int> RoomUsageStats { get; set; } = new();
    public Dictionary<string, int> MostUsedPlugins { get; set; } = new();
}