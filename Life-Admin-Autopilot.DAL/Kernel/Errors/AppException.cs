namespace Life_Admin_Autopilot.DAL.Kernel.Errors;

/// <summary>
/// Port of <c>server/src/lib/errors.ts</c> <c>AppError</c>.
///
/// The ONLY exception type a feature slice should throw for an expected,
/// client-visible failure. The kernel error middleware renders it as
/// <c>{"error":{"code":...,"message":...,"details":...}}</c> with
/// <c>details</c> omitted when <see cref="Details"/> is null.
///
/// <para>
/// <b>Message strings are part of the contract.</b> They are compared
/// byte-for-byte against the Node server, so copy them verbatim from the
/// corresponding Node route — do not "improve" the wording.
/// </para>
/// </summary>
public class AppException : Exception
{
    public int Status { get; }

    public string Code { get; }

    /// <summary>
    /// Serialized verbatim into the envelope's <c>details</c> member. Use one of
    /// the three mappers in <see cref="ValidationDetails"/> — never an ad-hoc
    /// object — so the shape matches the Node route being mirrored.
    /// </summary>
    public object? Details { get; }

    public AppException(int status, string code, string message, object? details = null)
        : base(message)
    {
        Status = status;
        Code = code;
        Details = details;
    }

    // ---- Factory helpers, one per helper exported by lib/errors.ts ----------

    public static AppException BadRequest(string code, string message, object? details = null) =>
        new(400, code, message, details);

    public static AppException Unauthorized(string code = "unauthorized", string message = "Unauthorized") =>
        new(401, code, message);

    public static AppException Forbidden(string code = "forbidden", string message = "Forbidden") =>
        new(403, code, message);

    public static AppException NotFound(string code = "not_found", string message = "Not found") =>
        new(404, code, message);

    public static AppException Conflict(string code, string message) =>
        new(409, code, message);

    /// <summary>402. Body shape differs per domain — pass the details the caller needs.</summary>
    public static AppException PaymentRequired(string code, string message, object? details = null) =>
        new(402, code, message, details);

    public static AppException Internal(string message = "Internal server error") =>
        new(500, "internal_error", message);
}
