using FluentValidation;
using MediatR;
using Microsoft.Extensions.Options;
using TicketHub.Application.Common.Exceptions;
using TicketHub.Domain.Auth;
using TicketHub.Domain.Common;
using TicketHub.Domain.Shared;

namespace TicketHub.Application.Auth;

/// <summary>Admin tạo tài khoản (đường mặc định của hệ thống nội bộ).</summary>
public sealed record CreateUserCommand(string Email, string FullName, string Password, IReadOnlyList<string> Roles)
    : IRequest<Result<Guid>>;

public sealed class CreateUserCommandValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserCommandValidator()
    {
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(User.FullNameMaxLength);
        RuleFor(x => x.Password).StrongPassword();
        RuleFor(x => x.Roles).NotEmpty();
        RuleForEach(x => x.Roles).Must(Roles.All.Contains).WithMessage("Role '{PropertyValue}' does not exist.");
    }
}

public sealed class CreateUserCommandHandler(
    IUserRepository users, IAuthUnitOfWork unitOfWork, IPasswordHasher passwordHasher)
    : IRequestHandler<CreateUserCommand, Result<Guid>>
{
    public Task<Result<Guid>> Handle(CreateUserCommand request, CancellationToken cancellationToken) =>
        UserCreation.CreateAsync(users, unitOfWork, passwordHasher,
            request.Email, request.FullName, request.Password, request.Roles, cancellationToken);
}

/// <summary>Người dùng tự đăng ký. Tắt mặc định (Auth:AllowSelfRegistration); vai trò luôn là User.</summary>
public sealed record RegisterCommand(string Email, string FullName, string Password) : IRequest<Result<Guid>>;

public sealed class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    public RegisterCommandValidator()
    {
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(User.FullNameMaxLength);
        RuleFor(x => x.Password).StrongPassword();
    }
}

public sealed class RegisterCommandHandler(
    IUserRepository users, IAuthUnitOfWork unitOfWork, IPasswordHasher passwordHasher, IOptions<AuthOptions> options)
    : IRequestHandler<RegisterCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        if (!options.Value.AllowSelfRegistration)
            return AuthErrors.SelfRegistrationDisabled;

        return await UserCreation.CreateAsync(users, unitOfWork, passwordHasher,
            request.Email, request.FullName, request.Password, [Roles.User], cancellationToken);
    }
}

internal static class UserCreation
{
    public static async Task<Result<Guid>> CreateAsync(
        IUserRepository users, IAuthUnitOfWork unitOfWork, IPasswordHasher passwordHasher,
        string email, string fullName, string password, IEnumerable<string> roles, CancellationToken cancellationToken)
    {
        var normalized = User.NormalizeEmail(email);
        if (await users.EmailExistsAsync(normalized, cancellationToken))
            return AuthErrors.EmailAlreadyExists(normalized);

        var created = User.Create(normalized, fullName, passwordHasher.Hash(password), roles);
        if (created.IsFailure)
            return created.Error;

        users.Add(created.Value);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintException)
        {
            return AuthErrors.EmailAlreadyExists(normalized);
        }

        return created.Value.Id;
    }
}
