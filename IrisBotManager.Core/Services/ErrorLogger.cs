using System;
using System.IO;
using System.Threading.Tasks;
using IrisBotManager.Core.Services;

namespace IrisBotManager.Core.Services
{
    public class ErrorLogger
    {
        private readonly string _logDirectory;
        private readonly object _fileLock = new object();

        public ErrorLogger(string baseDataPath)
        {
            _logDirectory = Path.Combine(baseDataPath, "error_logs");
            Directory.CreateDirectory(_logDirectory);
        }

        public async Task LogErrorAsync(string category, string message, string? stackTrace = null, string? additionalInfo = null)
        {
            try
            {
                var timestamp = DateTime.Now;
                var logEntry = CreateLogEntry(timestamp, category, "ERROR", message, stackTrace, additionalInfo);

                await WriteLogAsync(timestamp, logEntry);
            }
            catch (Exception ex)
            {
                // 로깅 실패 시 콘솔에만 출력 (무한 루프 방지)
                Console.WriteLine($"Error logging failed: {ex.Message}");
            }
        }

        public async Task LogWarningAsync(string category, string message, string? additionalInfo = null)
        {
            try
            {
                var timestamp = DateTime.Now;
                var logEntry = CreateLogEntry(timestamp, category, "WARNING", message, null, additionalInfo);

                await WriteLogAsync(timestamp, logEntry);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning logging failed: {ex.Message}");
            }
        }

        public async Task LogInfoAsync(string category, string message, string? additionalInfo = null)
        {
            try
            {
                var timestamp = DateTime.Now;
                var logEntry = CreateLogEntry(timestamp, category, "INFO", message, null, additionalInfo);

                await WriteLogAsync(timestamp, logEntry);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Info logging failed: {ex.Message}");
            }
        }

        private string CreateLogEntry(DateTime timestamp, string category, string level, string message, string? stackTrace, string? additionalInfo)
        {
            var entry = $"[{timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{category}] {message}";

            if (!string.IsNullOrEmpty(additionalInfo))
            {
                entry += $"\n    Additional Info: {additionalInfo}";
            }

            if (!string.IsNullOrEmpty(stackTrace))
            {
                entry += $"\n    Stack Trace: {stackTrace}";
            }

            return entry + "\n";
        }

        private async Task WriteLogAsync(DateTime timestamp, string logEntry)
        {
            var logFileName = $"error_{timestamp:yyyy-MM-dd}.log";
            var logFilePath = Path.Combine(_logDirectory, logFileName);

            // 파일 크기 체크 (10MB 제한)
            if (File.Exists(logFilePath))
            {
                var fileInfo = new FileInfo(logFilePath);
                if (fileInfo.Length > 10 * 1024 * 1024) // 10MB
                {
                    // 파일명에 시간 추가하여 새 파일 생성
                    logFileName = $"error_{timestamp:yyyy-MM-dd_HHmmss}.log";
                    logFilePath = Path.Combine(_logDirectory, logFileName);
                }
            }

            lock (_fileLock)
            {
                File.AppendAllText(logFilePath, logEntry);
            }
        }

        public void CleanupOldLogs(int keepDays = 30)
        {
            try
            {
                var cutoffDate = DateTime.Now.AddDays(-keepDays);
                var logFiles = Directory.GetFiles(_logDirectory, "error_*.log");

                foreach (var logFile in logFiles)
                {
                    var fileInfo = new FileInfo(logFile);
                    if (fileInfo.CreationTime < cutoffDate)
                    {
                        File.Delete(logFile);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Log cleanup failed: {ex.Message}");
            }
        }

        public string GetLogDirectory() => _logDirectory;
    }

    // 로그 카테고리 상수 클래스
    public static class LogCategories
    {
        public const string PLUGIN = "PLUGIN";
        public const string CONNECTION = "CONNECTION";
        public const string ADMIN_COMMAND = "ADMIN_COMMAND";
        public const string MESSAGE_PROCESSING = "MESSAGE_PROCESSING";
        public const string WEBSOCKET = "WEBSOCKET";
        public const string UI = "UI";
        public const string FILE_IO = "FILE_IO";
        public const string AUTO_REPLY = "AUTO_REPLY";
        public const string ROOM_SETTINGS = "ROOM_SETTINGS";
    }
}