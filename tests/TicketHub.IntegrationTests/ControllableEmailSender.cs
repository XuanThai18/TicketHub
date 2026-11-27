using System.Collections.Concurrent;
using TicketHub.Application.Abstractions;

namespace TicketHub.IntegrationTests;

/// <summary>
/// Bọc bộ gửi SMTP thật; <see cref="FailNext"/> làm N lần gửi tiếp theo tới một địa chỉ bị lỗi,
/// giả lập máy chủ thư chập chờn. Theo từng địa chỉ để test khác (người mua khác) không bị ảnh hưởng.
/// </summary>
public sealed class ControllableEmailSender(IEmailSender inner) : IEmailSender
{
    private readonly ConcurrentDictionary<string, int> _failuresLeft = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public void FailNext(string toAddress, int times) => _failuresLeft[toAddress] = times;

    /// <summary>Số lần consumer đã thử gửi tới địa chỉ này, kể cả lần lỗi.</summary>
    public int Attempts(string toAddress) => _attempts.GetValueOrDefault(toAddress);

    public Task SendAsync(EmailMessage email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        _attempts.AddOrUpdate(email.ToAddress, 1, (_, count) => count + 1);

        if (_failuresLeft.TryGetValue(email.ToAddress, out var left) && left > 0)
        {
            _failuresLeft[email.ToAddress] = left - 1;
            throw new IOException("SMTP server unavailable (simulated).");
        }

        return inner.SendAsync(email, cancellationToken);
    }
}
