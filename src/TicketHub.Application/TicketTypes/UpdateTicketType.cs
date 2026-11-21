using FluentValidation;
using MediatR;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Common.Exceptions;
using TicketHub.Domain.Events;
using TicketHub.Domain.Shared;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.Application.TicketTypes;

// Gửi giá trị mới (vd TotalQuantity = 300), không gửi phần thêm. Số vé còn được tính lại từ số đã bán.
public sealed record UpdateTicketTypeCommand(
    Guid EventId,
    Guid TicketTypeId,
    string Name,
    string? Description,
    decimal Price,
    int TotalQuantity,
    int MaxQuantityPerOrder) : IRequest<Result>;

public sealed class UpdateTicketTypeCommandValidator : AbstractValidator<UpdateTicketTypeCommand>
{
    public UpdateTicketTypeCommandValidator()
    {
        RuleFor(x => x.EventId).NotEmpty();
        RuleFor(x => x.TicketTypeId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(TicketType.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(TicketType.DescriptionMaxLength);
    }
}

public sealed class UpdateTicketTypeCommandHandler(
    IEventRepository eventRepository,
    ITicketTypeRepository ticketTypeRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<UpdateTicketTypeCommand, Result>
{
    // Bên admin ít tranh chấp: vài lần là đủ, hết lượt thì trả 409 để admin thử lại.
    private const int MaxAttempts = 3;

    public async Task<Result> Handle(UpdateTicketTypeCommand request, CancellationToken cancellationToken)
    {
        var existingEvent = await eventRepository.GetByIdAsync(request.EventId, cancellationToken);
        if (existingEvent is null)
        {
            return EventErrors.NotFound(request.EventId);
        }

        // Khác thêm loại vé (chỉ khi Draft): sửa được cả lúc đang bán, ví dụ tăng thêm vé khi bán chạy.
        if (existingEvent.Status == EventStatus.Cancelled)
        {
            return TicketTypeErrors.EventCancelled;
        }

        var name = request.Name.Trim();

        // Đọc → sửa → lưu có thể đụng người mua/huỷ vé chen vào giữa: xmin đã đổi nên SaveChanges ném xung đột.
        // Ý của admin là giá trị tuyệt đối nên đọc lại số mới rồi áp lại là đúng; mỗi lần đều kiểm lại TotalBelowSold.
        // ExecuteInTransactionAsync xoá tracker khi có exception, nhờ vậy lần sau đọc được bản mới thay vì bản cũ đã track.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await unitOfWork.ExecuteInTransactionAsync(
                    ct => UpdateOnceAsync(request, name, ct),
                    cancellationToken: cancellationToken);
            }
            catch (ConcurrencyConflictException) when (attempt < MaxAttempts)
            {
                // Thử lại với số liệu mới.
            }
            catch (ConcurrencyConflictException)
            {
                return TicketTypeErrors.ConcurrentUpdate;
            }
            catch (UniqueConstraintException)
            {
                // Hai admin cùng đổi sang một tên, cả hai đều qua bước kiểm tên trong UpdateOnceAsync.
                return TicketTypeErrors.DuplicateName(name);
            }
        }
    }

    private async Task<Result> UpdateOnceAsync(UpdateTicketTypeCommand request, string name, CancellationToken cancellationToken)
    {
        var ticketType = await ticketTypeRepository.GetByIdAsync(request.TicketTypeId, cancellationToken);
        if (ticketType is null || ticketType.EventId != request.EventId)
        {
            return TicketTypeErrors.NotFound(request.TicketTypeId);
        }

        // Kiểm tên sau khi chắc loại vé thuộc sự kiện này, để loại vé sai sự kiện nhận 404 chứ không phải 409 trùng tên.
        if (await ticketTypeRepository.NameExistsForOtherAsync(request.EventId, request.TicketTypeId, name, cancellationToken))
        {
            return TicketTypeErrors.DuplicateName(name);
        }

        var updated = ticketType.Update(
            name,
            request.Description,
            request.Price,
            request.TotalQuantity,
            request.MaxQuantityPerOrder);
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
