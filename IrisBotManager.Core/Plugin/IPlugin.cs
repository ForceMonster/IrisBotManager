using IrisBotManager.Core.Models;

namespace IrisBotManager.Core.Plugin;

public interface IPlugin
{
    // 기본 정보
    string Name { get; }
    string DisplayName { get; }
    string Version { get; }
    string Description { get; }           // 플러그인 설명
    string Category { get; }              // 카테고리 (자동응답, 모니터링, 관리 등)
    string[] Dependencies { get; }        // 의존성 (다른 플러그인명)
    UserRole RequiredRole { get; }
    bool SupportsRoomSettings { get; }    // 방별 설정 지원 여부

    // 생명주기 메서드
    Task InitializeAsync(IPluginContext context);
    Task ProcessMessageAsync(string message, string roomId, PluginRoomSettings? roomSettings = null);
    Task ShutdownAsync();

    // 설정 관리
    PluginConfigSchema GetConfigSchema();     // 설정 스키마 정의
    Task<bool> ValidateConfigAsync(object config);  // 설정 유효성 검증
}

// 플러그인 방별 설정
public class PluginRoomSettings
{
    public string RoomId { get; set; } = string.Empty;
    public string PluginName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public Dictionary<string, object> Config { get; set; } = new();
    public DateTime LastModified { get; set; } = DateTime.Now;
}

// 플러그인 설정 스키마
public class PluginConfigSchema
{
    public List<ConfigField> Fields { get; set; } = new();
    public Dictionary<string, object> DefaultValues { get; set; } = new();
}

public class ConfigField
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ConfigFieldType Type { get; set; }
    public bool IsRequired { get; set; }
    public object? DefaultValue { get; set; }
    public List<string>? Options { get; set; } // For dropdown/radio
}

public enum ConfigFieldType
{
    Text,
    Number,
    Boolean,
    Dropdown,
    Radio,
    TextArea
}