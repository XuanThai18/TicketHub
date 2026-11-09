using MediatR;
using TicketHub.Domain.Auth;
using TicketHub.Domain.Shared;

namespace TicketHub.Application.Auth;

/// <summary>Thu hồi phiên hiện tại. Idempotent: gọi lại hay phiên không tồn tại vẫn trả thành công.</summary>
public sealed record LogoutCommand(Guid UserId, Guid SessionId) : IRequest<Result>;

public sealed class LogoutCommandHandler(
    IUserSessionRepository sessions, IAuthUnitOfWork unitOfWork, TimeProvider timeProvider)
    : IRequestHandler<LogoutCommand, Result>
{
    public async Task<Result> Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        var session = await sessions.GetByIdAsync(request.SessionId, cancellationToken);
        if (session is not null && session.UserId == request.UserId)
        {
            session.Revoke(timeProvider.GetUtcNow(), SessionRevokeReason.Logout);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }
}

/// <summary>Đăng xuất khỏi mọi thiết bị.</summary>
public sealed record LogoutAllCommand(Guid UserId) : IRequest<Result>;

public sealed class LogoutAllCommandHandler(
    IUserSessionRepository sessions, IAuthUnitOfWork unitOfWork, TimeProvider timeProvider)
    : IRequestHandler<LogoutAllCommand, Result>
{
    public async Task<Result> Handle(LogoutAllCommand request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var session in await sessions.ListActiveByUserAsync(request.UserId, now, cancellationToken))
            session.Revoke(now, SessionRevokeReason.LogoutAll);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
