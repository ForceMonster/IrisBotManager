using System.Windows;
using System.Windows.Controls;
using IrisBotManager.Core.Services;
using Microsoft.VisualBasic;

namespace IrisBotManager.GUI.Controls;

public partial class AdminPanel : UserControl
{
    private AuthService? _authService;
    private AdminService? _adminService;

    public AdminPanel()
    {
        InitializeComponent();
    }

    public void Initialize(AuthService authService, AdminService adminService)
    {
        _authService = authService;
        _adminService = adminService;

        _authService.PinChanged += OnPinChanged;
        OnPinChanged(_authService.CurrentPin);
    }

    private void OnPinChanged(string newPin)
    {
        Dispatcher.Invoke(() =>
        {
            PinDisplay.Text = newPin;
        });
    }

    private void NewPinButton_Click(object sender, RoutedEventArgs e)
    {
        _authService?.GenerateNewPin();
        AppendResult("🔄 새로운 PIN이 생성되었습니다.");
    }

    private void AddAdminButton_Click(object sender, RoutedEventArgs e)
    {
        if (_authService == null) return;

        var userId = UserIdBox.Text.Trim();
        if (string.IsNullOrEmpty(userId))
        {
            AppendResult("⚠️ 사용자 ID를 입력하세요.");
            return;
        }

        var pin = Interaction.InputBox("PIN 번호를 입력하세요:", "PIN 확인", "");
        if (string.IsNullOrEmpty(pin))
        {
            return;
        }

        var result = _authService.AddAdmin(userId, pin);
        AppendResult(result);
        UserIdBox.Clear();
    }

    private void RemoveAdminButton_Click(object sender, RoutedEventArgs e)
    {
        if (_authService == null) return;

        var userId = UserIdBox.Text.Trim();
        if (string.IsNullOrEmpty(userId))
        {
            AppendResult("⚠️ 사용자 ID를 입력하세요.");
            return;
        }

        var pin = Interaction.InputBox("PIN 번호를 입력하세요:", "PIN 확인", "");
        if (string.IsNullOrEmpty(pin))
        {
            return;
        }

        var result = _authService.RemoveAdmin(userId, pin);
        AppendResult(result);
        UserIdBox.Clear();
    }

    private void ListAdminButton_Click(object sender, RoutedEventArgs e)
    {
        if (_authService == null) return;

        var pin = Interaction.InputBox("PIN 번호를 입력하세요:", "PIN 확인", "");
        if (string.IsNullOrEmpty(pin))
        {
            return;
        }

        var result = _authService.ShowAdminList(pin);
        AppendResult(result);
    }

    private void AppendResult(string message)
    {
        Dispatcher.Invoke(() =>
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            ResultBox.AppendText($"[{timestamp}] {message}\n");
            ResultBox.ScrollToEnd();
        });
    }
}