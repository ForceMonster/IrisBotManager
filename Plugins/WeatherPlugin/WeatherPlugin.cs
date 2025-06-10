using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using IrisBotManager.Core.Models;
using IrisBotManager.Core.Plugin;

namespace SamplePlugins;

public class WeatherPlugin : IPlugin
{
    // 기본 정보
    public string Name => "Weather";
    public string DisplayName => "날씨 정보";
    public string Version => "1.0.1"; // 버전 업
    public string Description => "!날씨 [지역명] 명령어로 지역별 날씨 정보를 제공합니다. 대기질, 생활지수 등 상세한 날씨 정보를 포함합니다.";
    public string Category => "정보";
    public string[] Dependencies => Array.Empty<string>();
    public UserRole RequiredRole => UserRole.User;
    public bool SupportsRoomSettings => true;

    private IPluginContext? _context;

    public async Task InitializeAsync(IPluginContext context)
    {
        _context = context;

        // 메시지 구독
        context.SubscribeToMessages(OnMessageReceived);

        // 플러그인 로드 알림
        context.ShowNotification($"✅ {DisplayName} 플러그인이 로드되었습니다. (사용법: !날씨 [지역명])");
    }

    public async Task ProcessMessageAsync(string message, string roomId, PluginRoomSettings? roomSettings = null)
    {
        if (_context == null) return;

        var trimmedMessage = message.Trim();

        // !날씨 [지역명] 명령어만 처리
        if (trimmedMessage.StartsWith("!날씨 "))
        {
            await HandleWeatherCommandAsync(trimmedMessage, roomId, roomSettings);
        }
        // !날씨 (지역명 없이)는 사용법 안내
        else if (trimmedMessage == "!날씨")
        {
            await _context.SendMessageAsync(roomId, "⚠️ 사용법: !날씨 [지역명]\n예시: !날씨 서울, !날씨 부산");
        }
    }

    private async void OnMessageReceived(string message, string roomId)
    {
        // ProcessMessageAsync에서 처리하므로 여기서는 빈 구현
    }

    private async Task HandleWeatherCommandAsync(string message, string roomId, PluginRoomSettings? roomSettings)
    {
        try
        {
            var parts = message.Trim().Split(' ', 2);
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
            {
                await _context!.SendMessageAsync(roomId, "⚠️ 지역명을 입력해 주세요.\n사용법: !날씨 [지역명]\n예시: !날씨 서울, !날씨 부산");
                return;
            }

            string region = parts[1].Trim();

            _context!.ShowNotification($"🔍 [Weather] 날씨 조회 중: {region}");

            string weatherInfo = await GetWeatherFromRegionAsync(region);
            await _context.SendMessageAsync(roomId, weatherInfo);

            _context.ShowNotification($"✅ [Weather] 날씨 정보 전송 완료: {region}");
        }
        catch (Exception ex)
        {
            var errorMsg = $"❌ 날씨 조회 중 오류가 발생했습니다: {ex.Message}";
            await _context!.SendMessageAsync(roomId, errorMsg);
            _context.ShowNotification($"❌ [Weather] 오류: {ex.Message}");
        }
    }

    #region 설정 스키마 (간소화)

    public PluginConfigSchema GetConfigSchema()
    {
        return new PluginConfigSchema
        {
            Fields = new List<ConfigField>
            {
                new ConfigField
                {
                    Name = "responseFormat",
                    DisplayName = "응답 형식",
                    Description = "날씨 정보 표시 형식",
                    Type = ConfigFieldType.Dropdown,
                    IsRequired = false,
                    DefaultValue = "detailed",
                    Options = new List<string> { "simple", "detailed", "minimal" }
                },
                new ConfigField
                {
                    Name = "includeLifeIndex",
                    DisplayName = "생활지수 포함",
                    Description = "날씨 정보에 생활지수를 포함할지 선택",
                    Type = ConfigFieldType.Boolean,
                    IsRequired = false,
                    DefaultValue = true
                },
                new ConfigField
                {
                    Name = "includeAirQuality",
                    DisplayName = "대기질 정보 포함",
                    Description = "날씨 정보에 대기질 정보를 포함할지 선택",
                    Type = ConfigFieldType.Boolean,
                    IsRequired = false,
                    DefaultValue = true
                }
            },
            DefaultValues = new Dictionary<string, object>
            {
                { "responseFormat", "detailed" },
                { "includeLifeIndex", true },
                { "includeAirQuality", true }
            }
        };
    }

    public async Task<bool> ValidateConfigAsync(object config)
    {
        try
        {
            if (config is not Dictionary<string, object> configDict)
                return false;

            // 응답 형식 검증
            if (configDict.TryGetValue("responseFormat", out var formatObj) &&
                formatObj is string format)
            {
                var validFormats = new[] { "simple", "detailed", "minimal" };
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

    #region 설정 헬퍼 메서드 (간소화)

    private string GetResponseFormat(PluginRoomSettings? roomSettings)
    {
        if (roomSettings?.Config.TryGetValue("responseFormat", out var formatObj) == true &&
            formatObj is string format)
        {
            return format;
        }
        return "detailed"; // 기본값
    }

    private bool GetIncludeLifeIndex(PluginRoomSettings? roomSettings)
    {
        if (roomSettings?.Config.TryGetValue("includeLifeIndex", out var includeObj) == true &&
            includeObj is bool include)
        {
            return include;
        }
        return true; // 기본값
    }

    private bool GetIncludeAirQuality(PluginRoomSettings? roomSettings)
    {
        if (roomSettings?.Config.TryGetValue("includeAirQuality", out var includeObj) == true &&
            includeObj is bool include)
        {
            return include;
        }
        return true; // 기본값
    }

    #endregion

    #region 날씨 API (WeatherBot 통합)

    public static async Task<string> GetWeatherFromRegionAsync(string region)
    {
        if (string.IsNullOrWhiteSpace(region))
            return "❌ 지역명을 입력해 주세요.";

        var geo = GetGeocoderWithFallback(region);
        if (geo == null)
            return $"❌ '{region}' 지역 정보를 찾을 수 없습니다.\n다른 지역명으로 시도해보세요.";

        double lat = geo.Item1;
        double lon = geo.Item2;

        try
        {
            string url = $"https://www.kr-weathernews.com/mv3/if/main2_v2.fcgi?lat={lat}&lon={lon}";
            using var client = new HttpClient();

            // 타임아웃 설정 (10초)
            client.Timeout = TimeSpan.FromSeconds(10);

            string json = await client.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("state", out var state) || !root.TryGetProperty("current", out var current))
                return "❌ 날씨 정보를 가져올 수 없습니다.";

            string city = root.GetProperty("city").GetString() ?? region;
            string stateStr = state.GetString() ?? "";
            int wx = int.Parse(current.GetProperty("wx").GetString() ?? "100");
            string sstelop = GetSstelopMap().TryGetValue(wx, out var val1) ? val1 : "알수없음";
            string desc = GetDescriptionMap().TryGetValue(sstelop, out var d) ? d : "🌈 알 수 없음";

            string msg = "🌏 " + stateStr + "\n";
            msg += "🏙️ " + city + "\n";
            msg += "──────────────\n";
            msg += desc + "\n";
            msg += "🌡️ " + current.GetProperty("temp").GetString() + "° (체감 " + current.GetProperty("feeltemp").GetString() + "°)\n";
            msg += "☀️ " + current.GetProperty("tmax").GetString() + "°   🌛 " + current.GetProperty("tmin").GetString() + "°\n";
            msg += "💧 " + current.GetProperty("rhum").GetString() + "%\n";
            msg += "💨 " + current.GetProperty("wdir").GetString() + " " + current.GetProperty("wspd").GetString() + "m/s\n";
            msg += "🧭 " + current.GetProperty("press").GetString() + " hPa\n";
            msg += "🌅 " + root.GetProperty("sunrise").GetString() + "  🌇 " + root.GetProperty("sunset").GetString() + "\n";
            msg += "──────────────" + new string('\u200B', 500) + "\n";

            if (root.TryGetProperty("aq", out var aq))
            {
                msg += "　🌫 대기질 지수\n";
                var states = new[] { "좋음", "보통", "나쁨", "매우나쁨" };
                var emojis = new[] { "🟢", "🟡", "🟠", "🔴" };
                var list = new[] {
                    new { Key="khai", Range=new[]{51.0,101.0,251.0,501.0}, Label="통합대기지수", Unit="", Name="CAI" },
                    new { Key="pm10", Range=new[]{31.0,81.0,151.0,501.0}, Label="초미세먼지", Unit="㎍/㎥", Name="PM10" },
                    new { Key="pm25", Range=new[]{16.0,36.0,76.0,501.0}, Label="미세먼지", Unit="㎍/㎥", Name="PM2.5" },
                    new { Key="co", Range=new[]{2.1,9.1,15.1,501.0}, Label="일산화탄소", Unit="ppm", Name="CO" },
                    new { Key="no2", Range=new[]{0.031,0.061,0.201,5.0}, Label="이산화질소", Unit="ppm", Name="NO₂" },
                    new { Key="so2", Range=new[]{0.021,0.051,0.151,5.0}, Label="이산화황", Unit="ppm", Name="SO₂" },
                    new { Key="o3", Range=new[]{0.031,0.091,0.151,5.0}, Label="오존", Unit="ppm", Name="O₃" }
                };

                foreach (var item in list)
                {
                    if (!aq.TryGetProperty(item.Key, out var valElement)) continue;
                    if (!double.TryParse(valElement.GetString(), out double value)) continue;
                    int levelIndex = 0;
                    for (int j = 0; j < item.Range.Length; j++)
                    {
                        if (value < item.Range[j])
                        {
                            levelIndex = j;
                            break;
                        }
                    }
                    msg += emojis[levelIndex] + " " + item.Name + " : " + value + item.Unit +
                           " (" + item.Label + ", " + states[levelIndex] + ")\n";
                }
                msg += "관측 위치 : " + aq.GetProperty("loc").GetString() + "\n";
            }

            bool hasLifeIdx = root.TryGetProperty("life_idx", out var lifeIdx);
            bool hasSeasonIdx = root.TryGetProperty("season_idx", out var seasonIdx);
            if (hasLifeIdx || hasSeasonIdx)
            {
                msg += "──────────────\n";
                msg += "　📋 생활지수\n";

                var lifeMap = new Dictionary<string, string>
                {
                    { "세차지수", "🚗" }, { "달리기지수", "👟" }, { "빨래지수", "👕" }, { "수면지수", "🛏" }, { "우산지수", "☂" }
                };
                var seasonMap = new Dictionary<string, string>
                {
                    { "자외선지수", "☀" }, { "식중독지수", "🦠" }, { "꽃가루지수", "🌺" }
                };

                if (hasLifeIdx && lifeIdx.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in lifeIdx.EnumerateArray())
                    {
                        string name = item.GetProperty("name").GetString() ?? "";
                        string val2 = item.GetProperty("val").GetString() ?? "";
                        string cmt = item.GetProperty("cmt").GetString() ?? "";
                        string icon = lifeMap.TryGetValue(name, out var lifeIcon) ? lifeIcon : "📋";
                        msg += $"{icon} {val2} ({cmt})\n";
                    }
                }

                if (hasSeasonIdx && seasonIdx.ValueKind == JsonValueKind.Array)
                {
                    int count = 0;
                    foreach (var item in seasonIdx.EnumerateArray())
                    {
                        if (count++ >= 3) break;
                        string name = item.GetProperty("name").GetString() ?? "";
                        string val3 = item.GetProperty("val").GetString() ?? "";
                        string cmt = item.GetProperty("cmt").GetString() ?? "";
                        string icon = seasonMap.TryGetValue(name, out var seasonIcon) ? seasonIcon : "📋";
                        msg += $"{icon} {val3} ({cmt})\n";
                    }
                }
            }

            if (root.TryGetProperty("news", out var news) && news.TryGetProperty("title", out var newsTitle))
            {
                msg += "──────────────\n📰 " + newsTitle.GetString();
            }

            return msg;
        }
        catch (HttpRequestException httpEx)
        {
            return $"❌ 날씨 서비스에 연결할 수 없습니다: {httpEx.Message}";
        }
        catch (TaskCanceledException)
        {
            return "❌ 날씨 정보 요청이 시간 초과되었습니다. 다시 시도해주세요.";
        }
        catch (Exception ex)
        {
            return $"❌ 날씨 정보 처리 중 오류가 발생했습니다: {ex.Message}";
        }
    }

    public static Tuple<double, double>? GetGeocoderWithFallback(string region)
    {
        var result = GetGeocoderGoogle(region);
        if (result == null)
        {
            result = GetGeocoderOSM(region);
        }
        return result;
    }

    public static Tuple<double, double>? GetGeocoderGoogle(string region)
    {
        try
        {
            string url = "https://www.google.com/maps/search/" + Uri.EscapeDataString(region);
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            string html = client.GetStringAsync(url).Result;
            if (!html.Contains("/@"))
            {
                return null;
            }
            var match = Regex.Match(html, @"\/@([\d\.]+),([\d\.]+)");
            if (match.Success)
            {
                double lat = double.Parse(match.Groups[1].Value);
                double lon = double.Parse(match.Groups[2].Value);
                return Tuple.Create(lat, lon);
            }
        }
        catch { }
        return null;
    }

    public static Tuple<double, double>? GetGeocoderOSM(string region)
    {
        try
        {
            string url = "https://nominatim.openstreetmap.org/search?format=json&q=" + Uri.EscapeDataString(region);
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            string json = client.GetStringAsync(url).Result;

            var results = System.Text.Json.JsonSerializer.Deserialize<List<OSMResult>>(json);
            if (results != null && results.Count > 0)
            {
                double lat = double.Parse(results[0].lat);
                double lon = double.Parse(results[0].lon);
                return Tuple.Create(lat, lon);
            }
        }
        catch { }
        return null;
    }

    public class OSMResult
    {
        public string lat { get; set; } = "";
        public string lon { get; set; } = "";
    }

    private static Dictionary<int, string> GetSstelopMap() => new Dictionary<int, string>
    {
        {100,"1"},{123,"1"},{124,"1"},{130,"1"},{131,"1"},{500,"1"},
        {600,"2"},{101,"2"},{132,"2"},
        {102,"13"},{103,"13"},{106,"13"},{107,"13"},{120,"13"},{121,"13"},{140,"13"},
        {108,"13"},{112,"13"},{113,"13"},{114,"13"},{118,"13"},{122,"13"},{126,"13"},
        {127,"13"},{128,"13"},{129,"13"},{119,"13"},{125,"13"},
        {104,"21"},{105,"21"},{160,"21"},{170,"21"},{115,"21"},{116,"21"},{117,"21"},
        {181,"21"},{228,"21"},{229,"21"},{230,"21"},{281,"21"},
        {200,"4"},{209,"4"},{231,"4"},{201,"3"},{223,"3"},
        {202,"13"},{203,"13"},{206,"13"},{207,"13"},{220,"13"},{221,"13"},{240,"13"},
        {208,"13"},{204,"21"},{205,"21"},{250,"21"},{260,"21"},{270,"21"},
        {210,"5"},{211,"5"},{212,"13"},{213,"13"},{214,"13"},{218,"13"},
        {222,"13"},{224,"13"},{225,"13"},{226,"13"},{227,"13"},{219,"13"},
        {300,"10"},{304,"10"},{306,"10"},{328,"10"},{329,"10"},{350,"10"},{308,"10"},
        {301,"15"},{302,"10"},{303,"26"},{309,"26"},{322,"26"},{311,"15"},{316,"15"},
        {320,"15"},{323,"15"},{324,"15"},{325,"15"},{313,"10"},{317,"10"},{321,"10"},
        {314,"26"},{315,"26"},{326,"26"},{327,"26"},
        {400,"18"},{405,"18"},{425,"18"},{426,"18"},{427,"18"},{450,"18"},{340,"18"},
        {406,"18"},{407,"18"},{401,"23"},{402,"18"},{403,"31"},{409,"31"},
        {411,"36"},{420,"36"},{361,"36"},{413,"18"},{421,"18"},{371,"18"},
        {414,"31"},{422,"31"},{423,"31"},{424,"31"},{430,"18"},
        {550,"1"},{552,"2"},{553,"15"},{558,"15"},{562,"6"},{563,"13"},{568,"13"},
        {800,"39"},{850,"10"},{851,"15"},{852,"10"},{853,"10"},{854,"26"},
        {855,"15"},{861,"15"},{862,"10"},{863,"10"},{864,"26"},{865,"15"},
        {999,"1"},{871,"13"},{872,"13"},{873,"10"},{874,"31"},{882,"13"},{881,"13"},{884,"31"},{583,"15"},{582,"5"},{573,"15"}
    };

    private static Dictionary<string, string> GetDescriptionMap() => new Dictionary<string, string>
    {
        {"1", "🌞 맑음"}, {"2", "⛅ 구름 조금"}, {"3", "☁️ 구름 많음"},
        {"4", "🌫️ 흐림"}, {"5", "⛅ 흐린 후 차차 갬"}, {"6", "🌤️ 맑은 후 차차 흐려짐"},
        {"10", "🌧️ 흐리고 비"}, {"13", "🌧️ 차차 흐리고 비"}, {"15", "🌦️ 비온 후 갬"},
        {"18", "🌨️ 흐리고 눈"}, {"21", "🌨️ 차차 흐려져 눈"}, {"23", "🌨️ 눈온 후 갬"},
        {"26", "🌧️ 비 또는 눈"}, {"31", "🌨️ 눈 또는 비"}, {"36", "🌨️ 눈 또는 비 후 갬"}, {"39", "⛈️ 천둥번개"}
    };

    #endregion

    public async Task ShutdownAsync()
    {
        _context?.ShowNotification($"❌ {DisplayName} 플러그인이 종료되었습니다.");
        await Task.CompletedTask;
    }
}