using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using TicketHub.Application.Abstractions;
using TicketHub.Domain.Shared;

namespace TicketHub.Infrastructure.Persistence;

/// <summary>Một cài đặt dùng cho mọi DbContext; mỗi module đăng ký interface riêng trỏ tới context của nó.</summary>
internal sealed partial class UnitOfWork<TContext>(TContext dbContext, ILogger<UnitOfWork<TContext>> logger) : IUnitOfWork
    where TContext : DbContext
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);

    public async Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> work,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await ExecuteInTransactionAsync<bool>(
            async ct =>
            {
                await work(ct);
                return true;
            },
            isolationLevel,
            cancellationToken);
    }

    public async Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> work,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        // Gọi lồng: chạy trong transaction đang mở, để lớp ngoài cùng quyết định commit/rollback.
        if (dbContext.Database.CurrentTransaction is not null)
            return await work(cancellationToken);

        // Bắt buộc khi bật EnableRetryOnFailure: transaction tự mở phải nằm trong execution strategy.
        var strategy = dbContext.Database.CreateExecutionStrategy();
        var attempt = 0;

        return await strategy.ExecuteAsync(
            async ct =>
            {
                if (attempt++ > 0)
                {
                    // Lần chạy trước đã rollback nhưng entity vẫn nằm trong tracker → xoá để không ghi trùng.
                    dbContext.ChangeTracker.Clear();
                    LogRetry(attempt);
                }

                await using var transaction = await dbContext.Database.BeginTransactionAsync(isolationLevel, ct);
                LogBegin(transaction.TransactionId, isolationLevel);

                TResult result;
                try
                {
                    result = await work(ct);
                }
                catch (Exception ex)
                {
                    // Không commit → DisposeAsync tự rollback. Exception đi lên nguyên kiểu.
                    dbContext.ChangeTracker.Clear();
                    LogRollbackOnException(ex, transaction.TransactionId);
                    throw;
                }

                if (result is Result { IsFailure: true } failed)
                {
                    await RollbackAsync(transaction);
                    LogRollbackOnFailure(transaction.TransactionId, failed.Error.Code);
                    return result;
                }

                await transaction.CommitAsync(ct);
                LogCommit(transaction.TransactionId);
                return result;
            },
            cancellationToken);
    }

    private async Task RollbackAsync(IDbContextTransaction transaction)
    {
        // CancellationToken.None: request bị huỷ giữa chừng vẫn phải rollback cho xong.
        await transaction.RollbackAsync(CancellationToken.None);
        dbContext.ChangeTracker.Clear();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Begin transaction {TransactionId} ({IsolationLevel})")]
    private partial void LogBegin(Guid transactionId, IsolationLevel isolationLevel);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Commit transaction {TransactionId}")]
    private partial void LogCommit(Guid transactionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rollback transaction {TransactionId}: business failure {ErrorCode}")]
    private partial void LogRollbackOnFailure(Guid transactionId, string errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rollback transaction {TransactionId} because of an exception")]
    private partial void LogRollbackOnException(Exception exception, Guid transactionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retrying transaction, attempt {Attempt}")]
    private partial void LogRetry(int attempt);
}
