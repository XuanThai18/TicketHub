using TicketHub.Domain.Shared;

namespace TicketHub.UnitTests.Domain;

public sealed class ResultTests
{
    [Fact]
    public void Success_HasNoError()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.Equal(Error.None, result.Error);
    }

    [Fact]
    public void Failure_WithNoneError_Throws() =>
        Assert.Throws<ArgumentException>(() => Result.Failure(Error.None));

    [Fact]
    public void Value_OnFailure_Throws()
    {
        Result<int> result = Error.NotFound("X.NotFound", "missing");

        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void ImplicitConversion_FromValue_IsSuccess()
    {
        Result<string> result = "ok";

        Assert.True(result.IsSuccess);
        Assert.Equal("ok", result.Value);
    }
}
