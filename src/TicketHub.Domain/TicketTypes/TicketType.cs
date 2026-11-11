using TicketHub.Domain.Common;
using TicketHub.Domain.Shared;

namespace TicketHub.Domain.TicketTypes;

public sealed class TicketType : Entity
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 1000;

    private TicketType() { } // For EF Core
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public decimal Price { get; private set; }
    public int TotalQuantity { get; private set; }
    public int AvailableQuantity { get; private set; }
    public int MaxQuantityPerOrder { get; private set; }
    public Guid EventId { get; private set; }

    public static Result<TicketType> Create(
        Guid eventId,
        string name,
        string? description,
        decimal price,
        int totalQuantity,
        int maxQuantityPerOrder)
    {
        if (Validate(price, totalQuantity, maxQuantityPerOrder) is { } error)
        {
            return error;
        }

        return new TicketType
        {
            EventId = eventId,
            Id = Guid.CreateVersion7(),
            Name = name.Trim(),
            Description = description?.Trim(),
            Price = price,
            TotalQuantity = totalQuantity,
            AvailableQuantity = totalQuantity,
            MaxQuantityPerOrder = maxQuantityPerOrder
        };
    }

    public Result Update(
        string name,
        string? description,
        decimal price,
        int totalQuantity,
        int maxQuantityPerOrder)
    {
        if (Validate(price, totalQuantity, maxQuantityPerOrder) is { } error)
        {
            return error;
        }

        var soldQuantity = TotalQuantity - AvailableQuantity; // Calculate the number of tickets already sold
        if (totalQuantity < soldQuantity)
        {
            return TicketTypeErrors.TotalBelowSold;
        }

        Name = name.Trim();
        Description = description?.Trim();
        Price = price;
        TotalQuantity = totalQuantity;
        AvailableQuantity = totalQuantity - soldQuantity; // Update available quantity based on the new total and sold quantity
        MaxQuantityPerOrder = maxQuantityPerOrder;
        return Result.Success();
    }

    private static Error? Validate(decimal price, int totalQuantity, int maxQuantityPerOrder)
    {
        if (price < 0) return TicketTypeErrors.InvalidPrice;
        if (totalQuantity <= 0) return TicketTypeErrors.InvalidTotalQuantity;
        if (maxQuantityPerOrder <= 0 || maxQuantityPerOrder > totalQuantity)
            return TicketTypeErrors.InvalidMaxQuantityPerOrder;
        return null;
    }
}
