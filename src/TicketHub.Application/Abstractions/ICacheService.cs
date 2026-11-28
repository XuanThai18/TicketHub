namespace TicketHub.Application.Abstractions;

/// <summary>
/// Cache dùng chung giữa các instance (Redis). Cache chỉ là đường tắt: Redis lỗi thì vẫn đọc DB, request không hỏng.
/// </summary>
public interface ICacheService
{
    /// <summary>
    /// Có trong cache thì trả bản cache; chưa có thì gọi <paramref name="factory"/> (đọc DB) rồi lưu lại.
    /// Kết quả null (vd không tìm thấy) không được lưu, để lần sau vẫn đọc lại DB.
    /// </summary>
    Task<T?> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T?>> factory, TimeSpan expiration,
        CancellationToken cancellationToken);

    /// <summary>Xoá một khoá, dùng khi dữ liệu gốc vừa đổi.</summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken);
}
