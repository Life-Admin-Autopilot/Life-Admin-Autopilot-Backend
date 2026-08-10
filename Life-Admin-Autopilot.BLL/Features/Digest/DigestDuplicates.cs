using Life_Admin_Autopilot.DAL.Features.Digest;

namespace Life_Admin_Autopilot.BLL.Features.Digest;

/// <summary>One pool row, reduced to the two fields the binning needs.</summary>
public readonly record struct DuplicateCandidate(string Id, string Title);

/// <summary>
/// Port of <c>findDuplicates</c> from <c>server/src/modules/tasks/summarize.ts</c>.
///
/// <para>
/// <b>Shared with the range summary in Node, and it should stay shared here.</b>
/// It lives in this slice only because the summarize slice has not landed yet;
/// whoever ports <c>summarizeRange</c> should call this rather than write a second
/// copy, and move it somewhere neutral if that reads better then. Two
/// implementations of "the same matter twice" that drift apart is precisely the
/// class of bug this endpoint exists to surface.
/// </para>
/// </summary>
public static class DigestDuplicates
{
    /// <summary>Node keeps the five biggest bins and no more.</summary>
    private const int MaxDuplicateGroups = 5;

    public static List<DailyDigestDuplicateDocument> Find(IReadOnlyList<DuplicateCandidate> tasks)
    {
        // Insertion-ordered, like a JS Map — the surviving group's reported title is
        // its FIRST member's, so bin order is part of the output.
        var bins = new Dictionary<string, List<DuplicateCandidate>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var task in tasks)
        {
            var key = JsText.CollapseWhitespace(JsText.Trim(task.Title).ToLowerInvariant());

            if (bins.TryGetValue(key, out var bin))
            {
                bin.Add(task);
                continue;
            }

            bins[key] = new List<DuplicateCandidate> { task };
            order.Add(key);
        }

        return order
            .Select(key => bins[key])
            .Where(group => group.Count > 1)

            // OrderByDescending, NOT List.Sort. V8's Array#sort is stable, so bins of
            // equal size keep insertion order and the digest is reproducible between
            // rebuilds; List.Sort is an unstable introsort and would shuffle them.
            .OrderByDescending(group => group.Count)
            .Take(MaxDuplicateGroups)
            .Select(group => new DailyDigestDuplicateDocument
            {
                // The FIRST member's ORIGINAL title — not the normalised bin key.
                Title = group[0].Title,
                Count = group.Count,
                TaskIds = group.Select(t => t.Id).ToList(),
            })
            .Where(duplicate => duplicate.Title.Length > 0)
            .ToList();
    }
}
