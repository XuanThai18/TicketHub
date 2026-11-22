using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketHub.Domain.Orders;
using TicketHub.Domain.Tickets;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.Infrastructure.Persistence.Configurations;

internal sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("tickets", t =>
            t.HasCheckConstraint("ck_tickets_status", "status IN('Valid', 'Used', 'Cancelled')"));
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Code).HasMaxLength(Ticket.CodeMaxLength).IsRequired();
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(t => t.IssuedAt).IsRequired();

        // Soát vé tra theo mã; unique để hai vé không bao giờ trùng mã (lưới an toàn sau mã ngẫu nhiên).
        builder.HasIndex(t => t.Code).IsUnique();
        builder.HasIndex(t => t.OrderId);

        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(t => t.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<TicketType>()
            .WithMany()
            .HasForeignKey(t => t.TicketTypeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
