

namespace TicketHub.Domain.Common;

/// <summary>Lớp cơ sở cho entity nghiệp vụ: có sẵn cột audit và xoá mềm.</summary>
public abstract class Entity : IAuditableEntity, ISoftDeletable
{
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}
