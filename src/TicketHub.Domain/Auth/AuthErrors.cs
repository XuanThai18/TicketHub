using TicketHub.Domain.Shared;

namespace TicketHub.Domain.Auth;

public static class AuthErrors
{
    // Cùng một thông báo cho "sai email" và "sai mật khẩu" để không lộ email nào tồn tại.
    public static readonly Error InvalidCredentials =
        Error.Unauthorized("Auth.InvalidCredentials", "Email or password is incorrect.");

    public static readonly Error AccountLocked =
        Error.Forbidden("Auth.AccountLocked", "Too many failed attempts. The account is temporarily locked.");

    public static readonly Error AccountDisabled =
        Error.Forbidden("Auth.AccountDisabled", "The account is disabled.");

    public static readonly Error InvalidRefreshToken =
        Error.Unauthorized("Auth.InvalidRefreshToken", "The refresh token is invalid or expired.");

    public static readonly Error SelfRegistrationDisabled =
        Error.Forbidden("Auth.SelfRegistrationDisabled", "Self registration is disabled. Ask an administrator to create your account.");

    public static readonly Error WrongCurrentPassword =
        Error.Validation("Auth.WrongCurrentPassword", "The current password is incorrect.");

    public static Error EmailAlreadyExists(string email) =>
        Error.Conflict("Auth.EmailAlreadyExists", $"Email '{email}' is already registered.");

    public static Error UserNotFound(Guid id) =>
        Error.NotFound("Auth.UserNotFound", $"User '{id}' was not found.");

    public static Error UnknownRole(string role) =>
        Error.Validation("Auth.UnknownRole", $"Role '{role}' does not exist.");
}
