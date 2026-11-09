using FluentValidation.TestHelper;
using TicketHub.Application.Auth;
using TicketHub.Domain.Common;

namespace TicketHub.UnitTests.Auth;

public sealed class AuthValidatorTests
{
    private readonly CreateUserCommandValidator _createUser = new();

    [Theory]
    [InlineData("short1")]            // < 8 ký tự
    [InlineData("onlyletters")]       // không có số
    [InlineData("1234567890")]        // không có chữ
    public void CreateUser_WeakPassword_IsRejected(string password) =>
        _createUser.TestValidate(new CreateUserCommand("a@b.com", "A", password, [Roles.User]))
            .ShouldHaveValidationErrorFor(x => x.Password);

    [Fact]
    public void CreateUser_TooLongPassword_IsRejected() =>
        _createUser.TestValidate(new CreateUserCommand("a@b.com", "A", "a1" + new string('x', 200), [Roles.User]))
            .ShouldHaveValidationErrorFor(x => x.Password);

    [Fact]
    public void CreateUser_UnknownRole_IsRejected() =>
        _createUser.TestValidate(new CreateUserCommand("a@b.com", "A", "Passw0rd!", ["Root"]))
            .ShouldHaveValidationErrorFor("Roles[0]");

    [Fact]
    public void CreateUser_Valid_HasNoErrors() =>
        _createUser.TestValidate(new CreateUserCommand("a@b.com", "Alice", "Passw0rd!", [Roles.Admin]))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void ChangePassword_SameAsCurrent_IsRejected() =>
        new ChangePasswordCommandValidator()
            .TestValidate(new ChangePasswordCommand(Guid.NewGuid(), Guid.NewGuid(), "Passw0rd!", "Passw0rd!"))
            .ShouldHaveValidationErrorFor(x => x.NewPassword);
}
