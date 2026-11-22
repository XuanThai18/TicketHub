namespace TicketHub.Api.Contracts.Orders;

public sealed record PlaceOrderRequest(IReadOnlyList<PlaceOrderLineRequest> Lines);

public sealed record PlaceOrderLineRequest(Guid TicketTypeId, int Quantity);

/// <summary>
/// Lượt thanh toán vừa mở (hoặc lượt đang chờ có sẵn). <c>CheckoutUrl</c> là link tới cổng thanh toán.
/// Cổng giả lập: gửi POST tới link này để hoàn tất (thêm <c>?outcome=failed</c> để giả lập thất bại).
/// </summary>
public sealed record PayOrderResponse(Guid PaymentId, decimal Amount, string CheckoutUrl);
