using FluentValidation.Results;

namespace TicketHub.Application.Common.Exceptions;

public class CustomValidationException : Exception
{
    public IDictionary<string, string[]> Errors { get; }

    public CustomValidationException(IEnumerable<ValidationFailure> failures)
        : base("One or more validation failures have occurred.")
    {
        Errors = failures
            .GroupBy(e => e.PropertyName, e => e.ErrorMessage)
            .ToDictionary(failureGroup => failureGroup.Key, failureGroup => failureGroup.ToArray());
    }
}