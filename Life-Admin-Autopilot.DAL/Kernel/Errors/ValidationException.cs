namespace Life_Admin_Autopilot.DAL.Kernel.Errors;

/// <summary>
/// The .NET stand-in for an uncaught <c>ZodError</c> — i.e. a route that mirrors
/// a THROWING <c>Schema.parse(req.body)</c> rather than <c>safeParse</c>.
///
/// The kernel error middleware renders it, before any <see cref="AppException"/>
/// handling, as:
/// <code>
/// 400 {"error":{"code":"validation_error",
///               "message":"Request validation failed",
///               "details":[{"path":"email","message":"Invalid email"}]}}
/// </code>
///
/// Throw this ONLY from routes whose Node counterpart uses <c>.parse()</c>.
/// Everywhere else use <see cref="AppException.BadRequest"/> with
/// <see cref="ValidationDetails.AsFlattened"/>.
/// </summary>
public sealed class ValidationException : Exception
{
    public const string ErrorCode = "validation_error";
    public const string ErrorMessage = "Request validation failed";

    public IReadOnlyList<ValidationIssue> Issues { get; }

    public ValidationException(IEnumerable<ValidationIssue> issues)
        : base(ErrorMessage)
    {
        Issues = issues.ToList();
    }

    public ValidationException(params ValidationIssue[] issues)
        : this((IEnumerable<ValidationIssue>)issues)
    {
    }

    public static ValidationException At(string path, string message) =>
        new(ValidationIssue.At(path, message));
}

/// <summary>
/// Raised when a route param that must be a Mongo ObjectId is not one — the
/// analogue of Mongoose's <c>CastError(kind: 'ObjectId')</c>. The error
/// middleware maps it, FIRST in the chain, to
/// <c>404 {"error":{"code":"not_found","message":"Not found"}}</c>.
///
/// Slices normally get this for free: <c>ObjectIdRouteValue.Parse</c> throws it.
/// </summary>
public sealed class ObjectIdCastException : Exception
{
    public string Value { get; }

    public ObjectIdCastException(string value)
        : base($"Cast to ObjectId failed for value \"{value}\"")
    {
        Value = value;
    }
}

/// <summary>
/// A request body that could not be read at all — malformed JSON, or a body over
/// the 256kb ceiling.
///
/// <b>Both are 500 <c>internal_error</c>, not 400 and not 413.</b> Express's
/// body-parser throws a <c>SyntaxError</c> / <c>PayloadTooLargeError</c> that
/// <c>errorHandler.ts</c> does not recognise, so it falls through to the generic
/// 500 branch. Verified live against the Node server for both cases. ASP.NET
/// defaults to 400/413 here, so the kernel deliberately overrides it.
/// </summary>
public sealed class BodyReadException : Exception
{
    public BodyReadException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
