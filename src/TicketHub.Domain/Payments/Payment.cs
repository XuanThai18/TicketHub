using TicketHub.Domain.Common;

namespace TicketHub.Domain.Payments;

/// <summary>Một lượt thanh toán cho một đơn. Kết quả do cổng thanh toán báo qua webhook, không do client báo.</summary>
public sealed class Payment : Entity
{
    public const string MockProvider = "Mock";
    public const int ProviderMaxLength = 30;
    public const int ProviderTransactionIdMaxLength = 100;

    private Payment() { } // For EF Core

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public string Provider { get; private set; } = string.Empty;

    /// <summary>Mã giao dịch phía cổng; webhook dùng mã này để chỉ ra đang nói về lượt thanh toán nào.</summary>
    public string ProviderTransactionId { get; private set; } = string.Empty;

    public decimal Amount { get; private set; }
    public PaymentStatus Status { get; private set; }

    /// <summary>Nguyên văn webhook đã nhận (JSON), để đối soát với cổng.</summary>
    public string? RawPayload { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>
    /// Mở lượt thanh toán ở cổng giả lập. Số tiền lấy từ đơn ở server, không bao giờ nhận từ client.
    /// Cổng thật (VNPay, Stripe...) sẽ trả mã giao dịch; cổng giả lập tự sinh.
    /// </summary>
    public static Payment CreateMock(Guid orderId, decimal amount) => new()
    {
        Id = Guid.CreateVersion7(),
        OrderId = orderId,
        Provider = MockProvider,
        ProviderTransactionId = $"mock_{Guid.NewGuid():N}",
        Amount = amount,
        Status = PaymentStatus.Pending
    };
}
