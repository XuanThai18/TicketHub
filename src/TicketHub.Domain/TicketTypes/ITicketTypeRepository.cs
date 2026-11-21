namespace TicketHub.Domain.TicketTypes;

public interface ITicketTypeRepository
{
    Task<TicketType?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    void Add(TicketType ticketType);
    Task<int> CountByEventIdAsync(Guid eventId, CancellationToken cancellationToken);
    Task<bool> NameExistsAsync(Guid eventId, string name, CancellationToken cancellationToken);

    /// <summary>Có loại vé khác (không tính <paramref name="excludeId"/>) cùng sự kiện đã dùng tên này chưa.</summary>
    Task<bool> NameExistsForOtherAsync(Guid eventId, Guid excludeId, string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<TicketType>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Trừ vé bằng một UPDATE có điều kiện còn đủ vé. Trả false nếu không đủ (hoặc loại vé không còn).</summary>
    Task<bool> TryReserveStockAsync(Guid id, int quantity, CancellationToken cancellationToken);

    /// <summary>Cộng lại vé bằng một UPDATE (không đọc rồi ghi đè). Trả false nếu vượt tổng số vé.</summary>
    Task<bool> TryReturnStockAsync(Guid id, int quantity, CancellationToken cancellationToken);
}
