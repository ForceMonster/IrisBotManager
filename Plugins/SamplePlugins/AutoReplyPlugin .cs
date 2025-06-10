using IrisBotManager.Core.Models;
using IrisBotManager.Core.Plugin;

namespace SamplePlugins;

public class AutoReplyPlugin : IPlugin
{
    // 기본 정보
    public string Name => "AutoReply";
    public string DisplayName => "자동 응답";
    public string Version => "1.0.2"; // 봇 필터링 기능 추가로 버전 업
    public string Description => "키워드 기반 자동 응답 시스템. 완전 일치 및 부분 일치를 지원하며, 방별로 다른 응답 세트를 사용할 수 있습니다. 봇 자신의 메시지는 무시합니다.";
    public string Category => "자동응답";
    public string[] Dependencies => Array.Empty<string>();
    public UserRole RequiredRole => UserRole.User;
    public bool SupportsRoomSettings => true;

    private IPluginContext? _context;
    private Dictionary<string, string> _globalResponses = new();

    // 🔧 추가: 봇 메시지 필터링을 위한 캐시
    private readonly Dictionary<string, HashSet<string>> _sentMessagesCache = new();
    private readonly Dictionary<string, DateTime> _lastSentTimes = new();
    private readonly TimeSpan _cacheExpireTime = TimeSpan.FromMinutes(5); // 5분 후 캐시 만료
    private readonly object _cacheLock = new object();

    public async Task InitializeAsync(IPluginContext context)
    {
        _context = context;

        // 🔧 추가: 초기화 로깅
        context.ShowNotification($"🚀 [AutoReply] 초기화 시작...");

        try
        {
            // 저장된 응답 데이터 로드
            _globalResponses = await context.GetDataAsync<Dictionary<string, string>>("global_responses") ?? new Dictionary<string, string>();
            context.ShowNotification($"📁 [AutoReply] 기존 응답 데이터 로드: {_globalResponses.Count}개");

            // 기본 응답 설정
            if (_globalResponses.Count == 0)
            {
                context.ShowNotification($"🔧 [AutoReply] 기본 응답 설정 중...");
                _globalResponses["안녕"] = "안녕하세요!";
                _globalResponses["하이"] = "하이~";
                _globalResponses["안녕하세요"] = "네, 안녕하세요!";
                _globalResponses["고마워"] = "천만에요!";
                _globalResponses["감사"] = "별말씀을요~";
                _globalResponses["하앍"] = "호옭!";
                await SaveGlobalResponses();
                context.ShowNotification($"✅ [AutoReply] 기본 응답 {_globalResponses.Count}개 설정 완료");
            }

            // 🔧 추가: 응답 목록 로깅
            context.ShowNotification($"📋 [AutoReply] 등록된 키워드: {string.Join(", ", _globalResponses.Keys)}");

            // 메시지 구독 (실제로는 PluginManager가 ProcessMessageAsync를 호출하므로 불필요하지만 유지)
            context.SubscribeToMessages(OnMessageReceived);

            // 🔧 추가: 캐시 정리 타스크 시작
            _ = Task.Run(CacheCleanupLoop);

            // 플러그인 로드 알림
            context.ShowNotification($"✅ [AutoReply] 플러그인 로드 완료 (전역 응답 {_globalResponses.Count}개, 봇 필터링 활성화)");
        }
        catch (Exception ex)
        {
            context.ShowNotification($"❌ [AutoReply] 초기화 실패: {ex.Message}");
            context.ShowNotification($"❌ [AutoReply] 스택: {ex.StackTrace}");
            throw; // 초기화 실패 시 예외 재발생
        }
    }

    public async Task ProcessMessageAsync(string message, string roomId, PluginRoomSettings? roomSettings = null)
    {
        try
        {
            // 🔧 추가: 처리 시작 로깅
            _context?.ShowNotification($"🔍 [AutoReply] 메시지 처리 시작: '{message}' (방: {roomId})");

            if (_context == null)
            {
                // 🔧 추가: context null 로깅
                Console.WriteLine("❌ [AutoReply] _context가 null입니다!");
                return;
            }

            var trimmedMessage = message.Trim();
            _context.ShowNotification($"🔧 [AutoReply] 정제된 메시지: '{trimmedMessage}'");

            // 🔧 추가: 봇 메시지 필터링 체크
            if (IsBotMessage(trimmedMessage, roomId))
            {
                _context.ShowNotification($"🤖 [AutoReply] 봇 메시지 감지됨, 무시: '{trimmedMessage}'");
                return;
            }

            // 방별 설정이 있으면 방별 응답 사용, 없으면 전역 응답 사용
            var responses = await GetEffectiveResponses(roomSettings);
            _context.ShowNotification($"📖 [AutoReply] 사용할 응답 세트: {responses.Count}개 (키워드: {string.Join(", ", responses.Keys)})");

            // 정확한 일치 검사 (우선순위)
            if (responses.TryGetValue(trimmedMessage, out var exactResponse))
            {
                _context.ShowNotification($"🎯 [AutoReply] 정확 일치 발견: '{trimmedMessage}' → '{exactResponse}'");

                try
                {
                    // 🔧 수정: 메시지 전송 전에 캐시에 기록
                    RecordSentMessage(exactResponse, roomId);

                    await _context.SendMessageAsync(roomId, exactResponse);
                    _context.ShowNotification($"✅ [AutoReply] 응답 전송 완료: '{exactResponse}'");
                }
                catch (Exception sendEx)
                {
                    _context.ShowNotification($"❌ [AutoReply] 응답 전송 실패: {sendEx.Message}");
                    _context.ShowNotification($"❌ [AutoReply] 전송 스택: {sendEx.StackTrace}");
                }
                return;
            }

            _context.ShowNotification($"⚠️ [AutoReply] 정확 일치 없음, 부분 일치 검사 시작...");

            // 부분 일치 검사 (대소문자 무시)
            foreach (var kvp in responses)
            {
                if (trimmedMessage.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                {
                    _context.ShowNotification($"🎯 [AutoReply] 부분 일치 발견: '{kvp.Key}' in '{trimmedMessage}' → '{kvp.Value}'");

                    try
                    {
                        // 🔧 수정: 메시지 전송 전에 캐시에 기록
                        RecordSentMessage(kvp.Value, roomId);

                        await _context.SendMessageAsync(roomId, kvp.Value);
                        _context.ShowNotification($"✅ [AutoReply] 부분 일치 응답 전송 완료: '{kvp.Value}'");
                    }
                    catch (Exception sendEx)
                    {
                        _context.ShowNotification($"❌ [AutoReply] 부분 일치 응답 전송 실패: {sendEx.Message}");
                    }
                    break; // 첫 번째 매칭만 응답
                }
            }

            _context.ShowNotification($"⚠️ [AutoReply] 매칭되는 응답이 없습니다: '{trimmedMessage}'");
        }
        catch (Exception ex)
        {
            _context?.ShowNotification($"❌ [AutoReply] ProcessMessageAsync 전체 실패: {ex.Message}");
            _context?.ShowNotification($"❌ [AutoReply] 전체 스택: {ex.StackTrace}");
        }
    }

    // 🔧 추가: 봇 메시지인지 확인하는 메서드
    private bool IsBotMessage(string message, string roomId)
    {
        lock (_cacheLock)
        {
            try
            {
                // 해당 방의 캐시가 있는지 확인
                if (!_sentMessagesCache.TryGetValue(roomId, out var roomCache))
                {
                    return false;
                }

                // 메시지가 캐시에 있는지 확인
                bool isCached = roomCache.Contains(message);

                if (isCached)
                {
                    // 캐시에서 제거 (한 번만 무시)
                    roomCache.Remove(message);
                    _context?.ShowNotification($"🗑️ [AutoReply] 캐시에서 제거됨: '{message}'");

                    // 캐시가 비었으면 제거
                    if (roomCache.Count == 0)
                    {
                        _sentMessagesCache.Remove(roomId);
                        _lastSentTimes.Remove(roomId);
                    }
                }

                return isCached;
            }
            catch (Exception ex)
            {
                _context?.ShowNotification($"⚠️ [AutoReply] 봇 메시지 확인 실패: {ex.Message}");
                return false;
            }
        }
    }

    // 🔧 추가: 전송한 메시지를 캐시에 기록
    private void RecordSentMessage(string message, string roomId)
    {
        lock (_cacheLock)
        {
            try
            {
                // 방별 캐시가 없으면 생성
                if (!_sentMessagesCache.TryGetValue(roomId, out var roomCache))
                {
                    roomCache = new HashSet<string>();
                    _sentMessagesCache[roomId] = roomCache;
                }

                // 메시지를 캐시에 추가
                roomCache.Add(message);
                _lastSentTimes[roomId] = DateTime.Now;

                _context?.ShowNotification($"💾 [AutoReply] 캐시에 기록됨: '{message}' (방: {roomId})");
            }
            catch (Exception ex)
            {
                _context?.ShowNotification($"⚠️ [AutoReply] 캐시 기록 실패: {ex.Message}");
            }
        }
    }

    // 🔧 추가: 캐시 정리 루프
    private async Task CacheCleanupLoop()
    {
        while (true)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1)); // 1분마다 정리
                CleanupExpiredCache();
            }
            catch (Exception ex)
            {
                _context?.ShowNotification($"⚠️ [AutoReply] 캐시 정리 루프 오류: {ex.Message}");
                await Task.Delay(TimeSpan.FromMinutes(5)); // 오류 시 5분 대기
            }
        }
    }

    // 🔧 추가: 만료된 캐시 정리
    private void CleanupExpiredCache()
    {
        lock (_cacheLock)
        {
            try
            {
                var now = DateTime.Now;
                var expiredRooms = new List<string>();

                foreach (var kvp in _lastSentTimes)
                {
                    if (now - kvp.Value > _cacheExpireTime)
                    {
                        expiredRooms.Add(kvp.Key);
                    }
                }

                foreach (var roomId in expiredRooms)
                {
                    _sentMessagesCache.Remove(roomId);
                    _lastSentTimes.Remove(roomId);
                }

                if (expiredRooms.Count > 0)
                {
                    _context?.ShowNotification($"🧹 [AutoReply] 만료된 캐시 정리: {expiredRooms.Count}개 방");
                }
            }
            catch (Exception ex)
            {
                _context?.ShowNotification($"⚠️ [AutoReply] 캐시 정리 실패: {ex.Message}");
            }
        }
    }

    private async void OnMessageReceived(string message, string roomId)
    {
        // 🔧 추가: SubscribeToMessages를 통한 호출 로깅
        _context?.ShowNotification($"📨 [AutoReply] OnMessageReceived 호출: '{message}' (방: {roomId})");

        // ProcessMessageAsync에서 처리하므로 여기서는 빈 구현
        // 하지만 실제로 PluginManager가 ProcessMessageAsync를 직접 호출하므로 이 메서드는 호출되지 않을 수 있음
    }

    private async Task<Dictionary<string, string>> GetEffectiveResponses(PluginRoomSettings? roomSettings)
    {
        try
        {
            _context?.ShowNotification($"🔍 [AutoReply] GetEffectiveResponses 시작 (roomSettings: {(roomSettings != null ? "있음" : "없음")})");

            // 방별 설정이 있고 응답 세트가 설정되어 있으면 그것을 사용
            if (roomSettings?.Config.TryGetValue("responseSet", out var responseSetObj) == true &&
                responseSetObj is string responseSet && !string.IsNullOrEmpty(responseSet))
            {
                _context?.ShowNotification($"🔧 [AutoReply] 방별 응답 세트 확인: '{responseSet}'");

                var roomResponses = await _context!.GetDataAsync<Dictionary<string, string>>($"room_responses_{responseSet}");
                if (roomResponses != null && roomResponses.Count > 0)
                {
                    _context?.ShowNotification($"✅ [AutoReply] 방별 응답 세트 사용: {roomResponses.Count}개");
                    return roomResponses;
                }
            }

            // 방별 커스텀 응답이 있으면 사용
            if (roomSettings?.Config.TryGetValue("customResponses", out var customResponsesObj) == true &&
                customResponsesObj is Dictionary<string, object> customResponsesDict)
            {
                _context?.ShowNotification($"🔧 [AutoReply] 방별 커스텀 응답 확인...");

                var customResponses = new Dictionary<string, string>();
                foreach (var kvp in customResponsesDict)
                {
                    if (kvp.Value is string responseValue)
                    {
                        customResponses[kvp.Key] = responseValue;
                    }
                }
                if (customResponses.Count > 0)
                {
                    _context?.ShowNotification($"✅ [AutoReply] 방별 커스텀 응답 사용: {customResponses.Count}개");
                    return customResponses;
                }
            }

            // 기본적으로 전역 응답 사용
            _context?.ShowNotification($"📖 [AutoReply] 전역 응답 사용: {_globalResponses.Count}개");
            return _globalResponses;
        }
        catch (Exception ex)
        {
            _context?.ShowNotification($"❌ [AutoReply] GetEffectiveResponses 실패: {ex.Message}");
            return _globalResponses; // 실패 시 전역 응답 반환
        }
    }

    #region 설정 스키마

    public PluginConfigSchema GetConfigSchema()
    {
        return new PluginConfigSchema
        {
            Fields = new List<ConfigField>
            {
                new ConfigField
                {
                    Name = "responseSet",
                    DisplayName = "응답 세트",
                    Description = "사용할 응답 세트를 선택하세요",
                    Type = ConfigFieldType.Dropdown,
                    IsRequired = false,
                    DefaultValue = "default",
                    Options = new List<string> { "default", "formal", "casual", "developer", "custom" }
                },
                new ConfigField
                {
                    Name = "triggerDelay",
                    DisplayName = "응답 지연 시간 (초)",
                    Description = "메시지 수신 후 응답까지의 지연 시간",
                    Type = ConfigFieldType.Number,
                    IsRequired = false,
                    DefaultValue = 0
                },
                new ConfigField
                {
                    Name = "enablePartialMatch",
                    DisplayName = "부분 일치 활성화",
                    Description = "키워드가 메시지에 포함되어 있을 때도 응답",
                    Type = ConfigFieldType.Boolean,
                    IsRequired = false,
                    DefaultValue = true
                },
                new ConfigField
                {
                    Name = "botFilterEnabled",
                    DisplayName = "봇 메시지 필터링",
                    Description = "봇 자신의 메시지에는 응답하지 않음",
                    Type = ConfigFieldType.Boolean,
                    IsRequired = false,
                    DefaultValue = true
                },
                new ConfigField
                {
                    Name = "cacheExpiryMinutes",
                    DisplayName = "캐시 만료 시간 (분)",
                    Description = "봇 메시지 캐시가 유지되는 시간",
                    Type = ConfigFieldType.Number,
                    IsRequired = false,
                    DefaultValue = 5
                }
            },
            DefaultValues = new Dictionary<string, object>
            {
                { "responseSet", "default" },
                { "triggerDelay", 0 },
                { "enablePartialMatch", true },
                { "botFilterEnabled", true },
                { "cacheExpiryMinutes", 5 }
            }
        };
    }

    public async Task<bool> ValidateConfigAsync(object config)
    {
        try
        {
            if (config is not Dictionary<string, object> configDict)
                return false;

            // 응답 세트 검증
            if (configDict.TryGetValue("responseSet", out var responseSetObj) &&
                responseSetObj is string responseSet)
            {
                var validSets = new[] { "default", "formal", "casual", "developer", "custom" };
                if (!validSets.Contains(responseSet))
                    return false;
            }

            // 지연 시간 검증
            if (configDict.TryGetValue("triggerDelay", out var delayObj))
            {
                if (delayObj is not int delay || delay < 0 || delay > 60)
                    return false;
            }

            // 캐시 만료 시간 검증
            if (configDict.TryGetValue("cacheExpiryMinutes", out var cacheExpiryObj))
            {
                if (cacheExpiryObj is not int cacheExpiry || cacheExpiry < 1 || cacheExpiry > 60)
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region 관리 메서드

    public async Task AddResponseAsync(string trigger, string response)
    {
        _globalResponses[trigger] = response;
        await SaveGlobalResponses();
        _context?.ShowNotification($"✅ [AutoReply] 응답 추가: '{trigger}' → '{response}'");
    }

    public async Task RemoveResponseAsync(string trigger)
    {
        if (_globalResponses.Remove(trigger))
        {
            await SaveGlobalResponses();
            _context?.ShowNotification($"🗑️ [AutoReply] 응답 제거: '{trigger}'");
        }
    }

    public Dictionary<string, string> GetGlobalResponses()
    {
        return new Dictionary<string, string>(_globalResponses);
    }

    public async Task UpdateResponseAsync(string trigger, string newResponse)
    {
        if (_globalResponses.ContainsKey(trigger))
        {
            _globalResponses[trigger] = newResponse;
            await SaveGlobalResponses();
            _context?.ShowNotification($"🔧 [AutoReply] 응답 수정: '{trigger}' → '{newResponse}'");
        }
    }

    public bool HasResponse(string trigger)
    {
        return _globalResponses.ContainsKey(trigger);
    }

    public string? GetResponse(string trigger)
    {
        return _globalResponses.TryGetValue(trigger, out var response) ? response : null;
    }

    public async Task CreateResponseSet(string setName, Dictionary<string, string> responses)
    {
        await _context!.SetDataAsync($"room_responses_{setName}", responses);
        _context?.ShowNotification($"📦 [AutoReply] 응답 세트 생성: '{setName}' ({responses.Count}개)");
    }

    public async Task<Dictionary<string, string>?> GetResponseSet(string setName)
    {
        return await _context!.GetDataAsync<Dictionary<string, string>>($"room_responses_{setName}");
    }

    // 🔧 추가: 봇 필터링 상태 확인 메서드
    public Dictionary<string, object> GetBotFilteringStatus()
    {
        lock (_cacheLock)
        {
            return new Dictionary<string, object>
            {
                { "totalCachedRooms", _sentMessagesCache.Count },
                { "totalCachedMessages", _sentMessagesCache.Values.Sum(cache => cache.Count) },
                { "cacheExpireTime", _cacheExpireTime.TotalMinutes },
                { "lastCleanupTime", DateTime.Now }
            };
        }
    }

    // 🔧 추가: 수동 캐시 정리
    public void ClearBotMessageCache()
    {
        lock (_cacheLock)
        {
            var totalMessages = _sentMessagesCache.Values.Sum(cache => cache.Count);
            _sentMessagesCache.Clear();
            _lastSentTimes.Clear();
            _context?.ShowNotification($"🧹 [AutoReply] 봇 메시지 캐시 수동 정리 완료: {totalMessages}개 메시지");
        }
    }

    #endregion

    private async Task SaveGlobalResponses()
    {
        if (_context != null)
        {
            try
            {
                await _context.SetDataAsync("global_responses", _globalResponses);
                _context.ShowNotification($"💾 [AutoReply] 전역 응답 저장 완료: {_globalResponses.Count}개");
            }
            catch (Exception ex)
            {
                _context.ShowNotification($"❌ [AutoReply] 전역 응답 저장 실패: {ex.Message}");
            }
        }
    }

    public async Task ShutdownAsync()
    {
        try
        {
            await SaveGlobalResponses();

            // 🔧 추가: 캐시 정리
            ClearBotMessageCache();

            _context?.ShowNotification($"❌ [AutoReply] 플러그인 종료 완료");
        }
        catch (Exception ex)
        {
            _context?.ShowNotification($"❌ [AutoReply] 종료 중 오류: {ex.Message}");
        }
    }
}