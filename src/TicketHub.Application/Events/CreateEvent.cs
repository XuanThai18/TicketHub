using FluentValidation;
using MediatR;
using TicketHub.Domain.Shared;
using TicketHub.Domain.Events;
using TicketHub.Application.Abstractions;

namespace TicketHub.Application.Events;

public sealed record CreateEventCommand(
    string Name,
    string? Description,
    string Venue,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset SaleStartsAt,
    DateTimeOffset SaleEndsAt) : IRequest<Result<Guid>>;

public sealed class CreateEventCommandValidator : AbstractValidator<CreateEventCommand>
{
    public CreateEventCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Event.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(Event.DescriptionMaxLength);
        RuleFor(x => x.Venue).NotEmpty().MaximumLength(Event.VenueMaxLength);
        RuleFor(x => x.StartsAt).NotEmpty();
        RuleFor(x => x.EndsAt).NotEmpty();
        RuleFor(x => x.SaleStartsAt).NotEmpty();
        RuleFor(x => x.SaleEndsAt).NotEmpty();
    }
}

public sealed class CreateEventCommandHandler(IEventRepository eventRepository, IUnitOfWork unitOfWork) : IRequestHandler<CreateEventCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateEventCommand request, CancellationToken cancellationToken)
    {
        var created = Event.Create(
            request.Name,
            request.Description,
            request.Venue,
            request.StartsAt,
            request.EndsAt,
            request.SaleStartsAt,
            request.SaleEndsAt);

        if (created.IsFailure)
        {
            return created.Error;
        }

        eventRepository.Add(created.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return created.Value.Id;
    }
}
