using Microsoft.EntityFrameworkCore;
using TicketHub.Application.Abstractions;

namespace TicketHub.Infrastructure.Persistence.Contexts;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : AppDbContextBase(options), IReadDbContext
{
    // Cấu hình EF của các module có DbContext riêng (vd Auth) nằm dưới namespace này → không nạp vào đây.
    internal const string ModulesNamespace = "TicketHub.Infrastructure.Auth";

    IQueryable<TEntity> IReadDbContext.Query<TEntity>() => Set<TEntity>().AsNoTracking();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(ApplicationDbContext).Assembly,
            t => t.Namespace?.StartsWith(ModulesNamespace, StringComparison.Ordinal) != true);
        ApplySoftDeleteQueryFilter(modelBuilder);
    }
}
