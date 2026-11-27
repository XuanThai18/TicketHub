using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketHub.Infrastructure.Messaging;

namespace TicketHub.Infrastructure.Persistence.Configurations;

internal sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    public void Configure(EntityTypeBuilder<ProcessedMessage> builder)
    {
        builder.ToTable("processed_messages");

        // Khoá chính (message_id, consumer): tra "đã xử lý chưa" đi thẳng vào index, và DB chặn ghi trùng.
        builder.HasKey(m => new { m.MessageId, m.Consumer });
        builder.Property(m => m.Consumer).HasMaxLength(ProcessedMessage.ConsumerMaxLength);
        builder.Property(m => m.ProcessedAt).IsRequired();
    }
}
