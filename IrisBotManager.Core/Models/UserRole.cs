namespace IrisBotManager.Core.Models;

public enum UserRole
{
    User = 1,           // 사용자 (기본)
    Admin = 2,          // 관리자
    SuperAdmin = 3      // 최고관리자
}

public static class UserRoleExtensions
{
    public static string GetDisplayName(this UserRole role)
    {
        return role switch
        {
            UserRole.User => "사용자",
            UserRole.Admin => "관리자",
            UserRole.SuperAdmin => "최고관리자",
            _ => "알 수 없음"
        };
    }

    public static bool HasPermission(this UserRole userRole, UserRole requiredRole)
    {
        return (int)userRole >= (int)requiredRole;
    }
}