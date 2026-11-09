using System.Data;

namespace TicketHub.Application.Abstractions;

public interface IUnitOfWork
{
    /// <summary>Lưu mọi thay đổi đang được track trong MỘT transaction (EF tự mở). Đủ cho phần lớn use case.</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Chạy <paramref name="work"/> trong một transaction tường minh. Chỉ cần khi gọi SaveChanges nhiều lần,
    /// trộn EF với ExecuteUpdate/SQL thô, hoặc cần isolation level khác mặc định.
    /// <list type="bullet">
    /// <item>Commit khi <paramref name="work"/> hoàn tất; rollback khi ném exception HOẶC trả <c>Result</c> thất bại.</item>
    /// <item>Gọi lồng: lời gọi trong nhập chung transaction ngoài, chỉ lớp ngoài cùng commit/rollback.</item>
    /// <item>Lỗi kết nối tạm thời: chạy lại toàn bộ <paramref name="work"/> với Change Tracker đã xoá,
    /// nên entity phải được tạo/nạp BÊN TRONG delegate, không mang từ ngoài vào.</item>
    /// </list>
    /// </summary>
    Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> work,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ExecuteInTransactionAsync{TResult}"/>
    Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> work,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default);
}
