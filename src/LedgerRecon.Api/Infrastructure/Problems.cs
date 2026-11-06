using LedgerRecon.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LedgerRecon.Api.Infrastructure;

/// <summary>Maps domain errors to RFC 9457 problem details with a stable <c>code</c> extension.</summary>
internal static class Problems
{
    public static ProblemHttpResult From(LedgerError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var status = error.Kind switch
        {
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status422UnprocessableEntity,
        };

        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };
        if (error.Field is not null)
        {
            extensions["field"] = error.Field;
        }

        return TypedResults.Problem(detail: error.Message, statusCode: status, title: Title(status), extensions: extensions);
    }

    public static ProblemHttpResult Validation(string code, string message, string? field = null) =>
        From(LedgerError.Validation(code, message, field));

    private static string Title(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "The request is invalid.",
        StatusCodes.Status404NotFound => "The resource was not found.",
        StatusCodes.Status409Conflict => "The request conflicts with existing state.",
        _ => "The request violates a business rule.",
    };
}
