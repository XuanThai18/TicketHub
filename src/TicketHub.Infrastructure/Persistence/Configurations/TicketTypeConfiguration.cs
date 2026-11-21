using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketHub.Domain.Events;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.Infrastructure.Persistence.Configurations;

internal sealed class TicketTypeConfiguration : IEntityTypeConfiguration<TicketType>
{
    public void Configure(EntityTypeBuilder<TicketType> builder)
    {
        builder.ToTable("ticket_types", table =>
        {
            table.HasCheckConstraint(
                "ck_ticket_types_price",
                "price >= 0");
            table.HasCheckConstraint(
                "ck_ticket_types_total_quantity",
                "total_quantity > 0 AND available_quantity >= 0 AND available_quantity <= total_quantity");
            table.HasCheckConstraint(
                "ck_ticket_types_max_quantity_per_order",
                "max_quantity_per_order > 0 AND max_quantity_per_order <= total_quantity");
        });

        builder.Property(tt => tt.Name).HasMaxLength(TicketType.NameMaxLength).IsRequired().UseCollation("case_insensitive");
        builder.Property(tt => tt.Description).HasMaxLength(TicketType.DescriptionMaxLength);
        builder.Property(tt => tt.Price).HasPrecision(18, 2);

        // Cột hệ thống xmin làm concurrency token. Mọi câu ghi vào dòng (kể cả UPDATE nguyên tử của đặt/huỷ vé)
        // đều đổi xmin, nên admin lưu bản đã đọc trước đó sẽ nhận xung đột thay vì ghi đè số vé.
        builder.Property<uint>("Version").IsRowVersion();

        builder.HasOne<Event>()
                .WithMany()
                .HasForeignKey(tt => tt.EventId)
                .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(tt => new { tt.EventId, tt.Name })
                .IsUnique()
                .HasFilter("is_deleted = false");
    }
}
