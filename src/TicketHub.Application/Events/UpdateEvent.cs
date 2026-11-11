using FluentValidation;
using MediatR;
using TicketHub.Application.Abstractions;
using TicketHub.Domain.Events;
using TicketHub.Domain.Shared;

namespace TicketHub.Application.Events;

public sealed record UpdateEventCommand(
    Guid Id,
    string Name,
    string? Description,
    string Venue,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset SaleStartsAt,
    DateTimeOffset SaleEndsAt) : IRequest<Result>;

public sealed class UpdateEventCommandValidator : AbstractValidator<UpdateEventCommand>
{
    public UpdateEventCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Event.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(Event.DescriptionMaxLength);
        RuleFor(x => x.Venue).NotEmpty().MaximumLength(Event.VenueMaxLength);
        RuleFor(x => x.StartsAt).NotEmpty();
        RuleFor(x => x.EndsAt).NotEmpty();
        RuleFor(x => x.SaleStartsAt).NotEmpty();
        RuleFor(x => x.SaleEndsAt).NotEmpty();
    }
}

public sealed class UpdateEventCommandHandler(IEventRepository eventRepository, IUnitOfWork unitOfWork) : IRequestHandler<UpdateEventCommand, Result>
{
    public async Task<Result> Handle(UpdateEventCommand request, CancellationToken cancellationToken)
    {
        var existingEvent = await eventRepository.GetByIdAsync(request.Id, cancellationToken);
        if (existingEvent is null)
        {
            return EventErrors.NotFound(request.Id);
        }

        var updated = existingEvent.Update(
            request.Name,
            request.Description,
            request.Venue,
            request.StartsAt,
            request.EndsAt,
            request.SaleStartsAt,
            request.SaleEndsAt);
        if (updated.IsFailure)
        {
            return updated.Error;
        }
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
