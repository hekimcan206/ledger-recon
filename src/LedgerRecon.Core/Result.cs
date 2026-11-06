using System.Diagnostics.CodeAnalysis;

namespace LedgerRecon.Core;

/// <summary>How an error is surfaced to callers; mapped to HTTP status codes at the edge.</summary>
public enum ErrorKind
{
    /// <summary>The request itself is malformed (400).</summary>
    Validation,

    /// <summary>The addressed resource does not exist (404).</summary>
    NotFound,

    /// <summary>The request collides with existing state (409).</summary>
    Conflict,

    /// <summary>The request is well-formed but violates a business rule (422).</summary>
    Unprocessable,
}

/// <summary>
/// An expected, recoverable failure. Rule violations are returned as values, so exceptions stay reserved
/// for bugs and infrastructure faults.
/// </summary>
/// <param name="Code">Stable machine-readable code, e.g. <c>entry.unbalanced</c>. Clients branch on this, never on the message.</param>
/// <param name="Message">Human-readable explanation.</param>
/// <param name="Kind">Category used to choose the HTTP status code.</param>
/// <param name="Field">Request field the error refers to, when there is one.</param>
public sealed record LedgerError(string Code, string Message, ErrorKind Kind, string? Field = null)
{
    public static LedgerError Validation(string code, string message, string? field = null) => new(code, message, ErrorKind.Validation, field);

    public static LedgerError NotFound(string code, string message) => new(code, message, ErrorKind.NotFound);

    public static LedgerError Conflict(string code, string message) => new(code, message, ErrorKind.Conflict);

    public static LedgerError Unprocessable(string code, string message) => new(code, message, ErrorKind.Unprocessable);
}

/// <summary>Outcome of an operation that produces a value on success.</summary>
public sealed class Result<T>
{
    private readonly T? _value;

    private Result(T? value, LedgerError? error)
    {
        _value = value;
        Error = error;
    }

    public LedgerError? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    /// <summary>The value; throws when the result is a failure, which always indicates a programming error.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot read the value of a failed result ({Error.Code}).");

    internal static Result<T> Ok(T value) => new(value, null);

    internal static Result<T> Fail(LedgerError error) => new(default, error);

    public static implicit operator Result<T>(LedgerError error) => Fail(error);
}

/// <summary>Factory methods for <see cref="Result{T}"/>.</summary>
public static class Result
{
    public static Result<T> Success<T>(T value) => Result<T>.Ok(value);

    public static Result<T> Failure<T>(LedgerError error) => Result<T>.Fail(error);
}
