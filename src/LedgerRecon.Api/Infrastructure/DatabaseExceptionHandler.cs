using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace LedgerRecon.Api.Infrastructure;

/// <summary>
/// Turns transient database contention into a retryable 503 instead of a 500. The failed transaction rolled back
/// completely, idempotency claim included, so the client can safely retry with the same Idempotency-Key.
/// </summary>
internal sealed class DatabaseExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } pg)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = "1";
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "The ledger is busy.",
                Detail = "The request lost a lock or serialization race and was rolled back. Retry with the same Idempotency-Key.",
                Extensions = { ["code"] = "ledger.busy", ["sqlState"] = pg.SqlState },
            },
        });
    }
}
