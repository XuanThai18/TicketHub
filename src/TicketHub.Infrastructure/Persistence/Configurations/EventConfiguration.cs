using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketHub.Domain.Events;

namespace TicketHub.Infrastructure.Persistence.Configurations;

internal sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("events", table => table.HasCheckConstraint(
            "ck_events_schedule",
            "sale_starts_at < sale_ends_at AND sale_ends_at <= starts_at AND starts_at < ends_at"));

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Name).HasMaxLength(Event.NameMaxLength).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(Event.DescriptionMaxLength);
        builder.Property(e => e.Venue).HasMaxLength(Event.VenueMaxLength).IsRequired();

        // Lưu enum dạng chữ ("Draft") thay vì số (1): đọc DB dễ, thêm giá trị mới không lệch số.
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);

        // Trang danh sách lọc theo trạng thái rồi sắp theo ngày diễn ra.
        builder.HasIndex(e => new { e.Status, e.StartsAt });
    }
}
