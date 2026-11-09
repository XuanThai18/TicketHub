using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TicketHub.Application.Common.Exceptions;
using TicketHub.Domain.Common;

namespace TicketHub.Infrastructure.Persistence.Contexts;

/// <summary>Hành vi chung cho mọi DbContext: dịch lỗi DB sang exception của Application, lọc xoá mềm.</summary>
public abstract class AppDbContextBase(DbContextOptions options) : DbContext(options)
{
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException("The record was modified by another request.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw new UniqueConstraintException(pg.MessageText, ex) { ConstraintName = pg.ConstraintName };
        }
    }

    protected static void ApplySoftDeleteQueryFilter(ModelBuilder modelBuilder)
    {
        // Cần xem cả bản ghi đã xoá thì dùng IgnoreQueryFilters().
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(t => typeof(ISoftDeletable).IsAssignableFrom(t.ClrType) && t.BaseType is null))
        {
            var parameter = Expression.Parameter(entityType.ClrType, "e");
            var body = Expression.Not(Expression.Property(parameter, nameof(ISoftDeletable.IsDeleted)));
            entityType.SetQueryFilter(Expression.Lambda(body, parameter));
        }
    }
}
