using System.Globalization;
using TicketHub.Application.Payments;
using TicketHub.Domain.Shared;

namespace TicketHub.UnitTests.Application;

public sealed class PaymentSignatureTests
{
    private const string Secret = "unit-test-webhook-secret-0123456789abcdef";
    private const string Body = """{"transactionId":"mock_1","status":"succeeded","occurredAt":"2026-12-01T00:00:00+00:00"}""";
    private static readonly DateTimeOffset _now = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _tolerance = TimeSpan.FromMinutes(5);
    private static readonly long _ts = _now.ToUnixTimeSeconds();

    private static Result Verify(string? timestamp, string? signature, string body = Body, string secret = Secret) =>
        PaymentSignature.Verify(secret, timestamp, signature, body, _now, _tolerance);

    [Fact]
    public void Verify_CorrectSignature_Succeeds()
    {
        var signature = PaymentSignature.Compute(Secret, _ts, Body);

        Assert.True(Verify(_ts.ToString(CultureInfo.InvariantCulture), signature).IsSuccess);
        Assert.StartsWith("sha256=", signature, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_BodyChangedByOneCharacter_Fails()
    {
        // Kẻ đứng giữa sửa "succeeded" của giao dịch khác thành của mình, hay chỉ thêm một khoảng trắng: chữ ký đều sai.
        var signature = PaymentSignature.Compute(Secret, _ts, Body);

        Assert.Equal(PaymentErrors.InvalidSignature, Verify(_ts.ToString(CultureInfo.InvariantCulture), signature, body: Body + " ").Error);
    }

    [Fact]
    public void Verify_SignedWithAnotherSecret_Fails()
    {
        var signature = PaymentSignature.Compute("someone-elses-secret-0123456789abcdef", _ts, Body);

        Assert.Equal(PaymentErrors.InvalidSignature, Verify(_ts.ToString(CultureInfo.InvariantCulture), signature).Error);
    }

    [Fact]
    public void Verify_TimestampChangedToRefreshOldWebhook_Fails()
    {
        // Timestamp nằm trong phần được ký: đổi timestamp để "làm mới" webhook cũ thì chữ ký không khớp nữa.
        var oldTs = _now.AddHours(-1).ToUnixTimeSeconds();
        var oldSignature = PaymentSignature.Compute(Secret, oldTs, Body);

        Assert.Equal(PaymentErrors.InvalidSignature, Verify(_ts.ToString(CultureInfo.InvariantCulture), oldSignature).Error);
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(6)]
    public void Verify_TimestampOutsideTolerance_ReturnsExpired(int minutes)
    {
        var ts = _now.AddMinutes(minutes).ToUnixTimeSeconds();
        var signature = PaymentSignature.Compute(Secret, ts, Body);

        Assert.Equal(PaymentErrors.SignatureExpired, Verify(ts.ToString(CultureInfo.InvariantCulture), signature).Error);
    }

    [Theory]
    [InlineData(null, "sha256=abc")]
    [InlineData("not-a-number", "sha256=abc")]
    [InlineData("-5", "sha256=abc")]
    [InlineData("1764547200", null)]
    [InlineData("1764547200", "")]
    public void Verify_MissingOrMalformedHeaders_Fails(string? timestamp, string? signature)
    {
        Assert.Equal(PaymentErrors.InvalidSignature, Verify(timestamp, signature).Error);
    }
}
