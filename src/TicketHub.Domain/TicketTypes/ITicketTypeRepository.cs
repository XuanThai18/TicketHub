namespace TicketHub.Domain.TicketTypes;

public interface ITicketTypeRepository
{
    Task<TicketType?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    void Add(TicketType ticketType);
    Task<int> CountByEventIdAsync(Guid eventId, CancellationToken cancellationToken);
    Task<bool> NameExistsAsync(Guid eventId, string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<TicketType>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Cộng lại vé bằng một UPDATE (không đọc rồi ghi đè). Trả false nếu vượt tổng số vé.</summary>
    Task<bool> TryReturnStockAsync(Guid id, int quantity, CancellationToken cancellationToken);
}
