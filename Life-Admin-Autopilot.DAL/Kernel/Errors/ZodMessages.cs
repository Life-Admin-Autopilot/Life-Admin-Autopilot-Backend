namespace Life_Admin_Autopilot.DAL.Kernel.Errors;

/// <summary>
/// zod's own error strings, reproduced exactly.
///
/// <para>
/// These ship inside <c>details</c> and are therefore part of the byte-level
/// contract. Every one below was copied from a live Node response — do not
/// reword, re-punctuate or re-case them, and add new ones only after seeing the
/// real output. When in doubt, curl the Node server on port 4100.
/// </para>
/// </summary>
public static class ZodMessages
{
    /// <summary>A required field was absent. zod: <c>"Required"</c>.</summary>
    public const string Required = "Required";

    /// <summary>zod's <c>.email()</c> failure. Note: no trailing period.</summary>
    public const string InvalidEmail = "Invalid email";

    /// <summary>zod's <c>.datetime()</c> failure.</summary>
    public const string InvalidDatetime = "Invalid datetime";

    /// <summary><c>z.string().min(n)</c>.</summary>
    public static string TooShort(int min) => $"String must contain at least {min} character(s)";

    /// <summary><c>z.string().max(n)</c>.</summary>
    public static string TooLong(int max) => $"String must contain at most {max} character(s)";

    /// <summary>
    /// <c>z.array(...).max(n)</c>. Note it is <c>element(s)</c>, not
    /// <c>character(s)</c> — the array and string forms differ by that one word and
    /// are easy to cross-wire.
    /// </summary>
    public static string ArrayTooBig(int max) => $"Array must contain at most {max} element(s)";

    /// <summary><c>z.array(...).min(n)</c>.</summary>
    public static string ArrayTooSmall(int min) => $"Array must contain at least {min} element(s)";

    /// <summary><c>z.number().min(n)</c> on an integer.</summary>
    public static string NumberTooSmall(int min) => $"Number must be greater than or equal to {min}";

    /// <summary><c>z.number().max(n)</c> on an integer.</summary>
    public static string NumberTooBig(int max) => $"Number must be less than or equal to {max}";

    /// <summary><c>z.enum([...])</c>. Values are single-quoted and joined with <c>" | "</c>.</summary>
    public static string InvalidEnum(IEnumerable<string> allowed, string? received) =>
        $"Invalid enum value. Expected {string.Join(" | ", allowed.Select(v => $"'{v}'"))}, received '{received}'";

    /// <summary>
    /// The custom message from <c>taskQuery.ts</c>'s <c>csvEnum</c> helper —
    /// pipe-joined and NOT quoted, unlike <see cref="InvalidEnum"/>.
    /// </summary>
    public static string CsvEnumMember(IEnumerable<string> allowed, string received) =>
        $"expected one of {string.Join("|", allowed)}, got \"{received}\"";

    /// <summary><c>csvEnum</c>'s empty-list message.</summary>
    public const string MustNotBeEmpty = "must not be empty";

    /// <summary>The <c>.strict()</c> rejection. Keys are single-quoted, comma-separated.</summary>
    public static string UnrecognizedKeys(IEnumerable<string> keys) =>
        $"Unrecognized key(s) in object: {string.Join(", ", keys.Select(k => $"'{k}'"))}";

    /// <summary><c>z.string().regex(...)</c> with no custom message.</summary>
    public const string InvalidRegex = "Invalid";

    /// <summary>Wrong JSON type for the field.</summary>
    public static string ExpectedType(string expected, string received) =>
        $"Expected {expected}, received {received}";
}
