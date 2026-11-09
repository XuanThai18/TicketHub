using System.Data;
using Dapper;

namespace TicketHub.Infrastructure.Persistence;

internal static class DapperConfiguration
{
    private static int _configured;

    public static void Configure()
    {
        if (Interlocked.Exchange(ref _configured, 1) == 1)
            return;

        // Cột snake_case (created_at) → property PascalCase (CreatedAt).
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
    }

    // Npgsql trả timestamptz dưới dạng DateTime (UTC); Dapper không tự đổi sang DateTimeOffset.
    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) =>
            parameter.Value = value.UtcDateTime;

        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            DateTimeOffset dto => dto,
            _ => throw new DataException($"Cannot convert {value.GetType()} to DateTimeOffset.")
        };
    }
}
