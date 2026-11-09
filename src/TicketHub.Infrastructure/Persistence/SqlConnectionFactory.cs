using System.Data.Common;
using Npgsql;
using TicketHub.Application.Abstractions;

namespace TicketHub.Infrastructure.Persistence;

internal sealed class SqlConnectionFactory(NpgsqlDataSource dataSource) : ISqlConnectionFactory
{
    public async ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
        await dataSource.OpenConnectionAsync(cancellationToken);
}
