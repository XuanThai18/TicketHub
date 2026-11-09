using Dapper;
using MediatR;
using TicketHub.Application.Abstractions;
using TicketHub.Domain.Auth;
using TicketHub.Domain.Shared;

namespace TicketHub.Application.Auth;

public sealed class CurrentUserResponse
{
    public Guid Id { get; init; }
    public string Email { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public IReadOnlyList<string> Roles { get; init; } = [];
    public DateTimeOffset? LastLoginAt { get; init; }
}

public sealed record GetCurrentUserQuery(Guid UserId) : IRequest<Result<CurrentUserResponse>>;

public sealed class GetCurrentUserQueryHandler(ISqlConnectionFactory connectionFactory)
    : IRequestHandler<GetCurrentUserQuery, Result<CurrentUserResponse>>
{
    private const string Sql = """
        SELECT u.id, u.email, u.full_name, u.last_login_at,
               coalesce(array_agg(r.role ORDER BY r.role) FILTER (WHERE r.role IS NOT NULL), '{}') AS roles
        FROM auth.users u
        LEFT JOIN auth.user_roles r ON r.user_id = u.id
        WHERE u.id = @UserId AND u.is_active
        GROUP BY u.id
        """;

    public async Task<Result<CurrentUserResponse>> Handle(GetCurrentUserQuery request, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<CurrentUserRow>(
            new CommandDefinition(Sql, new { request.UserId }, cancellationToken: cancellationToken));

        if (row is null)
            return AuthErrors.UserNotFound(request.UserId);

        return new CurrentUserResponse
        {
            Id = row.Id,
            Email = row.Email,
            FullName = row.FullName,
            Roles = row.Roles,
            LastLoginAt = row.LastLoginAt
        };
    }

    private sealed class CurrentUserRow
    {
        public Guid Id { get; init; }
        public string Email { get; init; } = string.Empty;
        public string FullName { get; init; } = string.Empty;
        public string[] Roles { get; init; } = [];
        public DateTimeOffset? LastLoginAt { get; init; }
    }
}

public sealed class SessionResponse
{
    public Guid Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public bool IsCurrent { get; init; }
}

/// <summary>Các thiết bị đang đăng nhập — cho màn "Quản lý phiên" để người dùng tự đăng xuất máy lạ.</summary>
public sealed record ListMySessionsQuery(Guid UserId, Guid CurrentSessionId) : IRequest<Result<IReadOnlyList<SessionResponse>>>;

public sealed class ListMySessionsQueryHandler(ISqlConnectionFactory connectionFactory, TimeProvider timeProvider)
    : IRequestHandler<ListMySessionsQuery, Result<IReadOnlyList<SessionResponse>>>
{
    private const string Sql = """
        SELECT id, created_at, expires_at, ip_address, user_agent, (id = @CurrentSessionId) AS is_current
        FROM auth.user_sessions
        WHERE user_id = @UserId AND revoked_at IS NULL AND expires_at > @Now
        ORDER BY created_at DESC
        """;

    public async Task<Result<IReadOnlyList<SessionResponse>>> Handle(ListMySessionsQuery request, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<SessionResponse>(new CommandDefinition(
            Sql,
            new { request.UserId, request.CurrentSessionId, Now = timeProvider.GetUtcNow() },
            cancellationToken: cancellationToken));

        return rows.AsList();
    }
}
