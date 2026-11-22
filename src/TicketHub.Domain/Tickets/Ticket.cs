using System.Security.Cryptography;
using TicketHub.Domain.Common;

namespace TicketHub.Domain.Tickets;

/// <summary>Một vé điện tử. Đơn mua 3 vé VIP sinh ra 3 vé, mỗi vé một mã riêng để in thành QR và soát vé.</summary>
public sealed class Ticket : Entity
{
    public const int CodeLength = 16;
    public const int CodeMaxLength = 32;

    // Bỏ các ký tự dễ đọc nhầm (0/O, 1/I/L) vì mã có thể phải đọc to hoặc gõ tay ở cổng soát vé.
    // 31 ký tự → 16 ký tự cho khoảng 79 bit ngẫu nhiên: không đoán được, trùng gần như không thể.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private Ticket() { } // For EF Core

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid TicketTypeId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public TicketStatus Status { get; private set; }
    public DateTimeOffset IssuedAt { get; private set; }

    /// <summary>Phát một vé hợp lệ cho một chỗ trong đơn đã thanh toán.</summary>
    public static Ticket Issue(Guid orderId, Guid ticketTypeId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        OrderId = orderId,
        TicketTypeId = ticketTypeId,
        Code = NewCode(),
        Status = TicketStatus.Valid,
        IssuedAt = now
    };

    // RandomNumberGenerator (mật mã), không dùng Random: mã vé là thứ người khác không được đoán ra.
    private static string NewCode() =>
        string.Create(CodeLength, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }
        });
}
