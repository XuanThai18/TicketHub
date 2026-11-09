using System.Data.Common;

namespace TicketHub.Application.Abstractions;

/// <summary>Kết nối cho truy vấn đọc bằng Dapper; dùng chung pool với EF Core.</summary>
public interface ISqlConnectionFactory
{
    ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}
