using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketHub.Domain.Outbox;

namespace TicketHub.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages", t =>
            t.HasCheckConstraint("ck_outbox_messages_attempts", "attempts >= 0"));
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Type).HasMaxLength(OutboxMessage.TypeMaxLength).IsRequired();
        builder.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(m => m.OccurredAt).IsRequired();
        builder.Property(m => m.Attempts).HasDefaultValue(0).IsRequired();
        builder.Property(m => m.LastError).HasMaxLength(OutboxMessage.LastErrorMaxLength);

        // Job gửi chỉ đọc tin chưa gửi, cũ trước. Index một phần: tin đã gửi (đa số) không nằm trong index nên index luôn nhỏ.
        builder.HasIndex(m => m.OccurredAt)
            .HasFilter("processed_at IS NULL")
            .HasDatabaseName("ix_outbox_messages_unprocessed");
    }
}
