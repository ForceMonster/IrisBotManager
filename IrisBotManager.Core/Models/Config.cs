namespace IrisBotManager.Core.Models;

public class Config
{
    public string Host { get; set; } = "127.0.0.1";
    public string Port { get; set; } = "3000";
    public string DataPath { get; set; } = "c:/forcebot";
    public string PluginPath { get; set; } = "./plugins";
    public WindowSettings WindowSettings { get; set; } = new();
}

public class WindowSettings
{
    public int Width { get; set; } = 450;
    public int Height { get; set; } = 400;
    public bool TopMost { get; set; } = false;
}