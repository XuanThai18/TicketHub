using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketHub.Domain.OrderItems;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.Infrastructure.Persistence.Configurations;

internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("order_items", t =>
        {
            t.HasCheckConstraint("ck_order_items_quantity", "quantity > 0");
            t.HasCheckConstraint("ck_order_items_unit_price", "unit_price >= 0");
        });
        builder.HasKey(i => i.Id);
        builder.Property(i => i.OrderId).IsRequired();
        builder.Property(i => i.TicketTypeId).IsRequired();
        builder.Property(i => i.Quantity).IsRequired();
        builder.Property(i => i.UnitPrice).HasPrecision(18, 2).IsRequired();
        // index
        builder.HasIndex(i => new { i.OrderId, i.TicketTypeId }).IsUnique(); // 1 loại vé chỉ có 1 dòng trong đơn hàng

        builder.HasOne<TicketType>()
            .WithMany()
            .HasForeignKey(i => i.TicketTypeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
