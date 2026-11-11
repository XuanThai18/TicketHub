using Microsoft.EntityFrameworkCore;
using TicketHub.Domain.TicketTypes;
using TicketHub.Infrastructure.Persistence.Contexts;

namespace TicketHub.Infrastructure.Persistence.Repositories;

internal sealed class TicketTypeRepository(ApplicationDbContext dbContext) : ITicketTypeRepository
{
    public void Add(TicketType ticketType) => dbContext.Set<TicketType>().Add(ticketType);
    public Task<int> CountByEventIdAsync(Guid eventId, CancellationToken cancellationToken) => dbContext.Set<TicketType>().CountAsync(tt => tt.EventId == eventId, cancellationToken);
    public Task<TicketType?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => dbContext.Set<TicketType>().FirstOrDefaultAsync(tt => tt.Id == id, cancellationToken);
}
