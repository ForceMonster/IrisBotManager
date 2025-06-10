using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using IrisBotManager.Core.Models;

namespace IrisBotManager.Core.Services
{
    public class RoomSettingsService
    {
        private readonly string _roomSettingsPath;
        private readonly PluginStateManager _stateManager;
        private readonly Dictionary<string, RoomSettingsInfo> _roomSettingsCache = new();

        // 🔧 추가: 기본 방 목록 (플러그인 활성화가 허용된 방들)
        private readonly HashSet<string> _defaultEnabledRooms = new()
        {
            // 여기에 플러그인이 기본적으로 활성화되어야 하는 방 ID들을 추가
            // 예: "관리자방ID", "테스트방ID" 등
        };

        // 이벤트
        public event Action<string>? RoomSettingsChanged;
        public event Action<string>? RoomSettingsError;

        public RoomSettingsService(PluginStateManager stateManager)
        {
            _stateManager = stateManager;

            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            _roomSettingsPath = Path.Combine(baseDirectory, "data", "room_settings");
            Directory.CreateDirectory(_roomSettingsPath);

            LoadAllRoomSettings();
        }

        #region 방 목록 관리

        /// <summary>
        /// 사용 가능한 방 목록 조회
        /// </summary>
        public List<string> GetAvailableRooms()
        {
            try
            {
                var roomFiles = Directory.GetFiles(_roomSettingsPath, "*.json");
                var roomIds = roomFiles.Select(f => Path.GetFileNameWithoutExtension(f))
                                      .Where(id => !string.IsNullOrEmpty(id))
                                      .ToList();

                return roomIds.OrderBy(id => id).ToList();
            }
            catch (Exception ex)
            {
                RoomSettingsError?.Invoke($"방 목록 조회 실패: {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// 활성 방 목록 조회 (설정이 있는 방들)
        /// </summary>
        public List<string> GetConfiguredRooms()
        {
            return _roomSettingsCache.Keys.ToList();
        }

        #endregion

        #region 🔧 수정된 방별 플러그인 설정 관리

        /// <summary>
        /// 🔧 수정: 방별 플러그인 설정 조회 (기본 설정 개선)
        /// </summary>
        public RoomSettingsInfo GetRoomPluginSettings(string roomId)
        {
            if (_roomSettingsCache.TryGetValue(roomId, out var cached))
            {
                return cached;
            }

            // 캐시에 없으면 파일에서 로드
            var roomSettings = LoadRoomSettings(roomId);

            // 🔧 추가: 새로운 방인지 확인하고 기본 설정 적용
            if (IsNewRoom(roomSettings))
            {
                ApplyDefaultRoomSettings(roomSettings, roomId);
            }

            _roomSettingsCache[roomId] = roomSettings;
            return roomSettings;
        }

        /// <summary>
        /// 🔧 추가: 새로운 방인지 확인
        /// </summary>
        private bool IsNewRoom(RoomSettingsInfo roomSettings)
        {
            // 플러그인 설정이 없거나, 생성 시간이 최근이면 새로운 방으로 판단
            return roomSettings.PluginSettings.Count == 0 ||
                   (DateTime.Now - roomSettings.CreatedAt).TotalMinutes < 5;
        }

        /// <summary>
        /// 🔧 추가: 새로운 방에 기본 설정 적용
        /// </summary>
        private void ApplyDefaultRoomSettings(RoomSettingsInfo roomSettings, string roomId)
        {
            try
            {
                // 모든 전역 플러그인 가져오기
                var globalPlugins = GetAllGlobalPlugins();

                foreach (var globalPlugin in globalPlugins)
                {
                    // 🔧 핵심 수정: 기본적으로 모든 플러그인을 비활성화로 설정
                    var isDefaultEnabled = _defaultEnabledRooms.Contains(roomId);

                    var roomPluginSetting = new RoomPluginDisplayInfo
                    {
                        PluginName = globalPlugin.Name,
                        DisplayName = globalPlugin.DisplayName,
                        Description = globalPlugin.Description,
                        Category = globalPlugin.Category,
                        IsEnabled = isDefaultEnabled, // 🔧 기본 비활성화
                        HasCustomSettings = false,
                        CustomConfig = new Dictionary<string, object>()
                    };

                    roomSettings.PluginSettings.Add(roomPluginSetting);
                }

                // 기본 설정 저장
                roomSettings.LastModified = DateTime.Now;
                roomSettings.ModifiedBy = "시스템 (기본 설정)";
                SaveRoomSettings(roomSettings);

                // StateManager에도 반영
                foreach (var plugin in roomSettings.PluginSettings)
                {
                    _stateManager.SetRoomEnabled(roomId, plugin.PluginName, plugin.IsEnabled);
                }

                Console.WriteLine($"방 {roomId}에 기본 설정 적용됨 (플러그인 {roomSettings.PluginSettings.Count}개 모두 비활성화)");
            }
            catch (Exception ex)
            {
                RoomSettingsError?.Invoke($"기본 설정 적용 실패 (방 {roomId}): {ex.Message}");
            }
        }

        /// <summary>
        /// 🔧 추가: 모든 전역 플러그인 정보 가져오기 (임시 구현)
        /// </summary>
        private List<PluginDisplayInfo> GetAllGlobalPlugins()
        {
            // 실제 구현에서는 PluginUIService에서 가져와야 함
            // 여기서는 임시로 빈 리스트 반환
            try
            {
                // 플러그인 정보를 가져올 수 없는 경우를 대비한 안전한 구현
                return new List<PluginDisplayInfo>();
            }
            catch
            {
                return new List<PluginDisplayInfo>();
            }
        }

        /// <summary>
        /// 전역 플러그인 목록과 동기화하여 완전한 설정 정보 생성
        /// </summary>
        public RoomSettingsInfo GetRoomPluginSettingsWithGlobalSync(string roomId, List<PluginDisplayInfo> globalPlugins)
        {
            var roomSettings = GetRoomPluginSettings(roomId);

            // 전역 플러그인 목록과 동기화
            var existingPluginNames = roomSettings.PluginSettings.Select(p => p.PluginName).ToHashSet();

            foreach (var globalPlugin in globalPlugins)
            {
                if (!existingPluginNames.Contains(globalPlugin.Name))
                {
                    // 🔧 수정: 새로 추가되는 플러그인도 기본 비활성화
                    var isDefaultEnabled = _defaultEnabledRooms.Contains(roomId);

                    var roomPluginSetting = new RoomPluginDisplayInfo
                    {
                        PluginName = globalPlugin.Name,
                        DisplayName = globalPlugin.DisplayName,
                        Description = globalPlugin.Description,
                        Category = globalPlugin.Category,
                        IsEnabled = isDefaultEnabled, // 🔧 기본 비활성화 
                        HasCustomSettings = false,
                        CustomConfig = new Dictionary<string, object>()
                    };
                    roomSettings.PluginSettings.Add(roomPluginSetting);
                }
            }

            // 존재하지 않는 플러그인 제거 (삭제된 플러그인)
            roomSettings.PluginSettings = roomSettings.PluginSettings
                .Where(p => globalPlugins.Any(g => g.Name == p.PluginName))
                .ToList();

            // 캐시 업데이트
            _roomSettingsCache[roomId] = roomSettings;

            return roomSettings;
        }

        /// <summary>
        /// 🔧 추가: 특정 방을 기본 활성화 방으로 추가
        /// </summary>
        public void AddDefaultEnabledRoom(string roomId)
        {
            _defaultEnabledRooms.Add(roomId);
        }

        /// <summary>
        /// 🔧 추가: 특정 방을 기본 활성화 방에서 제거
        /// </summary>
        public void RemoveDefaultEnabledRoom(string roomId)
        {
            _defaultEnabledRooms.Remove(roomId);
        }

        /// <summary>
        /// 🔧 추가: 방이 기본 활성화 방인지 확인
        /// </summary>
        public bool IsDefaultEnabledRoom(string roomId)
        {
            return _defaultEnabledRooms.Contains(roomId);
        }

        #endregion

        #region 방별 플러그인 상태 제어

        /// <summary>
        /// 방별 플러그인 활성화/비활성화
        /// </summary>
        public void SetRoomPluginEnabled(string roomId, string pluginName, bool enabled)
        {
            try
            {
                var roomSettings = GetRoomPluginSettings(roomId);
                var pluginSetting = roomSettings.PluginSettings.FirstOrDefault(p => p.PluginName == pluginName);

                if (pluginSetting == null)
                {
                    // 새 플러그인 설정 생성
                    pluginSetting = new RoomPluginDisplayInfo
                    {
                        PluginName = pluginName,
                        DisplayName = pluginName,
                        IsEnabled = enabled,
                        HasCustomSettings = false
                    };
                    roomSettings.PluginSettings.Add(pluginSetting);
                }
                else
                {
                    pluginSetting.IsEnabled = enabled;
                }

                roomSettings.LastModified = DateTime.Now;
                roomSettings.ModifiedBy = "사용자";
                SaveRoomSettings(roomSettings);

                // StateManager에도 반영
                _stateManager.SetRoomEnabled(roomId, pluginName, enabled);

                RoomSettingsChanged?.Invoke(roomId);
            }
            catch (Exception ex)
            {
                RoomSettingsError?.Invoke($"플러그인 상태 변경 실패: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 방별 플러그인 설정 구성
        /// </summary>
        public void SetRoomPluginConfig(string roomId, string pluginName, Dictionary<string, object> config)
        {
            try
            {
                var roomSettings = GetRoomPluginSettings(roomId);
                var pluginSetting = roomSettings.PluginSettings.FirstOrDefault(p => p.PluginName == pluginName);

                if (pluginSetting == null)
                {
                    pluginSetting = new RoomPluginDisplayInfo
                    {
                        PluginName = pluginName,
                        DisplayName = pluginName,
                        IsEnabled = false, // 🔧 수정: 새 설정도 기본 비활성화
                        HasCustomSettings = config.Count > 0,
                        CustomConfig = config
                    };
                    roomSettings.PluginSettings.Add(pluginSetting);
                }
                else
                {
                    pluginSetting.CustomConfig = config;
                    pluginSetting.HasCustomSettings = config.Count > 0;
                }

                roomSettings.LastModified = DateTime.Now;
                roomSettings.ModifiedBy = "사용자";
                SaveRoomSettings(roomSettings);

                // StateManager에도 반영
                _stateManager.SetRoomConfig(roomId, pluginName, config);

                RoomSettingsChanged?.Invoke(roomId);
            }
            catch (Exception ex)
            {
                RoomSettingsError?.Invoke($"플러그인 설정 변경 실패: {ex.Message}");
                throw;
            }
        }

        #endregion

        #region 플러그인 설정 초기화

        /// <summary>
        /// 방별 플러그인 설정 초기화
        /// </summary>
        public void ResetRoomPluginSettings(string roomId, string pluginName)
        {
            try
            {
                var roomSettings = GetRoomPluginSettings(roomId);
                var pluginSetting = roomSettings.PluginSettings.FirstOrDefault(p => p.PluginName == pluginName);

                if (pluginSetting != null)
                {
                    // 🔧 수정: 초기화 시 비활성화 상태로 설정
                    pluginSetting.IsEnabled = IsDefaultEnabledRoom(roomId);
                    pluginSetting.HasCustomSettings = false;
                    pluginSetting.CustomConfig = new Dictionary<string, object>();

                    roomSettings.LastModified = DateTime.Now;
                    roomSettings.ModifiedBy = "사용자 (초기화)";
                    SaveRoomSettings(roomSettings);

                    // StateManager에도 반영
                    _stateManager.SetRoomEnabled(roomId, pluginName, pluginSetting.IsEnabled);
                    _stateManager.SetRoomConfig(roomId, pluginName, new Dictionary<string, object>());

                    RoomSettingsChanged?.Invoke(roomId);
                }
            }
            catch (Exception ex)
            {
                RoomSettingsError?.Invoke($"플러그인 설정 초기화 실패: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 방 전체 설정 초기화
        /// </summary>
        public void ResetAllRoomSettings(string roomId)
        {
            try
            {
                var filePath = GetRoomSettingsFilePath(roomId);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                _roomSettingsCache.Remove(roomId);

                // 🔧 추가: 초기화 후 새로 로드하여 기본 설정 적용
                GetRoomPluginSettings(roomId);

                RoomSettingsChanged?.Invoke(roomId);
            }
            catch (Exception ex)
            {
                RoomSettingsError?.Invoke($"방 설정 전체 초기화 실패: {ex.Message}");
                throw;
            }
        }

        #endregion

        #region 방별 대량 작업

        /// <summary>
        /// 방의 모든 플러그인 활성화
        /// </summary>
        public void EnableAllPluginsInRoom(string roomId, List<string> pluginNames)
        {
            try
            {
                var roomSettings = GetRoomPluginSettings(roomId);

                foreach (var pluginName in pluginNames)
                {
                    var pluginSetting = roomSettings.PluginSettings.FirstOrDefault(p => p.PluginName == pluginName);

                    if (pluginSetting == null)
                    {
                        pluginSetting = new RoomPluginDisplayInfo
                        {
                            PluginName = pluginName,
                            DisplayName = pluginName,
                            IsEnabled = true,
                            HasCustomSettings = false
                        };
                        roomSettings.PluginSettings.Add(pluginSetting);
                    }
                    else
                    {
                        pluginSetting.IsEnabled = true;
                    }

                    // StateManager에도 반영
                    _stateManager.SetRoomEnabled(roomId, pluginName, true);
                }

                roomSettings.LastModified = DateTime.Now;
                roomSettings.ModifiedBy = "사용자 (전체 활성화)";
                SaveRoomSettings(roomSettings);

                RoomSettingsChanged?.Invoke(roomId);
            }
            catch (Exception ex)
            {
                RoomSettingsError?.Invoke($"전체 활성화 실패: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 방의 모든 플러그인 비활성화
        /// </summary>
        public void DisableAllPluginsInRoom(string roomId, List<string> pluginNames)
        {
            try
            {
                var roomSettings = GetRoomPluginSettings(roomId);

                foreach (var pluginName in pluginNames)
                {
                    var pluginSetting = roomSettings.PluginSettings.FirstOrDefault(p => p.PluginName == pluginName);

                    if (pluginSetting != null)
                    {
                        pluginSetting.IsEnabled = false;
                    }

                    // StateManager에도 반영
                    _stateManager.SetRoomEnabled(roomId, pluginName, false);
                }

                roomSettings.LastModified = DateTime.Now;
                roomSettings.ModifiedBy = "사용자 (전체 비활성화)";
                SaveRoomSettings(roomSettings);

                RoomSettingsChanged?.Invoke(roomId);
            }
            catch (Exception ex)
            {
                RoomSettingsError?.Invoke($"전체 비활성화 실패: {ex.Message}");
                throw;
            }
        }

        #endregion

        #region 방간 설정 복사

        /// <summary>
        /// 방별 설정 복사
        /// </summary>
        public void CopyRoomSettings(string sourceRoomId, string targetRoomId)
        {
            try
            {
                var sourceSettings = GetRoomPluginSettings(sourceRoomId);
                var targetSettings = new RoomSettingsInfo
                {
                    RoomId = targetRoomId,
                    RoomName = $"방 {targetRoomId}",
                    RoomDescription = $"{sourceSettings.RoomName}에서 복사됨",
                    CreatedAt = DateTime.Now,
                    LastModified = DateTime.Now,
                    ModifiedBy = $"복사 (원본: {sourceRoomId})",
                    Version = 1,
                    PluginSettings = new List<RoomPluginDisplayInfo>()
                };

                // 플러그인 설정들을 복사
                foreach (var sourcePlugin in sourceSettings.PluginSettings)
                {
                    var copiedPlugin = new RoomPluginDisplayInfo
                    {
                        PluginName = sourcePlugin.PluginName,
                        DisplayName = sourcePlugin.DisplayName,
                        Description = sourcePlugin.Description,
                        Category = sourcePlugin.Category,
                        IsEnabled = sourcePlugin.IsEnabled,
                        HasCustomSettings = sourcePlugin.HasCustomSettings,
                        CustomConfig = new Dictionary<string, object>(sourcePlugin.CustomConfig),
                        LastModified = DateTime.Now,
                        ModifiedBy = "복사됨"
                    };
                    targetSettings.PluginSettings.Add(copiedPlugin);
                }

                SaveRoomSettings(targetSettings);
                _roomSettingsCache[targetRoomId] = targetSettings;

                // StateManager에도 반영
                foreach (var plugin in targetSettings.PluginSettings)
                {
                    _stateManager.SetRoomEnabled(targetRoomId, plugin.PluginName, plugin.IsEnabled);
                    if (plugin.HasCustomSettings)
                    {
                        _stateManager.SetRoomConfig(targetRoomId, plugin.PluginName, plugin.CustomConfig);
                    }
                }

                RoomSettingsChanged?.Invoke(targetRoomId);
            }
            catch (Exception ex)
            {
                RoomSettingsError?.Invoke($"방 설정 복사 실패: {ex.Message}");
                throw;
            }
        }

        #endregion

        #region 설정 내보내기/가져오기

        /// <summary>
        /// 방 설정 내보내기
        /// </summary>
        public string ExportRoomSettings(string roomId, string filePath)
        {
            try
            {
                var roomSettings = GetRoomPluginSettings(roomId);
                var json = JsonSerializer.Serialize(roomSettings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(filePath, json);
                return $"✅ 방 '{roomId}' 설정이 '{filePath}'로 내보내졌습니다.";
            }
            catch (Exception ex)
            {
                var errorMsg = $"설정 내보내기 실패: {ex.Message}";
                RoomSettingsError?.Invoke(errorMsg);
                return $"❌ {errorMsg}";
            }
        }

        /// <summary>
        /// 방 설정 가져오기 (기본 오버로드)
        /// </summary>
        public string ImportRoomSettings(string roomId, string filePath)
        {
            return ImportRoomSettings(roomId, filePath, false);
        }

        /// <summary>
        /// 방 설정 가져오기 (덮어쓰기 옵션 포함)
        /// </summary>
        public string ImportRoomSettings(string roomId, string filePath, bool overwriteExisting)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return "❌ 파일을 찾을 수 없습니다.";
                }

                var json = File.ReadAllText(filePath);
                var importedSettings = JsonSerializer.Deserialize<RoomSettingsInfo>(json);

                if (importedSettings != null)
                {
                    // 기존 설정이 있고 덮어쓰기를 허용하지 않는 경우
                    if (!overwriteExisting && _roomSettingsCache.ContainsKey(roomId))
                    {
                        // 새로운 설정만 추가 (병합 방식)
                        var existingSettings = GetRoomPluginSettings(roomId);
                        var existingPluginNames = existingSettings.PluginSettings.Select(p => p.PluginName).ToHashSet();

                        foreach (var importedPlugin in importedSettings.PluginSettings)
                        {
                            if (!existingPluginNames.Contains(importedPlugin.PluginName))
                            {
                                existingSettings.PluginSettings.Add(importedPlugin);
                            }
                        }

                        existingSettings.LastModified = DateTime.Now;
                        existingSettings.ModifiedBy = "사용자 (가져오기-병합)";
                        SaveRoomSettings(existingSettings);
                        _roomSettingsCache[roomId] = existingSettings;

                        var newCount = importedSettings.PluginSettings.Count(p => !existingPluginNames.Contains(p.PluginName));
                        RoomSettingsChanged?.Invoke(roomId);
                        return $"✅ 설정이 병합되었습니다. ({newCount}개 새 플러그인 추가)";
                    }
                    else
                    {
                        // 전체 덮어쓰기
                        importedSettings.RoomId = roomId; // 현재 방 ID로 변경
                        importedSettings.LastModified = DateTime.Now;
                        importedSettings.ModifiedBy = "사용자 (가져오기-덮어쓰기)";

                        SaveRoomSettings(importedSettings);
                        _roomSettingsCache[roomId] = importedSettings;

                        RoomSettingsChanged?.Invoke(roomId);
                        return $"✅ 설정이 성공적으로 가져와졌습니다. ({importedSettings.PluginSettings.Count}개 플러그인)";
                    }
                }

                return "❌ 설정 파일 형식이 올바르지 않습니다.";
            }
            catch (Exception ex)
            {
                var errorMsg = $"설정 가져오기 실패: {ex.Message}";
                RoomSettingsError?.Invoke(errorMsg);
                return $"❌ {errorMsg}";
            }
        }

        #endregion

        #region 파일 I/O 관련 메서드들

        private string GetRoomSettingsFilePath(string roomId)
        {
            return Path.Combine(_roomSettingsPath, $"{roomId}.json");
        }

        private RoomSettingsInfo LoadRoomSettings(string roomId)
        {
            var filePath = GetRoomSettingsFilePath(roomId);

            if (!File.Exists(filePath))
            {
                // 파일이 없으면 새로운 설정 생성
                return new RoomSettingsInfo
                {
                    RoomId = roomId,
                    RoomName = $"방 {roomId}",
                    CreatedAt = DateTime.Now,
                    LastModified = DateTime.Now,
                    ModifiedBy = "시스템",
                    PluginSettings = new List<RoomPluginDisplayInfo>()
                };
            }

            try
            {
                var json = File.ReadAllText(filePath);
                var settings = JsonSerializer.Deserialize<RoomSettingsInfo>(json);
                return settings ?? new RoomSettingsInfo
                {
                    RoomId = roomId,
                    RoomName = $"방 {roomId}",
                    CreatedAt = DateTime.Now,
                    LastModified = DateTime.Now,
                    ModifiedBy = "시스템",
                    PluginSettings = new List<RoomPluginDisplayInfo>()
                };
            }
            catch (Exception ex)
            {
                RoomSettingsError?.Invoke($"방 설정 로드 실패 (방 {roomId}): {ex.Message}");
                return new RoomSettingsInfo
                {
                    RoomId = roomId,
                    RoomName = $"방 {roomId}",
                    CreatedAt = DateTime.Now,
                    LastModified = DateTime.Now,
                    ModifiedBy = "시스템 (오류 복구)",
                    PluginSettings = new List<RoomPluginDisplayInfo>()
                };
            }
        }

        private void SaveRoomSettings(RoomSettingsInfo roomSettings)
        {
            try
            {
                var filePath = GetRoomSettingsFilePath(roomSettings.RoomId);
                var json = JsonSerializer.Serialize(roomSettings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                RoomSettingsError?.Invoke($"방 설정 저장 실패 (방 {roomSettings.RoomId}): {ex.Message}");
                throw;
            }
        }

        private void LoadAllRoomSettings()
        {
            try
            {
                var roomFiles = Directory.GetFiles(_roomSettingsPath, "*.json");
                foreach (var file in roomFiles)
                {
                    var roomId = Path.GetFileNameWithoutExtension(file);
                    if (!string.IsNullOrEmpty(roomId))
                    {
                        var settings = LoadRoomSettings(roomId);
                        _roomSettingsCache[roomId] = settings;
                    }
                }
            }
            catch (Exception ex)
            {
                RoomSettingsError?.Invoke($"전체 방 설정 로드 실패: {ex.Message}");
            }
        }

        #endregion

        #region 통계 관련 메서드들

        /// <summary>
        /// 방 통계 조회
        /// </summary>
        public RoomStatistics GetRoomStatistics(string roomId)
        {
            var roomSettings = GetRoomPluginSettings(roomId);

            return new RoomStatistics
            {
                RoomId = roomId,
                RoomName = roomSettings.RoomName,
                EnabledPlugins = roomSettings.PluginSettings.Count(p => p.IsEnabled),
                TotalPlugins = roomSettings.PluginSettings.Count,
                LastActivity = roomSettings.LastModified,
                PluginUsageStats = roomSettings.PluginSettings.ToDictionary(
                    p => p.PluginName,
                    p => p.IsEnabled ? 1 : 0)
            };
        }

        #endregion
    }

    #region 관련 모델 클래스들

    public class RoomStatistics
    {
        public string RoomId { get; set; } = "";
        public string RoomName { get; set; } = "";
        public int EnabledPlugins { get; set; }
        public int TotalPlugins { get; set; }
        public DateTime LastActivity { get; set; }
        public Dictionary<string, int> PluginUsageStats { get; set; } = new();
    }

    public class RoomCopyOptions
    {
        public bool CopyEnabledState { get; set; } = true;
        public bool CopyCustomConfigs { get; set; } = true;
        public List<string> ExcludePlugins { get; set; } = new();
        public List<string> IncludeOnlyPlugins { get; set; } = new();
    }

    #endregion
}