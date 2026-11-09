using FluentValidation;
using MediatR;
using Microsoft.Extensions.Options;
using TicketHub.Domain.Auth;
using TicketHub.Domain.Shared;

namespace TicketHub.Application.Auth;

public sealed record LoginCommand(string Email, string Password, string? IpAddress, string? UserAgent)
    : IRequest<Result<AuthTokens>>;

public sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(User.EmailMaxLength);
        // Không áp luật độ mạnh khi đăng nhập: chỉ chặn chuỗi quá dài làm chậm hàm băm.
        RuleFor(x => x.Password).NotEmpty().MaximumLength(AuthValidationRules.PasswordMaxLength);
    }
}

public sealed class LoginCommandHandler(
    IUserRepository users,
    IUserSessionRepository sessions,
    IAuthUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    ITokenService tokens,
    IOptions<AuthOptions> options,
    TimeProvider timeProvider) : IRequestHandler<LoginCommand, Result<AuthTokens>>
{
    public async Task<Result<AuthTokens>> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var settings = options.Value;

        var user = await users.GetByEmailAsync(User.NormalizeEmail(request.Email), cancellationToken);
        if (user is null)
        {
            passwordHasher.SimulateVerification(request.Password);
            return AuthErrors.InvalidCredentials;
        }

        // Kiểm khoá TRƯỚC khi kiểm mật khẩu: đang khoá thì đoán đúng cũng không vào được.
        if (user.IsLockedOut(now))
            return AuthErrors.AccountLocked;

        var check = passwordHasher.Verify(user.PasswordHash, request.Password);
        if (check == PasswordCheck.Failed)
        {
            user.RegisterFailedLogin(now, settings.MaxFailedLoginAttempts, settings.LockoutDuration);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return user.IsLockedOut(now) ? AuthErrors.AccountLocked : AuthErrors.InvalidCredentials;
        }

        // Chỉ báo "bị vô hiệu" sau khi đúng mật khẩu, tránh lộ trạng thái tài khoản cho người đoán mò.
        if (!user.IsActive)
            return AuthErrors.AccountDisabled;

        if (check == PasswordCheck.SuccessRehashNeeded)
            user.UpgradePasswordHash(passwordHasher.Hash(request.Password));

        user.RegisterSuccessfulLogin(now);

        var refreshToken = tokens.GenerateRefreshToken();
        var session = UserSession.Start(
            user.Id, tokens.HashRefreshToken(refreshToken), now, settings.RefreshTokenLifetime, request.IpAddress, request.UserAgent);
        sessions.Add(session);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return AuthTokens.Issue(tokens, user, session, refreshToken);
    }
}
