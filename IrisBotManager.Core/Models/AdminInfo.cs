namespace IrisBotManager.Core.Models;

public class AdminInfo
{
    public string UserId { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Admin;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string DisplayName { get; set; } = string.Empty;
}