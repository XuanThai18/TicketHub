using Microsoft.AspNetCore.Mvc;
using TicketHub.Domain.Shared;

namespace TicketHub.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
public abstract class BaseApiController : ControllerBase
{
    protected IActionResult HandleResult(Result result) =>
        result.IsSuccess ? NoContent() : ToProblem(result.Error);

    protected IActionResult HandleResult<T>(Result<T> result) =>
        result.IsSuccess ? Ok(result.Value) : ToProblem(result.Error);

    protected ObjectResult ToProblem(Error error)
    {
        var problem = Problem(
            statusCode: error.Type switch
            {
                ErrorType.Validation => StatusCodes.Status400BadRequest,
                ErrorType.NotFound => StatusCodes.Status404NotFound,
                ErrorType.Conflict => StatusCodes.Status409Conflict,
                ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
                ErrorType.Forbidden => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status400BadRequest
            },
            title: error.Type.ToString(),
            detail: error.Message);

        if (problem.Value is ProblemDetails details)
            details.Extensions["code"] = error.Code;

        return problem;
    }
}
