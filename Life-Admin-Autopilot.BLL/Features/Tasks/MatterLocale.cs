using Life_Admin_Autopilot.BLL.Kernel.Dtos;
using Life_Admin_Autopilot.BLL.Kernel.Mappers;
using Life_Admin_Autopilot.DAL.Kernel.Documents;

namespace Life_Admin_Autopilot.BLL.Features.Tasks;

/// <summary>
/// Port of <c>server/src/modules/tasks/matterLocale.ts</c> — presenting a matter
/// in the reader's language.
///
/// <para>
/// The split this enforces: <c>title</c>/<c>notes</c>/<c>subtasks[].text</c> on
/// the Task are CANONICAL and never rewritten. <c>i18n[locale]</c> holds
/// read-only presentation copies. Everything that REASONS about a matter keeps
/// reading canonical — the list query's regex searches one field set so a matter
/// can never match twice, and the reminder lead-time table runs English keyword
/// regexes over the title, so keeping the English original is what stops a
/// passport renewal losing its six-month warning.
/// </para>
///
/// <para>
/// <b>Applied to EXACTLY TWO endpoints</b> — <c>GET /me/tasks</c> and
/// <c>GET /me/tasks/{id}</c>. Every other task-returning endpoint ships the raw
/// <c>toJSON</c>, <c>i18n</c> and all. That internal field leaks by design; do not
/// "tidy" it away from the others, and do not add the overlay to them.
/// </para>
/// </summary>
public static class MatterLocale
{
    /// <summary>
    /// The subtask-translation lookup key, reproduced verbatim from Node.
    ///
    /// <para>
    /// <b>FROZEN BUG — ported deliberately.</b> Node overlays with
    /// <c>copy.subtasks?.[String(sub._id)]</c>, but <c>sub</c> has already been
    /// through <c>toJSON</c>, which did <c>ret.id = String(ret._id)</c> and then
    /// <c>delete ret._id</c>. So <c>sub._id</c> is <c>undefined</c> and the lookup
    /// key is the literal five-letter string <c>"undefined"</c> on every row.
    /// Subtask text is therefore NEVER translated in practice. Fixing it here
    /// would be a behavioural change the contract does not permit.
    /// </para>
    /// </summary>
    private const string FrozenSubtaskKey = "undefined";

    /// <summary>
    /// Overlay one matter and map it to the wire shape. <c>i18n</c> is stripped
    /// EITHER WAY — the client renders one language at a time, and shipping every
    /// translation on every row would grow the list payload without a screen using
    /// it.
    /// </summary>
    public static TaskDto Present(TaskDocument doc, string locale)
    {
        var translation = SelectTranslation(doc, locale);
        if (translation is null)
        {
            return StripI18n(doc).ToDto();
        }

        var presented = StripI18n(doc);

        if (!string.IsNullOrEmpty(translation.Title))
        {
            presented.Title = translation.Title;
        }

        if (!string.IsNullOrEmpty(translation.Notes))
        {
            presented.Notes = translation.Notes;
        }

        if (translation.Subtasks is { } subtaskCopy && presented.Subtasks.Count > 0)
        {
            // See FrozenSubtaskKey: one key for the whole array, and it is a
            // string literal rather than any subtask's real id.
            presented.Subtasks = subtaskCopy.TryGetValue(FrozenSubtaskKey, out var translated)
                ? presented.Subtasks
                    .Select(sub => new SubtaskDocument
                    {
                        Id = sub.Id,
                        Text = translated,
                        Done = sub.Done,
                        CreatedAt = sub.CreatedAt,
                    })
                    .ToList()
                : presented.Subtasks;
        }

        return presented.ToDto();
    }

    public static IReadOnlyList<TaskDto> PresentMany(IEnumerable<TaskDocument> docs, string locale) =>
        docs.Select(doc => Present(doc, locale)).ToList();

    /// <summary>
    /// The copy to overlay, or null when there is nothing to do — the matter is
    /// already in the reader's language, or carries no translation for it. This is
    /// the common case and must stay cheap: it runs per row on every list read.
    /// </summary>
    private static TaskTranslationDocument? SelectTranslation(TaskDocument doc, string locale)
    {
        if (doc.I18n is null)
        {
            return null;
        }

        if (AiLocales.Resolve(doc.SourceLocale) == locale)
        {
            return null;
        }

        return doc.I18n.TryGetValue(locale, out var copy) ? copy : null;
    }

    /// <summary>
    /// A shallow copy with <c>i18n</c> cleared, so the overlay never mutates the
    /// document the caller still holds.
    ///
    /// <para>
    /// <b>This was a hand-written property list and it dropped four fields</b> —
    /// <c>Amount</c>, <c>SchemaVersion</c>, <c>GoogleEventId</c> and
    /// <c>GooglePushedAt</c>. Only <c>Amount</c> reaches the wire today, and it is
    /// the one that mattered: a matter with a price came back from these two routes
    /// with <c>amount: null</c>, so the app's amount field sat on its "0.00"
    /// placeholder while the finance summary — which reads the documents directly —
    /// showed the real figure. Two screens, one database, different answers, no
    /// error anywhere.
    /// </para>
    ///
    /// <para>
    /// It delegates to <see cref="TaskDocument.ShallowCopy"/> now, so a property
    /// added to the document is carried here without anyone remembering to.
    /// <c>MatterLocaleFidelityTests</c> fails if this ever stops being true.
    /// </para>
    /// </summary>
    private static TaskDocument StripI18n(TaskDocument doc)
    {
        var copy = doc.ShallowCopy();
        copy.I18n = null;
        return copy;
    }
}
