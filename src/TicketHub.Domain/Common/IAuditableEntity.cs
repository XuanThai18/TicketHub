namespace TicketHub.Domain.Common;

/// <summary>Được AuditableEntityInterceptor tự điền khi SaveChanges.</summary>
public interface IAuditableEntity
{
    DateTimeOffset CreatedAt { get; set; }
    string? CreatedBy { get; set; }
    DateTimeOffset? UpdatedAt { get; set; }
    string? UpdatedBy { get; set; }
}
