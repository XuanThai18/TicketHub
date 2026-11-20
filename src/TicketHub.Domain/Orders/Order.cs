using TicketHub.Domain.Common;
using TicketHub.Domain.OrderItems;
using TicketHub.Domain.Shared;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.Domain.Orders;

public sealed class Order : Entity
{
    public const int CodeMaxLength = 30;
    public const int CodeMinLength = 6;
    public const int IdempotencyKeyMaxLength = 100;
    public const int ReservationMinutes = 10;   // thời gian giữ chỗ

    private readonly List<OrderItem> _items = [];
    public IReadOnlyList<OrderItem> Items => _items; // Danh sách vé trong đơn hàng, không cho phép sửa trực tiếp từ bên ngoài.

    private Order() { } // For EF Core

    public Guid Id { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public Guid UserId { get; private set; }
    public OrderStatus Status { get; private set; }
    public decimal TotalAmount { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; } // het han giu cho = luc khoi tao + 10p
    public DateTimeOffset? PaidAt { get; private set; }
    public string? IdempotencyKey { get; private set; }
    public Guid EventId { get; private set; } // mỗi đơn chỉ chứa vé của **một** sự kiện

    public static Result<Order> Create(
        Guid userId,
        IReadOnlyList<(TicketType TicketType, int Quantity)> lines,
        DateTimeOffset now)
    {
        if (Validate(lines) is { } error)
            return error;

        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            Code = $"TH-{now:yyyyMMdd}-{Guid.NewGuid():N}"[..21].ToUpperInvariant(),
            UserId = userId,
            EventId = lines[0].TicketType.EventId,
            Status = OrderStatus.Pending,
            ExpiresAt = now.AddMinutes(ReservationMinutes)
        };

        foreach (var (ticketType, quantity) in lines)
        {
            order._items.Add(OrderItem.Create(order.Id, ticketType.Id, quantity, ticketType.Price));
        }

        order.TotalAmount = order._items.Sum(i => i.Quantity * i.UnitPrice);
        return order;
    }

    public Result Expire(DateTimeOffset now)
    {
        if (Status != OrderStatus.Pending || now < ExpiresAt)
            return OrderErrors.NotExpirable;

        Status = OrderStatus.Expired;
        return Result.Success();
    }

    private static Error? Validate(IReadOnlyList<(TicketType TicketType, int Quantity)> lines)
    {
        if (lines.Count == 0)
            return OrderErrors.Empty;

        if (lines.Select(l => l.TicketType.Id).Distinct().Count() != lines.Count)
            return OrderErrors.DuplicateTicketType;

        if (lines.Select(l => l.TicketType.EventId).Distinct().Count() > 1)
            return OrderErrors.MixedEvents;

        foreach (var (ticketType, quantity) in lines)
        {
            if (quantity <= 0)
                return OrderErrors.InvalidQuantity;

            if (quantity > ticketType.MaxQuantityPerOrder)
                return OrderErrors.ExceedsMaxPerOrder;
        }

        return null;
    }
}
