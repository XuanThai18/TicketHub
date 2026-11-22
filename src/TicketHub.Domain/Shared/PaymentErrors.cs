namespace TicketHub.Domain.Shared;

public static class PaymentErrors
{
    public static readonly Error InvalidSignature = Error.Unauthorized(
        "Payments.InvalidSignature", "The webhook signature is missing or does not match.");

    public static readonly Error SignatureExpired = Error.Unauthorized(
        "Payments.SignatureExpired", "The webhook timestamp is too old or too far in the future.");

    public static readonly Error InvalidPayload = Error.Validation(
        "Payments.InvalidPayload", "The webhook body is not a valid payment notification.");

    public static Error UnknownTransaction(string transactionId) => Error.NotFound(
        "Payments.UnknownTransaction", $"Payment transaction '{transactionId}' was not found.");
}
