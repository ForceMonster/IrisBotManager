using System.IO;
using IrisBotManager.Core.Models;
using IrisBotManager.Core.Plugin;

namespace SamplePlugins;

public class MessageLoggerPlugin : IPlugin
{
    // 기본 정보
    public string Name => "MessageLogger";
    public string DisplayName => "메시지 로거";
    public string Version => "1.0.0";
    public string Description => "모든 수신 메시지를 자동으로 기록하고 저장합니다. 월별/일별 로그 파일을 생성하며 방별로 다른 로그 레벨을 설정할 수 있습니다.";
    public string Category => "모니터링";
    public string[] Dependencies => Array.Empty<string>();
    public UserRole RequiredRole => UserRole.User;
    public bool SupportsRoomSettings => true;

    private IPluginContext? _context;
    private bool _globallyEnabled = true;
    private readonly object _fileLock = new object();
    private int _logCount = 0;
    private readonly Dictionary<string, DateTime> _lastLogTimes = new();

    public async Task InitializeAsync(IPluginContext context)
    {
        _context = context;

        // 설정 로드
        var settings = await context.GetDataAsync<LoggerSettings>("global_settings") ?? new LoggerSettings();
        _globallyEnabled = settings.IsEnabled;

        // 메시지 구독
        context.SubscribeToMessages(OnMessageReceived);

        // 플러그인 로드 알림
        context.ShowNotification($"✅ {DisplayName} 플러그인이 로드되었습니다. (로깅: {(_globallyEnabled ? "활성화" : "비활성화")})");
    }

    public async Task ProcessMessageAsync(string message, string roomId, PluginRoomSettings? roomSettings = null)
    {
        if (_context == null) return;

        // 전역적으로 비활성화되어 있으면 로깅하지 않음
        if (!_globallyEnabled) return;

        // 방별 설정 확인
        var logLevel = GetEffectiveLogLevel(roomSettings);
        if (logLevel == LogLevel.Disabled) return;

        try
        {
            var logEntry = new LogEntry
            {
                Timestamp = DateTime.Now,
                RoomId = roomId,
                Message = message,
                RawData = message,
                LogLevel = logLevel
            };

            // 로그 레벨에 따른 필터링
            if (ShouldLog(logEntry, roomSettings))
            {
                await SaveLogEntryAsync(logEntry);
                _logCount++;

                // 100개마다 통계 알림 (너무 자주 알림을 방지)
                if (_logCount % 100 == 0)
                {
                    _context.ShowNotification($"📝 메시지 로그: {_logCount}개 기록됨");
                }
            }
        }
        catch (Exception ex)
        {
            // 로그 저장 실패 시 무시 (무한 루프 방지)
            Console.WriteLine($"로그 저장 실패: {ex.Message}");
        }
    }

    private async void OnMessageReceived(string message, string roomId)
    {
        // ProcessMessageAsync에서 처리하므로 여기서는 빈 구현
    }

    private LogLevel GetEffectiveLogLevel(PluginRoomSettings? roomSettings)
    {
        // 방별 설정이 있으면 그것을 사용
        if (roomSettings?.Config.TryGetValue("logLevel", out var logLevelObj) == true)
        {
            if (logLevelObj is string logLevelStr && Enum.TryParse<LogLevel>(logLevelStr, out var logLevel))
            {
                return logLevel;
            }
        }

        // 기본값은 All
        return LogLevel.All;
    }

    private bool ShouldLog(LogEntry entry, PluginRoomSettings? roomSettings)
    {
        // 로그 레벨 확인
        if (entry.LogLevel == LogLevel.Disabled) return false;

        // 중복 메시지 필터링 (같은 방에서 1초 이내 같은 메시지)
        if (roomSettings?.Config.TryGetValue("filterDuplicates", out var filterObj) == true &&
            filterObj is bool filter && filter)
        {
            var key = $"{entry.RoomId}:{entry.Message}";
            if (_lastLogTimes.TryGetValue(key, out var lastTime) &&
                (entry.Timestamp - lastTime).TotalSeconds < 1)
            {
                return false;
            }
            _lastLogTimes[key] = entry.Timestamp;
        }

        // 메시지 길이 필터링
        if (roomSettings?.Config.TryGetValue("minMessageLength", out var minLengthObj) == true &&
            minLengthObj is int minLength && entry.Message.Length < minLength)
        {
            return false;
        }

        return true;
    }

    private async Task SaveLogEntryAsync(LogEntry entry)
    {
        try
        {
            var logDir = Path.Combine(_context!.DataPath, "logs", entry.Timestamp.ToString("yyyy-MM"));
            Directory.CreateDirectory(logDir);

            var logFile = Path.Combine(logDir, $"{entry.Timestamp:yyyy-MM-dd}.log");
            var logLine = $"[{entry.Timestamp:HH:mm:ss}] [{entry.LogLevel}] [{entry.RoomId}] {entry.Message}";

            // 비동기 파일 쓰기
            await File.AppendAllTextAsync(logFile, logLine + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"로그 파일 저장 실패: {ex.Message}");
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
                    Name = "logLevel",
                    DisplayName = "로그 레벨",
                    Description = "이 방에서 기록할 로그 레벨을 선택하세요",
                    Type = ConfigFieldType.Dropdown,
                    IsRequired = false,
                    DefaultValue = "All",
                    Options = new List<string> { "Disabled", "Important", "All", "Debug" }
                },
                new ConfigField
                {
                    Name = "filterDuplicates",
                    DisplayName = "중복 메시지 필터링",
                    Description = "1초 이내 같은 메시지 중복 기록 방지",
                    Type = ConfigFieldType.Boolean,
                    IsRequired = false,
                    DefaultValue = false
                },
                new ConfigField
                {
                    Name = "minMessageLength",
                    DisplayName = "최소 메시지 길이",
                    Description = "이 길이보다 짧은 메시지는 기록하지 않음",
                    Type = ConfigFieldType.Number,
                    IsRequired = false,
                    DefaultValue = 1
                },
                new ConfigField
                {
                    Name = "logFormat",
                    DisplayName = "로그 형식",
                    Description = "로그 파일 저장 형식",
                    Type = ConfigFieldType.Dropdown,
                    IsRequired = false,
                    DefaultValue = "Text",
                    Options = new List<string> { "Text", "JSON", "CSV" }
                }
            },
            DefaultValues = new Dictionary<string, object>
            {
                { "logLevel", "All" },
                { "filterDuplicates", false },
                { "minMessageLength", 1 },
                { "logFormat", "Text" }
            }
        };
    }

    public async Task<bool> ValidateConfigAsync(object config)
    {
        try
        {
            if (config is not Dictionary<string, object> configDict)
                return false;

            // 로그 레벨 검증
            if (configDict.TryGetValue("logLevel", out var logLevelObj) &&
                logLevelObj is string logLevelStr)
            {
                if (!Enum.TryParse<LogLevel>(logLevelStr, out _))
                    return false;
            }

            // 최소 메시지 길이 검증
            if (configDict.TryGetValue("minMessageLength", out var minLengthObj))
            {
                if (minLengthObj is not int minLength || minLength < 0 || minLength > 1000)
                    return false;
            }

            // 로그 형식 검증
            if (configDict.TryGetValue("logFormat", out var formatObj) &&
                formatObj is string format)
            {
                var validFormats = new[] { "Text", "JSON", "CSV" };
                if (!validFormats.Contains(format))
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

    #region 공개 메서드

    public async Task SetGloballyEnabledAsync(bool enabled)
    {
        _globallyEnabled = enabled;
        var settings = new LoggerSettings { IsEnabled = enabled };
        await _context!.SetDataAsync("global_settings", settings);

        _context.ShowNotification($"📝 메시지 로그: {(enabled ? "전역 활성화" : "전역 비활성화")}됨");
    }

    public bool IsGloballyEnabled => _globallyEnabled;

    public int LogCount => _logCount;

    public async Task<List<LogEntry>> GetLogsAsync(DateTime date, string? roomId = null)
    {
        var logs = new List<LogEntry>();
        var logFile = Path.Combine(_context!.DataPath, "logs", date.ToString("yyyy-MM"), $"{date:yyyy-MM-dd}.log");

        if (!File.Exists(logFile)) return logs;

        try
        {
            var lines = await File.ReadAllLinesAsync(logFile);
            foreach (var line in lines)
            {
                if (TryParseLogLine(line, out var entry))
                {
                    if (roomId == null || entry.RoomId == roomId)
                    {
                        logs.Add(entry);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"로그 파일 읽기 실패: {ex.Message}");
        }

        return logs;
    }

    public async Task<List<string>> GetLogFilesAsync()
    {
        var logFiles = new List<string>();
        var logsDir = Path.Combine(_context!.DataPath, "logs");

        if (!Directory.Exists(logsDir)) return logFiles;

        try
        {
            var monthDirs = Directory.GetDirectories(logsDir);
            foreach (var monthDir in monthDirs)
            {
                var files = Directory.GetFiles(monthDir, "*.log");
                logFiles.AddRange(files);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"로그 파일 목록 조회 실패: {ex.Message}");
        }

        return logFiles;
    }

    public async Task<long> GetTotalLogSizeAsync()
    {
        long totalSize = 0;
        var logFiles = await GetLogFilesAsync();

        foreach (var file in logFiles)
        {
            try
            {
                var fileInfo = new FileInfo(file);
                totalSize += fileInfo.Length;
            }
            catch
            {
                // 파일 크기 조회 실패 시 무시
            }
        }

        return totalSize;
    }

    public async Task<Dictionary<string, int>> GetRoomStatisticsAsync(DateTime date)
    {
        var stats = new Dictionary<string, int>();
        var logs = await GetLogsAsync(date);

        foreach (var log in logs)
        {
            stats[log.RoomId] = stats.GetValueOrDefault(log.RoomId, 0) + 1;
        }

        return stats;
    }

    #endregion

    private bool TryParseLogLine(string line, out LogEntry entry)
    {
        entry = new LogEntry();

        try
        {
            // 향상된 로그 파싱 - [HH:mm:ss] [LogLevel] [RoomId] Message
            if (line.StartsWith("[") && line.Length > 30)
            {
                var firstBracketEnd = line.IndexOf(']');
                var secondBracketStart = line.IndexOf('[', firstBracketEnd);
                var secondBracketEnd = line.IndexOf(']', secondBracketStart);
                var thirdBracketStart = line.IndexOf('[', secondBracketEnd);
                var thirdBracketEnd = line.IndexOf(']', thirdBracketStart);

                if (firstBracketEnd > 0 && secondBracketEnd > secondBracketStart &&
                    thirdBracketEnd > thirdBracketStart)
                {
                    var timeStr = line.Substring(1, firstBracketEnd - 1);
                    var logLevelStr = line.Substring(secondBracketStart + 1, secondBracketEnd - secondBracketStart - 1);
                    var roomId = line.Substring(thirdBracketStart + 1, thirdBracketEnd - thirdBracketStart - 1);
                    var message = line.Substring(thirdBracketEnd + 2);

                    if (TimeSpan.TryParse(timeStr, out var time) &&
                        Enum.TryParse<LogLevel>(logLevelStr, out var logLevel))
                    {
                        entry.Timestamp = DateTime.Today.Add(time);
                        entry.LogLevel = logLevel;
                        entry.RoomId = roomId;
                        entry.Message = message;
                        entry.RawData = line;
                        return true;
                    }
                }
            }
        }
        catch
        {
            // 파싱 실패
        }

        return false;
    }

    public async Task ShutdownAsync()
    {
        await SetGloballyEnabledAsync(_globallyEnabled); // 설정 저장
        _context?.ShowNotification($"❌ {DisplayName} 플러그인이 종료되었습니다. (총 {_logCount}개 기록)");
    }
}

// 로그 레벨 열거형
public enum LogLevel
{
    Disabled = 0,
    Important = 1,
    All = 2,
    Debug = 3
}

// 로그 엔트리 클래스 (업데이트됨)
public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public string RoomId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string RawData { get; set; } = string.Empty;
    public LogLevel LogLevel { get; set; } = LogLevel.All;
}

// 로거 설정 클래스 (업데이트됨)
public class LoggerSettings
{
    public bool IsEnabled { get; set; } = true;
    public int MaxFileSize { get; set; } = 10 * 1024 * 1024; // 10MB
    public int MaxFiles { get; set; } = 100;
    public bool CompressOldLogs { get; set; } = false;
    public LogLevel DefaultLogLevel { get; set; } = LogLevel.All;
}