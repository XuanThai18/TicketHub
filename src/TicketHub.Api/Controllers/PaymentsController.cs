using System.Text;
using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TicketHub.Application.Payments;

namespace TicketHub.Api.Controllers;

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/payments")]
public sealed class PaymentsController(ISender sender) : BaseApiController
{
    /// <summary>
    /// Cổng thanh toán báo kết quả. Không cần đăng nhập (cổng không có token của ta) nhưng phải ký HMAC đúng.
    /// Nhận hợp lệ (kể cả bản gửi lặp) luôn trả 200 để cổng ngừng gửi lại.
    /// </summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Webhook(CancellationToken cancellationToken)
    {
        // Đọc body thô: chữ ký tính trên đúng các byte cổng gửi. Bind sang object rồi serialize lại có thể đổi
        // thứ tự trường/khoảng trắng và làm chữ ký sai.
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(cancellationToken);

        var result = await sender.Send(new HandlePaymentWebhookCommand(
            body,
            Request.Headers[PaymentSignature.TimestampHeader].ToString(),
            Request.Headers[PaymentSignature.SignatureHeader].ToString()), cancellationToken);

        return result.IsSuccess ? Ok() : ToProblem(result.Error);
    }
}
