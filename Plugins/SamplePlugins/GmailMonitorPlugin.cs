using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using IrisBotManager.Core.Models;
using IrisBotManager.Core.Plugin;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Timers;

namespace SamplePlugins;

public class GmailMonitorPlugin : IPlugin
{
    // 기본 정보
    public string Name => "GmailMonitor";
    public string DisplayName => "Gmail 모니터링";
    public string Version => "1.0.0";
    public string Description => "Gmail에서 Netflix 로그인 코드를 자동으로 감지하고 지정된 채팅방으로 전송합니다. 30초 간격으로 모니터링하며 방별로 알림 설정을 다르게 할 수 있습니다.";
    public string Category => "모니터링";
    public string[] Dependencies => Array.Empty<string>();
    public UserRole RequiredRole => UserRole.Admin;
    public bool SupportsRoomSettings => true;

    private IPluginContext? _context;
    private GmailService? _gmailService;
    private System.Timers.Timer? _gmailCheckTimer;
    private string? _defaultTargetRoom;
    private HashSet<string> _processedEmails = new();
    private bool _isMonitoring = false;

    // 설정 파일 경로들
    private string GmailCredentialsPath => Path.Combine(_context!.DataPath, "gmail_credentials.json");
    private string GmailTokenPath => Path.Combine(_context!.DataPath, "gmail_token.json");

    public async Task InitializeAsync(IPluginContext context)
    {
        _context = context;

        // 처리된 이메일 목록 로드
        await LoadProcessedEmailsAsync();

        // 설정 로드
        await LoadSettingsAsync();

        // 메시지 구독
        context.SubscribeToMessages(OnMessageReceived);

        // 플러그인 로드 알림
        context.ShowNotification($"✅ {DisplayName} 플러그인이 로드되었습니다.");

        // 저장된 설정이 있다면 자동 연결 시도
        if (File.Exists(GmailCredentialsPath) && File.Exists(GmailTokenPath))
        {
            try
            {
                await InitializeGmailServiceAsync();
            }
            catch
            {
                // 자동 연결 실패 시 무시 (수동 연결 필요)
            }
        }
    }

    public async Task ProcessMessageAsync(string message, string roomId, PluginRoomSettings? roomSettings = null)
    {
        if (_context == null) return;

        var trimmedMessage = message.Trim();

        // Gmail 관리자 명령어 처리
        if (trimmedMessage.StartsWith("!Gmail연동 ") ||
            trimmedMessage.StartsWith("!코드방설정 ") ||
            trimmedMessage.StartsWith("!Gmail상태 "))
        {
            var response = await HandleGmailCommandAsync(trimmedMessage, roomId, roomSettings);
            if (!string.IsNullOrEmpty(response))
            {
                await _context.SendMessageAsync(roomId, response);
            }
        }
    }

    private async void OnMessageReceived(string message, string roomId)
    {
        // ProcessMessageAsync에서 처리하므로 여기서는 빈 구현
    }

    private async Task<string> HandleGmailCommandAsync(string message, string roomId, PluginRoomSettings? roomSettings)
    {
        try
        {
            var parts = message.Trim().Split(' ');
            if (parts.Length < 2)
                return "⚠️ 형식: !명령어 PIN번호";

            var command = parts[0];
            var pin = parts[1];

            // PIN 검증
            if (!_context!.ValidatePin(pin, UserRole.Admin))
            {
                return "❌ 잘못된 PIN입니다.";
            }

            return command switch
            {
                "!Gmail연동" => await HandleGmailConnectAsync(),
                "!코드방설정" => await SetGmailTargetRoomAsync(roomId, roomSettings),
                "!Gmail상태" => await ShowGmailStatusAsync(roomSettings),
                _ => "⚠️ 알 수 없는 Gmail 명령어입니다."
            };
        }
        catch (Exception ex)
        {
            return $"❌ 명령어 처리 중 오류: {ex.Message}";
        }
    }

    private async Task<string> HandleGmailConnectAsync()
    {
        try
        {
            if (!File.Exists(GmailCredentialsPath))
            {
                return "❌ Gmail credentials.json 파일이 없습니다.\n" +
                       "📋 설정 방법:\n" +
                       "1. Google Cloud Console에서 OAuth2 클라이언트 생성\n" +
                       "2. credentials.json을 플러그인 데이터 폴더에 저장\n" +
                       $"3. 경로: {GmailCredentialsPath}";
            }

            await InitializeGmailServiceAsync();
            return "✅ Gmail 연동을 시작합니다.\n브라우저에서 Google 계정 인증을 완료해주세요.";
        }
        catch (Exception ex)
        {
            return $"❌ Gmail 연동 실패: {ex.Message}";
        }
    }

    private async Task<string> SetGmailTargetRoomAsync(string roomId, PluginRoomSettings? roomSettings)
    {
        try
        {
            // 현재 방을 기본 타겟으로 설정
            _defaultTargetRoom = roomId;
            await SaveSettingsAsync();

            return $"✅ 현재 채팅방이 Netflix 코드 수신 대상으로 설정됨\n📍 Room ID: {roomId}";
        }
        catch (Exception ex)
        {
            return $"❌ 코드방 설정 실패: {ex.Message}";
        }
    }

    private async Task<string> ShowGmailStatusAsync(PluginRoomSettings? roomSettings)
    {
        var status = _gmailService != null ? "✅ 연결됨" : "❌ 연결 안됨";
        var target = !string.IsNullOrEmpty(_defaultTargetRoom)
            ? _defaultTargetRoom
            : "❌ 설정 안됨";
        var monitoring = _isMonitoring ? "✅ 활성" : "❌ 비활성";

        // 방별 설정 확인
        var roomNotifications = GetRoomNotificationLevel(roomSettings);

        return $"📧 Gmail 상태 보고\n" +
               $"🔗 연결: {status}\n" +
               $"📍 기본 대상방: {target}\n" +
               $"🔍 모니터링: {monitoring}\n" +
               $"📨 처리된 이메일: {_processedEmails.Count}개\n" +
               $"🔔 현재 방 알림: {roomNotifications}";
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
                    Name = "isTargetRoom",
                    DisplayName = "코드 수신 방으로 설정",
                    Description = "이 방에서 Netflix 로그인 코드를 받을지 선택",
                    Type = ConfigFieldType.Boolean,
                    IsRequired = false,
                    DefaultValue = false
                },
                new ConfigField
                {
                    Name = "notificationLevel",
                    DisplayName = "알림 레벨",
                    Description = "코드 감지 시 알림 레벨",
                    Type = ConfigFieldType.Dropdown,
                    IsRequired = false,
                    DefaultValue = "Normal",
                    Options = new List<string> { "Silent", "Normal", "High", "Critical" }
                },
                new ConfigField
                {
                    Name = "customMessage",
                    DisplayName = "커스텀 메시지",
                    Description = "코드 전송 시 사용할 커스텀 메시지 (비어두면 기본 메시지)",
                    Type = ConfigFieldType.TextArea,
                    IsRequired = false,
                    DefaultValue = ""
                },
                new ConfigField
                {
                    Name = "autoForward",
                    DisplayName = "자동 전달",
                    Description = "다른 방으로도 자동 전달",
                    Type = ConfigFieldType.Boolean,
                    IsRequired = false,
                    DefaultValue = false
                }
            },
            DefaultValues = new Dictionary<string, object>
            {
                { "isTargetRoom", false },
                { "notificationLevel", "Normal" },
                { "customMessage", "" },
                { "autoForward", false }
            }
        };
    }

    public async Task<bool> ValidateConfigAsync(object config)
    {
        try
        {
            if (config is not Dictionary<string, object> configDict)
                return false;

            // 알림 레벨 검증
            if (configDict.TryGetValue("notificationLevel", out var levelObj) &&
                levelObj is string level)
            {
                var validLevels = new[] { "Silent", "Normal", "High", "Critical" };
                if (!validLevels.Contains(level))
                    return false;
            }

            // 커스텀 메시지 길이 검증
            if (configDict.TryGetValue("customMessage", out var messageObj) &&
                messageObj is string customMessage && customMessage.Length > 500)
            {
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

    #region Gmail 서비스 관리

    private async Task InitializeGmailServiceAsync()
    {
        try
        {
            if (!File.Exists(GmailCredentialsPath))
            {
                throw new FileNotFoundException("Gmail credentials.json 파일이 없습니다.");
            }

            UserCredential credential;
            using (var stream = new FileStream(GmailCredentialsPath, FileMode.Open, FileAccess.Read))
            {
                string credPath = Path.GetDirectoryName(GmailTokenPath)!;
                Directory.CreateDirectory(credPath);

                credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    GoogleClientSecrets.FromStream(stream).Secrets,
                    new[] { GmailService.Scope.GmailReadonly },
                    "user",
                    CancellationToken.None,
                    new FileDataStore(credPath, true));
            }

            _gmailService = new GmailService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = "IrisBotManager Gmail Monitor",
            });

            _context?.ShowNotification("✅ Gmail 서비스 초기화 완료");
            StartGmailMonitoring();
        }
        catch (Exception ex)
        {
            _context?.ShowNotification($"❌ Gmail 초기화 실패: {ex.Message}");
            throw;
        }
    }

    private void StartGmailMonitoring()
    {
        try
        {
            // 기존 타이머가 있다면 정지
            _gmailCheckTimer?.Stop();
            _gmailCheckTimer?.Dispose();

            // 30초마다 새 이메일 확인
            _gmailCheckTimer = new System.Timers.Timer(30000); // 30초
            _gmailCheckTimer.Elapsed += async (sender, e) => await CheckNewEmailsAsync();
            _gmailCheckTimer.AutoReset = true;
            _gmailCheckTimer.Start();

            _isMonitoring = true;
            _context?.ShowNotification("🔍 Gmail 모니터링 시작 (30초 간격)");
        }
        catch (Exception ex)
        {
            _context?.ShowNotification($"❌ Gmail 모니터링 시작 실패: {ex.Message}");
        }
    }

    private async Task CheckNewEmailsAsync()
    {
        try
        {
            if (_gmailService == null) return;

            var request = _gmailService.Users.Messages.List("me");
            request.Q = "from:info@account.netflix.com is:unread newer_than:1d"; // Netflix 이메일만 + 읽지 않은 것만 + 1일 이내
            request.MaxResults = 10;

            var response = await request.ExecuteAsync();
            if (response.Messages == null) return;

            foreach (var messageInfo in response.Messages)
            {
                if (_processedEmails.Contains(messageInfo.Id)) continue;

                var message = await _gmailService.Users.Messages.Get("me", messageInfo.Id).ExecuteAsync();
                await ProcessNetflixEmailAsync(message);

                _processedEmails.Add(messageInfo.Id);
                await SaveProcessedEmailsAsync();
            }
        }
        catch (Exception ex)
        {
            _context?.ShowNotification($"⚠️ Gmail 확인 중 오류: {ex.Message}");
        }
    }

    private async Task ProcessNetflixEmailAsync(Google.Apis.Gmail.v1.Data.Message message)
    {
        try
        {
            // 이메일 제목에서 로그인 코드 관련 확인
            var subject = message.Payload.Headers?.FirstOrDefault(h => h.Name == "Subject")?.Value ?? "";
            if (!subject.Contains("로그인 코드") && !subject.Contains("login code")) return;

            // 이메일 본문 추출
            string emailBody = ExtractEmailBody(message.Payload);

            // 코드 추출 (4자리 숫자 패턴)
            var codeMatch = Regex.Match(emailBody, @"(?:코드[:\s]*)?(\d{4})(?!\d)", RegexOptions.IgnoreCase);
            if (!codeMatch.Success)
            {
                // HTML 디코딩된 경우도 시도
                var decodedBody = System.Net.WebUtility.HtmlDecode(emailBody);
                codeMatch = Regex.Match(decodedBody, @"(?:코드[:\s]*)?(\d{4})(?!\d)", RegexOptions.IgnoreCase);
                if (!codeMatch.Success) return;
            }

            string code = codeMatch.Groups[1].Value;
            _context?.ShowNotification($"📧 Netflix 로그인 코드 발견: {code}");

            // 설정된 모든 방으로 전송
            await SendCodeToTargetRoomsAsync(code);
        }
        catch (Exception ex)
        {
            _context?.ShowNotification($"⚠️ Netflix 이메일 처리 오류: {ex.Message}");
        }
    }

    private async Task SendCodeToTargetRoomsAsync(string code)
    {
        var sentRooms = new List<string>();

        // 기본 타겟 방으로 전송
        if (!string.IsNullOrEmpty(_defaultTargetRoom) && _context != null)
        {
            var messageText = CreateCodeMessage(code, null);
            await _context.SendMessageAsync(_defaultTargetRoom, messageText);
            sentRooms.Add(_defaultTargetRoom);
        }

        // TODO: 방별 설정에서 isTargetRoom이 true인 방들로도 전송
        // 현재는 기본 타겟 방만 지원

        if (sentRooms.Any())
        {
            _context?.ShowNotification($"✅ 코드 전송 완료: {code} → {sentRooms.Count}개 방");
        }
        else
        {
            _context?.ShowNotification("⚠️ 코드 전송 대상 채팅방이 설정되지 않았습니다.");
        }
    }

    private string CreateCodeMessage(string code, PluginRoomSettings? roomSettings)
    {
        // 방별 커스텀 메시지 확인
        if (roomSettings?.Config.TryGetValue("customMessage", out var customMessageObj) == true &&
            customMessageObj is string customMessage && !string.IsNullOrEmpty(customMessage))
        {
            return customMessage.Replace("{code}", code);
        }

        // 기본 메시지
        return $"📧 Netflix 로그인 코드\n🔢 {code}\n⏰ 15분 후 만료";
    }

    private string GetRoomNotificationLevel(PluginRoomSettings? roomSettings)
    {
        if (roomSettings?.Config.TryGetValue("notificationLevel", out var levelObj) == true &&
            levelObj is string level)
        {
            return level;
        }
        return "Normal";
    }

    #endregion

    #region 데이터 관리

    private async Task LoadProcessedEmailsAsync()
    {
        try
        {
            var emails = await _context!.GetDataAsync<List<string>>("processed_emails");
            if (emails != null)
            {
                _processedEmails = new HashSet<string>(emails);
            }
        }
        catch (Exception ex)
        {
            _context?.ShowNotification($"⚠️ 처리된 이메일 목록 로드 실패: {ex.Message}");
        }
    }

    private async Task SaveProcessedEmailsAsync()
    {
        try
        {
            await _context!.SetDataAsync("processed_emails", _processedEmails.ToList());
        }
        catch (Exception ex)
        {
            _context?.ShowNotification($"⚠️ 처리된 이메일 목록 저장 실패: {ex.Message}");
        }
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            var settings = await _context!.GetDataAsync<GmailSettings>("settings");
            if (settings != null)
            {
                _defaultTargetRoom = settings.DefaultTargetRoom;
            }
        }
        catch (Exception ex)
        {
            _context?.ShowNotification($"⚠️ Gmail 설정 로드 실패: {ex.Message}");
        }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            var settings = new GmailSettings
            {
                DefaultTargetRoom = _defaultTargetRoom
            };
            await _context!.SetDataAsync("settings", settings);
        }
        catch (Exception ex)
        {
            _context?.ShowNotification($"⚠️ Gmail 설정 저장 실패: {ex.Message}");
        }
    }

    #endregion

    #region 유틸리티

    private string ExtractEmailBody(MessagePart payload)
    {
        if (payload.Body?.Data != null)
        {
            var data = payload.Body.Data.Replace('-', '+').Replace('_', '/');
            // Base64 패딩 추가
            while (data.Length % 4 != 0)
            {
                data += "=";
            }

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(data));
            }
            catch
            {
                return "";
            }
        }

        if (payload.Parts != null)
        {
            foreach (var part in payload.Parts)
            {
                if (part.MimeType == "text/plain" || part.MimeType == "text/html")
                {
                    var body = ExtractEmailBody(part);
                    if (!string.IsNullOrEmpty(body)) return body;
                }
            }
        }

        return "";
    }

    #endregion

    public async Task ShutdownAsync()
    {
        try
        {
            // 타이머 정지
            _gmailCheckTimer?.Stop();
            _gmailCheckTimer?.Dispose();
            _isMonitoring = false;

            // Gmail 서비스 정리
            _gmailService?.Dispose();

            // 데이터 저장
            await SaveProcessedEmailsAsync();
            await SaveSettingsAsync();

            _context?.ShowNotification($"❌ {DisplayName} 플러그인이 종료되었습니다.");
        }
        catch (Exception ex)
        {
            _context?.ShowNotification($"⚠️ 플러그인 종료 중 오류: {ex.Message}");
        }
    }
}

// Gmail 설정 클래스
public class GmailSettings
{
    public string? DefaultTargetRoom { get; set; }
    public int CheckIntervalSeconds { get; set; } = 30;
    public bool IsEnabled { get; set; } = true;
}