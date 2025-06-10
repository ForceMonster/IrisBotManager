using IrisBotManager.Core.Models;
using IrisBotManager.Core.Plugin;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace IrisBotManager.Core.Services;

public class PluginManager : IDisposable
{
    private readonly ConfigService _configService;
    private readonly AuthService _authService;
    private readonly WebSocketService _webSocketService;
    private readonly PluginStateManager _stateManager;

    // 🔧 추가: 오류 로깅 시스템
    private readonly ErrorLogger _errorLogger;

    private readonly List<IPlugin> _loadedPlugins = new();
    private readonly Dictionary<string, object> _pluginData = new();
    private readonly Dictionary<string, DateTime> _lastScanTimes = new();
    private readonly Dictionary<string, Dictionary<string, string>> _pluginConfigs = new();
    private FileSystemWatcher? _pluginWatcher;

    public event Action<string, object, UserRole>? TabAddRequested;
    public event Action<string>? NotificationRequested;

    // 플러그인 스캔 통계
    public PluginScanResult LastScanResult { get; private set; } = new();

    public PluginManager(ConfigService configService, AuthService authService,
                        WebSocketService webSocketService, PluginStateManager stateManager)
    {
        _configService = configService;
        _authService = authService;
        _webSocketService = webSocketService;
        _stateManager = stateManager;

        // 🔧 추가: ErrorLogger 초기화
        _errorLogger = new ErrorLogger(_configService.DataPath);

        // 플러그인 설정 로드
        LoadAllPluginConfigs();
    }

    #region 🔧 수정된 플러그인 로딩 메서드들

    public async Task LoadPluginsAsync()
    {
        var scanResult = new PluginScanResult();

        try
        {
            // 디버깅: 현재 실행 경로와 설정된 플러그인 경로 확인
            var currentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var configuredPluginPath = _configService.PluginPath;
            var pluginPath = Path.Combine(currentDirectory, configuredPluginPath);

            NotificationRequested?.Invoke($"🔍 플러그인 스캔 시작...");
            NotificationRequested?.Invoke($"📁 현재 실행 경로: {currentDirectory}");
            NotificationRequested?.Invoke($"📁 설정된 플러그인 경로: {configuredPluginPath}");
            NotificationRequested?.Invoke($"📁 전체 플러그인 경로: {pluginPath}");

            // 🔧 추가: 정보 로그 저장
            await _errorLogger.LogInfoAsync(LogCategories.PLUGIN, $"플러그인 스캔 시작: {pluginPath}");

            // 여러 가능한 경로들을 시도해봅니다
            var possiblePaths = new List<string>
            {
                pluginPath, // 설정된 경로
                Path.Combine(currentDirectory, "plugins"), // 기본 plugins 폴더
                Path.Combine(Path.GetDirectoryName(currentDirectory)!, "plugins"), // 상위 폴더의 plugins
                Path.Combine(currentDirectory, "bin", "Debug", "net9.0-windows", "plugins"), // 빌드 폴더
            };

            // SamplePlugins.csproj의 OutputPath에 맞춘 경로도 추가
            var samplePluginsPath = Path.Combine(currentDirectory, "plugins");
            if (!possiblePaths.Contains(samplePluginsPath))
            {
                possiblePaths.Add(samplePluginsPath);
            }

            string? foundPluginPath = null;
            foreach (var path in possiblePaths)
            {
                NotificationRequested?.Invoke($"🔍 경로 확인 중: {path}");
                if (Directory.Exists(path))
                {
                    var dllFiles = Directory.GetFiles(path, "*.dll", SearchOption.AllDirectories);
                    NotificationRequested?.Invoke($"📦 {path}에서 {dllFiles.Length}개 DLL 파일 발견");

                    if (dllFiles.Length > 0)
                    {
                        foundPluginPath = path;
                        foreach (var dll in dllFiles)
                        {
                            NotificationRequested?.Invoke($"📄 발견된 DLL: {Path.GetFileName(dll)}");
                        }
                        break;
                    }
                }
                else
                {
                    NotificationRequested?.Invoke($"❌ 경로가 존재하지 않음: {path}");
                }
            }

            if (foundPluginPath == null)
            {
                // 플러그인 폴더가 없으면 생성
                Directory.CreateDirectory(pluginPath);
                NotificationRequested?.Invoke($"📁 플러그인 폴더가 생성되었습니다: {pluginPath}");
                NotificationRequested?.Invoke("ℹ️ 플러그인 DLL 파일을 이 폴더에 복사하세요.");

                // 🔧 추가: 경고 로그 저장
                await _errorLogger.LogWarningAsync(LogCategories.PLUGIN, $"플러그인 폴더가 비어있음: {pluginPath}");

                LastScanResult = scanResult;
                return;
            }

            // 실제 사용할 경로 업데이트
            pluginPath = foundPluginPath;
            NotificationRequested?.Invoke($"✅ 플러그인 경로 확정: {pluginPath}");

            // 1단계: 모든 DLL 파일 재귀 검색
            var allDllFiles = ScanAllDllFiles(pluginPath, scanResult);

            // 2단계: 중복 파일 처리 (최신 파일만 선택)
            var uniqueDllFiles = SelectLatestFiles(allDllFiles, scanResult);

            // 3단계: 플러그인 로드
            await LoadPluginsFromFiles(uniqueDllFiles, scanResult);

            // 🔧 추가: StateManager에 플러그인 목록 등록
            var pluginNames = _loadedPlugins.Select(p => p.Name).ToList();
            _stateManager.InitializeNewPlugins(pluginNames);

            // 4단계: 파일 시스템 감시자 설정
            SetupFileSystemWatcher(pluginPath);

            // 스캔 결과 로그
            LogScanResult(scanResult);
            LastScanResult = scanResult;

            // 🔧 추가: 성공 로그 저장
            await _errorLogger.LogInfoAsync(LogCategories.PLUGIN, $"플러그인 로드 완료: {_loadedPlugins.Count}개 로드됨");
        }
        catch (Exception ex)
        {
            var errorMsg = $"플러그인 매니저 초기화 실패: {ex.Message}";
            scanResult.Errors.Add(errorMsg);
            NotificationRequested?.Invoke($"❌ {errorMsg}");
            NotificationRequested?.Invoke($"🔍 스택 트레이스: {ex.StackTrace}");

            // 🔧 추가: 오류 로그 저장
            await _errorLogger.LogErrorAsync(LogCategories.PLUGIN, errorMsg, ex.StackTrace, $"플러그인 경로: {_configService.PluginPath}");

            LastScanResult = scanResult;
        }
    }

    // 🔧 수정: 개별 플러그인 파일 로딩 강화
    private async Task LoadPluginFromFile(string filePath, PluginScanResult scanResult)
    {
        var fileName = Path.GetFileName(filePath);

        try
        {
            NotificationRequested?.Invoke($"🔍 플러그인 분석 중: {fileName}");

            if (!File.Exists(filePath))
            {
                var errorMsg = $"파일이 존재하지 않음: {fileName}";
                scanResult.Errors.Add(errorMsg);
                NotificationRequested?.Invoke($"❌ {errorMsg}");
                return;
            }

            // 🔧 추가: 파일 크기 및 날짜 확인
            var fileInfo = new FileInfo(filePath);
            NotificationRequested?.Invoke($"📊 파일 정보: {fileName} ({fileInfo.Length} bytes, {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss})");

            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(filePath);
                NotificationRequested?.Invoke($"✅ 어셈블리 로드 성공: {assembly.FullName}");
            }
            catch (Exception ex)
            {
                var errorMsg = $"어셈블리 로드 실패 [{fileName}]: {ex.Message}";
                scanResult.InvalidFiles.Add(new InvalidFileInfo
                {
                    FilePath = filePath,
                    Reason = errorMsg,
                    Exception = ex
                });
                NotificationRequested?.Invoke($"❌ {errorMsg}");

                // 🔧 추가: 상세 오류 로그 저장
                await _errorLogger.LogErrorAsync(LogCategories.PLUGIN, errorMsg, ex.StackTrace, $"파일 경로: {filePath}");
                return;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
                NotificationRequested?.Invoke($"🔍 타입 분석: {types.Length}개 타입 발견");
            }
            catch (ReflectionTypeLoadException ex)
            {
                var errorMsg = $"타입 로드 실패 [{fileName}]: {ex.Message}";
                scanResult.Errors.Add(errorMsg);
                NotificationRequested?.Invoke($"❌ {errorMsg}");

                // 🔧 추가: 로더 예외 상세 로그
                var loaderErrors = ex.LoaderExceptions?.Where(e => e != null).Select(e => e!.Message).ToList() ?? new List<string>();
                await _errorLogger.LogErrorAsync(LogCategories.PLUGIN, errorMsg, ex.StackTrace, $"로더 오류: {string.Join(", ", loaderErrors)}");

                foreach (var loaderException in ex.LoaderExceptions)
                {
                    if (loaderException != null)
                    {
                        NotificationRequested?.Invoke($"⚠️ 로더 예외: {loaderException.Message}");
                    }
                }
                return;
            }

            bool hasPluginInterface = false;
            foreach (var type in types)
            {
                try
                {
                    NotificationRequested?.Invoke($"🔍 타입 검사: {type.FullName}");

                    if (typeof(IPlugin).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
                    {
                        hasPluginInterface = true;
                        NotificationRequested?.Invoke($"✅ IPlugin 구현 타입 발견: {type.FullName}");

                        try
                        {
                            var plugin = Activator.CreateInstance(type) as IPlugin;
                            if (plugin != null)
                            {
                                NotificationRequested?.Invoke($"🧩 플러그인 인스턴스 생성 성공: {plugin.Name}");

                                // 🔧 추가: 플러그인 중복 확인
                                if (_loadedPlugins.Any(p => p.Name == plugin.Name))
                                {
                                    var warningMsg = $"중복 플러그인 무시: {plugin.Name} ({fileName})";
                                    NotificationRequested?.Invoke($"⚠️ {warningMsg}");
                                    await _errorLogger.LogWarningAsync(LogCategories.PLUGIN, warningMsg, $"파일: {filePath}");
                                    continue;
                                }

                                var context = new PluginContext(plugin.Name, this);
                                await plugin.InitializeAsync(context);
                                _loadedPlugins.Add(plugin);

                                // 🔧 추가: StateManager에 플러그인 등록
                                _stateManager.RegisterPlugin(plugin.Name, false); // 기본 비활성화

                                scanResult.LoadedPlugins.Add(new LoadedPluginInfo
                                {
                                    Name = plugin.Name,
                                    DisplayName = plugin.DisplayName,
                                    Version = plugin.Version,
                                    FilePath = filePath,
                                    RequiredRole = plugin.RequiredRole
                                });

                                NotificationRequested?.Invoke($"✅ 플러그인 로드 완료: {plugin.DisplayName} v{plugin.Version}");

                                // 🔧 추가: 성공 로그 저장
                                await _errorLogger.LogInfoAsync(LogCategories.PLUGIN, $"플러그인 로드 성공: {plugin.Name} v{plugin.Version}", $"파일: {filePath}");
                            }
                            else
                            {
                                var errorMsg = $"플러그인 인스턴스 생성 실패: {type.FullName}";
                                NotificationRequested?.Invoke($"❌ {errorMsg}");
                                await _errorLogger.LogErrorAsync(LogCategories.PLUGIN, errorMsg, null, $"파일: {filePath}");
                            }
                        }
                        catch (Exception ex)
                        {
                            var errorMsg = $"플러그인 초기화 실패 [{type.FullName}]: {ex.Message}";
                            scanResult.Errors.Add(errorMsg);
                            NotificationRequested?.Invoke($"❌ {errorMsg}");
                            await _errorLogger.LogErrorAsync(LogCategories.PLUGIN, errorMsg, ex.StackTrace, $"파일: {filePath}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    var errorMsg = $"타입 처리 실패 [{type.FullName}]: {ex.Message}";
                    NotificationRequested?.Invoke($"⚠️ {errorMsg}");
                    await _errorLogger.LogWarningAsync(LogCategories.PLUGIN, errorMsg, $"파일: {filePath}");
                }
            }

            if (!hasPluginInterface)
            {
                scanResult.NoPluginFiles.Add(new NoPluginFileInfo
                {
                    FilePath = filePath,
                    Reason = "IPlugin 인터페이스를 구현하는 클래스가 없음",
                    FoundTypes = types.Select(t => t.FullName ?? "Unknown").ToList()
                });
                NotificationRequested?.Invoke($"ℹ️ 플러그인 인터페이스 없음: {fileName}");
            }
        }
        catch (Exception ex)
        {
            var errorMsg = $"파일 처리 중 예상치 못한 오류 [{fileName}]: {ex.Message}";
            scanResult.Errors.Add(errorMsg);
            NotificationRequested?.Invoke($"❌ {errorMsg}");

            // 🔧 추가: 예상치 못한 오류 로그 저장
            await _errorLogger.LogErrorAsync(LogCategories.PLUGIN, errorMsg, ex.StackTrace, $"파일: {filePath}");
        }
    }

    #endregion

    // PluginManager.cs - 메시지 처리 로직 강화

    #region 🔧 수정된 메시지 처리 메서드

    public async Task ProcessMessageAsync(string message, string roomId)
    {
        var processedPlugins = 0;
        var failedPlugins = 0;

        // 🔧 추가: 플러그인 처리 시작 로깅
        NotificationRequested?.Invoke($"🔍 [PluginManager] 메시지 처리 시작 - 메시지: '{message}', 방: {roomId}");
        NotificationRequested?.Invoke($"📦 [PluginManager] 로드된 플러그인 목록: {string.Join(", ", _loadedPlugins.Select(p => p.Name))}");

        foreach (var plugin in _loadedPlugins)
        {
            try
            {
                // 🔧 추가: 개별 플러그인 처리 시작 로깅
                NotificationRequested?.Invoke($"🔍 [PluginManager] 플러그인 검사 시작: {plugin.Name}");

                // 플러그인 실행 여부 확인
                var shouldExecute = _stateManager.ShouldExecutePlugin(plugin.Name, roomId);
                NotificationRequested?.Invoke($"🎯 [PluginManager] {plugin.Name} 실행 여부: {(shouldExecute ? "✅ 실행" : "❌ 건너뛰기")}");

                if (!shouldExecute)
                {
                    NotificationRequested?.Invoke($"⏭️ [PluginManager] {plugin.Name} 건너뛰기 (비활성화 상태)");
                    continue;
                }

                // 🔧 추가: 실행할 플러그인 로깅
                NotificationRequested?.Invoke($"🚀 [PluginManager] {plugin.Name} 실행 시작...");

                // 방별 설정 가져오기
                var roomSettings = _stateManager.GetRoomSettings(roomId, plugin.Name);
                NotificationRequested?.Invoke($"⚙️ [PluginManager] {plugin.Name} 방별 설정: {(roomSettings != null ? "있음" : "없음")}");

                // 🔧 추가: 개별 플러그인 실행 시간 측정
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // 플러그인 실행
                await plugin.ProcessMessageAsync(message, roomId, roomSettings);

                stopwatch.Stop();
                processedPlugins++;

                NotificationRequested?.Invoke($"✅ [PluginManager] {plugin.Name} 실행 완료 ({stopwatch.ElapsedMilliseconds}ms)");

                // 🔧 추가: 긴 실행 시간 감지 (1초 이상)
                if (stopwatch.ElapsedMilliseconds > 1000)
                {
                    var warningMsg = $"플러그인 실행 시간 경고 [{plugin.Name}]: {stopwatch.ElapsedMilliseconds}ms";
                    NotificationRequested?.Invoke($"⚠️ {warningMsg}");
                    await _errorLogger.LogWarningAsync(LogCategories.MESSAGE_PROCESSING, warningMsg,
                        $"방: {roomId}, 메시지 길이: {message.Length}");
                }
            }
            catch (TimeoutException ex)
            {
                failedPlugins++;
                var errorMsg = $"플러그인 실행 타임아웃 [{plugin.Name}]: {ex.Message}";
                NotificationRequested?.Invoke($"⏰ [PluginManager] {errorMsg}");
                await _errorLogger.LogErrorAsync(LogCategories.MESSAGE_PROCESSING, errorMsg, ex.StackTrace);
            }
            catch (Exception ex)
            {
                failedPlugins++;
                var errorMsg = $"플러그인 실행 실패 [{plugin.Name}]: {ex.Message}";
                NotificationRequested?.Invoke($"❌ [PluginManager] {errorMsg}");
                NotificationRequested?.Invoke($"❌ [PluginManager] 스택 트레이스: {ex.StackTrace}");
                await _errorLogger.LogErrorAsync(LogCategories.MESSAGE_PROCESSING, errorMsg, ex.StackTrace);
            }
        }

        // 🔧 추가: 처리 결과 요약
        NotificationRequested?.Invoke($"📊 [PluginManager] 메시지 처리 완료 - 성공: {processedPlugins}개, 실패: {failedPlugins}개");

        if (processedPlugins == 0 && _loadedPlugins.Count > 0)
        {
            NotificationRequested?.Invoke($"⚠️ [PluginManager] 경고: 로드된 플러그인이 있지만 실행된 플러그인이 없습니다!");
            NotificationRequested?.Invoke($"🔧 [PluginManager] 플러그인 상태를 확인하세요 (전역/방별 활성화 상태)");
        }
    }
    #endregion

    #region 🔧 추가된 디버깅 메서드들

    /// <summary>
    /// 특정 플러그인의 실행 조건 상세 확인
    /// </summary>
    public string DiagnosePluginExecution(string pluginName, string roomId)
    {
        var diagnostic = new StringBuilder();
        diagnostic.AppendLine($"🔍 플러그인 '{pluginName}' 실행 진단 (방: {roomId})");
        diagnostic.AppendLine($"진단 시간: {DateTime.Now:HH:mm:ss}");
        diagnostic.AppendLine();

        try
        {
            // 1. 플러그인 존재 여부 확인
            var plugin = _loadedPlugins.FirstOrDefault(p => p.Name == pluginName);
            if (plugin == null)
            {
                diagnostic.AppendLine("❌ 플러그인을 찾을 수 없습니다!");
                diagnostic.AppendLine("   - 플러그인이 로드되었는지 확인하세요");
                diagnostic.AppendLine("   - 플러그인 이름이 정확한지 확인하세요");
                return diagnostic.ToString();
            }

            diagnostic.AppendLine($"✅ 플러그인 발견: {plugin.DisplayName} v{plugin.Version}");
            diagnostic.AppendLine($"   카테고리: {plugin.Category}");
            diagnostic.AppendLine($"   필요 권한: {plugin.RequiredRole}");
            diagnostic.AppendLine();

            // 2. StateManager 상태 확인
            var globallyEnabled = _stateManager.IsGloballyEnabled(pluginName);
            var roomEnabled = _stateManager.IsRoomEnabled(roomId, pluginName);
            var shouldExecute = _stateManager.ShouldExecutePlugin(pluginName, roomId);

            diagnostic.AppendLine("🔧 StateManager 상태:");
            diagnostic.AppendLine($"   전역 활성화: {(globallyEnabled ? "✅ 활성화" : "❌ 비활성화")}");
            diagnostic.AppendLine($"   방별 활성화: {(roomEnabled ? "✅ 활성화" : "❌ 비활성화")}");
            diagnostic.AppendLine($"   실행 여부: {(shouldExecute ? "✅ 실행함" : "❌ 실행 안함")}");
            diagnostic.AppendLine();

            // 3. 방별 설정 확인
            var roomSettings = _stateManager.GetRoomSettings(roomId, pluginName);
            if (roomSettings != null)
            {
                diagnostic.AppendLine("🏠 방별 설정:");
                diagnostic.AppendLine($"   활성화 상태: {roomSettings.IsEnabled}");
                diagnostic.AppendLine($"   설정 개수: {roomSettings.Config?.Count ?? 0}개");
                diagnostic.AppendLine($"   마지막 수정: {roomSettings.LastModified:yyyy-MM-dd HH:mm:ss}");
            }
            else
            {
                diagnostic.AppendLine("🏠 방별 설정: 없음");
            }
            diagnostic.AppendLine();

            // 4. 실행 불가 이유 분석
            if (!shouldExecute)
            {
                diagnostic.AppendLine("❌ 실행되지 않는 이유:");
                if (!globallyEnabled)
                {
                    diagnostic.AppendLine("   • 전역적으로 비활성화되어 있습니다");
                    diagnostic.AppendLine("   • 해결: 플러그인 탭에서 전역 활성화하세요");
                }
                else if (!roomEnabled)
                {
                    diagnostic.AppendLine("   • 이 방에서 비활성화되어 있습니다");
                    diagnostic.AppendLine("   • 해결: 방별 설정에서 활성화하세요");
                }
                else
                {
                    diagnostic.AppendLine("   • 알 수 없는 이유");
                    diagnostic.AppendLine("   • StateManager 로직을 확인하세요");
                }
            }
            else
            {
                diagnostic.AppendLine("✅ 실행 조건을 모두 만족합니다!");
            }

        }
        catch (Exception ex)
        {
            diagnostic.AppendLine($"❌ 진단 중 오류: {ex.Message}");
        }

        return diagnostic.ToString();
    }

    /// <summary>
    /// 모든 플러그인의 실행 조건 요약
    /// </summary>
    public string GetPluginExecutionSummary(string roomId)
    {
        var summary = new StringBuilder();
        summary.AppendLine($"📊 방 {roomId}의 플러그인 실행 상태 요약");
        summary.AppendLine($"확인 시간: {DateTime.Now:HH:mm:ss}");
        summary.AppendLine();

        try
        {
            var plugins = _loadedPlugins.ToList();
            var executableCount = 0;
            var totalCount = plugins.Count;

            summary.AppendLine($"총 플러그인: {totalCount}개");
            summary.AppendLine();

            foreach (var plugin in plugins)
            {
                var shouldExecute = _stateManager.ShouldExecutePlugin(plugin.Name, roomId);
                var status = shouldExecute ? "✅ 실행됨" : "❌ 실행 안됨";

                summary.AppendLine($"{status} {plugin.DisplayName}");

                if (shouldExecute)
                {
                    executableCount++;
                }
                else
                {
                    var globallyEnabled = _stateManager.IsGloballyEnabled(plugin.Name);
                    var reason = globallyEnabled ? "(방별 비활성화)" : "(전역 비활성화)";
                    summary.AppendLine($"     {reason}");
                }
            }

            summary.AppendLine();
            summary.AppendLine($"실행 가능한 플러그인: {executableCount}/{totalCount}개 ({(double)executableCount / totalCount * 100:F1}%)");

            if (executableCount == 0)
            {
                summary.AppendLine();
                summary.AppendLine("⚠️ 실행 가능한 플러그인이 없습니다!");
                summary.AppendLine("   1. 플러그인 탭에서 플러그인을 전역 활성화하세요");
                summary.AppendLine("   2. 방별 설정에서 플러그인을 활성화하세요");
            }
        }
        catch (Exception ex)
        {
            summary.AppendLine($"❌ 요약 생성 실패: {ex.Message}");
        }

        return summary.ToString();
    }

    #endregion

    #region 기존 메서드들 (향상된 오류 처리 포함)

    private List<FileInfo> ScanAllDllFiles(string pluginPath, PluginScanResult scanResult)
    {
        var dllFiles = new List<FileInfo>();

        try
        {
            NotificationRequested?.Invoke($"🔍 DLL 파일 검색 시작: {pluginPath}");

            var files = Directory.GetFiles(pluginPath, "*.dll", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    dllFiles.Add(fileInfo);
                    scanResult.FoundFiles.Add(file);

                    NotificationRequested?.Invoke($"📄 DLL 파일 발견: {fileInfo.Name} ({fileInfo.Length} bytes)");
                }
                catch (Exception ex)
                {
                    var errorMsg = $"파일 정보 조회 실패: {file} - {ex.Message}";
                    scanResult.Errors.Add(errorMsg);
                    NotificationRequested?.Invoke($"⚠️ {errorMsg}");
                    _ = _errorLogger.LogWarningAsync(LogCategories.FILE_IO, errorMsg);
                }
            }

            var subdirectories = Directory.GetDirectories(pluginPath, "*", SearchOption.AllDirectories);
            scanResult.SubfoldersScanned = subdirectories.Length;

            NotificationRequested?.Invoke($"📁 스캔된 하위 폴더: {subdirectories.Length}개");
            NotificationRequested?.Invoke($"📄 발견된 DLL 파일: {dllFiles.Count}개");
        }
        catch (Exception ex)
        {
            var errorMsg = $"DLL 파일 스캔 실패: {ex.Message}";
            scanResult.Errors.Add(errorMsg);
            NotificationRequested?.Invoke($"❌ {errorMsg}");
            _ = _errorLogger.LogErrorAsync(LogCategories.FILE_IO, errorMsg, ex.StackTrace, $"경로: {pluginPath}");
        }

        return dllFiles;
    }

    private List<FileInfo> SelectLatestFiles(List<FileInfo> allFiles, PluginScanResult scanResult)
    {
        var uniqueFiles = new List<FileInfo>();

        try
        {
            // 파일명별로 그룹화 (확장자 제외)
            var groupedFiles = allFiles.GroupBy(f => Path.GetFileNameWithoutExtension(f.Name))
                                     .ToList();

            NotificationRequested?.Invoke($"🔄 중복 파일 검사: {groupedFiles.Count}개 그룹");

            foreach (var group in groupedFiles)
            {
                var filesInGroup = group.ToList();

                if (filesInGroup.Count == 1)
                {
                    uniqueFiles.Add(filesInGroup[0]);
                    NotificationRequested?.Invoke($"📄 단일 파일: {filesInGroup[0].Name}");
                }
                else
                {
                    // 중복 파일 발견 - 최신 파일 선택
                    var latestFile = SelectLatestFile(filesInGroup);
                    uniqueFiles.Add(latestFile);

                    // 중복 정보 기록
                    var duplicateInfo = new DuplicateFileInfo
                    {
                        FileName = group.Key,
                        AllFiles = filesInGroup.Select(f => f.FullName).ToList(),
                        SelectedFile = latestFile.FullName,
                        SelectionReason = GetSelectionReason(latestFile, filesInGroup)
                    };
                    scanResult.DuplicateFiles.Add(duplicateInfo);

                    NotificationRequested?.Invoke($"🔄 중복 파일 처리: {group.Key} -> {latestFile.Name} 선택");

                    // 🔧 추가: 중복 파일 경고 로그
                    _ = _errorLogger.LogWarningAsync(LogCategories.FILE_IO,
                        $"중복 플러그인 파일 발견: {group.Key}",
                        $"선택된 파일: {latestFile.FullName}, 총 {filesInGroup.Count}개 파일");
                }
            }

            if (scanResult.DuplicateFiles.Any())
            {
                NotificationRequested?.Invoke($"🔄 중복 파일 {scanResult.DuplicateFiles.Count}개 그룹 처리됨");
            }
        }
        catch (Exception ex)
        {
            var errorMsg = $"중복 파일 처리 실패: {ex.Message}";
            scanResult.Errors.Add(errorMsg);
            NotificationRequested?.Invoke($"❌ {errorMsg}");
            _ = _errorLogger.LogErrorAsync(LogCategories.FILE_IO, errorMsg, ex.StackTrace);
        }

        return uniqueFiles;
    }

    private async Task LoadPluginsFromFiles(List<FileInfo> dllFiles, PluginScanResult scanResult)
    {
        NotificationRequested?.Invoke($"🧩 플러그인 로드 시작 ({dllFiles.Count}개 파일)");

        foreach (var fileInfo in dllFiles)
        {
            try
            {
                NotificationRequested?.Invoke($"🔍 플러그인 로드 시도: {fileInfo.Name}");
                await LoadPluginFromFile(fileInfo.FullName, scanResult);
            }
            catch (Exception ex)
            {
                var errorMsg = $"플러그인 파일 로드 실패 [{fileInfo.Name}]: {ex.Message}";
                scanResult.Errors.Add(errorMsg);
                NotificationRequested?.Invoke($"❌ {errorMsg}");
                await _errorLogger.LogErrorAsync(LogCategories.PLUGIN, errorMsg, ex.StackTrace, $"파일: {fileInfo.FullName}");
            }
        }

        NotificationRequested?.Invoke($"✅ 플러그인 로드 완료: {_loadedPlugins.Count}개 로드됨");
    }

    public List<IPlugin> GetLoadedPlugins()
    {
        return _loadedPlugins.ToList();
    }

    public IPlugin? GetPlugin(string name)
    {
        return _loadedPlugins.FirstOrDefault(p => p.Name == name);
    }

    public PluginScanResult GetLastScanResult()
    {
        return LastScanResult;
    }

    #endregion

    #region 설정 관리

    private void LoadAllPluginConfigs()
    {
        try
        {
            var configPath = Path.Combine(_configService.DataPath, "plugin_configs");

            if (!Directory.Exists(configPath))
            {
                Directory.CreateDirectory(configPath);
                return;
            }

            var configFiles = Directory.GetFiles(configPath, "*.json");

            foreach (var configFile in configFiles)
            {
                try
                {
                    var pluginName = Path.GetFileNameWithoutExtension(configFile);
                    var json = File.ReadAllText(configFile);
                    var config = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

                    if (config != null)
                    {
                        _pluginConfigs[pluginName] = config;
                        NotificationRequested?.Invoke($"✅ 플러그인 설정 로드: {pluginName}");
                    }
                }
                catch (Exception ex)
                {
                    NotificationRequested?.Invoke($"⚠️ 플러그인 설정 로드 실패 [{Path.GetFileName(configFile)}]: {ex.Message}");
                    _ = _errorLogger.LogWarningAsync(LogCategories.PLUGIN, $"설정 로드 실패: {configFile}", ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            NotificationRequested?.Invoke($"⚠️ 플러그인 설정 디렉토리 액세스 실패: {ex.Message}");
            _ = _errorLogger.LogErrorAsync(LogCategories.PLUGIN, "설정 디렉토리 액세스 실패", ex.StackTrace);
        }
    }

    private void SavePluginConfig(string pluginName, Dictionary<string, string> config)
    {
        try
        {
            var configPath = Path.Combine(_configService.DataPath, "plugin_configs");
            Directory.CreateDirectory(configPath);

            var configFile = Path.Combine(configPath, $"{pluginName}.json");
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(configFile, json);
            _pluginConfigs[pluginName] = config;
        }
        catch (Exception ex)
        {
            NotificationRequested?.Invoke($"⚠️ 플러그인 설정 저장 실패 [{pluginName}]: {ex.Message}");
            _ = _errorLogger.LogErrorAsync(LogCategories.PLUGIN, $"설정 저장 실패: {pluginName}", ex.StackTrace);
        }
    }

    #endregion

    #region 파일 시스템 감시

    private void SetupFileSystemWatcher(string pluginPath)
    {
        try
        {
            _pluginWatcher?.Dispose();

            _pluginWatcher = new FileSystemWatcher(pluginPath)
            {
                Filter = "*.dll",
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.FileName
            };

            _pluginWatcher.Changed += OnPluginFileChanged;
            _pluginWatcher.Created += OnPluginFileChanged;
            _pluginWatcher.Deleted += OnPluginFileChanged;
            _pluginWatcher.EnableRaisingEvents = true;

            NotificationRequested?.Invoke($"👁️ 파일 시스템 감시 시작: {pluginPath}");
        }
        catch (Exception ex)
        {
            NotificationRequested?.Invoke($"⚠️ 파일 시스템 감시 설정 실패: {ex.Message}");
            _ = _errorLogger.LogWarningAsync(LogCategories.PLUGIN, "파일 시스템 감시 설정 실패", ex.Message);
        }
    }

    private async void OnPluginFileChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            // 빠른 연속 변경 이벤트 방지
            var key = e.FullPath;
            var now = DateTime.Now;

            if (_lastScanTimes.TryGetValue(key, out var lastScan) &&
                (now - lastScan).TotalMilliseconds < 1000)
            {
                return;
            }

            _lastScanTimes[key] = now;

            NotificationRequested?.Invoke($"📁 파일 변경 감지: {e.Name} ({e.ChangeType})");
            await ReloadChangedPlugin(e.FullPath, e.ChangeType);
        }
        catch (Exception ex)
        {
            NotificationRequested?.Invoke($"⚠️ 파일 변경 처리 실패: {ex.Message}");
            _ = _errorLogger.LogWarningAsync(LogCategories.PLUGIN, "파일 변경 처리 실패", ex.Message);
        }
    }

    private async Task ReloadChangedPlugin(string filePath, WatcherChangeTypes changeType)
    {
        try
        {
            var fileName = Path.GetFileName(filePath);

            switch (changeType)
            {
                case WatcherChangeTypes.Created:
                case WatcherChangeTypes.Changed:
                    NotificationRequested?.Invoke($"🔄 플러그인 재로드: {fileName}");

                    // 기존 플러그인 언로드
                    await UnloadPluginByFile(filePath);

                    // 새 플러그인 로드
                    if (File.Exists(filePath))
                    {
                        var scanResult = new PluginScanResult();
                        await LoadPluginFromFile(filePath, scanResult);

                        if (scanResult.LoadedPlugins.Any())
                        {
                            NotificationRequested?.Invoke($"✅ 플러그인 재로드 완료: {fileName}");
                        }
                    }
                    break;

                case WatcherChangeTypes.Deleted:
                    NotificationRequested?.Invoke($"🗑️ 플러그인 삭제됨: {fileName}");
                    await UnloadPluginByFile(filePath);
                    break;
            }
        }
        catch (Exception ex)
        {
            NotificationRequested?.Invoke($"⚠️ 플러그인 재로드 실패: {ex.Message}");
            _ = _errorLogger.LogErrorAsync(LogCategories.PLUGIN, "플러그인 재로드 실패", ex.StackTrace);
        }
    }

    private async Task UnloadPluginByFile(string filePath)
    {
        try
        {
            var fileName = Path.GetFileName(filePath);
            var pluginsToRemove = new List<IPlugin>();

            foreach (var plugin in _loadedPlugins)
            {
                try
                {
                    var pluginAssembly = plugin.GetType().Assembly;
                    var pluginFile = Path.GetFileName(pluginAssembly.Location);

                    if (string.Equals(pluginFile, fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        await plugin.ShutdownAsync();
                        pluginsToRemove.Add(plugin);
                        NotificationRequested?.Invoke($"🔄 플러그인 언로드: {plugin.DisplayName}");
                    }
                }
                catch (Exception ex)
                {
                    NotificationRequested?.Invoke($"⚠️ 플러그인 언로드 실패 [{plugin.Name}]: {ex.Message}");
                    pluginsToRemove.Add(plugin); // 오류가 있어도 목록에서 제거
                    _ = _errorLogger.LogWarningAsync(LogCategories.PLUGIN, $"플러그인 언로드 실패: {plugin.Name}", ex.Message);
                }
            }

            foreach (var plugin in pluginsToRemove)
            {
                _loadedPlugins.Remove(plugin);
            }
        }
        catch (Exception ex)
        {
            NotificationRequested?.Invoke($"⚠️ 플러그인 언로드 프로세스 실패: {ex.Message}");
            _ = _errorLogger.LogErrorAsync(LogCategories.PLUGIN, "플러그인 언로드 프로세스 실패", ex.StackTrace);
        }
    }

    #endregion

    #region 유틸리티 메서드들

    private FileInfo SelectLatestFile(List<FileInfo> files)
    {
        return files.OrderByDescending(f => f.LastWriteTime).First();
    }

    private string GetSelectionReason(FileInfo selectedFile, List<FileInfo> allFiles)
    {
        return $"최신 수정일 기준 선택 ({selectedFile.LastWriteTime:yyyy-MM-dd HH:mm:ss})";
    }

    private void LogScanResult(PluginScanResult scanResult)
    {
        var summary = new List<string>();

        summary.Add($"✅ 플러그인 스캔 완료: {scanResult.FoundFiles.Count}개 DLL 발견");

        if (scanResult.SubfoldersScanned > 0)
            summary.Add($"📁 서브폴더: {scanResult.SubfoldersScanned}개 폴더 스캔됨");

        if (scanResult.DuplicateFiles.Any())
            summary.Add($"🔄 중복 파일: {scanResult.DuplicateFiles.Count}개 그룹 → 최신 버전 선택");

        if (scanResult.LoadedPlugins.Any())
            summary.Add($"🧩 로드된 플러그인: {scanResult.LoadedPlugins.Count}개");

        if (scanResult.ExcludedFiles.Any())
            summary.Add($"⏭️ 제외된 파일: {scanResult.ExcludedFiles.Count}개");

        if (scanResult.InvalidFiles.Any())
            summary.Add($"⚠️ 무효한 파일: {scanResult.InvalidFiles.Count}개");

        if (scanResult.NoPluginFiles.Any())
            summary.Add($"📄 플러그인 없는 DLL: {scanResult.NoPluginFiles.Count}개");

        if (scanResult.Errors.Any())
            summary.Add($"❌ 오류: {scanResult.Errors.Count}개");

        foreach (var line in summary)
        {
            NotificationRequested?.Invoke(line);
        }

        // 오류 상세 출력
        if (scanResult.Errors.Any())
        {
            NotificationRequested?.Invoke("❌ 오류 상세:");
            foreach (var error in scanResult.Errors.Take(5)) // 처음 5개만
            {
                NotificationRequested?.Invoke($"   • {error}");
            }
            if (scanResult.Errors.Count > 5)
            {
                NotificationRequested?.Invoke($"   ... 총 {scanResult.Errors.Count}개 오류");
            }
        }
    }

    #endregion

    #region 데이터 관리

    internal void AddTab(string header, object content, UserRole requiredRole)
    {
        TabAddRequested?.Invoke(header, content, requiredRole);
    }

    internal void ShowNotification(string message)
    {
        NotificationRequested?.Invoke(message);
    }

    internal async Task<T?> GetPluginDataAsync<T>(string pluginName, string key)
    {
        var fullKey = $"{pluginName}_{key}";

        if (_pluginData.TryGetValue(fullKey, out var value) && value is T typedValue)
        {
            return typedValue;
        }

        // 파일에서 로드 시도
        try
        {
            var dataPath = Path.Combine(_configService.DataPath, "plugin_data", pluginName, $"{key}.json");
            if (File.Exists(dataPath))
            {
                var json = await File.ReadAllTextAsync(dataPath);
                var data = JsonSerializer.Deserialize<T>(json);
                if (data != null)
                {
                    _pluginData[fullKey] = data;
                    return data;
                }
            }
        }
        catch (Exception ex)
        {
            NotificationRequested?.Invoke($"⚠️ 플러그인 데이터 로드 실패 [{pluginName}.{key}]: {ex.Message}");
            _ = _errorLogger.LogWarningAsync(LogCategories.PLUGIN, $"데이터 로드 실패: {pluginName}.{key}", ex.Message);
        }

        return default(T);
    }

    internal async Task SetPluginDataAsync<T>(string pluginName, string key, T value)
    {
        var fullKey = $"{pluginName}_{key}";
        _pluginData[fullKey] = value!;

        // 파일에 저장
        try
        {
            var dataDir = Path.Combine(_configService.DataPath, "plugin_data", pluginName);
            Directory.CreateDirectory(dataDir);

            var dataPath = Path.Combine(dataDir, $"{key}.json");
            var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(dataPath, json);
        }
        catch (Exception ex)
        {
            NotificationRequested?.Invoke($"⚠️ 플러그인 데이터 저장 실패 [{pluginName}.{key}]: {ex.Message}");
            _ = _errorLogger.LogErrorAsync(LogCategories.PLUGIN, $"데이터 저장 실패: {pluginName}.{key}", ex.StackTrace);
        }
    }

    #endregion

    #region 메시지 파싱

    // PluginManager.cs - 간소화된 ParseMessage (chat_id 중심)

    /// <summary>
    /// WebSocket 메시지를 파싱하여 메시지 내용, 방 ID(chat_id), 사용자 ID를 추출합니다.
    /// </summary>
    /// <param name="rawMessage">원본 WebSocket 메시지</param>
    /// <returns>(메시지 내용, 방 ID, 사용자 ID)</returns>
    private (string message, string roomId, string userId) ParseMessage(string rawMessage)
    {
        try
        {
            NotificationRequested?.Invoke($"🔍 [PluginManager] 메시지 파싱 시작: {rawMessage.Length}자");

            var messageData = JsonSerializer.Deserialize<JsonElement>(rawMessage);

            // 최상위 레벨에서 기본 정보 추출
            var message = messageData.TryGetProperty("msg", out var msgElement)
                ? msgElement.GetString() ?? "" : "";
            var roomName = messageData.TryGetProperty("room", out var roomElement)
                ? roomElement.GetString() ?? "" : "";
            var sender = messageData.TryGetProperty("sender", out var senderElement)
                ? senderElement.GetString() ?? "" : "";

            NotificationRequested?.Invoke($"📄 [PluginManager] 기본 정보: 메시지='{message}', 방이름='{roomName}', 발신자='{sender}'");

            // 기본값
            var actualRoomId = roomName;
            var actualUserId = sender;

            // json 객체에서 실제 chat_id 추출
            if (messageData.TryGetProperty("json", out var jsonElement))
            {
                JsonElement innerJson;

                // json이 문자열인 경우 파싱
                if (jsonElement.ValueKind == JsonValueKind.String)
                {
                    var jsonString = jsonElement.GetString();
                    if (!string.IsNullOrEmpty(jsonString))
                    {
                        try
                        {
                            innerJson = JsonSerializer.Deserialize<JsonElement>(jsonString);
                        }
                        catch (Exception parseEx)
                        {
                            NotificationRequested?.Invoke($"⚠️ [PluginManager] JSON 문자열 파싱 실패: {parseEx.Message}");
                            return (message, actualRoomId, actualUserId);
                        }
                    }
                    else
                    {
                        return (message, actualRoomId, actualUserId);
                    }
                }
                else
                {
                    innerJson = jsonElement;
                }

                // 메시지 타입 확인 - 일반 메시지(타입 1)만 처리
                var messageType = 1;
                if (innerJson.TryGetProperty("type", out var typeElement))
                {
                    if (typeElement.ValueKind == JsonValueKind.Number)
                    {
                        messageType = typeElement.GetInt32();
                    }
                    else if (typeElement.ValueKind == JsonValueKind.String &&
                             int.TryParse(typeElement.GetString(), out var parsedType))
                    {
                        messageType = parsedType;
                    }
                }

                if (messageType != 1)
                {
                    NotificationRequested?.Invoke($"⚠️ [PluginManager] 일반 메시지가 아님 (타입: {messageType}), 건너뜀");
                    return ("", "", "");
                }

                // 🔧 핵심: chat_id 추출 (채팅방 고유 번호)
                if (innerJson.TryGetProperty("chat_id", out var chatIdElement))
                {
                    var chatId = chatIdElement.GetString();
                    if (!string.IsNullOrEmpty(chatId))
                    {
                        actualRoomId = chatId;
                        NotificationRequested?.Invoke($"✅ [PluginManager] chat_id 추출 성공: '{actualRoomId}' (방이름: '{roomName}')");
                    }
                    else
                    {
                        NotificationRequested?.Invoke($"⚠️ [PluginManager] chat_id가 비어있음, 방이름 사용: '{actualRoomId}'");
                    }
                }
                else
                {
                    NotificationRequested?.Invoke($"⚠️ [PluginManager] chat_id 필드 없음, 방이름 사용: '{actualRoomId}'");
                }

                // 사용자 ID 추출
                if (innerJson.TryGetProperty("user_id", out var userIdElement))
                {
                    var userId = userIdElement.GetString();
                    if (!string.IsNullOrEmpty(userId))
                    {
                        actualUserId = userId;
                        NotificationRequested?.Invoke($"✅ [PluginManager] user_id 추출 성공: '{actualUserId}'");
                    }
                }

                // 메시지 내용 재확인
                if (innerJson.TryGetProperty("message", out var innerMessageElement))
                {
                    var innerMessage = innerMessageElement.GetString();
                    if (!string.IsNullOrEmpty(innerMessage))
                    {
                        message = innerMessage;
                        NotificationRequested?.Invoke($"🔄 [PluginManager] 메시지 내용 업데이트: '{message}'");
                    }
                }
            }

            // 최종 검증
            if (string.IsNullOrEmpty(message))
            {
                NotificationRequested?.Invoke($"⚠️ [PluginManager] 메시지 내용이 없음");
                return ("", "", "");
            }

            NotificationRequested?.Invoke($"✅ [PluginManager] 최종 파싱 결과: 메시지='{message}', chat_id='{actualRoomId}', user_id='{actualUserId}'");
            return (message, actualRoomId, actualUserId);
        }
        catch (Exception ex)
        {
            NotificationRequested?.Invoke($"❌ [PluginManager] 메시지 파싱 실패: {ex.Message}");
            _ = _errorLogger.LogWarningAsync(LogCategories.MESSAGE_PROCESSING, "메시지 파싱 실패", ex.Message);
            return ("", "", "");
        }
    }
    #endregion

    #region 🔧 수정된 정리 및 종료

    public void Dispose()
    {
        try
        {
            _pluginWatcher?.Dispose();

            // 모든 플러그인 정리
            var shutdownTasks = _loadedPlugins.Select(plugin =>
                Task.Run(async () =>
                {
                    try
                    {
                        await plugin.ShutdownAsync();
                    }
                    catch (Exception ex)
                    {
                        NotificationRequested?.Invoke($"⚠️ 플러그인 종료 실패 [{plugin.Name}]: {ex.Message}");
                        _ = _errorLogger.LogWarningAsync(LogCategories.PLUGIN, $"플러그인 종료 실패: {plugin.Name}", ex.Message);
                    }
                })).ToArray();

            // 최대 5초 대기
            Task.WaitAll(shutdownTasks, TimeSpan.FromSeconds(5));

            _loadedPlugins.Clear();
            _pluginData.Clear();
            _pluginConfigs.Clear();

            // 🔧 추가: 오래된 로그 정리
            _errorLogger.CleanupOldLogs();

            NotificationRequested?.Invoke("✅ PluginManager 정리 완료");
        }
        catch (Exception ex)
        {
            NotificationRequested?.Invoke($"⚠️ PluginManager 정리 중 오류: {ex.Message}");
            _ = _errorLogger.LogErrorAsync(LogCategories.PLUGIN, "PluginManager 정리 중 오류", ex.StackTrace);
        }
    }

    #endregion

    #region 플러그인 컨텍스트 (기존 유지)

    private class PluginContext : IPluginContext
    {
        private readonly PluginManager _manager;

        public string PluginName { get; }
        public string DataPath => Path.Combine(_manager._configService.DataPath, "plugin_data", PluginName);

        public PluginContext(string pluginName, PluginManager manager)
        {
            PluginName = pluginName;
            _manager = manager;
        }

        public void AddTab(string header, object content, UserRole requiredRole = UserRole.User)
        {
            _manager.AddTab(header, content, requiredRole);
        }

        public void ShowNotification(string message)
        {
            _manager.ShowNotification(message);
        }

        public bool HasPermission(string userId, UserRole requiredRole)
        {
            return _manager._authService.HasPermission(userId, requiredRole);
        }

        public bool ValidatePin(string pin, UserRole requiredRole)
        {
            return _manager._authService.ValidatePin(pin, requiredRole);
        }

        public async Task SendMessageAsync(string roomId, string message)
        {
            await _manager._webSocketService.SendMessageAsync(roomId, message);
        }

        public void SubscribeToMessages(Action<string, string> handler)
        {
            _manager._webSocketService.MessageReceived += (rawMessage) =>
            {
                try
                {
                    var (message, roomId, userId) = _manager.ParseMessage(rawMessage);
                    if (!string.IsNullOrEmpty(message) && !string.IsNullOrEmpty(roomId))
                    {
                        handler(message, roomId);
                    }
                }
                catch (Exception ex)
                {
                    _manager.NotificationRequested?.Invoke($"⚠️ 메시지 구독 처리 실패 [{PluginName}]: {ex.Message}");
                    _ = _manager._errorLogger.LogWarningAsync(LogCategories.MESSAGE_PROCESSING,
                        $"메시지 구독 처리 실패: {PluginName}", ex.Message);
                }
            };
        }

        public async Task<T?> GetDataAsync<T>(string key)
        {
            return await _manager.GetPluginDataAsync<T>(PluginName, key);
        }

        public async Task SetDataAsync<T>(string key, T value)
        {
            await _manager.SetPluginDataAsync(PluginName, key, value);
        }

        public string GetConfig(string key)
        {
            if (_manager._pluginConfigs.TryGetValue(PluginName, out var config))
            {
                return config.TryGetValue(key, out var value) ? value : "";
            }
            return "";
        }

        public void SetConfig(string key, string value)
        {
            if (!_manager._pluginConfigs.ContainsKey(PluginName))
            {
                _manager._pluginConfigs[PluginName] = new Dictionary<string, string>();
            }

            _manager._pluginConfigs[PluginName][key] = value;
            _manager.SavePluginConfig(PluginName, _manager._pluginConfigs[PluginName]);
        }
    }

    #endregion
}