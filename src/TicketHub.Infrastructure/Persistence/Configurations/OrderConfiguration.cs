using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketHub.Domain.Events;
using TicketHub.Domain.Orders;

namespace TicketHub.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders", t =>
        {
            t.HasCheckConstraint("ck_orders_total_amount", "total_amount >= 0");
            t.HasCheckConstraint("ck_orders_code_length", $"length(code) >= {Order.CodeMinLength} AND length(code) <= {Order.CodeMaxLength}");
            t.HasCheckConstraint("ck_orders_status", "status IN('Pending', 'Paid', 'Expired', 'Cancelled')");

            t.HasCheckConstraint("ck_orders_paid_at", "(status = 'Paid') = (paid_at IS NOT NULL)");
        });
        builder.HasKey(o => o.Id);

        builder.Property(o => o.Code).HasMaxLength(Order.CodeMaxLength).IsRequired();
        builder.HasIndex(o => o.Code).IsUnique();

        builder.Property(o => o.UserId).IsRequired();
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(o => o.TotalAmount).HasPrecision(18, 2).IsRequired();
        builder.Property(o => o.ExpiresAt).IsRequired();
        builder.Property(o => o.PaidAt);
        builder.Property(o => o.IdempotencyKey).HasMaxLength(Order.IdempotencyKeyMaxLength);
        builder.Property(o => o.EventId).IsRequired();

        // index
        builder.HasIndex(o => new { o.Status, o.ExpiresAt }); // cho job tự huỷ đơn hết hạn
        builder.HasIndex(o => new { o.UserId, o.CreatedAt }); // cho trang danh sách đơn hàng của user, sắp theo ngày tạo
        builder.HasIndex(o => new { o.UserId, o.IdempotencyKey })
                .IsUnique()
                .HasFilter("idempotency_key IS NOT NULL");

        builder.HasMany(o => o.Items)
            .WithOne()
            .HasForeignKey(i => i.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Event>()
            .WithMany()
            .HasForeignKey(o => o.EventId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Navigation(o => o.Items).UsePropertyAccessMode(PropertyAccessMode.Field); 
    }
}
