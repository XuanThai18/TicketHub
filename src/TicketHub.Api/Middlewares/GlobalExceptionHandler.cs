using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using TicketHub.Application.Common.Exceptions;

namespace TicketHub.Api.Middlewares;

public sealed partial class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    // Mã không chuẩn (nginx) cho "client đã huỷ request".
    private const int ClientClosedRequest = 499;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails problemDetails;
        var path = httpContext.Request.Path.Value ?? string.Empty;

        switch (exception)
        {
            case OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested:
                LogRequestCancelled(path);
                httpContext.Response.StatusCode = ClientClosedRequest;
                return true;

            case CustomValidationException validationEx:
                LogValidationFailed(path, validationEx.Errors);
                problemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Validation Error",
                    Detail = "One or more validation errors occurred.",
                    Extensions = { ["errors"] = validationEx.Errors }
                };
                break;

            default:
                LogUnhandledException(exception, path);
                problemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "Server Error",
                    Detail = "An unexpected error occurred."
                };
                break;
        }

        httpContext.Response.StatusCode = problemDetails.Status!.Value;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
            Exception = exception
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Request {Path} was cancelled by the client")]
    private partial void LogRequestCancelled(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Validation failed for {Path}: {@Errors}")]
    private partial void LogValidationFailed(string path, IDictionary<string, string[]> errors);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Path}")]
    private partial void LogUnhandledException(Exception exception, string path);
}
