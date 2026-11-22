namespace TicketHub.Domain.Outbox;

public interface IOutboxRepository
{
    void Add(OutboxMessage message);
}
