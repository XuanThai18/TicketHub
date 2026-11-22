using TicketHub.Domain.Tickets;
using TicketHub.Infrastructure.Persistence.Contexts;

namespace TicketHub.Infrastructure.Persistence.Repositories;

internal sealed class TicketRepository(ApplicationDbContext dbContext) : ITicketRepository
{
    public void Add(Ticket ticket) => dbContext.Set<Ticket>().Add(ticket);
}
