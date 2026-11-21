using FluentValidation;
using MediatR;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Common.Exceptions;
using TicketHub.Domain.Events;
using TicketHub.Domain.Orders;
using TicketHub.Domain.Shared;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.Application.Orders;

public sealed record PlaceOrderLine(Guid TicketTypeId, int Quantity);

/// <param name="Lines">Các dòng (loại vé, số lượng) của đơn.</param>
/// <param name="IdempotencyKey">Header Idempotency-Key; null = không chống gửi lặp, luôn tạo đơn mới.</param>
public sealed record PlaceOrderCommand(IReadOnlyList<PlaceOrderLine> Lines, string? IdempotencyKey = null)
    : IRequest<Result<PlaceOrderResult>>;

/// <param name="OrderId">Id của đơn.</param>
/// <param name="Replayed">true khi đây là đơn đã tạo từ trước với cùng Idempotency-Key, không phải đơn mới.</param>
public sealed record PlaceOrderResult(Guid OrderId, bool Replayed);

public sealed class PlaceOrderCommandValidator : AbstractValidator<PlaceOrderCommand>
{
    public PlaceOrderCommandValidator()
    {
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.TicketTypeId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0);
        });

        // Có gửi header thì key phải có nội dung và vừa cột idempotency_key.
        RuleFor(x => x.IdempotencyKey)
            .NotEmpty()
            .MaximumLength(Order.IdempotencyKeyMaxLength)
            .When(x => x.IdempotencyKey is not null);
    }
}

public sealed class PlaceOrderCommandHandler(
    ICurrentUser currentUser,
    IEventRepository eventRepository,
    ITicketTypeRepository ticketTypeRepository,
    IOrderRepository orderRepository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IRequestHandler<PlaceOrderCommand, Result<PlaceOrderResult>>
{
    // Tên unique index (user_id, idempotency_key) do EF đặt trong OrderConfiguration. Integration test giữ khớp hai nơi.
    public const string IdempotencyKeyIndex = "ix_orders_user_id_idempotency_key";

    public async Task<Result<PlaceOrderResult>> Handle(PlaceOrderCommand request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(currentUser.UserId, out var userId))
        {
            return Error.Unauthorized("Orders.Unauthorized", "The user is not signed in.");
        }

        var key = request.IdempotencyKey;

        // 0. Đường nhanh: key này đã tạo đơn rồi (gửi lại sau vài giây) → trả lại đơn đó, không chạy lại gì.
        //    Đặt trước mọi kiểm tra khác: lần gửi lại vẫn nhận đơn cũ dù lúc này sự kiện đã hết giờ bán.
        if (key is not null && await orderRepository.GetByIdempotencyKeyAsync(userId, key, cancellationToken) is { } existing)
        {
            return Replay(existing, request);
        }

        var now = timeProvider.GetUtcNow();

        // 1. Tải tất cả loại vé trong một truy vấn, rồi kiểm từng dòng có tồn tại không.
        var ids = request.Lines.Select(l => l.TicketTypeId).Distinct().ToList();
        var ticketTypes = await ticketTypeRepository.GetByIdsAsync(ids, cancellationToken);
        var byId = ticketTypes.ToDictionary(t => t.Id);

        foreach (var line in request.Lines)
        {
            if (!byId.ContainsKey(line.TicketTypeId))
            {
                return TicketTypeErrors.NotFound(line.TicketTypeId);
            }
        }

        var lines = request.Lines
            .Select(l => (TicketType: byId[l.TicketTypeId], Quantity: l.Quantity))
            .ToList();

        // 2. Dựng đơn: kiểm luật thuần tuý (rỗng, trùng loại vé, khác sự kiện, số lượng). Chưa trừ vé.
        var created = Order.Create(userId, lines, now, key);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var order = created.Value;

        // 3. Sự kiện phải đang mở bán.
        var ev = await eventRepository.GetByIdAsync(order.EventId, cancellationToken);
        if (ev is null)
        {
            return EventErrors.NotFound(order.EventId);
        }

        if (ev.Status != EventStatus.Published || now < ev.SaleStartsAt || now >= ev.SaleEndsAt)
        {
            return OrderErrors.NotOnSale;
        }

        // 4. Trừ vé bằng UPDATE có điều kiện rồi lưu đơn, tất cả trong một transaction.
        //    Một dòng hết vé thì rollback cả những dòng đã trừ. Sắp theo Id để hai đơn nhiều loại vé
        //    luôn khoá dòng theo cùng thứ tự, không khoá chéo nhau (deadlock).
        var reservations = lines
            .Select(l => (TicketTypeId: l.TicketType.Id, l.Quantity))
            .OrderBy(l => l.TicketTypeId)
            .ToList();

        try
        {
            return await unitOfWork.ExecuteInTransactionAsync<Result<PlaceOrderResult>>(async ct =>
            {
                foreach (var (ticketTypeId, quantity) in reservations)
                {
                    if (!await ticketTypeRepository.TryReserveStockAsync(ticketTypeId, quantity, ct))
                    {
                        return TicketTypeErrors.SoldOut;
                    }
                }

                // Add nằm trong delegate: nếu transaction chạy lại, tracker đã bị xoá nên phải thêm đơn lần nữa.
                orderRepository.Add(order);
                await unitOfWork.SaveChangesAsync(ct);
                return new PlaceOrderResult(order.Id, Replayed: false);
            }, cancellationToken: cancellationToken);
        }
        catch (UniqueConstraintException ex) when (key is not null && ex.ConstraintName == IdempotencyKeyIndex)
        {
            // 5. Đường đua: request khác cùng key đã qua bước 0 cùng lúc với request này và commit trước.
            //    Postgres bắt INSERT này chờ request kia xong rồi báo trùng; transaction đã rollback nên vé vừa trừ
            //    được trả lại. Đọc đơn của request kia và trả lại như một lần gửi lặp.
            var winner = await orderRepository.GetByIdempotencyKeyAsync(userId, key, cancellationToken)
                ?? throw new InvalidOperationException($"Idempotency key conflict for user {userId} but no order was found.", ex);
            return Replay(winner, request);
        }
    }

    // Cùng nội dung → trả lại đơn cũ. Khác nội dung → client đang dùng nhầm key cho một lần mua khác.
    private static Result<PlaceOrderResult> Replay(Order existing, PlaceOrderCommand request) =>
        existing.HasSameLines(request.Lines.Select(l => (l.TicketTypeId, l.Quantity)))
            ? new PlaceOrderResult(existing.Id, Replayed: true)
            : OrderErrors.IdempotencyKeyReused;
}
