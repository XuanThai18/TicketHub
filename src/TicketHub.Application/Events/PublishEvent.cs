using FluentValidation;
using MediatR;
using TicketHub.Application.Abstractions;
using TicketHub.Domain.Events;
using TicketHub.Domain.Shared;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.Application.Events;

public sealed record PublishEventCommand(Guid Id) : IRequest<Result>;

public sealed class PublishEventCommandValidator : AbstractValidator<PublishEventCommand>
{
    public PublishEventCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}

public sealed class PublishEventCommandHandler(IEventRepository eventRepository, ITicketTypeRepository ticketTypeRepository, IUnitOfWork unitOfWork) : IRequestHandler<PublishEventCommand, Result>
{ 
    public async Task<Result> Handle(PublishEventCommand request, CancellationToken cancellationToken)
    {
        var existingEvent = await eventRepository.GetByIdAsync(request.Id, cancellationToken);
        if (existingEvent is null)
        {
            return EventErrors.NotFound(request.Id);
        }

        int ticketTypeCount = await ticketTypeRepository.CountByEventIdAsync(request.Id, cancellationToken);

        var result = existingEvent.Publish(ticketTypeCount);
        if (result.IsFailure)
        {
            return result.Error;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
