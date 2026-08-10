using Life_Admin_Autopilot.DAL.Features.Digest;

namespace Life_Admin_Autopilot.BLL.Features.Digest;

/// <summary>
/// Port of <c>keepRealThemes</c> from
/// <c>server/src/modules/tasks/dailyDigestProse.ts</c>.
///
/// <para>
/// <b>Only this function is ported from that file.</b> The rest of
/// <c>dailyDigestProse.ts</c> is the background model call that upgrades the
/// headline and theme labels, and it belongs to the deferred AI phase — see the
/// note on <c>DailyDigestService</c>. This half is not AI code: it is the gate that
/// decides which of an EARLIER build's themes may be carried forward, and the
/// no-AI path runs it on every cache miss.
/// </para>
///
/// <para>
/// Drops every id that is not in the pool actually being described, and every
/// duplicate placement. Without it a stale theme becomes something the user can tap
/// into and find empty — which reads as the app losing their data.
/// </para>
/// </summary>
public static class DigestThemes
{
    public static List<DailyDigestThemeDocument> KeepReal(
        IReadOnlyList<DailyDigestThemeDocument> themes,
        IReadOnlySet<string> poolIds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<DailyDigestThemeDocument>();

        foreach (var theme in themes)
        {
            // Filtered against `seen` as it stood BEFORE this theme, then the whole
            // batch is added. So an id repeated WITHIN one theme's own list survives
            // twice, while one already claimed by an earlier theme does not. That is
            // what the Node filter/forEach pair does, and the count is derived from
            // the surviving list either way.
            var taskIds = theme.TaskIds
                .Where(id => poolIds.Contains(id) && !seen.Contains(id))
                .ToList();

            foreach (var id in taskIds)
            {
                seen.Add(id);
            }

            var label = JsText.Trim(theme.Label);
            if (label.Length == 0 || taskIds.Count == 0)
            {
                continue;
            }

            kept.Add(new DailyDigestThemeDocument
            {
                Label = label,
                Count = taskIds.Count,
                TaskIds = taskIds,
            });
        }

        return kept;
    }
}
