using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TicketHub.Domain.Shared;

namespace TicketHub.Application.Payments;

/// <summary>
/// Chữ ký webhook: <c>sha256=hex(HMAC-SHA256(secret, "{timestamp}.{body}"))</c>, gửi trong header X-Signature,
/// timestamp (giây Unix) gửi trong X-Signature-Timestamp. Cùng kiểu với Stripe.
/// </summary>
public static class PaymentSignature
{
    public const string SignatureHeader = "X-Signature";
    public const string TimestampHeader = "X-Signature-Timestamp";
    private const string Prefix = "sha256=";

    public static string Compute(string secret, long timestamp, string body)
    {
        var signed = Encoding.UTF8.GetBytes($"{timestamp.ToString(CultureInfo.InvariantCulture)}.{body}");
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
        return Prefix + Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Ký trên nguyên văn body: chỉ cần đổi một ký tự (kể cả khoảng trắng) là chữ ký sai.
    /// Timestamp nằm trong phần được ký, nên không sửa được timestamp để "làm mới" một webhook cũ.
    /// </summary>
    public static Result Verify(
        string secret, string? timestampHeader, string? signatureHeader, string body, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrEmpty(signatureHeader)
            || !long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
        {
            return PaymentErrors.InvalidSignature;
        }

        var expected = Encoding.UTF8.GetBytes(Compute(secret, timestamp, body));
        var actual = Encoding.UTF8.GetBytes(signatureHeader);

        // So trong thời gian không đổi: so từng byte rồi dừng sớm thì kẻ tấn công đo thời gian là đoán dần được chữ ký.
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            return PaymentErrors.InvalidSignature;
        }

        // Kiểm sau chữ ký: chỉ webhook thật (đúng khoá) mới đáng báo "quá hạn".
        var sentAt = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        if ((now - sentAt).Duration() > tolerance)
        {
            return PaymentErrors.SignatureExpired;
        }

        return Result.Success();
    }
}
