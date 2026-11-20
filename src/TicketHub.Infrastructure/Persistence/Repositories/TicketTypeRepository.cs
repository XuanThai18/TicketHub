using Microsoft.EntityFrameworkCore;
using TicketHub.Domain.TicketTypes;
using TicketHub.Infrastructure.Persistence.Contexts;

namespace TicketHub.Infrastructure.Persistence.Repositories;

internal sealed class TicketTypeRepository(ApplicationDbContext dbContext) : ITicketTypeRepository
{
    public void Add(TicketType ticketType) => dbContext.Set<TicketType>().Add(ticketType);
    public Task<int> CountByEventIdAsync(Guid eventId, CancellationToken cancellationToken) => dbContext.Set<TicketType>().CountAsync(tt => tt.EventId == eventId, cancellationToken);
    public Task<TicketType?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => dbContext.Set<TicketType>().FirstOrDefaultAsync(tt => tt.Id == id, cancellationToken);
    public async Task<IReadOnlyList<TicketType>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        return await dbContext.Set<TicketType>().Where(tt => ids.Contains(tt.Id)).ToListAsync(cancellationToken);
    }
    public async Task<bool> TryReturnStockAsync(Guid id, int quantity, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Set<TicketType>()
            .Where(tt => tt.Id == id && tt.AvailableQuantity + quantity <= tt.TotalQuantity)
            .ExecuteUpdateAsync(s => s.SetProperty(tt => tt.AvailableQuantity, tt => tt.AvailableQuantity + quantity), cancellationToken);
        return rows == 1;
    }

    public Task<bool> NameExistsAsync(Guid eventId, string name, CancellationToken cancellationToken) => dbContext.Set<TicketType>().AnyAsync(tt => tt.EventId == eventId && tt.Name == name, cancellationToken);
}
