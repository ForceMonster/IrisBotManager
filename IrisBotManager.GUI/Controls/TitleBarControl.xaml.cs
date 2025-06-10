using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IrisBotManager.GUI.Controls;

public partial class TitleBarControl : UserControl
{
    private Window? _mainWindow;

    public TitleBarControl()
    {
        InitializeComponent();
    }

    public void SetMainWindow(Window mainWindow)
    {
        _mainWindow = mainWindow;
    }

    private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mainWindow?.DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow != null)
        {
            _mainWindow.WindowState = WindowState.Minimized;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.Close();
    }
}