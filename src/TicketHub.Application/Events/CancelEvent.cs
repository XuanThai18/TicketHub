using FluentValidation;
using MediatR;
using TicketHub.Application.Abstractions;
using TicketHub.Domain.Events;
using TicketHub.Domain.Shared;

namespace TicketHub.Application.Events;

public sealed record CancelEventCommand(Guid Id) : IRequest<Result>;

public sealed class CancelEventCommandValidator : AbstractValidator<CancelEventCommand>
{
    public CancelEventCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}

public sealed class CancelEventCommandHandler(IEventRepository eventRepository, IUnitOfWork unitOfWork,
    ICacheService cache) : IRequestHandler<CancelEventCommand, Result>
{
    public async Task<Result> Handle(CancelEventCommand request, CancellationToken cancellationToken)
    {
        var existingEvent = await eventRepository.GetByIdAsync(request.Id, cancellationToken);
        if (existingEvent is null)
        {
            return EventErrors.NotFound(request.Id);
        }
        var result = existingEvent.Cancel();
        if (result.IsFailure)
        {
            return result.Error;
        }
        await unitOfWork.SaveChangesAsync(cancellationToken);
        // Sau khi lưu: sự kiện hết hiệu lực, đổi cả danh sách lẫn chi tiết.
        await cache.RemoveAsync(GetEventByIdQueryHandler.PublicCacheKey(request.Id), cancellationToken);
        await cache.RemoveAsync(ListEventsQueryHandler.VersionKey, cancellationToken);
        return Result.Success();
    }
}
