using System.Security.Cryptography;
using System.Text;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Common.Paging;
using TicketHub.Domain.Events;
using TicketHub.Domain.Shared;

namespace TicketHub.Application.Events;

public sealed record ListEventsResponse(
    Guid Id,
    string Name,
    string? Description,
    string Venue,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset SaleStartsAt,
    DateTimeOffset SaleEndsAt,
    string Status);

public sealed class ListEventsQueryValidator : AbstractValidator<ListEventsQuery>
{
    public ListEventsQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Search).MaximumLength(Event.NameMaxLength);
        RuleFor(x => x.From)
            .LessThan(x => x.To)
            .When(x => x.From is not null && x.To is not null)
            .WithMessage("'From' must be earlier than 'To'.");
    }
}

/// <summary>Lọc theo giờ diễn: <c>From ≤ StartsAt &lt; To</c>. Bỏ trống một đầu thì không giới hạn đầu đó.</summary>
public sealed record ListEventsQuery(string? Search, int Page, int PageSize, DateTimeOffset? From = null, DateTimeOffset? To = null)
    : IRequest<Result<PagedResult<ListEventsResponse>>>;

public sealed class ListEventsQueryHandler(IReadDbContext readDbContext, ICacheService cache)
    : IRequestHandler<ListEventsQuery, Result<PagedResult<ListEventsResponse>>>
{
    /// <summary>Khoá phiên bản: mọi trang danh sách gắn với một GUID; đổi GUID là mọi trang cũ coi như không còn.</summary>
    internal const string VersionKey = "events:list:version";
    internal static readonly TimeSpan VersionTtl = TimeSpan.FromDays(30);
    internal static readonly TimeSpan ListTtl = TimeSpan.FromSeconds(60);

    public async Task<Result<PagedResult<ListEventsResponse>>> Handle(ListEventsQuery request, CancellationToken cancellationToken)
    {
        // Danh sách công khai (không có bản nháp) nên mọi người dùng chung một kết quả cho cùng tham số.
        var version = await cache.GetOrCreateAsync(
            VersionKey,
            _ => Task.FromResult<string?>(Guid.NewGuid().ToString("N")),
            VersionTtl,
            cancellationToken) ?? throw new InvalidOperationException("Version factory always returns a value.");

        var page = await cache.GetOrCreateAsync(
            ListKey(version, request),
            ct => LoadPageAsync(request, ct),
            ListTtl,
            cancellationToken);

        return Result.Success(page!);
    }

    // Mỗi tham số có mặt trong khoá: đổi bất kỳ tham số nào là ra khoá khác, không lẫn kết quả.
    internal static string ListKey(string version, ListEventsQuery request) =>
        FormattableString.Invariant(
            $"events:list:{version}:s={SearchHash(request.Search)}:p={request.Page}:ps={request.PageSize}:f={request.From?.ToUniversalTime():O}:t={request.To?.ToUniversalTime():O}");

    // Băm từ khoá: người dùng nhập ":" hay "=" không làm lệch cấu trúc khoá.
    private static string SearchHash(string? search)
    {
        var normalized = search?.Trim().ToLowerInvariant() ?? string.Empty;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private async Task<PagedResult<ListEventsResponse>?> LoadPageAsync(ListEventsQuery request, CancellationToken cancellationToken)
    {
        var query = readDbContext.Query<Event>()
            .Where(e => e.Status != EventStatus.Draft);

        var search = request.Search?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(search))
        {
            // Biểu thức dịch thành lower() của Postgres; Npgsql chỉ dịch được ToLower() không tham số.
            #pragma warning disable CA1304, CA1311, CA1862
                        query = query.Where(e => e.Name.ToLower().Contains(search));
            #pragma warning restore CA1304, CA1311, CA1862
        }

        // Npgsql chỉ nhận DateTimeOffset offset 0 cho timestamptz: client gửi +07:00 thì đổi về UTC (cùng thời điểm).
        if (request.From is { } from)
        {
            var fromUtc = from.ToUniversalTime();
            query = query.Where(e => e.StartsAt >= fromUtc);
        }

        if (request.To is { } to)
        {
            var toUtc = to.ToUniversalTime();
            query = query.Where(e => e.StartsAt < toUtc);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var events = await query
            .OrderBy(e => e.StartsAt)
            .ThenBy(e => e.Id)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(e => new ListEventsResponse(
                e.Id,
                e.Name,
                e.Description,
                e.Venue,
                e.StartsAt,
                e.EndsAt,
                e.SaleStartsAt,
                e.SaleEndsAt,
                e.Status.ToString()))
            .ToListAsync(cancellationToken);
        return new PagedResult<ListEventsResponse>(
            Items: events,
            Page: request.Page,
            PageSize: request.PageSize,
            TotalCount: totalCount
        );
    }
}
