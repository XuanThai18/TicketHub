namespace TicketHub.Application.Common.Exceptions;

/// <summary>
/// Infrastructure dịch lỗi unique của DB sang exception này để handler trả Conflict
/// mà không phụ thuộc EF Core/Npgsql (trường hợp hai request cùng lúc vượt qua bước kiểm trùng).
/// </summary>
public sealed class UniqueConstraintException : Exception
{
    public UniqueConstraintException()
    {
    }

    public UniqueConstraintException(string message) : base(message)
    {
    }

    public UniqueConstraintException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public string? ConstraintName { get; init; }
}
