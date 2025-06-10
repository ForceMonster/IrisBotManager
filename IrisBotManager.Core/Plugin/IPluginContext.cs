using IrisBotManager.Core.Models;

namespace IrisBotManager.Core.Plugin;

public interface IPluginContext
{
    string PluginName { get; }
    string DataPath { get; }

    // UI 확장 (object로 변경하여 WPF 의존성 제거)
    void AddTab(string header, object content, UserRole requiredRole = UserRole.User);
    void ShowNotification(string message);

    // 권한 확인
    bool HasPermission(string userId, UserRole requiredRole);
    bool ValidatePin(string pin, UserRole requiredRole);

    // 통신
    Task SendMessageAsync(string roomId, string message);
    void SubscribeToMessages(Action<string, string> handler); // message, roomId

    // 데이터 저장
    Task<T?> GetDataAsync<T>(string key);
    Task SetDataAsync<T>(string key, T value);

    // 설정
    string GetConfig(string key);
    void SetConfig(string key, string value);
}