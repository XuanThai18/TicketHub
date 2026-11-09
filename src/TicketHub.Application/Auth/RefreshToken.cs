using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketHub.Application.Common.Exceptions;
using TicketHub.Domain.Auth;
using TicketHub.Domain.Shared;

namespace TicketHub.Application.Auth;

public sealed record RefreshTokenCommand(string RefreshToken, string? IpAddress, string? UserAgent)
    : IRequest<Result<AuthTokens>>;

public sealed class RefreshTokenCommandValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenCommandValidator() => RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(200);
}

public sealed partial class RefreshTokenCommandHandler(
    IUserRepository users,
    IUserSessionRepository sessions,
    IAuthUnitOfWork unitOfWork,
    ITokenService tokens,
    IOptions<AuthOptions> options,
    TimeProvider timeProvider,
    ILogger<RefreshTokenCommandHandler> logger) : IRequestHandler<RefreshTokenCommand, Result<AuthTokens>>
{
    public async Task<Result<AuthTokens>> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var settings = options.Value;

        var session = await sessions.GetByRefreshTokenHashAsync(tokens.HashRefreshToken(request.RefreshToken), cancellationToken);
        if (session is null)
            return AuthErrors.InvalidRefreshToken;

        if (session.IsReusedAfterRotation(now, settings.RefreshReuseGrace))
        {
            // Token đã đổi từ lâu mà vẫn bị dùng: có người khác đang giữ nó → cắt cả họ phiên.
            foreach (var active in await sessions.ListActiveByFamilyAsync(session.FamilyId, now, cancellationToken))
                active.Revoke(now, SessionRevokeReason.ReuseDetected);

            await unitOfWork.SaveChangesAsync(cancellationToken);
            LogReuseDetected(session.UserId, session.FamilyId);
            return AuthErrors.InvalidRefreshToken;
        }

        if (!session.IsActive(now))
            return AuthErrors.InvalidRefreshToken;

        var user = await users.GetByIdAsync(session.UserId, cancellationToken);
        if (user is not { IsActive: true })
        {
            session.Revoke(now, SessionRevokeReason.UserDeactivated);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return AuthErrors.InvalidRefreshToken;
        }

        var refreshToken = tokens.GenerateRefreshToken();
        var next = session.Rotate(
            tokens.HashRefreshToken(refreshToken), now, settings.RefreshTokenLifetime, request.IpAddress, request.UserAgent);
        sessions.Add(next);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // Request khác vừa đổi đúng phiên này (hai tab refresh cùng lúc): để request kia thắng.
            return AuthErrors.InvalidRefreshToken;
        }

        return AuthTokens.Issue(tokens, user, next, refreshToken);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Refresh token reuse detected for user {UserId}; revoked session family {FamilyId}")]
    private partial void LogReuseDetected(Guid userId, Guid familyId);
}
