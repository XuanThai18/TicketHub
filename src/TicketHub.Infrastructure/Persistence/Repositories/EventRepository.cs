using Microsoft.EntityFrameworkCore;
using TicketHub.Domain.Events;
using TicketHub.Infrastructure.Persistence.Contexts;

namespace TicketHub.Infrastructure.Persistence.Repositories;

internal sealed class EventRepository(ApplicationDbContext dbContext) : IEventRepository
{
    public void Add(Event @event) => dbContext.Set<Event>().Add(@event);
    public Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => dbContext.Set<Event>().FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
}
