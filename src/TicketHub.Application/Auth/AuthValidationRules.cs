using FluentValidation;
using TicketHub.Domain.Auth;

namespace TicketHub.Application.Auth;

internal static class AuthValidationRules
{
    public const int PasswordMinLength = 8;
    public const int PasswordMaxLength = 128;

    public static IRuleBuilderOptions<T, string> ValidEmail<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(User.EmailMaxLength).EmailAddress();

    public static IRuleBuilderOptions<T, string> StrongPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .MinimumLength(PasswordMinLength)
            .MaximumLength(PasswordMaxLength) // chặn chuỗi cực dài làm chậm hàm băm (DoS)
            .Must(p => p.Any(char.IsLetter) && p.Any(char.IsDigit))
            .WithMessage("Password must contain both letters and digits.");
}
