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
    }
}

public sealed record ListEventsQuery(string? Search, int Page, int PageSize) : IRequest<Result<PagedResult<ListEventsResponse>>>;

public sealed class ListEventsQueryHandler(IReadDbContext readDbContext) : IRequestHandler<ListEventsQuery, Result<PagedResult<ListEventsResponse>>>
{
    public async Task<Result<PagedResult<ListEventsResponse>>> Handle(ListEventsQuery request, CancellationToken cancellationToken)
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
