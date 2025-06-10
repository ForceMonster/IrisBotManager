using IrisBotManager.Core.Models;

namespace IrisBotManager.Core.Models
{
    // 로드된 플러그인 정보
    public class LoadedPluginInfo
    {
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public UserRole RequiredRole { get; set; }
        public string Category { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool SupportsRoomSettings { get; set; }
    }

    // 플러그인 스캔 결과
    public class PluginScanResult
    {
        public List<string> FoundFiles { get; set; } = new();
        public List<string> ExcludedFiles { get; set; } = new();
        public List<InvalidFileInfo> InvalidFiles { get; set; } = new();
        public List<NoPluginFileInfo> NoPluginFiles { get; set; } = new();
        public List<LoadedPluginInfo> LoadedPlugins { get; set; } = new();
        public List<DuplicateFileInfo> DuplicateFiles { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public int SubfoldersScanned { get; set; }
        public DateTime ScanTime { get; set; } = DateTime.Now;
    }

    // 중복 파일 정보
    public class DuplicateFileInfo
    {
        public string FileName { get; set; } = string.Empty;
        public List<string> AllFiles { get; set; } = new();
        public string SelectedFile { get; set; } = string.Empty;
        public string SelectionReason { get; set; } = string.Empty;
    }

    // 유효하지 않은 파일 정보
    public class InvalidFileInfo
    {
        public string FilePath { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public Exception? Exception { get; set; }
    }

    // 플러그인이 없는 파일 정보
    public class NoPluginFileInfo
    {
        public string FilePath { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public List<string> FoundTypes { get; set; } = new();
    }
}