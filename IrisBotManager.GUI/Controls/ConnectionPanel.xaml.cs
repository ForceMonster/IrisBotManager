using System.Windows;
using System.Windows.Controls;
using IrisBotManager.Core.Services;
using Newtonsoft.Json.Linq;

namespace IrisBotManager.GUI.Controls;

public partial class ConnectionPanel : UserControl
{
    private ConfigService? _configService;
    private WebSocketService? _webSocketService;
    private AuthService? _authService;

    public ConnectionPanel()
    {
        InitializeComponent();
    }

    public void Initialize(ConfigService configService, WebSocketService webSocketService, AuthService authService)
    {
        _configService = configService;
        _webSocketService = webSocketService;
        _authService = authService;

        HostBox.Text = _configService.Host;
        PortBox.Text = _configService.Port;

        _webSocketService.LogMessage += OnLogMessage;
    }

    private void OnLogMessage(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
            LogBox.ScrollToEnd();
        });
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_configService == null || _webSocketService == null) return;

        var host = HostBox.Text.Trim();
        var port = PortBox.Text.Trim();

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(port))
        {
            OnLogMessage("⚠️ Host 또는 Port 값이 비어 있습니다.");
            return;
        }

        _configService.Host = host;
        _configService.Port = port;

        OnLogMessage($"✅ 설정 저장 완료: {host}:{port}");

        // 연결 시도
        await _webSocketService.ConnectAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_webSocketService == null) return;

        try
        {
            OnLogMessage("📋 채팅방 목록 조회 중...");

            var result = await _webSocketService.QueryDatabaseAsync(
                "SELECT id, meta FROM chat_rooms ORDER BY id");

            if (result != null && result.Count > 0)
            {
                var roomItems = new List<ComboBoxItem>();

                foreach (var room in result)
                {
                    var roomData = JObject.FromObject(room);
                    var id = roomData["id"]?.ToString() ?? "unknown";
                    var name = "(제목 없음)";

                    var metaRaw = roomData["meta"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(metaRaw))
                    {
                        try
                        {
                            var metaArray = JArray.Parse(metaRaw);
                            foreach (var metaItem in metaArray) // Changed 'item' to 'metaItem' to avoid CS0136
                            {
                                if ((int?)metaItem["type"] == 16)
                                {
                                    var contentRaw = metaItem["content"]?.ToString();
                                    if (!string.IsNullOrEmpty(contentRaw))
                                    {
                                        var contentObj = JObject.Parse(contentRaw);
                                        name = contentObj["title"]?.ToString() ?? name;
                                    }
                                    break;
                                }
                            }
                        }
                        catch
                        {
                            // meta 파싱 오류 무시
                        }
                    }

                    var item = new ComboBoxItem
                    {
                        Content = $"{name} ({id})",
                        Tag = id
                    };
                    roomItems.Add(item);
                }

                RoomDropdown.ItemsSource = roomItems;
                if (roomItems.Count > 0)
                {
                    RoomDropdown.SelectedIndex = 0;
                }

                OnLogMessage($"✅ 채팅방 {roomItems.Count}개 조회됨");
            }
            else
            {
                OnLogMessage("⚠️ 조회된 채팅방이 없습니다.");
            }
        }
        catch (Exception ex)
        {
            OnLogMessage($"❌ 채팅방 조회 실패: {ex.Message}");
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_webSocketService == null) return;

        var message = MessageBox.Text.Trim();
        if (string.IsNullOrEmpty(message))
        {
            OnLogMessage("⚠️ 메시지를 입력하세요.");
            return;
        }

        var selectedRoom = RoomDropdown.SelectedItem as ComboBoxItem;
        if (selectedRoom?.Tag == null)
        {
            OnLogMessage("⚠️ 채팅방을 선택하세요.");
            return;
        }

        var roomId = selectedRoom.Tag.ToString()!;

        try
        {
            await _webSocketService.SendMessageAsync(roomId, message);
            MessageBox.Clear();
            OnLogMessage($"📤 메시지 전송: {message}");
        }
        catch (Exception ex)
        {
            OnLogMessage($"❌ 메시지 전송 실패: {ex.Message}");
        }
    }
}