using System.Windows;
using System.Windows.Controls;
using IrisBotManager.Core.Services;

namespace IrisBotManager.GUI.Controls;

public partial class MessagePanel : UserControl
{
    private WebSocketService? _webSocketService;

    public MessagePanel()
    {
        InitializeComponent();
    }

    public void Initialize(WebSocketService webSocketService)
    {
        _webSocketService = webSocketService;
        _webSocketService.MessageReceived += OnMessageReceived;
    }

    private void OnMessageReceived(string rawMessage)
    {
        Dispatcher.Invoke(() =>
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            MessageLogBox.AppendText($"[{timestamp}] {rawMessage}\n");

            if (AutoScrollCheckBox.IsChecked == true)
            {
                MessageLogBox.ScrollToEnd();
            }
        });
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        MessageLogBox.Clear();
    }
}