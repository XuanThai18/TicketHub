namespace TicketHub.Application.Abstractions;

/// <summary>
/// Cửa đọc cho query handler: trả IQueryable KHÔNG tracking, đã áp query filter (soft-delete).
/// Luôn kết thúc bằng Select(...) sang DTO để chỉ lấy cột cần. Không dùng để ghi — ghi đi qua repository + IUnitOfWork.
/// </summary>
public interface IReadDbContext
{
    IQueryable<TEntity> Query<TEntity>() where TEntity : class;
}
