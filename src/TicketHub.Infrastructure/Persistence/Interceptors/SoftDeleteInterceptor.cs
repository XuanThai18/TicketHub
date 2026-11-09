using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TicketHub.Application.Abstractions;
using TicketHub.Domain.Common;

namespace TicketHub.Infrastructure.Persistence.Interceptors;

public sealed class SoftDeleteInterceptor(TimeProvider timeProvider, ICurrentUser currentUser)
    : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
            Apply(eventData.Context);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null)
            Apply(eventData.Context);

        return base.SavingChanges(eventData, result);
    }

    private void Apply(DbContext context)
    {
        var now = timeProvider.GetUtcNow();

        foreach (var entry in context.ChangeTracker.Entries<ISoftDeletable>()
                     .Where(e => e.State == EntityState.Deleted))
        {
            entry.State = EntityState.Modified;
            entry.Entity.IsDeleted = true;
            entry.Entity.DeletedAt = now;
            entry.Entity.DeletedBy = currentUser.UserId;
        }
    }
}
