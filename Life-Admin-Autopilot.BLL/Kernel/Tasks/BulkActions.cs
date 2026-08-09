namespace Life_Admin_Autopilot.BLL.Kernel.Tasks;

/// <summary>
/// The five bulk actions, as a closed hierarchy — the .NET form of Node's
/// <c>z.discriminatedUnion('action', ...)</c>. A slice binds its request body into
/// one of these and hands it to <see cref="BulkService"/>; it never builds the
/// Mongo update itself.
/// </summary>
public abstract record BulkActionInput(string Action)
{
    public sealed record Delete() : BulkActionInput("delete");

    public sealed record Complete() : BulkActionInput("complete");

    public sealed record Snooze(DateTime Until) : BulkActionInput("snooze");

    public sealed record SetDomain(string Domain) : BulkActionInput("setDomain");

    public sealed record AddTags(IReadOnlyList<string> Tags) : BulkActionInput("addTags");
}

/// <summary>
/// Either an explicit id list or a filter — never both, never neither. Node
/// enforces the xor in zod; the slice enforces it at bind time and
/// <see cref="BulkService"/> re-checks.
/// </summary>
public sealed class BulkTarget
{
    public IReadOnlyList<string>? Ids { get; init; }

    public TaskQuery.TaskFilter? Filter { get; init; }
}

/// <summary>
/// What the user deserves to be warned about before a bulk op lands.
/// </summary>
/// <param name="FromDocuments">
/// Tasks that came from a scanned document — deleting them orphans the review
/// trail back to that document.
/// </param>
/// <param name="RemindersFired">
/// Tasks whose reminder already fired. These are commitments the user was
/// actively notified about, not silent backlog.
/// </param>
/// <param name="Truncated">True when the filter matched more than one op may touch.</param>
public readonly record struct BulkWarnings(int FromDocuments, int RemindersFired, bool Truncated);

/// <param name="UndoToken">
/// The <c>TaskBulkOp</c> id. Null when nothing changed — a no-op op is never
/// journaled, so undo cannot "restore" a change that never happened.
/// </param>
public readonly record struct BulkResult(int Affected, string? UndoToken, BulkWarnings Warnings);
