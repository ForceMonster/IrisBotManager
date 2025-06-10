using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace IrisBotManager.Core.Models
{
    /// <summary>
    /// 채팅방 정보
    /// </summary>
    public class RoomInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public DateTime? LastActivity { get; set; }
        public int MemberCount { get; set; }
        public bool IsActive { get; set; }
        public string Description { get; set; } = "";
        public List<string> Tags { get; set; } = new();
    }

    /// <summary>
    /// 채팅방 멤버 정보
    /// </summary>
    public class ChatMember
    {
        public string UserId { get; set; } = "";
        public string Nickname { get; set; } = "";
        public int MessageCount { get; set; }
        public DateTime LastSeen { get; set; }
        public bool IsActive { get; set; }
        public UserRole Role { get; set; } = UserRole.User;
        public string AvatarUrl { get; set; } = "";
    }

    /// <summary>
    /// 방별 플러그인 표시 정보 (개선된 버전)
    /// </summary>
    public class RoomPluginDisplayInfo : INotifyPropertyChanged
    {
        private bool _isEnabled;
        private bool _hasCustomSettings;
        private string _statusText = "";

        public string PluginName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public string Category { get; set; } = "";

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(StatusIcon));
                }
            }
        }

        public bool HasCustomSettings
        {
            get => _hasCustomSettings;
            set
            {
                if (_hasCustomSettings != value)
                {
                    _hasCustomSettings = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SettingsIcon));
                }
            }
        }

        public Dictionary<string, object> CustomConfig { get; set; } = new();
        public DateTime LastModified { get; set; } = DateTime.Now;
        public string ModifiedBy { get; set; } = "";

        // UI 편의 속성들
        public string StatusText => IsEnabled ? "✅ 활성화" : "❌ 비활성화";
        public string StatusIcon => IsEnabled ? "✅" : "❌";
        public string SettingsIcon => HasCustomSettings ? "🔧" : "⚙️";

        // 전역 상태 관련
        public bool IsGloballyEnabled { get; set; } = true;
        public string GlobalStatusText => IsGloballyEnabled ? "전역 활성화" : "전역 비활성화";
        public bool CanBeEnabled => IsGloballyEnabled;

        // 우선순위 표시
        public string PriorityText
        {
            get
            {
                if (!IsGloballyEnabled) return "전역 비활성화";
                if (HasCustomSettings) return "방별 설정";
                return "전역 설정 상속";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// 방별 설정 정보 (확장된 버전)
    /// </summary>
    public class RoomSettingsInfo
    {

        public string RoomId { get; set; } = "";
        public string RoomName { get; set; } = "";
        public string RoomDescription { get; set; } = "";
        public List<RoomPluginDisplayInfo> PluginSettings { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime LastModified { get; set; } = DateTime.Now;
        public string ModifiedBy { get; set; } = "";
        public int Version { get; set; } = 1;
        public Dictionary<string, object> Metadata { get; set; } = new();

        // 통계 정보
        public int EnabledPluginsCount => PluginSettings.Count(p => p.IsEnabled);
        public int TotalPluginsCount => PluginSettings.Count;
        public int CustomSettingsCount => PluginSettings.Count(p => p.HasCustomSettings);

        // 카테고리별 통계
        public Dictionary<string, int> GetCategoryStatistics()
        {
            return PluginSettings.GroupBy(p => p.Category)
                                 .ToDictionary(g => g.Key, g => g.Count());
        }

        // 최근 수정된 플러그인들
        public List<RoomPluginDisplayInfo> GetRecentlyModified(int count = 5)
        {
            return PluginSettings.OrderByDescending(p => p.LastModified)
                                 .Take(count)
                                 .ToList();
        }
    }

    /// <summary>
    /// 방 설정 변경 이벤트 정보 (확장된 버전)
    /// </summary>
    public class RoomSettingsChangedEventArgs : EventArgs
    {
        public string RoomId { get; set; } = "";
        public string PluginName { get; set; } = "";
        public RoomSettingsChangeType ChangeType { get; set; }
        public Dictionary<string, object> OldConfig { get; set; } = new();
        public Dictionary<string, object> NewConfig { get; set; } = new();
        public bool OldEnabled { get; set; }
        public bool NewEnabled { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string UserId { get; set; } = "";
        public string Description { get; set; } = "";
    }

    /// <summary>
    /// 방 설정 변경 타입
    /// </summary>
    public enum RoomSettingsChangeType
    {
        PluginEnabled,
        PluginDisabled,
        ConfigurationChanged,
        PluginAdded,
        PluginRemoved,
        BulkUpdate,
        ImportSettings,
        ResetSettings
    }

    /// <summary>
    /// 방 복사 옵션 (확장된 버전)
    /// </summary>
    public class RoomCopyOptions
    {
        public string SourceRoomId { get; set; } = "";
        public string TargetRoomId { get; set; } = "";
        public bool CopyEnabledStates { get; set; } = true;
        public bool CopyConfigurations { get; set; } = true;
        public List<string> ExcludedPlugins { get; set; } = new();
        public List<string> IncludedPlugins { get; set; } = new(); // 빈 리스트면 모든 플러그인
        public bool OverwriteExisting { get; set; } = false;
        public bool CopyOnlyEnabledPlugins { get; set; } = false;
        public bool PreserveCopyTimestamp { get; set; } = false;
        public string CopyReason { get; set; } = "사용자 복사";

        // 필터 옵션
        public List<string> IncludeCategories { get; set; } = new();
        public List<string> ExcludeCategories { get; set; } = new();
        public bool OnlyCustomConfigured { get; set; } = false;
    }

    /// <summary>
    /// 방 통계 정보 (확장된 버전)
    /// </summary>
    public class RoomStatistics
    {
        public string RoomId { get; set; } = "";
        public string RoomName { get; set; } = "";
        public int TotalMembers { get; set; }
        public int ActiveMembers { get; set; }
        public int TotalMessages { get; set; }
        public int EnabledPlugins { get; set; }
        public int TotalPlugins { get; set; }
        public DateTime LastActivity { get; set; }
        public Dictionary<string, int> PluginUsageStats { get; set; } = new();

        // 확장된 통계
        public Dictionary<string, int> CategoryUsageStats { get; set; } = new();
        public Dictionary<string, DateTime> PluginLastUsed { get; set; } = new();
        public int CustomConfiguredPlugins { get; set; }
        public double PluginUtilizationRate => TotalPlugins > 0 ? (double)EnabledPlugins / TotalPlugins * 100 : 0;

        // 활동 통계
        public TimeSpan TimeSinceLastActivity => DateTime.Now - LastActivity;
        public bool IsActiveRoom => TimeSinceLastActivity.TotalHours < 24;

        // 설정 복잡도
        public RoomComplexityLevel ComplexityLevel
        {
            get
            {
                if (CustomConfiguredPlugins == 0) return RoomComplexityLevel.Simple;
                if (CustomConfiguredPlugins <= 3) return RoomComplexityLevel.Moderate;
                if (CustomConfiguredPlugins <= 7) return RoomComplexityLevel.Complex;
                return RoomComplexityLevel.Advanced;
            }
        }
    }

    /// <summary>
    /// 방 복잡도 레벨
    /// </summary>
    public enum RoomComplexityLevel
    {
        Simple,      // 기본 설정만 사용
        Moderate,    // 일부 커스터마이징
        Complex,     // 다수 플러그인 커스터마이징
        Advanced     // 고급 설정 다수 사용
    }

    /// <summary>
    /// 방 필터 옵션
    /// </summary>
    public class RoomFilterOptions
    {
        public string? SearchText { get; set; }
        public bool ActiveOnly { get; set; } = false;
        public bool WithCustomSettings { get; set; } = false;
        public int? MinEnabledPlugins { get; set; }
        public int? MaxEnabledPlugins { get; set; }
        public DateTime? ModifiedAfter { get; set; }
        public DateTime? ModifiedBefore { get; set; }
        public List<string> IncludeRoomIds { get; set; } = new();
        public List<string> ExcludeRoomIds { get; set; } = new();
        public RoomComplexityLevel? ComplexityLevel { get; set; }
        public string? SortBy { get; set; } = "LastModified"; // LastModified, RoomName, EnabledPlugins, etc.
        public bool SortDescending { get; set; } = true;
    }

    /// <summary>
    /// 플러그인 설정 템플릿
    /// </summary>
    public class PluginSettingsTemplate
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Category { get; set; } = "";
        public List<TemplatePluginSetting> PluginSettings { get; set; } = new();
        public DateTime Created { get; set; } = DateTime.Now;
        public DateTime LastModified { get; set; } = DateTime.Now;
        public string CreatedBy { get; set; } = "";
        public List<string> Tags { get; set; } = new();
        public bool IsSystemTemplate { get; set; } = false;
        public int UsageCount { get; set; } = 0;
    }

    /// <summary>
    /// 템플릿 플러그인 설정
    /// </summary>
    public class TemplatePluginSetting
    {
        public string PluginName { get; set; } = "";
        public bool IsEnabled { get; set; } = true;
        public Dictionary<string, object> Configuration { get; set; } = new();
        public string Notes { get; set; } = "";
        public bool IsOptional { get; set; } = false;
    }

    /// <summary>
    /// 방별 설정 백업 정보
    /// </summary>
    public class RoomSettingsBackup
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string RoomId { get; set; } = "";
        public string RoomName { get; set; } = "";
        public RoomSettingsInfo Settings { get; set; } = new();
        public DateTime BackupTime { get; set; } = DateTime.Now;
        public string BackupReason { get; set; } = "";
        public string CreatedBy { get; set; } = "";
        public long BackupSize { get; set; }
        public string FilePath { get; set; } = "";
        public bool IsAutoBackup { get; set; } = false;
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// 방별 설정 동기화 정보
    /// </summary>
    public class RoomSyncInfo
    {
        public string RoomId { get; set; } = "";
        public string RoomName { get; set; } = "";
        public DateTime LastSyncTime { get; set; }
        public string LastSyncSource { get; set; } = "";
        public SyncStatus Status { get; set; } = SyncStatus.Unknown;
        public List<string> ConflictingPlugins { get; set; } = new();
        public List<string> PendingChanges { get; set; } = new();
        public string ErrorMessage { get; set; } = "";
    }

    /// <summary>
    /// 동기화 상태
    /// </summary>
    public enum SyncStatus
    {
        Unknown,
        Synchronized,
        OutOfSync,
        Conflict,
        Error,
        Pending
    }

    /// <summary>
    /// 방별 설정 검증 결과
    /// </summary>
    public class RoomSettingsValidationResult
    {
        public bool IsValid { get; set; } = true;
        public List<ValidationIssue> Issues { get; set; } = new();
        public List<ValidationWarning> Warnings { get; set; } = new();
        public Dictionary<string, object> Metadata { get; set; } = new();
        public DateTime ValidationTime { get; set; } = DateTime.Now;

        public bool HasErrors => Issues.Any(i => i.Severity == ValidationSeverity.Error);
        public bool HasWarnings => Issues.Any(i => i.Severity == ValidationSeverity.Warning) || Warnings.Any();
        public int ErrorCount => Issues.Count(i => i.Severity == ValidationSeverity.Error);
        public int WarningCount => Issues.Count(i => i.Severity == ValidationSeverity.Warning) + Warnings.Count;
    }

    /// <summary>
    /// 검증 이슈
    /// </summary>
    public class ValidationIssue
    {
        public string PluginName { get; set; } = "";
        public string FieldName { get; set; } = "";
        public ValidationSeverity Severity { get; set; }
        public string Message { get; set; } = "";
        public string SuggestedFix { get; set; } = "";
        public object? CurrentValue { get; set; }
        public object? ExpectedValue { get; set; }
    }

    /// <summary>
    /// 검증 경고
    /// </summary>
    public class ValidationWarning
    {
        public string PluginName { get; set; } = "";
        public string Message { get; set; } = "";
        public string Recommendation { get; set; } = "";
        public WarningType Type { get; set; }
    }

    /// <summary>
    /// 검증 심각도
    /// </summary>
    public enum ValidationSeverity
    {
        Info,
        Warning,
        Error,
        Critical
    }

    /// <summary>
    /// 경고 타입
    /// </summary>
    public enum WarningType
    {
        Performance,
        Security,
        Compatibility,
        Configuration,
        Deprecated,
        BestPractice
    }

    /// <summary>
    /// 방별 설정 내보내기 옵션
    /// </summary>
    public class RoomExportOptions
    {
        public bool IncludeDisabledPlugins { get; set; } = true;
        public bool IncludeCustomConfigurations { get; set; } = true;
        public bool IncludeMetadata { get; set; } = true;
        public bool IncludeTimestamps { get; set; } = true;
        public ExportFormat Format { get; set; } = ExportFormat.Json;
        public bool CompressOutput { get; set; } = false;
        public string? EncryptionPassword { get; set; }
        public List<string> ExcludeFields { get; set; } = new();
        public bool ValidateBeforeExport { get; set; } = true;
    }

    /// <summary>
    /// 내보내기 형식
    /// </summary>
    public enum ExportFormat
    {
        Json,
        Xml,
        Yaml,
        Csv,
        Excel
    }

    /// <summary>
    /// 방별 설정 가져오기 옵션
    /// </summary>
    public class RoomImportOptions
    {
        public bool OverwriteExisting { get; set; } = false;
        public bool MergeConfigurations { get; set; } = true;
        public bool ValidateAfterImport { get; set; } = true;
        public bool CreateBackupBeforeImport { get; set; } = true;
        public bool SkipInvalidEntries { get; set; } = true;
        public ImportConflictResolution ConflictResolution { get; set; } = ImportConflictResolution.Skip;
        public string? DecryptionPassword { get; set; }
        public List<string> OnlyImportPlugins { get; set; } = new();
        public List<string> ExcludePlugins { get; set; } = new();
    }

    /// <summary>
    /// 가져오기 충돌 해결 방식
    /// </summary>
    public enum ImportConflictResolution
    {
        Skip,           // 충돌 시 건너뛰기
        Overwrite,      // 덮어쓰기
        Merge,          // 병합
        Rename,         // 이름 변경
        Ask             // 사용자에게 묻기
    }
}