namespace TicketHub.Application.Common.Exceptions;

/// <summary>Infrastructure dịch DbUpdateConcurrencyException sang đây: bản ghi đã bị request khác sửa trước.</summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException()
    {
    }

    public ConcurrencyConflictException(string message) : base(message)
    {
    }

    public ConcurrencyConflictException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
