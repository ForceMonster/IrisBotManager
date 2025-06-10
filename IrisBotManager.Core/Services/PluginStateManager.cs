using IrisBotManager.Core.Models;
using IrisBotManager.Core.Plugin;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace IrisBotManager.Core.Services
{
    public class PluginStateManager
    {
        private readonly ConfigService _configService;
        private readonly object _stateLock = new object();

        // 전역 상태
        private readonly Dictionary<string, GlobalPluginState> _globalStates = new();

        // 방별 상태
        private readonly Dictionary<string, Dictionary<string, RoomPluginState>> _roomStates = new();

        // 🔧 추가: 기본 설정 관리
        private readonly HashSet<string> _knownPlugins = new();
        private DateTime _lastGlobalSettingsCheck = DateTime.MinValue;

        // 이벤트
        public event Action<string, bool>? GlobalStateChanged;
        public event Action<string, string, bool>? RoomStateChanged;  // ← 이 부분 수정 (roomId, pluginName, enabled)
        public event Action<string>? PluginConfigChanged;

        public PluginStateManager(ConfigService configService)
        {
            _configService = configService;
            LoadAllSettings();
        }

        #region 🔧 수정된 전역 설정 관리

        /// <summary>
        /// 🔧 수정: 플러그인 등록 및 기본 상태 설정
        /// </summary>
        public void RegisterPlugin(string pluginName, bool defaultEnabled = false)
        {
            lock (_stateLock)
            {
                // 🔧 추가: 플러그인을 알려진 플러그인 목록에 추가
                _knownPlugins.Add(pluginName);

                // 전역 상태가 없으면 기본 상태로 생성
                if (!_globalStates.ContainsKey(pluginName))
                {
                    _globalStates[pluginName] = new GlobalPluginState
                    {
                        PluginName = pluginName,
                        IsEnabled = defaultEnabled, // 🔧 기본값은 false (비활성화)
                        LastModified = DateTime.Now,
                        GlobalConfig = null
                    };

                    SaveGlobalSettings();
                    Console.WriteLine($"플러그인 '{pluginName}' 등록됨 (기본 상태: {(defaultEnabled ? "활성화" : "비활성화")})");
                }
            }
        }

        /// <summary>
        /// 🔧 추가: 모든 신규 플러그인을 기본 비활성화로 초기화
        /// </summary>
        public void InitializeNewPlugins(List<string> allPluginNames)
        {
            bool hasChanges = false;

            lock (_stateLock)
            {
                foreach (var pluginName in allPluginNames)
                {
                    if (!_globalStates.ContainsKey(pluginName))
                    {
                        _globalStates[pluginName] = new GlobalPluginState
                        {
                            PluginName = pluginName,
                            IsEnabled = false, // 🔧 기본 비활성화
                            LastModified = DateTime.Now,
                            GlobalConfig = null
                        };
                        hasChanges = true;

                        Console.WriteLine($"신규 플러그인 '{pluginName}' 비활성화 상태로 초기화됨");
                    }

                    _knownPlugins.Add(pluginName);
                }

                // 삭제된 플러그인 제거
                var deletedPlugins = _globalStates.Keys.Except(allPluginNames).ToList();
                foreach (var deletedPlugin in deletedPlugins)
                {
                    _globalStates.Remove(deletedPlugin);
                    _knownPlugins.Remove(deletedPlugin);
                    hasChanges = true;

                    Console.WriteLine($"삭제된 플러그인 '{deletedPlugin}' 상태 제거됨");
                }
            }

            if (hasChanges)
            {
                SaveGlobalSettings();
            }
        }


        public void SetGlobalEnabled(string pluginName, bool enabled)
        {
            bool stateChanged = false;
            bool previousState;

            lock (_stateLock)
            {
                if (!_globalStates.ContainsKey(pluginName))
                {
                    // 🔧 수정: 새로운 플러그인도 기본 비활성화로 시작
                    _globalStates[pluginName] = new GlobalPluginState
                    {
                        PluginName = pluginName,
                        IsEnabled = false,
                        LastModified = DateTime.Now
                    };
                    previousState = false;
                }
                else
                {
                    previousState = _globalStates[pluginName].IsEnabled;
                }

                if (previousState != enabled)
                {
                    _globalStates[pluginName].IsEnabled = enabled;
                    _globalStates[pluginName].LastModified = DateTime.Now;
                    SaveGlobalSettings();
                    stateChanged = true;
                }

                _knownPlugins.Add(pluginName);
            }

            if (stateChanged)
            {
                GlobalStateChanged?.Invoke(pluginName, enabled);

                // 전역 비활성화 시 모든 방별 설정도 비활성화
                if (!enabled)
                {
                    DisablePluginInAllRooms(pluginName);
                }
            }
        }

        public void SetGlobalConfig<T>(string pluginName, T config)
        {
            lock (_stateLock)
            {
                if (!_globalStates.ContainsKey(pluginName))
                {
                    _globalStates[pluginName] = new GlobalPluginState
                    {
                        PluginName = pluginName,
                        IsEnabled = false, // 🔧 기본 비활성화
                        LastModified = DateTime.Now
                    };
                }

                _globalStates[pluginName].GlobalConfig = config;
                _globalStates[pluginName].LastModified = DateTime.Now;
                SaveGlobalSettings();
                _knownPlugins.Add(pluginName);
            }

            PluginConfigChanged?.Invoke(pluginName);
        }

        public T? GetGlobalConfig<T>(string pluginName)
        {
            lock (_stateLock)
            {
                if (_globalStates.TryGetValue(pluginName, out var state) && state.GlobalConfig != null)
                {
                    if (state.GlobalConfig is JsonElement element)
                    {
                        return element.Deserialize<T>();
                    }
                    if (state.GlobalConfig is T directValue)
                    {
                        return directValue;
                    }
                }
                return default;
            }
        }

        /// <summary>
        /// 모든 전역 플러그인 상태 조회
        /// </summary>
        public Dictionary<string, bool> GetAllGlobalStates()
        {
            lock (_stateLock)
            {
                return _globalStates.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.IsEnabled);
            }
        }

        /// <summary>
        /// 🔧 수정: 전역 설정 초기화 (기본 비활성화 적용)
        /// </summary>
        public void ResetGlobalSettings()
        {
            List<string> affectedPlugins;

            lock (_stateLock)
            {
                affectedPlugins = _globalStates.Keys.ToList();

                // 🔧 수정: 모든 플러그인을 비활성화로 초기화
                foreach (var pluginName in _knownPlugins)
                {
                    _globalStates[pluginName] = new GlobalPluginState
                    {
                        PluginName = pluginName,
                        IsEnabled = false, // 🔧 기본 비활성화
                        LastModified = DateTime.Now,
                        GlobalConfig = null
                    };
                }

                SaveGlobalSettings();
            }

            // 모든 플러그인에 대해 상태 변경 이벤트 발생
            foreach (var pluginName in affectedPlugins)
            {
                GlobalStateChanged?.Invoke(pluginName, false);
            }
        }

        #endregion

        #region 🔧 수정된 방별 설정 관리

        

        /// <summary>
        /// 방별 플러그인 상태 설정 (안전성 강화)
        /// </summary>
        public void SetRoomEnabled(string roomId, string pluginName, bool enabled)
        {
            bool stateChanged = false;

            lock (_stateLock)
            {
                // 전역적으로 비활성화된 플러그인은 방별로 활성화할 수 없음
                if (enabled && !IsGloballyEnabled(pluginName))
                {
                    throw new InvalidOperationException($"플러그인 '{pluginName}'이 전역적으로 비활성화되어 있어 방별로 활성화할 수 없습니다.");
                }

                if (!_roomStates.ContainsKey(roomId))
                {
                    _roomStates[roomId] = new Dictionary<string, RoomPluginState>();
                }

                var currentEnabled = IsRoomEnabled(roomId, pluginName);
                if (currentEnabled == enabled)
                {
                    return; // 상태가 같으면 변경하지 않음
                }

                if (!_roomStates[roomId].ContainsKey(pluginName))
                {
                    _roomStates[roomId][pluginName] = new RoomPluginState
                    {
                        RoomId = roomId,
                        PluginName = pluginName,
                        IsEnabled = enabled,
                        LastModified = DateTime.Now
                    };
                }
                else
                {
                    _roomStates[roomId][pluginName].IsEnabled = enabled;
                    _roomStates[roomId][pluginName].LastModified = DateTime.Now;
                }

                SaveRoomSettings(roomId);
                stateChanged = true;
            }

            if (stateChanged)
            {
                // 🔧 수정: 3개 매개변수로 이벤트 호출
                RoomStateChanged?.Invoke(roomId, pluginName, enabled);
            }
        }

        public void SetRoomConfig(string roomId, string pluginName, Dictionary<string, object> config)
        {
            lock (_stateLock)
            {
                if (!_roomStates.ContainsKey(roomId))
                {
                    _roomStates[roomId] = new Dictionary<string, RoomPluginState>();
                }

                if (!_roomStates[roomId].ContainsKey(pluginName))
                {
                    _roomStates[roomId][pluginName] = new RoomPluginState
                    {
                        RoomId = roomId,
                        PluginName = pluginName,
                        IsEnabled = false, // 🔧 기본 비활성화
                        LastModified = DateTime.Now
                    };
                }

                _roomStates[roomId][pluginName].Config = config;
                _roomStates[roomId][pluginName].LastModified = DateTime.Now;

                SaveRoomSettings(roomId);
            }

            PluginConfigChanged?.Invoke(pluginName);
        }

        public PluginRoomSettings? GetRoomSettings(string roomId, string pluginName)
        {
            lock (_stateLock)
            {
                if (_roomStates.TryGetValue(roomId, out var roomPlugins) &&
                    roomPlugins.TryGetValue(pluginName, out var state))
                {
                    return new PluginRoomSettings
                    {
                        RoomId = roomId,
                        PluginName = pluginName,
                        IsEnabled = state.IsEnabled,
                        Config = state.Config,
                        LastModified = state.LastModified
                    };
                }
                return null;
            }
        }

        /// <summary>
        /// 전역 비활성화 시 모든 방에서 플러그인 비활성화
        /// </summary>
        private void DisablePluginInAllRooms(string pluginName)
        {
            List<string> affectedRooms = new();

            lock (_stateLock)
            {
                foreach (var roomKvp in _roomStates.ToList())
                {
                    if (roomKvp.Value.TryGetValue(pluginName, out var pluginState) && pluginState.IsEnabled)
                    {
                        pluginState.IsEnabled = false;
                        pluginState.LastModified = DateTime.Now;
                        affectedRooms.Add(roomKvp.Key);
                        SaveRoomSettings(roomKvp.Key);
                    }
                }
            }

            // 🔧 수정: 3개 매개변수로 이벤트 발생
            foreach (var roomId in affectedRooms)
            {
                RoomStateChanged?.Invoke(roomId, pluginName, false);
            }
        }

        /// <summary>
        /// 🔧 추가: 방별 플러그인 기본 설정 초기화
        /// </summary>
        public void InitializeRoomDefaults(string roomId, List<string> pluginNames)
        {
            lock (_stateLock)
            {
                if (!_roomStates.ContainsKey(roomId))
                {
                    _roomStates[roomId] = new Dictionary<string, RoomPluginState>();
                }

                var roomPlugins = _roomStates[roomId];
                bool hasChanges = false;

                foreach (var pluginName in pluginNames)
                {
                    if (!roomPlugins.ContainsKey(pluginName))
                    {
                        roomPlugins[pluginName] = new RoomPluginState
                        {
                            RoomId = roomId,
                            PluginName = pluginName,
                            IsEnabled = false, // 🔧 기본 비활성화
                            Config = new Dictionary<string, object>(),
                            LastModified = DateTime.Now
                        };
                        hasChanges = true;
                    }
                }

                if (hasChanges)
                {
                    SaveRoomSettings(roomId);
                    Console.WriteLine($"방 {roomId}에 {pluginNames.Count}개 플러그인 기본 설정 적용됨 (모두 비활성화)");
                }
            }
        }

        #endregion

        // PluginStateManager.cs - 핵심 로직 수정

        #region 🔧 수정된 실행 결정 로직

        /// <summary>
        /// 🔧 수정: 플러그인 실행 여부 결정 (디버깅 강화)
        /// </summary>
        public bool ShouldExecutePlugin(string pluginName, string roomId)
        {
            lock (_stateLock)
            {
                try
                {
                    Console.WriteLine($"[StateManager] ShouldExecutePlugin 확인: {pluginName} @ {roomId}");

                    // 1. 전역적으로 비활성화면 실행 안함
                    var globallyEnabled = IsGloballyEnabled(pluginName);
                    Console.WriteLine($"[StateManager] 전역 활성화 상태: {globallyEnabled}");

                    if (!globallyEnabled)
                    {
                        Console.WriteLine($"[StateManager] 전역 비활성화로 인해 실행 안함: {pluginName}");
                        return false;
                    }

                    // 2. 방별 설정이 있으면 방별 설정 우선
                    if (_roomStates.TryGetValue(roomId, out var roomPlugins) &&
                        roomPlugins.ContainsKey(pluginName))
                    {
                        var roomEnabled = roomPlugins[pluginName].IsEnabled;
                        Console.WriteLine($"[StateManager] 방별 설정 발견: {roomEnabled}");
                        return roomEnabled;
                    }

                    // 🔧 핵심 수정: 방별 설정이 없으면 기본 활성화로 변경 (테스트용)
                    Console.WriteLine($"[StateManager] 방별 설정 없음, 기본 활성화 적용: {pluginName}");

                    // 🔧 임시 수정: 디버깅을 위해 방별 설정이 없어도 활성화
                    // 원래는 false였지만 문제 진단을 위해 true로 변경
                    return true; // 임시로 true 반환하여 플러그인이 실행되도록 함
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[StateManager] ShouldExecutePlugin 오류: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// 🔧 수정: 방별 플러그인 활성화 상태 확인 (로깅 강화)
        /// </summary>
        public bool IsRoomEnabled(string roomId, string pluginName)
        {
            lock (_stateLock)
            {
                try
                {
                    Console.WriteLine($"[StateManager] IsRoomEnabled 확인: {pluginName} @ {roomId}");

                    // 1. 전역적으로 비활성화면 무조건 비활성화
                    if (!IsGloballyEnabled(pluginName))
                    {
                        Console.WriteLine($"[StateManager] 전역 비활성화로 인해 방별 비활성화: {pluginName}");
                        return false;
                    }

                    // 2. 방별 설정이 있으면 방별 설정 우선
                    if (_roomStates.TryGetValue(roomId, out var roomPlugins) &&
                        roomPlugins.TryGetValue(pluginName, out var state))
                    {
                        Console.WriteLine($"[StateManager] 방별 설정 발견: {state.IsEnabled}");
                        return state.IsEnabled;
                    }

                    // 🔧 수정: 방별 설정이 없으면 기본 활성화 (임시)
                    Console.WriteLine($"[StateManager] 방별 설정 없음, 기본 활성화 적용");
                    return true; // 임시로 true 반환
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[StateManager] IsRoomEnabled 오류: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// 🔧 수정: 전역 활성화 상태 확인 (로깅 강화)
        /// </summary>
        public bool IsGloballyEnabled(string pluginName)
        {
            lock (_stateLock)
            {
                try
                {
                    if (_globalStates.TryGetValue(pluginName, out var state))
                    {
                        Console.WriteLine($"[StateManager] 전역 상태 발견: {pluginName} = {state.IsEnabled}");
                        return state.IsEnabled;
                    }

                    // 🔧 수정: 알려지지 않은 플러그인은 기본 활성화 (임시)
                    Console.WriteLine($"[StateManager] 전역 상태 없음, 기본 활성화 적용: {pluginName}");

                    // 🔧 임시 수정: 새로운 플러그인을 자동으로 활성화 상태로 등록
                    RegisterPlugin(pluginName, true); // 기본 활성화로 등록
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[StateManager] IsGloballyEnabled 오류: {ex.Message}");
                    return false;
                }
            }
        }

        #endregion

        #region 🔧 추가된 디버깅 및 강제 설정 메서드들

        /// <summary>
        /// 🔧 추가: 플러그인을 강제로 전역 및 방별 활성화
        /// </summary>
        public void ForceEnablePluginEverywhere(string pluginName)
        {
            lock (_stateLock)
            {
                try
                {
                    Console.WriteLine($"[StateManager] 강제 활성화 시작: {pluginName}");

                    // 1. 전역 활성화
                    if (!_globalStates.ContainsKey(pluginName))
                    {
                        _globalStates[pluginName] = new GlobalPluginState
                        {
                            PluginName = pluginName,
                            IsEnabled = true,
                            LastModified = DateTime.Now
                        };
                    }
                    else
                    {
                        _globalStates[pluginName].IsEnabled = true;
                        _globalStates[pluginName].LastModified = DateTime.Now;
                    }

                    _knownPlugins.Add(pluginName);
                    SaveGlobalSettings();
                    Console.WriteLine($"[StateManager] 전역 활성화 완료: {pluginName}");

                    // 2. 모든 알려진 방에서 활성화
                    var roomsActivated = 0;
                    foreach (var roomKvp in _roomStates.ToList())
                    {
                        var roomId = roomKvp.Key;
                        var roomPlugins = roomKvp.Value;

                        if (!roomPlugins.ContainsKey(pluginName))
                        {
                            roomPlugins[pluginName] = new RoomPluginState
                            {
                                RoomId = roomId,
                                PluginName = pluginName,
                                IsEnabled = true,
                                LastModified = DateTime.Now
                            };
                        }
                        else
                        {
                            roomPlugins[pluginName].IsEnabled = true;
                            roomPlugins[pluginName].LastModified = DateTime.Now;
                        }

                        SaveRoomSettings(roomId);
                        roomsActivated++;
                    }

                    Console.WriteLine($"[StateManager] 강제 활성화 완료: {pluginName} ({roomsActivated}개 방)");

                    // 3. 이벤트 발생
                    GlobalStateChanged?.Invoke(pluginName, true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[StateManager] 강제 활성화 실패: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 🔧 추가: 모든 플러그인을 강제로 활성화
        /// </summary>
        public void ForceEnableAllPlugins(List<string> pluginNames)
        {
            Console.WriteLine($"[StateManager] 모든 플러그인 강제 활성화 시작: {pluginNames.Count}개");

            foreach (var pluginName in pluginNames)
            {
                try
                {
                    ForceEnablePluginEverywhere(pluginName);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[StateManager] 플러그인 강제 활성화 실패 [{pluginName}]: {ex.Message}");
                }
            }

            Console.WriteLine($"[StateManager] 모든 플러그인 강제 활성화 완료");
        }

        /// <summary>
        /// 🔧 추가: StateManager 상태 전체 출력
        /// </summary>
        public string GetFullStateReport()
        {
            var report = new StringBuilder();

            lock (_stateLock)
            {
                report.AppendLine("=== StateManager 전체 상태 리포트 ===");
                report.AppendLine($"생성 시간: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                report.AppendLine();

                // 전역 상태
                report.AppendLine($"🌍 전역 플러그인 상태 ({_globalStates.Count}개):");
                foreach (var kvp in _globalStates)
                {
                    var status = kvp.Value.IsEnabled ? "✅ 활성화" : "❌ 비활성화";
                    report.AppendLine($"   • {kvp.Key}: {status} (수정: {kvp.Value.LastModified:MM-dd HH:mm})");
                }
                report.AppendLine();

                // 방별 상태
                report.AppendLine($"🏠 방별 플러그인 상태 ({_roomStates.Count}개 방):");
                foreach (var roomKvp in _roomStates)
                {
                    var roomId = roomKvp.Key;
                    var roomPlugins = roomKvp.Value;
                    var enabledCount = roomPlugins.Values.Count(p => p.IsEnabled);

                    report.AppendLine($"   방 {roomId}: {enabledCount}/{roomPlugins.Count}개 활성화");

                    foreach (var pluginKvp in roomPlugins)
                    {
                        var status = pluginKvp.Value.IsEnabled ? "✅" : "❌";
                        report.AppendLine($"     {status} {pluginKvp.Key}");
                    }
                }

                // 알려진 플러그인
                report.AppendLine();
                report.AppendLine($"📦 알려진 플러그인 ({_knownPlugins.Count}개):");
                foreach (var pluginName in _knownPlugins)
                {
                    report.AppendLine($"   • {pluginName}");
                }
            }

            return report.ToString();
        }

        /// <summary>
        /// 🔧 추가: 특정 방에 기본 플러그인 설정 생성
        /// </summary>
        public void CreateDefaultRoomSettings(string roomId, List<string> pluginNames)
        {
            lock (_stateLock)
            {
                try
                {
                    Console.WriteLine($"[StateManager] 방 {roomId}에 기본 설정 생성 시작");

                    if (!_roomStates.ContainsKey(roomId))
                    {
                        _roomStates[roomId] = new Dictionary<string, RoomPluginState>();
                    }

                    var roomPlugins = _roomStates[roomId];
                    var addedCount = 0;

                    foreach (var pluginName in pluginNames)
                    {
                        if (!roomPlugins.ContainsKey(pluginName))
                        {
                            roomPlugins[pluginName] = new RoomPluginState
                            {
                                RoomId = roomId,
                                PluginName = pluginName,
                                IsEnabled = true, // 🔧 기본 활성화
                                Config = new Dictionary<string, object>(),
                                LastModified = DateTime.Now
                            };
                            addedCount++;
                        }
                    }

                    if (addedCount > 0)
                    {
                        SaveRoomSettings(roomId);
                        Console.WriteLine($"[StateManager] 방 {roomId}에 {addedCount}개 플러그인 기본 설정 생성 완료");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[StateManager] 기본 방 설정 생성 실패: {ex.Message}");
                }
            }
        }

        #endregion

        #region 🔧 향상된 데이터 저장/로드

        private void LoadAllSettings()
        {
            try
            {
                LoadGlobalSettings();
                LoadAllRoomSettings();
                _lastGlobalSettingsCheck = DateTime.Now;
                Console.WriteLine("플러그인 상태 설정 로드 완료");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"플러그인 상태 설정 로드 실패: {ex.Message}");
            }
        }

        private void LoadGlobalSettings()
        {
            try
            {
                var globalSettingsPath = Path.Combine(_configService.DataPath, "plugin_data", "global_settings.json");
                if (File.Exists(globalSettingsPath))
                {
                    var json = File.ReadAllText(globalSettingsPath);
                    var data = JsonSerializer.Deserialize<Dictionary<string, GlobalPluginState>>(json);
                    if (data != null)
                    {
                        lock (_stateLock)
                        {
                            _globalStates.Clear();
                            foreach (var kvp in data)
                            {
                                _globalStates[kvp.Key] = kvp.Value;
                                _knownPlugins.Add(kvp.Key);
                            }
                        }
                        Console.WriteLine($"전역 플러그인 설정 로드됨: {data.Count}개");
                    }
                }
                else
                {
                    Console.WriteLine("전역 플러그인 설정 파일이 없습니다. 기본 설정으로 시작합니다.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"전역 플러그인 설정 로드 실패: {ex.Message}");
            }
        }

        private void SaveGlobalSettings()
        {
            try
            {
                var globalSettingsPath = Path.Combine(_configService.DataPath, "plugin_data", "global_settings.json");
                Directory.CreateDirectory(Path.GetDirectoryName(globalSettingsPath)!);

                Dictionary<string, GlobalPluginState> dataToSave;
                lock (_stateLock)
                {
                    dataToSave = new Dictionary<string, GlobalPluginState>(_globalStates);
                }

                var json = JsonSerializer.Serialize(dataToSave, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(globalSettingsPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"전역 플러그인 설정 저장 실패: {ex.Message}");
            }
        }

        private void LoadAllRoomSettings()
        {
            try
            {
                var roomSettingsDir = Path.Combine(_configService.DataPath, "plugin_data", "room_settings");
                if (Directory.Exists(roomSettingsDir))
                {
                    var files = Directory.GetFiles(roomSettingsDir, "room_*.json");
                    foreach (var file in files)
                    {
                        var fileName = Path.GetFileNameWithoutExtension(file);
                        if (fileName.StartsWith("room_"))
                        {
                            var roomId = fileName.Substring(5); // "room_" 제거
                            LoadRoomSettings(roomId);
                        }
                    }
                    Console.WriteLine($"방별 플러그인 설정 로드됨: {files.Length}개 방");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"방별 플러그인 설정 로드 실패: {ex.Message}");
            }
        }

        private void LoadRoomSettings(string roomId)
        {
            try
            {
                var roomSettingsPath = Path.Combine(_configService.DataPath, "plugin_data", "room_settings", $"room_{roomId}.json");
                if (File.Exists(roomSettingsPath))
                {
                    var json = File.ReadAllText(roomSettingsPath);
                    var data = JsonSerializer.Deserialize<Dictionary<string, RoomPluginState>>(json);
                    if (data != null)
                    {
                        lock (_stateLock)
                        {
                            _roomStates[roomId] = data;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"방 {roomId} 플러그인 설정 로드 실패: {ex.Message}");
            }
        }

        private void SaveRoomSettings(string roomId)
        {
            try
            {
                var roomSettingsDir = Path.Combine(_configService.DataPath, "plugin_data", "room_settings");
                Directory.CreateDirectory(roomSettingsDir);

                var roomSettingsPath = Path.Combine(roomSettingsDir, $"room_{roomId}.json");

                Dictionary<string, RoomPluginState>? dataToSave = null;
                lock (_stateLock)
                {
                    if (_roomStates.TryGetValue(roomId, out var roomData))
                    {
                        dataToSave = new Dictionary<string, RoomPluginState>(roomData);
                    }
                }

                if (dataToSave != null)
                {
                    var json = JsonSerializer.Serialize(dataToSave, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(roomSettingsPath, json);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"방 {roomId} 플러그인 설정 저장 실패: {ex.Message}");
            }
        }

        private void DeleteRoomSettingsFile(string roomId)
        {
            try
            {
                var roomSettingsPath = Path.Combine(_configService.DataPath, "plugin_data", "room_settings", $"room_{roomId}.json");
                if (File.Exists(roomSettingsPath))
                {
                    File.Delete(roomSettingsPath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"방 {roomId} 설정 파일 삭제 실패: {ex.Message}");
            }
        }

        #endregion

        #region 통계 및 정보

        /// <summary>
        /// 플러그인 사용 통계 조회
        /// </summary>
        public PluginUsageStatistics GetPluginUsageStatistics(string pluginName)
        {
            lock (_stateLock)
            {
                var stats = new PluginUsageStatistics
                {
                    PluginName = pluginName,
                    IsGloballyEnabled = IsGloballyEnabled(pluginName),
                    TotalRoomsConfigured = 0,
                    RoomsEnabled = 0,
                    RoomsDisabled = 0,
                    LastGlobalModified = _globalStates.TryGetValue(pluginName, out var globalState) ? globalState.LastModified : DateTime.MinValue,
                    LastRoomModified = DateTime.MinValue
                };

                foreach (var roomKvp in _roomStates)
                {
                    if (roomKvp.Value.TryGetValue(pluginName, out var roomState))
                    {
                        stats.TotalRoomsConfigured++;
                        if (roomState.IsEnabled)
                            stats.RoomsEnabled++;
                        else
                            stats.RoomsDisabled++;

                        if (roomState.LastModified > stats.LastRoomModified)
                            stats.LastRoomModified = roomState.LastModified;
                    }
                }

                return stats;
            }
        }

        /// <summary>
        /// 방별 설정 통계 조회
        /// </summary>
        public Dictionary<string, RoomConfigurationStatistics> GetRoomConfigurationStatistics()
        {
            var statistics = new Dictionary<string, RoomConfigurationStatistics>();

            lock (_stateLock)
            {
                foreach (var roomKvp in _roomStates)
                {
                    var roomId = roomKvp.Key;
                    var roomPlugins = roomKvp.Value;

                    var stats = new RoomConfigurationStatistics
                    {
                        RoomId = roomId,
                        TotalPluginsConfigured = roomPlugins.Count,
                        PluginsEnabled = roomPlugins.Values.Count(p => p.IsEnabled),
                        PluginsDisabled = roomPlugins.Values.Count(p => !p.IsEnabled),
                        LastModified = roomPlugins.Values.Any() ? roomPlugins.Values.Max(p => p.LastModified) : DateTime.MinValue
                    };

                    statistics[roomId] = stats;
                }
            }

            return statistics;
        }

        #endregion
    }

    #region 관련 모델 클래스들

    // 전역 플러그인 상태
    public class GlobalPluginState
    {
        public string PluginName { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = false; // 🔧 기본값 변경
        public DateTime LastModified { get; set; } = DateTime.Now;
        public object? GlobalConfig { get; set; }
    }

    // 방별 플러그인 상태
    public class RoomPluginState
    {
        public string RoomId { get; set; } = string.Empty;
        public string PluginName { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = false; // 🔧 기본값 변경
        public Dictionary<string, object> Config { get; set; } = new();
        public DateTime LastModified { get; set; } = DateTime.Now;
    }

    // 플러그인 사용 통계
    public class PluginUsageStatistics
    {
        public string PluginName { get; set; } = string.Empty;
        public bool IsGloballyEnabled { get; set; }
        public int TotalRoomsConfigured { get; set; }
        public int RoomsEnabled { get; set; }
        public int RoomsDisabled { get; set; }
        public DateTime LastGlobalModified { get; set; }
        public DateTime LastRoomModified { get; set; }
    }

    // 방별 설정 통계
    public class RoomConfigurationStatistics
    {
        public string RoomId { get; set; } = string.Empty;
        public int TotalPluginsConfigured { get; set; }
        public int PluginsEnabled { get; set; }
        public int PluginsDisabled { get; set; }
        public DateTime LastModified { get; set; }
    }

    #endregion
}