using System.ComponentModel.DataAnnotations;

namespace TicketHub.Application.Payments;

public sealed class PaymentOptions
{
    public const string SectionName = "Payments";

    /// <summary>Khoá bí mật dùng chung với cổng thanh toán để ký webhook (HMAC-SHA256). Production đặt qua biến môi trường.</summary>
    [Required, MinLength(32)]
    public string WebhookSecret { get; init; } = string.Empty;

    /// <summary>Webhook có timestamp lệch quá khoảng này so với giờ server thì bị từ chối (chống gửi lại webhook cũ).</summary>
    [Range(30, 3600)]
    public int SignatureToleranceSeconds { get; init; } = 300;
}
