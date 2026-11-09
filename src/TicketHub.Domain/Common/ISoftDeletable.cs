namespace TicketHub.Domain.Common;

/// <summary>Remove() được đổi thành cập nhật cờ; query mặc định lọc bỏ bản ghi đã xoá.</summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; set; }
    DateTimeOffset? DeletedAt { get; set; }
    string? DeletedBy { get; set; }
}
