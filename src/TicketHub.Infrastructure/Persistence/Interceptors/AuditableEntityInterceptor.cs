using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TicketHub.Application.Abstractions;
using TicketHub.Domain.Common;

namespace TicketHub.Infrastructure.Persistence.Interceptors;

public sealed class AuditableEntityInterceptor(TimeProvider timeProvider, ICurrentUser currentUser)
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
        var userId = currentUser.UserId;

        foreach (var entry in context.ChangeTracker.Entries<IAuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.CreatedBy = userId;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
                entry.Entity.UpdatedBy = userId;
            }
        }
    }
}
