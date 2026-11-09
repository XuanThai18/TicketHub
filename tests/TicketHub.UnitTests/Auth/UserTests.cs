using TicketHub.Domain.Auth;
using TicketHub.Domain.Common;

namespace TicketHub.UnitTests.Auth;

public sealed class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

    private static User NewUser() => User.Create(" Alice@Example.COM ", "Alice", "hash", [Roles.User]).Value;

    [Fact]
    public void Create_NormalizesEmailAndIsActive()
    {
        var user = NewUser();

        Assert.Equal("alice@example.com", user.Email);
        Assert.True(user.IsActive);
        Assert.Equal([Roles.User], user.Roles.Select(r => r.Role));
    }

    [Fact]
    public void Create_UnknownRole_Fails() =>
        Assert.True(User.Create("a@b.com", "A", "hash", ["SuperUser"]).IsFailure);

    [Fact]
    public void RegisterFailedLogin_ReachingMax_LocksAccount()
    {
        var user = NewUser();

        for (var i = 0; i < 4; i++)
            user.RegisterFailedLogin(Now, maxAttempts: 5, Lockout);
        Assert.False(user.IsLockedOut(Now));

        user.RegisterFailedLogin(Now, maxAttempts: 5, Lockout);
        Assert.True(user.IsLockedOut(Now));
        Assert.False(user.IsLockedOut(Now + Lockout)); // tự mở khi hết hạn
    }

    [Fact]
    public void RegisterFailedLogin_AfterLockExpired_StartsCountingAgain()
    {
        var user = NewUser();
        for (var i = 0; i < 5; i++)
            user.RegisterFailedLogin(Now, 5, Lockout);

        user.RegisterFailedLogin(Now + Lockout + TimeSpan.FromSeconds(1), 5, Lockout);

        Assert.Equal(1, user.FailedLoginCount);
        Assert.False(user.IsLockedOut(Now + Lockout + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void RegisterSuccessfulLogin_ResetsFailures()
    {
        var user = NewUser();
        user.RegisterFailedLogin(Now, 5, Lockout);

        user.RegisterSuccessfulLogin(Now);

        Assert.Equal(0, user.FailedLoginCount);
        Assert.Equal(Now, user.LastLoginAt);
    }

    [Fact]
    public void ChangePassword_RotatesSecurityStamp()
    {
        var user = NewUser();
        var stamp = user.SecurityStamp;

        user.ChangePassword("new-hash");

        Assert.Equal("new-hash", user.PasswordHash);
        Assert.NotEqual(stamp, user.SecurityStamp);
    }

    [Fact]
    public void UpgradePasswordHash_KeepsSecurityStamp()
    {
        var user = NewUser();
        var stamp = user.SecurityStamp;

        user.UpgradePasswordHash("rehashed");

        Assert.Equal(stamp, user.SecurityStamp);
    }
}
