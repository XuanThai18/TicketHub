using FluentValidation;
using MediatR;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Common.Exceptions;
using TicketHub.Domain.Events;
using TicketHub.Domain.Shared;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.Application.TicketTypes;

public sealed record AddTicketTypeCommand(Guid EventId, string Name, string? Description, decimal Price, int TotalQuantity, int MaxQuantityPerOrder) : IRequest<Result<Guid>>;

public sealed class AddTicketTypeCommandValidator : AbstractValidator<AddTicketTypeCommand>
{
    public AddTicketTypeCommandValidator()
    {
        RuleFor(x => x.EventId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(TicketType.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(TicketType.DescriptionMaxLength);
    }
}

public sealed class AddTicketTypeCommandHandler(IEventRepository eventRepository, ITicketTypeRepository ticketTypeRepository, IUnitOfWork unitOfWork) : IRequestHandler<AddTicketTypeCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AddTicketTypeCommand request, CancellationToken cancellationToken)
    {
        var existingEvent = await eventRepository.GetByIdAsync(request.EventId, cancellationToken);
        if (existingEvent is null)
        {
            return EventErrors.NotFound(request.EventId);
        }

        var editable = existingEvent.EnsureEditable();
        if (editable.IsFailure)
        {
            return editable.Error;
        }

        var name = request.Name.Trim();
        var checkExistingName = await ticketTypeRepository.NameExistsAsync(request.EventId, name, cancellationToken);
        if (checkExistingName)
        {
            return TicketTypeErrors.DuplicateName(name);
        }

        var created = TicketType.Create(
            request.EventId,
            name,
            request.Description,
            request.Price,
            request.TotalQuantity,
            request.MaxQuantityPerOrder);
        if (created.IsFailure)
        {
            return created.Error;
        }
        ticketTypeRepository.Add(created.Value);

        // bắt trường hợp hai request cùng lúc
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintException)
        {
            return TicketTypeErrors.DuplicateName(name);
        }

        return created.Value.Id;
    }
}
