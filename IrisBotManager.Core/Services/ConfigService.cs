using System.Text.Json;
using IrisBotManager.Core.Models;

namespace IrisBotManager.Core.Services;

public class ConfigService
{
    private readonly string _configPath;
    private Config _config;

    public ConfigService()
    {
        _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        LoadConfig();
    }

    public Config Config => _config;
    public string DataPath => _config.DataPath;
    public string PluginPath => _config.PluginPath;

    public string Host
    {
        get => _config.Host;
        set
        {
            _config.Host = value;
            SaveConfig();
        }
    }

    public string Port
    {
        get => _config.Port;
        set
        {
            _config.Port = value;
            SaveConfig();
        }
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                _config = JsonSerializer.Deserialize<Config>(json) ?? new Config();
            }
            else
            {
                _config = new Config();
                SaveConfig();
            }
        }
        catch
        {
            _config = new Config();
        }
    }

    private void SaveConfig()
    {
        try
        {
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
        catch
        {
            // 설정 저장 실패 시 무시
        }
    }
}