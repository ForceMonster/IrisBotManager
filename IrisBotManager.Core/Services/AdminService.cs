using IrisBotManager.Core.Models;

namespace IrisBotManager.Core.Services;

public class AdminService
{
    private readonly string _adminFilePath;
    private readonly string _superAdminFilePath;
    private HashSet<string> _admins = new();
    private HashSet<string> _superAdmins = new();

    public AdminService(ConfigService configService)
    {
        var adminDir = Path.Combine(configService.DataPath, "admin_db");
        _adminFilePath = Path.Combine(adminDir, "ID.txt");
        _superAdminFilePath = Path.Combine(adminDir, "SuperAdmin.txt");
        LoadAdmins();
    }

    public bool IsAdmin(string userId)
    {
        return _admins.Contains(userId) || _superAdmins.Contains(userId);
    }

    public bool IsSuperAdmin(string userId)
    {
        return _superAdmins.Contains(userId);
    }

    public bool AddAdmin(string userId)
    {
        if (_admins.Add(userId))
        {
            SaveAdmins();
            return true;
        }
        return false;
    }

    public bool RemoveAdmin(string userId)
    {
        if (_admins.Remove(userId))
        {
            SaveAdmins();
            return true;
        }
        return false;
    }

    public bool AddSuperAdmin(string userId)
    {
        if (_superAdmins.Add(userId))
        {
            SaveSuperAdmins();
            return true;
        }
        return false;
    }

    public bool RemoveSuperAdmin(string userId)
    {
        if (_superAdmins.Remove(userId))
        {
            SaveSuperAdmins();
            return true;
        }
        return false;
    }

    public List<string> GetAdmins()
    {
        return _admins.ToList();
    }

    public List<string> GetSuperAdmins()
    {
        return _superAdmins.ToList();
    }

    public UserRole GetUserRole(string userId)
    {
        if (_superAdmins.Contains(userId))
            return UserRole.SuperAdmin;
        if (_admins.Contains(userId))
            return UserRole.Admin;
        return UserRole.User;
    }

    private void LoadAdmins()
    {
        try
        {
            if (File.Exists(_adminFilePath))
            {
                _admins = new HashSet<string>(File.ReadAllLines(_adminFilePath));
            }
        }
        catch
        {
            _admins = new HashSet<string>();
        }

        try
        {
            if (File.Exists(_superAdminFilePath))
            {
                _superAdmins = new HashSet<string>(File.ReadAllLines(_superAdminFilePath));
            }
        }
        catch
        {
            _superAdmins = new HashSet<string>();
        }
    }

    private void SaveAdmins()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_adminFilePath)!);
            File.WriteAllLines(_adminFilePath, _admins);
        }
        catch
        {
            // 저장 실패 시 무시
        }
    }

    private void SaveSuperAdmins()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_superAdminFilePath)!);
            File.WriteAllLines(_superAdminFilePath, _superAdmins);
        }
        catch
        {
            // 저장 실패 시 무시
        }
    }
}