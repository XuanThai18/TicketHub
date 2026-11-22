using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketHub.Application.Payments;
using TicketHub.Domain.Orders;
using TicketHub.Domain.Payments;

namespace TicketHub.Infrastructure.Persistence.Configurations;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments", t =>
        {
            t.HasCheckConstraint("ck_payments_amount", "amount >= 0");
            t.HasCheckConstraint("ck_payments_status", "status IN('Pending', 'Succeeded', 'Failed', 'RefundRequired')");
            // Còn chờ thì chưa có kết quả; đã có kết quả thì phải biết lúc nào.
            t.HasCheckConstraint("ck_payments_completed_at", "(status = 'Pending') = (completed_at IS NULL)");
        });
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Provider).HasMaxLength(Payment.ProviderMaxLength).IsRequired();
        builder.Property(p => p.ProviderTransactionId).HasMaxLength(Payment.ProviderTransactionIdMaxLength).IsRequired();
        builder.Property(p => p.Amount).HasPrecision(18, 2).IsRequired();
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.RawPayload).HasColumnType("jsonb");

        // Webhook tìm lượt thanh toán theo mã này; unique để một giao dịch của cổng chỉ ứng với một dòng.
        builder.HasIndex(p => p.ProviderTransactionId).IsUnique();

        // Mỗi đơn có tối đa một lượt Pending: bấm "Thanh toán" hai lần cùng lúc thì lần sau vấp index này.
        builder.HasIndex(p => p.OrderId)
            .IsUnique()
            .HasFilter("status = 'Pending'")
            .HasDatabaseName(PayOrderCommandHandler.PendingPaymentIndex);

        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(p => p.OrderId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
