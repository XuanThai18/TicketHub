using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TicketHub.Api.Contracts.Common;
using TicketHub.Api.Contracts.Orders;
using TicketHub.Application.Common.Paging;
using TicketHub.Application.Orders;
using TicketHub.Application.Payments;

namespace TicketHub.Api.Controllers;

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/orders")]
public sealed class OrdersController(ISender sender) : BaseApiController
{
    [HttpPost]
    [Authorize]
    [ProducesResponseType<CreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> PlaceOrder(
        [FromBody] PlaceOrderRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var lines = request.Lines
            .Select(l => new PlaceOrderLine(l.TicketTypeId, l.Quantity))
            .ToList();

        // Header gửi rỗng (hoặc toàn khoảng trắng, bị cắt thành rỗng) bị model binding đổi thành null, trông như không gửi.
        // Giữ lại chuỗi rỗng để validator trả 400, thay vì âm thầm tạo đơn không chống gửi lặp.
        if (idempotencyKey is null && Request.Headers.ContainsKey(IdempotencyKeyHeader))
        {
            idempotencyKey = string.Empty;
        }

        var result = await sender.Send(new PlaceOrderCommand(lines, idempotencyKey), cancellationToken);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        // Gửi lặp cùng key vẫn trả 201 y hệt lần đầu, để client chỉ cần một nhánh xử lý thành công;
        // header cho biết đây là đơn cũ, không phải vừa tạo.
        var (orderId, replayed) = result.Value;
        if (replayed)
        {
            Response.Headers[IdempotentReplayedHeader] = "true";
        }

        return Created($"/api/v1/orders/{orderId}", new CreatedResponse(orderId));
    }

    [HttpGet("my")]
    [Authorize]
    [ProducesResponseType<PagedResult<OrderSummaryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListMyOrders(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10, CancellationToken cancellationToken = default) =>
        HandleResult(await sender.Send(new ListMyOrdersQuery(page, pageSize), cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize]
    [ProducesResponseType<OrderDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrderById(Guid id, CancellationToken cancellationToken) =>
        HandleResult(await sender.Send(new GetOrderByIdQuery(id), cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelOrder(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new CancelOrderCommand(id), cancellationToken);
        return HandleResult(result);
    }

    /// <summary>Mở lượt thanh toán cho đơn Pending. Bấm lại khi lượt trước còn chờ thì nhận lại lượt đó (200).</summary>
    [HttpPost("{id:guid}/pay")]
    [Authorize]
    [ProducesResponseType<PayOrderResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<PayOrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Pay(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new PayOrderCommand(id), cancellationToken);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var payment = result.Value;
        // Cổng giả lập: "trang thanh toán" là endpoint hoàn tất ở MockGatewayController. Cổng thật sẽ trả URL của nó.
        var response = new PayOrderResponse(
            payment.PaymentId, payment.Amount, $"/api/v1/mock-gateway/checkout/{payment.ProviderTransactionId}/complete");

        return payment.Created
            ? StatusCode(StatusCodes.Status201Created, response)
            : Ok(response);
    }

    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private const string IdempotentReplayedHeader = "Idempotent-Replayed";
}
