using TicketHub.Domain.Auth;

namespace TicketHub.UnitTests.Auth;

public sealed class UserSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    private static UserSession NewSession() => UserSession.Start(Guid.NewGuid(), "hash-1", Now, Lifetime, "1.2.3.4", "test");

    [Fact]
    public void Start_IsActiveUntilExpiry()
    {
        var session = NewSession();

        Assert.True(session.IsActive(Now));
        Assert.False(session.IsActive(Now + Lifetime));
    }

    [Fact]
    public void Rotate_RevokesCurrentAndKeepsFamily()
    {
        var session = NewSession();

        var next = session.Rotate("hash-2", Now.AddHours(1), Lifetime, null, null);

        Assert.False(session.IsActive(Now.AddHours(1)));
        Assert.Equal(SessionRevokeReason.Rotated, session.RevokedReason);
        Assert.Equal(next.Id, session.ReplacedBySessionId);
        Assert.Equal(session.FamilyId, next.FamilyId);
        Assert.Equal(session.UserId, next.UserId);
        Assert.True(next.IsActive(Now.AddHours(1)));
    }

    [Fact]
    public void IsReusedAfterRotation_InsideGrace_IsNotTreatedAsTheft()
    {
        var session = NewSession();
        session.Rotate("hash-2", Now, Lifetime, null, null);

        Assert.False(session.IsReusedAfterRotation(Now + Grace, Grace));
        Assert.True(session.IsReusedAfterRotation(Now + Grace + TimeSpan.FromSeconds(1), Grace));
    }

    [Fact]
    public void IsReusedAfterRotation_LoggedOutSession_IsNotReuse()
    {
        var session = NewSession();
        session.Revoke(Now, SessionRevokeReason.Logout);

        Assert.False(session.IsReusedAfterRotation(Now.AddDays(1), Grace));
    }

    [Fact]
    public void Revoke_IsIdempotentAndKeepsFirstReason()
    {
        var session = NewSession();

        session.Revoke(Now, SessionRevokeReason.Logout);
        session.Revoke(Now.AddMinutes(5), SessionRevokeReason.LogoutAll);

        Assert.Equal(Now, session.RevokedAt);
        Assert.Equal(SessionRevokeReason.Logout, session.RevokedReason);
    }

    [Fact]
    public void Start_TruncatesOversizedUserAgent()
    {
        var session = UserSession.Start(Guid.NewGuid(), "h", Now, Lifetime, null, new string('x', 2000));

        Assert.Equal(UserSession.UserAgentMaxLength, session.UserAgent!.Length);
    }
}
