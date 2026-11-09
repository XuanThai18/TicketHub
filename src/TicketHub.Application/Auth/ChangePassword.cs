using FluentValidation;
using MediatR;
using TicketHub.Domain.Auth;
using TicketHub.Domain.Shared;

namespace TicketHub.Application.Auth;

public sealed record ChangePasswordCommand(Guid UserId, Guid CurrentSessionId, string CurrentPassword, string NewPassword)
    : IRequest<Result>;

public sealed class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordCommandValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(AuthValidationRules.PasswordMaxLength);
        RuleFor(x => x.NewPassword).StrongPassword()
            .NotEqual(x => x.CurrentPassword).WithMessage("New password must differ from the current one.");
    }
}

/// <summary>Đổi mật khẩu rồi thu hồi mọi phiên KHÁC: thiết bị lạ đang giữ phiên cũ bị đăng xuất ngay.</summary>
public sealed class ChangePasswordCommandHandler(
    IUserRepository users,
    IUserSessionRepository sessions,
    IAuthUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    TimeProvider timeProvider) : IRequestHandler<ChangePasswordCommand, Result>
{
    public async Task<Result> Handle(ChangePasswordCommand request, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return AuthErrors.UserNotFound(request.UserId);

        if (passwordHasher.Verify(user.PasswordHash, request.CurrentPassword) == PasswordCheck.Failed)
            return AuthErrors.WrongCurrentPassword;

        user.ChangePassword(passwordHasher.Hash(request.NewPassword));

        var now = timeProvider.GetUtcNow();
        foreach (var session in await sessions.ListActiveByUserAsync(user.Id, now, cancellationToken))
        {
            if (session.Id != request.CurrentSessionId)
                session.Revoke(now, SessionRevokeReason.PasswordChanged);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
