using System.Globalization;
using System.Text.Json;
using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TicketHub.Application.Payments;

namespace TicketHub.Api.Controllers;

/// <summary>
/// Cổng thanh toán GIẢ LẬP, chỉ có ở Development và Testing (môi trường khác trả 404).
/// Đóng vai cổng: dựng webhook, ký HMAC bằng khoá chung, rồi đưa vào đúng handler webhook thật,
/// nên vẫn đi qua bước kiểm chữ ký. Trên Scalar bấm một lần là "đã trả tiền".
/// </summary>
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/mock-gateway")]
public sealed class MockGatewayController(
    ISender sender,
    IOptions<PaymentOptions> options,
    TimeProvider timeProvider,
    IWebHostEnvironment environment) : BaseApiController
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <param name="transactionId">Mã giao dịch trong checkoutUrl mà POST /orders/{id}/pay trả về.</param>
    /// <param name="outcome"><c>succeeded</c> (mặc định) hoặc <c>failed</c>.</param>
    /// <param name="cancellationToken">Huỷ khi client ngắt kết nối.</param>
    [HttpPost("checkout/{transactionId}/complete")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Complete(
        string transactionId, [FromQuery] string outcome = PaymentWebhookPayload.Succeeded, CancellationToken cancellationToken = default)
    {
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            return NotFound();
        }

        if (outcome is not (PaymentWebhookPayload.Succeeded or PaymentWebhookPayload.Failed))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "outcome must be 'succeeded' or 'failed'.");
        }

        var now = timeProvider.GetUtcNow();
        var body = JsonSerializer.Serialize(new PaymentWebhookPayload(transactionId, outcome, now), _json);
        var timestamp = now.ToUnixTimeSeconds();
        var signature = PaymentSignature.Compute(options.Value.WebhookSecret, timestamp, body);

        var result = await sender.Send(new HandlePaymentWebhookCommand(
            body, timestamp.ToString(CultureInfo.InvariantCulture), signature), cancellationToken);

        return result.IsSuccess ? Ok(new { delivered = true, webhook = body }) : ToProblem(result.Error);
    }
}
