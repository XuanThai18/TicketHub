using System.Globalization;
using System.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Payments;
using TicketHub.Domain.Orders;
using TicketHub.Domain.Payments;
using TicketHub.Domain.Shared;
using TicketHub.Domain.Tickets;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.UnitTests.Application;

public sealed class HandlePaymentWebhookTests
{
    private const string Secret = "unit-test-webhook-secret-0123456789abcdef";
    private static readonly DateTimeOffset _now = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IPaymentRepository _payments = Substitute.For<IPaymentRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ITicketRepository _tickets = Substitute.For<ITicketRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly TicketType _vip = TicketType.Create(Guid.NewGuid(), "VIP", null, 500_000m, totalQuantity: 10, maxQuantityPerOrder: 4).Value;
    private readonly TicketType _normal;
    private readonly Order _order;
    private readonly Payment _payment;

    public HandlePaymentWebhookTests()
    {
        // Đơn 2 VIP + 1 Thường → khi trả xong phải phát đúng 3 vé.
        _normal = TicketType.Create(_vip.EventId, "Thường", null, 200_000m, totalQuantity: 10, maxQuantityPerOrder: 4).Value;
        _order = Order.Create(Guid.NewGuid(), [(_vip, 2), (_normal, 1)], _now).Value;
        _payment = Payment.CreateMock(_order.Id, _order.TotalAmount);
        _orders.GetByIdAsync(_order.Id, Arg.Any<CancellationToken>()).Returns(_order);

        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<Result>>>(), Arg.Any<IsolationLevel>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Func<CancellationToken, Task<Result>>>(0)(CancellationToken.None));

        _payments.GetByProviderTransactionIdAsync(_payment.ProviderTransactionId, Arg.Any<CancellationToken>()).Returns(_payment);
        _payments.TryCompleteAsync(Arg.Any<Guid>(), Arg.Any<PaymentStatus>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _orders.TryMarkPaidAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private HandlePaymentWebhookCommandHandler Handler() => new(
        Options.Create(new PaymentOptions { WebhookSecret = Secret }),
        _payments, _orders, _tickets, _unitOfWork, new FixedTimeProvider(_now),
        NullLogger<HandlePaymentWebhookCommandHandler>.Instance);

    // Webhook do "cổng" ký đúng khoá.
    private static HandlePaymentWebhookCommand Signed(string body)
    {
        var ts = _now.ToUnixTimeSeconds();
        return new HandlePaymentWebhookCommand(body, ts.ToString(CultureInfo.InvariantCulture), PaymentSignature.Compute(Secret, ts, body));
    }

    private string Body(string status, string? transactionId = null) =>
        $$"""{"transactionId":"{{transactionId ?? _payment.ProviderTransactionId}}","status":"{{status}}","occurredAt":"2026-12-01T00:00:00+00:00"}""";

    [Fact]
    public async Task Handle_Succeeded_CompletesPaymentAndMarksOrderPaid()
    {
        var result = await Handler().Handle(Signed(Body("succeeded")), CancellationToken.None);

        Assert.True(result.IsSuccess);
        await _payments.Received(1).TryCompleteAsync(_payment.Id, PaymentStatus.Succeeded, Arg.Any<string>(), _now, Arg.Any<CancellationToken>());
        await _orders.Received(1).TryMarkPaidAsync(_payment.OrderId, _now, Arg.Any<CancellationToken>());
        await _payments.DidNotReceive().MarkRefundRequiredAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Succeeded_IssuesOneTicketPerSeatAndSaves()
    {
        var issued = new List<Ticket>();
        _tickets.When(t => t.Add(Arg.Any<Ticket>())).Do(ci => issued.Add(ci.Arg<Ticket>()));

        await Handler().Handle(Signed(Body("succeeded")), CancellationToken.None);

        Assert.Equal(2, issued.Count(t => t.TicketTypeId == _vip.Id));
        Assert.Equal(1, issued.Count(t => t.TicketTypeId == _normal.Id));
        Assert.All(issued, t => Assert.Equal((_order.Id, TicketStatus.Valid, _now), (t.OrderId, t.Status, t.IssuedAt)));
        Assert.Equal(3, issued.Select(t => t.Code).Distinct().Count());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AlreadyProcessed_ReturnsSuccessAndTouchesNothingElse()
    {
        // Cổng gửi lặp: lượt thanh toán không còn Pending → coi như đã xử lý, không đụng tới đơn.
        _payments.TryCompleteAsync(Arg.Any<Guid>(), Arg.Any<PaymentStatus>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await Handler().Handle(Signed(Body("succeeded")), CancellationToken.None);

        Assert.True(result.IsSuccess);
        await _orders.DidNotReceive().TryMarkPaidAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _payments.DidNotReceive().MarkRefundRequiredAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _tickets.DidNotReceive().Add(Arg.Any<Ticket>()); // gửi lặp không được phát vé lần hai
    }

    [Fact]
    public async Task Handle_OrderNoLongerPending_MarksRefundRequired()
    {
        // Job hết hạn đã thắng: vé đã về kho, tiền về muộn → phải hoàn tiền.
        _orders.TryMarkPaidAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await Handler().Handle(Signed(Body("succeeded")), CancellationToken.None);

        Assert.True(result.IsSuccess);
        await _payments.Received(1).MarkRefundRequiredAsync(_payment.Id, Arg.Any<CancellationToken>());
        _tickets.DidNotReceive().Add(Arg.Any<Ticket>()); // vé đã về kho: không phát vé cho đơn hết hạn
    }

    [Fact]
    public async Task Handle_Failed_RecordsFailureAndLeavesOrderPending()
    {
        var result = await Handler().Handle(Signed(Body("failed")), CancellationToken.None);

        Assert.True(result.IsSuccess);
        await _payments.Received(1).TryCompleteAsync(_payment.Id, PaymentStatus.Failed, Arg.Any<string>(), _now, Arg.Any<CancellationToken>());
        await _orders.DidNotReceive().TryMarkPaidAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_BadSignature_RejectsBeforeTouchingDatabase()
    {
        var body = Body("succeeded");
        var command = new HandlePaymentWebhookCommand(body, _now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), "sha256=deadbeef");

        var result = await Handler().Handle(command, CancellationToken.None);

        Assert.Equal(PaymentErrors.InvalidSignature, result.Error);
        await _payments.DidNotReceive().GetByProviderTransactionIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"transactionId":"mock_1","status":"refunded","occurredAt":"2026-12-01T00:00:00+00:00"}""")]
    [InlineData("""{"status":"succeeded","occurredAt":"2026-12-01T00:00:00+00:00"}""")]
    public async Task Handle_SignedButInvalidPayload_ReturnsInvalidPayload(string body)
    {
        var result = await Handler().Handle(Signed(body), CancellationToken.None);

        Assert.Equal(PaymentErrors.InvalidPayload, result.Error);
    }

    [Fact]
    public async Task Handle_UnknownTransaction_ReturnsNotFound()
    {
        var result = await Handler().Handle(Signed(Body("succeeded", transactionId: "mock_unknown")), CancellationToken.None);

        Assert.Equal(PaymentErrors.UnknownTransaction("mock_unknown"), result.Error);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
