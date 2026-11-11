namespace TicketHub.Domain.TicketTypes;

public interface ITicketTypeRepository
{
    Task<TicketType?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    void Add(TicketType ticketType);
    Task<int> CountByEventIdAsync(Guid eventId, CancellationToken cancellationToken);
}
