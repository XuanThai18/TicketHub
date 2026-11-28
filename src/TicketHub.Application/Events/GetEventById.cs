using MediatR;
using Microsoft.EntityFrameworkCore;
using TicketHub.Application.Abstractions;
using TicketHub.Domain.Common;
using TicketHub.Domain.Events;
using TicketHub.Domain.Shared;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.Application.Events;

public sealed record EventDetailResponse(
    Guid Id,
    string Name,
    string? Description,
    string Venue,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset SaleStartsAt,
    DateTimeOffset SaleEndsAt,
    string Status,
    IReadOnlyList<TicketTypeResponse> TicketTypes);

public sealed record TicketTypeResponse(
    Guid Id,
    string Name,
    string? Description,
    decimal Price,
    int AvailableQuantity,
    int MaxQuantityPerOrder);

public sealed record GetEventByIdQuery(Guid Id) : IRequest<Result<EventDetailResponse>>;

public sealed class GetEventByIdQueryHandler(IReadDbContext readDbContext, ICurrentUser currentUser, ICacheService cache)
    : IRequestHandler<GetEventByIdQuery, Result<EventDetailResponse>>
{
    /// <summary>Phần ít đổi (tên, giờ, loại vé, giá) được cache; số vé còn thì không, xem <see cref="WithAvailableQuantitiesAsync"/>.</summary>
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    internal static string PublicCacheKey(Guid eventId) => $"event:{eventId}:public";

    public async Task<Result<EventDetailResponse>> Handle(GetEventByIdQuery request, CancellationToken cancellationToken)
    {
        // Bản nháp chỉ ban tổ chức (Admin, Manager) thấy, vì họ là người tạo và sửa nó; bản công khai thì ai cũng thấy.
        var canSeeDraft = currentUser.IsInRole(Roles.Admin) || currentUser.IsInRole(Roles.Manager);

        // Người có quyền xem bản nháp đi thẳng vào DB: không ghi bản nháp vào cache công khai.
        if (canSeeDraft)
        {
            var draft = await LoadEventAsync(request.Id, includeDraft: true, cancellationToken);
            return draft is null
                ? EventErrors.NotFound(request.Id)
                : await WithAvailableQuantitiesAsync(draft, cancellationToken);
        }

        var cached = await cache.GetOrCreateAsync(
            PublicCacheKey(request.Id),
            ct => LoadEventAsync(request.Id, includeDraft: false, ct),
            CacheTtl,
            cancellationToken);

        return cached is null
            ? EventErrors.NotFound(request.Id)
            : await WithAvailableQuantitiesAsync(cached, cancellationToken);
    }

    private async Task<CachedEventDetail?> LoadEventAsync(Guid eventId, bool includeDraft, CancellationToken cancellationToken)
    {
        var ev = await readDbContext.Query<Event>()
            .Where(e => e.Id == eventId && (includeDraft || e.Status != EventStatus.Draft))
            .Select(e => new CachedEventDetail(
                e.Id,
                e.Name,
                e.Description,
                e.Venue,
                e.StartsAt,
                e.EndsAt,
                e.SaleStartsAt,
                e.SaleEndsAt,
                e.Status.ToString(),
                new List<CachedTicketType>()))
            .FirstOrDefaultAsync(cancellationToken);

        if (ev is null)
        {
            return null;
        }

        var ticketTypes = await readDbContext.Query<TicketType>()
            .Where(t => t.EventId == eventId)
            .OrderBy(t => t.Price)
            .Select(t => new CachedTicketType(t.Id, t.Name, t.Description, t.Price, t.MaxQuantityPerOrder))
            .ToListAsync(cancellationToken);

        return ev with { TicketTypes = ticketTypes };
    }

    // Số vé còn đổi theo từng đơn, nên luôn đọc lại từ DB (một truy vấn nhẹ theo EventId), không lấy từ cache.
    private async Task<Result<EventDetailResponse>> WithAvailableQuantitiesAsync(CachedEventDetail detail, CancellationToken cancellationToken)
    {
        var available = (await readDbContext.Query<TicketType>()
                .Where(t => t.EventId == detail.Id)
                .Select(t => new { t.Id, t.AvailableQuantity })
                .ToListAsync(cancellationToken))
            .ToDictionary(t => t.Id, t => t.AvailableQuantity);

        return new EventDetailResponse(
            detail.Id,
            detail.Name,
            detail.Description,
            detail.Venue,
            detail.StartsAt,
            detail.EndsAt,
            detail.SaleStartsAt,
            detail.SaleEndsAt,
            detail.Status,
            detail.TicketTypes
                .Select(t => new TicketTypeResponse(
                    t.Id,
                    t.Name,
                    t.Description,
                    t.Price,
                    available.GetValueOrDefault(t.Id),
                    t.MaxQuantityPerOrder))
                .ToList());
    }
}

/// <summary>Chi tiết sự kiện lưu trong cache: không có số vé còn.</summary>
public sealed record CachedEventDetail(
    Guid Id,
    string Name,
    string? Description,
    string Venue,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset SaleStartsAt,
    DateTimeOffset SaleEndsAt,
    string Status,
    List<CachedTicketType> TicketTypes);

public sealed record CachedTicketType(Guid Id, string Name, string? Description, decimal Price, int MaxQuantityPerOrder);
