using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;

namespace TicketHub.Application.Behaviors;

public sealed partial class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private const long SlowRequestThresholdMs = 500;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var response = await next(cancellationToken);
        var elapsedMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

        if (elapsedMs > SlowRequestThresholdMs)
            LogSlowRequest(typeof(TRequest).Name, elapsedMs);
        else
            LogHandled(typeof(TRequest).Name, elapsedMs);

        return response;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Handled {RequestName} in {ElapsedMs} ms")]
    private partial void LogHandled(string requestName, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Slow request {RequestName} took {ElapsedMs} ms")]
    private partial void LogSlowRequest(string requestName, long elapsedMs);
}
