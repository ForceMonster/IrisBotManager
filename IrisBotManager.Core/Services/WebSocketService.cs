using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using IrisBotManager.Core.Models; // RoomInfo, ChatMember를 위한 using 추가

namespace IrisBotManager.Core.Services
{
    public class WebSocketService : IDisposable
    {
        private readonly ConfigService _configService;
        private ClientWebSocket? _webSocket;
        private bool _isConnected = false;
        private bool _isConnecting = false;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly HttpClient _httpClient;

        // 이벤트
        public event Action<bool>? ConnectionChanged;
        public event Action<string>? LogMessage;
        public event Action<string>? MessageReceived;
        // 🔧 추가: 이벤트 구독 상태 확인용 프로퍼티
        public bool HasMessageReceivedSubscribers => MessageReceived != null;

        public WebSocketService(ConfigService configService)
        {
            _configService = configService;
            _httpClient = new HttpClient();
        }

        public bool IsConnected => _isConnected;

        #region 연결 관리

        public async Task ConnectAsync()
        {
            if (_isConnected || _isConnecting)
                return;

            _isConnecting = true;
            LogMessage?.Invoke("WebSocket 연결 시도 중...");

            try
            {
                _cancellationTokenSource = new CancellationTokenSource();
                _webSocket = new ClientWebSocket();

                var uri = new Uri($"ws://{_configService.Host}:{_configService.Port}/ws");
                await _webSocket.ConnectAsync(uri, _cancellationTokenSource.Token);

                _isConnected = true;
                _isConnecting = false;

                ConnectionChanged?.Invoke(true);
                LogMessage?.Invoke("✅ WebSocket 연결 성공");

                // 메시지 수신 시작
                _ = Task.Run(ReceiveMessagesAsync);
            }
            catch (Exception ex)
            {
                _isConnecting = false;
                LogMessage?.Invoke($"❌ WebSocket 연결 실패: {ex.Message}");
                ConnectionChanged?.Invoke(false);
                throw;
            }
        }

        public async Task DisconnectAsync()
        {
            if (!_isConnected)
                return;

            try
            {
                _cancellationTokenSource?.Cancel();

                if (_webSocket?.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnect requested", CancellationToken.None);
                }

                _isConnected = false;
                ConnectionChanged?.Invoke(false);
                LogMessage?.Invoke("WebSocket 연결 해제됨");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"WebSocket 해제 중 오류: {ex.Message}");
            }
            finally
            {
                _webSocket?.Dispose();
                _webSocket = null;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        #endregion

        #region 메시지 수신

        private async Task ReceiveMessagesAsync()
        {
            var buffer = new byte[4096];
            var messageBuilder = new StringBuilder();

            try
            {
                while (_webSocket?.State == WebSocketState.Open && !_cancellationTokenSource!.Token.IsCancellationRequested)
                {
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            LogMessage?.Invoke("WebSocket 연결 종료됨");
                            _isConnected = false;
                            ConnectionChanged?.Invoke(false);
                            return;
                        }

                        messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    var message = messageBuilder.ToString();
                    messageBuilder.Clear();

                    // 메시지 수신 이벤트 발생
                    MessageReceived?.Invoke(message);
                }
            }
            catch (OperationCanceledException)
            {
                // 정상적인 취소
            }
            catch (WebSocketException ex)
            {
                LogMessage?.Invoke($"WebSocket 수신 오류: {ex.Message}");
                _isConnected = false;
                ConnectionChanged?.Invoke(false);
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"메시지 수신 중 예외: {ex.Message}");
                _isConnected = false;
                ConnectionChanged?.Invoke(false);
            }
        }

        #endregion

        #region 메시지 전송

        public async Task SendMessageAsync(string roomId, string message)
        {
            if (!_isConnected || _webSocket?.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("WebSocket이 연결되지 않았습니다.");
            }

            try
            {
                // 🔧 수정: 방 ID가 문자열인 경우 숫자 ID로 변환 시도
                var actualRoomId = roomId;

                // 만약 roomId가 "봇테스트" 같은 문자열이면, 실제 숫자 ID를 찾아야 함
                // 또는 서버가 문자열 방 ID를 지원하도록 API 수정 필요

                var payload = new
                {
                    room = actualRoomId, // 또는 숫자로 변환된 값
                    type = "text",
                    data = message
                };

                await SendPostAsync(payload);
                LogMessage?.Invoke($"메시지 전송 완료: {roomId}");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"메시지 전송 실패: {ex.Message}");
                throw;
            }
        }

        private async Task SendPostAsync(object payload)
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var url = $"http://{_configService.Host}:{_configService.Port}/reply";

            var response = await _httpClient.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"전송 실패 ({response.StatusCode}): {error}");
            }
        }

        #endregion

        #region 데이터베이스 쿼리

        /// <summary>
        /// 데이터베이스 쿼리 실행
        /// </summary>
        public async Task<List<Dictionary<string, object>>> QueryDatabaseAsync(string query, params object[] parameters)
        {
            try
            {
                var payload = new { query, bind = parameters ?? Array.Empty<object>() };
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var url = $"http://{_configService.Host}:{_configService.Port}/query";

                var response = await _httpClient.PostAsync(url, content);
                var responseText = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var errorObj = JObject.Parse(responseText);
                    var errorMessage = errorObj["message"]?.ToString() ?? "Unknown error";
                    throw new Exception($"SQL 오류 ({(int)response.StatusCode}): {errorMessage}");
                }

                var responseObj = JObject.Parse(responseText);
                var dataArray = responseObj["data"] as JArray;

                if (dataArray == null)
                    return new List<Dictionary<string, object>>();

                var result = new List<Dictionary<string, object>>();
                foreach (var item in dataArray)
                {
                    if (item is JObject jObj)
                    {
                        var dict = new Dictionary<string, object>();
                        foreach (var prop in jObj.Properties())
                        {
                            dict[prop.Name] = prop.Value?.ToObject<object>() ?? "";
                        }
                        result.Add(dict);
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"데이터베이스 쿼리 실패: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 채팅방 목록 조회 (개선된 버전)
        /// </summary>
        public async Task<List<RoomInfo>> GetChatRoomsAsync()
        {
            try
            {
                var query = "SELECT id, meta, link_id FROM chat_rooms ORDER BY id";
                var result = await QueryDatabaseAsync(query);

                var rooms = new List<RoomInfo>();

                foreach (var room in result)
                {
                    var roomId = room["id"]?.ToString() ?? "unknown";
                    var roomName = "(제목 없음)";

                    // 1차: meta에서 제목 추출 시도
                    var metaRaw = room["meta"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(metaRaw))
                    {
                        try
                        {
                            var metaArray = JArray.Parse(metaRaw);
                            foreach (var item in metaArray)
                            {
                                if ((int?)item["type"] == 16)
                                {
                                    var contentRaw = item["content"]?.ToString();
                                    if (!string.IsNullOrEmpty(contentRaw))
                                    {
                                        var contentObj = JObject.Parse(contentRaw);
                                        var title = contentObj["title"]?.ToString();
                                        if (!string.IsNullOrEmpty(title))
                                        {
                                            roomName = title;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // meta 파싱 실패 시 무시
                        }
                    }

                    // 2차: meta에서 제목을 못 가져왔으면 open_link에서 시도
                    if (roomName == "(제목 없음)")
                    {
                        var linkName = await GetRoomNameByIdAsync(roomId);
                        if (!string.IsNullOrEmpty(linkName))
                        {
                            roomName = linkName;
                        }
                    }

                    rooms.Add(new RoomInfo
                    {
                        Id = roomId,
                        Name = roomName,
                        DisplayName = $"{roomName} ({roomId})"
                    });
                }

                LogMessage?.Invoke($"채팅방 {rooms.Count}개 조회 완료");
                return rooms;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"채팅방 목록 조회 실패: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// open_link 테이블에서 방 이름 조회
        /// </summary>
        private async Task<string?> GetRoomNameByIdAsync(string roomId)
        {
            try
            {
                var query = @"
                    SELECT open_link.name
                    FROM chat_rooms
                    LEFT JOIN db2.open_link ON open_link.id = chat_rooms.link_id
                    WHERE chat_rooms.id = ?";

                var result = await QueryDatabaseAsync(query, roomId);

                if (result.Count > 0)
                {
                    return result[0]["name"]?.ToString();
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 활성 채팅방 목록 조회 (최근 활동 기준)
        /// </summary>
        public async Task<List<string>> GetActiveRoomsAsync(int daysBack = 7)
        {
            try
            {
                var query = @"
                    SELECT DISTINCT chat_id 
                    FROM chat_logs 
                    WHERE datetime(created_at) > datetime('now', '-{0} days')
                    ORDER BY chat_id";

                var result = await QueryDatabaseAsync(string.Format(query, daysBack));

                var activeRooms = new List<string>();
                foreach (var row in result)
                {
                    var roomId = row["chat_id"]?.ToString();
                    if (!string.IsNullOrEmpty(roomId))
                    {
                        activeRooms.Add(roomId);
                    }
                }

                return activeRooms;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"활성 채팅방 조회 실패: {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// 채팅방 멤버 조회
        /// </summary>
        public async Task<List<ChatMember>> GetChatRoomMembersAsync(string roomId, int daysBack = 30)
        {
            try
            {
                var query = @"
                    SELECT DISTINCT user_id, sender_name, COUNT(*) as message_count
                    FROM chat_logs 
                    WHERE chat_id = ? AND datetime(created_at) > datetime('now', '-{0} days')
                    GROUP BY user_id, sender_name
                    ORDER BY message_count DESC";

                var result = await QueryDatabaseAsync(string.Format(query, daysBack), roomId);

                var members = new List<ChatMember>();
                foreach (var row in result)
                {
                    members.Add(new ChatMember
                    {
                        UserId = row["user_id"]?.ToString() ?? "",
                        Nickname = row["sender_name"]?.ToString() ?? "",
                        MessageCount = Convert.ToInt32(row["message_count"] ?? 0)
                    });
                }

                return members;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"채팅방 멤버 조회 실패: {ex.Message}");
                return new List<ChatMember>();
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            try
            {
                DisconnectAsync().Wait(1000);
            }
            catch
            {
                // 정리 중 오류 무시
            }
            finally
            {
                _httpClient?.Dispose();
                _webSocket?.Dispose();
                _cancellationTokenSource?.Dispose();
            }
        }

        #endregion
    }

}