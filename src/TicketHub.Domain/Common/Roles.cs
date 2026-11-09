namespace TicketHub.Domain.Common;

/// <summary>Tên vai trò dùng chung cho claim "role" trong JWT và policy phân quyền.</summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string User = "User";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Admin, Manager, User };
}
