using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Messaging;
using TicketHub.Domain.Orders;
using TicketHub.Domain.Outbox;
using TicketHub.Domain.Payments;
using TicketHub.Domain.Shared;
using TicketHub.Domain.Tickets;

namespace TicketHub.Application.Payments;

/// <param name="RawBody">Nguyên văn body webhook; chữ ký được tính trên chuỗi này nên không được parse lại rồi serialize.</param>
/// <param name="Timestamp">Header X-Signature-Timestamp.</param>
/// <param name="Signature">Header X-Signature.</param>
public sealed record HandlePaymentWebhookCommand(string RawBody, string? Timestamp, string? Signature) : IRequest<Result>;

public sealed class HandlePaymentWebhookCommandHandler(
    IOptions<PaymentOptions> options,
    IPaymentRepository paymentRepository,
    IOrderRepository orderRepository,
    ITicketRepository ticketRepository,
    IOutboxRepository outboxRepository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<HandlePaymentWebhookCommandHandler> logger) : IRequestHandler<HandlePaymentWebhookCommand, Result>
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<Result> Handle(HandlePaymentWebhookCommand request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // 1. Chỉ tin webhook ký đúng khoá chung với cổng. Endpoint không cần đăng nhập nên đây là lớp bảo vệ duy nhất.
        var verified = PaymentSignature.Verify(
            options.Value.WebhookSecret, request.Timestamp, request.Signature, request.RawBody,
            now, TimeSpan.FromSeconds(options.Value.SignatureToleranceSeconds));
        if (verified.IsFailure)
        {
            return verified.Error;
        }

        // 2. Đọc nội dung.
        if (ParsePayload(request.RawBody) is not { } payload)
        {
            return PaymentErrors.InvalidPayload;
        }

        var payment = await paymentRepository.GetByProviderTransactionIdAsync(payload.TransactionId, cancellationToken);
        if (payment is null)
        {
            return PaymentErrors.UnknownTransaction(payload.TransactionId);
        }

        var result = payload.Status == PaymentWebhookPayload.Succeeded ? PaymentStatus.Succeeded : PaymentStatus.Failed;

        // 3. Ba UPDATE có điều kiện trong một transaction.
        return await unitOfWork.ExecuteInTransactionAsync<Result>(async ct =>
        {
            // a) Chốt lượt thanh toán. Cổng gọi lặp (kể cả cùng lúc) thì chỉ một lời gọi đổi được Pending;
            //    các lời gọi sau nhận false và trả thành công luôn, để cổng ngừng gửi lại.
            if (!await paymentRepository.TryCompleteAsync(payment.Id, result, request.RawBody, now, ct))
            {
                return Result.Success();
            }

            if (result != PaymentStatus.Succeeded)
            {
                return Result.Success(); // trả thất bại: đơn vẫn Pending, người dùng có thể thanh toán lại
            }

            // b) Đơn Pending → Paid. Đua với job hết hạn (và với huỷ đơn): ai UPDATE trước thì thắng.
            if (await orderRepository.TryMarkPaidAsync(payment.OrderId, now, ct))
            {
                // Sinh vé và xếp tin "gửi email vé" vào outbox ngay trong transaction này:
                // commit thì có đủ đơn Paid + vé + tin, lỗi thì rollback cả ba.
                // Chỉ chạy khi chính lời gọi này vừa đổi được đơn sang Paid, nên webhook gửi lặp không sinh vé lần hai.
                var order = await orderRepository.GetByIdAsync(payment.OrderId, ct)
                    ?? throw new InvalidOperationException($"Order {payment.OrderId} was just marked Paid but could not be loaded.");
                IssueTickets(order, now);
                outboxRepository.Add(new OrderPaidMessage(order.Id, order.UserId).ToOutbox(now));
                await unitOfWork.SaveChangesAsync(ct);
                return Result.Success();
            }

            // c) Tiền đã về nhưng đơn không còn Pending: vé đã trả kho, không xuất vé được → phải hoàn tiền.
            await paymentRepository.MarkRefundRequiredAsync(payment.Id, ct);
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(
                    "Payment {PaymentId} succeeded but order {OrderId} is no longer pending; marked RefundRequired.",
                    payment.Id, payment.OrderId);
            }
            return Result.Success();
        }, cancellationToken: cancellationToken);
    }

    // Mỗi chỗ trong đơn một vé: đơn 2 VIP + 1 Thường → 3 vé, mỗi vé mã riêng.
    private void IssueTickets(Order order, DateTimeOffset now)
    {
        foreach (var item in order.Items)
        {
            for (var i = 0; i < item.Quantity; i++)
            {
                ticketRepository.Add(Ticket.Issue(order.Id, item.TicketTypeId, now));
            }
        }
    }

    private static PaymentWebhookPayload? ParsePayload(string body)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<PaymentWebhookPayload>(body, _json);
            var validStatus = payload?.Status is PaymentWebhookPayload.Succeeded or PaymentWebhookPayload.Failed;
            return validStatus && !string.IsNullOrWhiteSpace(payload!.TransactionId) ? payload : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
