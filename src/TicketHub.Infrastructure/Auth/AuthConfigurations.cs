using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketHub.Domain.Auth;

namespace TicketHub.Infrastructure.Auth;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);

        // Email đã chuẩn hoá chữ thường trong domain → index unique thường là đủ.
        builder.Property(u => u.Email).HasMaxLength(User.EmailMaxLength).IsRequired();
        builder.HasIndex(u => u.Email).IsUnique();

        builder.Property(u => u.FullName).HasMaxLength(User.FullNameMaxLength).IsRequired();
        builder.Property(u => u.PasswordHash).HasMaxLength(512).IsRequired();
        builder.Property(u => u.SecurityStamp).HasMaxLength(64).IsRequired();

        builder.HasMany(u => u.Roles).WithOne().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(u => u.Roles).HasField("_roles").UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
    }
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("user_roles");
        builder.HasKey(r => new { r.UserId, r.Role });
        builder.Property(r => r.Role).HasMaxLength(50);
    }
}

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("user_sessions");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.RefreshTokenHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(s => s.RefreshTokenHash).IsUnique();
        builder.HasIndex(s => s.UserId);
        builder.HasIndex(s => s.FamilyId);

        builder.Property(s => s.RevokedReason).HasConversion<string>().HasMaxLength(32);
        builder.Property(s => s.IpAddress).HasMaxLength(UserSession.IpMaxLength);
        builder.Property(s => s.UserAgent).HasMaxLength(UserSession.UserAgentMaxLength);

        builder.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);

        // Cột hệ thống xmin của Postgres làm concurrency token: hai request cùng rotate một phiên
        // thì request lưu sau nhận DbUpdateConcurrencyException thay vì sinh ra hai phiên mới.
        builder.Property<uint>("Version").IsRowVersion();
    }
}
