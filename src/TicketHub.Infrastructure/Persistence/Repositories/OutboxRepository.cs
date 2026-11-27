using TicketHub.Domain.Outbox;
using TicketHub.Infrastructure.Persistence.Contexts;

namespace TicketHub.Infrastructure.Persistence.Repositories;

internal sealed class OutboxRepository(ApplicationDbContext dbContext) : IOutboxRepository
{
    public void Add(OutboxMessage message) => dbContext.Set<OutboxMessage>().Add(message);
}
