using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using IrisBotManager.Core.Services;
using System.Diagnostics;
using System.Reflection;
using System.IO;

namespace IrisBotManager.GUI;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private bool _isShuttingDown = false;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // 플러그인 의존성 해결을 위한 AssemblyResolve 이벤트 등록
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();

            // 처리되지 않은 예외 핸들러 등록
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"애플리케이션 시작 실패:\n{ex.Message}", "시작 오류",
                          MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        try
        {
            // AssemblyResolve 이벤트 해제
            AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;

            // 매우 짧은 시간 내에 정리 작업 수행 (최대 500ms)
            var cleanupTask = Task.Run(() =>
            {
                try
                {
                    _serviceProvider?.Dispose();
                }
                catch
                {
                    // Dispose 실패 시 무시
                }
            });

            // 500ms 후에도 완료되지 않으면 강제 진행
            if (!cleanupTask.Wait(500))
            {
                // 강제 종료
                ForceExit();
                return;
            }
        }
        catch
        {
            // 정리 실패 시 강제 종료
            ForceExit();
            return;
        }
        finally
        {
            base.OnExit(e);
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var ex = e.ExceptionObject as Exception;
            var message = $"처리되지 않은 예외가 발생했습니다:\n{ex?.Message}\n\n프로그램을 종료합니다.";

            MessageBox.Show(message, "치명적 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // 메시지 박스 표시도 실패하면 무시
        }
        finally
        {
            ForceExit();
        }
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var message = $"UI 스레드에서 처리되지 않은 예외가 발생했습니다:\n{e.Exception.Message}\n\n계속하시겠습니까?";

            var result = MessageBox.Show(message, "UI 오류", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                e.Handled = true; // 예외를 처리했다고 표시하여 프로그램 계속 실행
            }
            else
            {
                ForceExit();
            }
        }
        catch
        {
            // 메시지 박스 표시도 실패하면 강제 종료
            ForceExit();
        }
    }

    private static void ForceExit()
    {
        try
        {
            // 현재 프로세스의 모든 스레드를 즉시 종료
            Environment.Exit(0);
        }
        catch
        {
            try
            {
                // Environment.Exit도 실패하면 프로세스 강제 종료
                Process.GetCurrentProcess().Kill();
            }
            catch
            {
                // 마지막 수단으로 시스템 종료 호출
                // 이것도 실패하면 어쩔 수 없음
            }
        }
    }

    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        try
        {
            // 어셈블리 이름 추출
            var assemblyName = new AssemblyName(args.Name);
            var fileName = assemblyName.Name + ".dll";

            // 플러그인 폴더에서 찾기
            var currentDir = AppDomain.CurrentDomain.BaseDirectory;
            var pluginsDir = Path.Combine(currentDir, "plugins");

            if (Directory.Exists(pluginsDir))
            {
                var assemblyPath = Path.Combine(pluginsDir, fileName);
                if (File.Exists(assemblyPath))
                {
                    return Assembly.LoadFrom(assemblyPath);
                }

                // 서브폴더에서도 찾기
                var subDirs = Directory.GetDirectories(pluginsDir, "*", SearchOption.AllDirectories);
                foreach (var subDir in subDirs)
                {
                    var subAssemblyPath = Path.Combine(subDir, fileName);
                    if (File.Exists(subAssemblyPath))
                    {
                        return Assembly.LoadFrom(subAssemblyPath);
                    }
                }
            }

            // 기본 애플리케이션 폴더에서 찾기
            var defaultPath = Path.Combine(currentDir, fileName);
            if (File.Exists(defaultPath))
            {
                return Assembly.LoadFrom(defaultPath);
            }
        }
        catch (Exception ex)
        {
            // 로그만 남기고 계속 진행
            Console.WriteLine($"AssemblyResolve 실패: {args.Name} - {ex.Message}");
        }

        return null;
    }

    private static void ConfigureServices(ServiceCollection services)
    {
        try
        {
            // Core 서비스
            services.AddSingleton<ConfigService>();
            services.AddSingleton<AdminService>();
            services.AddSingleton<AuthService>();
            services.AddSingleton<WebSocketService>();

            // 플러그인 관련 서비스 (의존성 순서 중요)
            services.AddSingleton<PluginStateManager>();
            services.AddSingleton<PluginManager>();
            services.AddSingleton<PluginUIService>();
            services.AddSingleton<RoomSettingsService>();

            // UI
            services.AddTransient<MainWindow>();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"서비스 구성 실패:\n{ex.Message}", "구성 오류",
                          MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
    }

    // 애플리케이션 강제 종료를 위한 정적 메서드 (다른 클래스에서 호출 가능)
    public static void ForceShutdown()
    {
        Current?.Dispatcher.Invoke(() =>
        {
            try
            {
                Current.Shutdown();
            }
            catch
            {
                ForceExit();
            }
        });
    }
}