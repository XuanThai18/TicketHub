namespace TicketHub.Application.Abstractions;

public interface ICurrentUser
{
    /// <summary>Claim "sub"; null khi không có user (job nền, request ẩn danh).</summary>
    string? UserId { get; }
    bool IsAuthenticated { get; }
    bool IsInRole(string role);
}
