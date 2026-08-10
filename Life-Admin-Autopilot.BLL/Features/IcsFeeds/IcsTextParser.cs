using System.Text;

namespace Life_Admin_Autopilot.BLL.Features.IcsFeeds;

/// <summary>
/// One content line: <c>NAME;PARAM=VALUE:VALUE</c>.
/// </summary>
/// <param name="Name">Lower-cased, so lookups never depend on a publisher's casing.</param>
/// <param name="Parameters">Lower-cased keys; values keep their case (a TZID is case-sensitive).</param>
/// <param name="Value">Raw value text, still escaped. Use <see cref="IcsProperty.UnescapeText"/> for TEXT values.</param>
/// <param name="RawLine">The unfolded source line, verbatim. Rendered behind the citation chip.</param>
public sealed record IcsProperty(
    string Name,
    IReadOnlyDictionary<string, string> Parameters,
    string Value,
    string RawLine)
{
    public string? Parameter(string name) =>
        Parameters.TryGetValue(name.ToLowerInvariant(), out var value) ? value : null;

    /// <summary>
    /// RFC 5545 §3.3.11 TEXT unescaping. <c>ical.js</c> applies this when reading a
    /// property value, so a summary containing an escaped comma must arrive
    /// unescaped or the matter title carries a stray backslash.
    /// </summary>
    public static string UnescapeText(string value)
    {
        if (value.IndexOf('\\') < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i += 1)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                builder.Append(value[i]);
                continue;
            }

            i += 1;
            builder.Append(value[i] switch
            {
                'n' or 'N' => "\n",
                '\\' => "\\",
                ',' => ",",
                ';' => ";",
                _ => value[i].ToString(),
            });
        }

        return builder.ToString();
    }
}

/// <summary>A <c>BEGIN:X</c> … <c>END:X</c> block.</summary>
public sealed class IcsComponent
{
    public IcsComponent(string name)
    {
        Name = name;
    }

    /// <summary>Lower-cased, e.g. <c>vcalendar</c>, <c>vevent</c>.</summary>
    public string Name { get; }

    public List<IcsProperty> Properties { get; } = new();

    public List<IcsComponent> Children { get; } = new();

    public IcsProperty? FirstProperty(string name) =>
        Properties.FirstOrDefault(p => p.Name == name.ToLowerInvariant());

    public IEnumerable<IcsProperty> AllProperties(string name)
    {
        var lower = name.ToLowerInvariant();
        return Properties.Where(p => p.Name == lower);
    }

    public IEnumerable<IcsComponent> Subcomponents(string name)
    {
        var lower = name.ToLowerInvariant();
        return Children.Where(c => c.Name == lower);
    }
}

/// <summary>
/// A minimal, dependency-free iCalendar reader: line unfolding, component nesting,
/// and property/parameter splitting.
///
/// <para>
/// Hand-written rather than taken from a library because the reference goes out of
/// its way NOT to let its parser resolve times (see <see cref="IcsTimeResolver"/>):
/// what this slice needs from a parser is the raw wall-clock components and the
/// TZID <i>parameter</i>, and every general-purpose library's convenience API
/// hands back a resolved instant instead. Keeping the reader here also keeps the
/// whole slice inside its own folder with no shared project-file edit.
/// </para>
///
/// <para>
/// <b>Throws on a structurally broken feed</b>, matching <c>ICAL.parse</c>. The
/// caller turns that into <c>status: 'error'</c> with "That feed could not be
/// read." — a feed is third-party input and must never take the request down.
/// </para>
/// </summary>
public static class IcsTextParser
{
    public static IcsComponent Parse(string body)
    {
        IcsComponent? root = null;
        var stack = new Stack<IcsComponent>();

        foreach (var line in Unfold(body))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var property = ParseLine(line);

            if (property.Name == "begin")
            {
                var component = new IcsComponent(property.Value.Trim().ToLowerInvariant());
                if (stack.Count > 0)
                {
                    stack.Peek().Children.Add(component);
                }
                else
                {
                    root ??= component;
                }

                stack.Push(component);
                continue;
            }

            if (property.Name == "end")
            {
                if (stack.Count == 0)
                {
                    throw new FormatException("Unbalanced END with no matching BEGIN.");
                }

                var closing = property.Value.Trim().ToLowerInvariant();
                if (stack.Peek().Name != closing)
                {
                    throw new FormatException($"Mismatched END:{closing.ToUpperInvariant()}.");
                }

                stack.Pop();
                continue;
            }

            if (stack.Count == 0)
            {
                throw new FormatException("Content line outside any component.");
            }

            stack.Peek().Properties.Add(property);
        }

        if (stack.Count > 0)
        {
            throw new FormatException("Unterminated component.");
        }

        if (root is null || root.Name != "vcalendar")
        {
            throw new FormatException("No VCALENDAR component.");
        }

        return root;
    }

    /// <summary>
    /// RFC 5545 §3.1 line unfolding: a CRLF followed by a single space or tab
    /// continues the previous line. Publishers wrap at 75 octets, so a long SUMMARY
    /// arrives split and a reader that ignores folding truncates every one of them.
    /// </summary>
    public static IReadOnlyList<string> Unfold(string body)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        var started = false;

        foreach (var raw in body.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;

            if (started && line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                current.Append(line, 1, line.Length - 1);
                continue;
            }

            if (started)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            current.Append(line);
            started = true;
        }

        if (started)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    /// <summary>
    /// Split <c>NAME;PARAM="a:b";P2=c:VALUE</c> at the first UNQUOTED colon. Quoted
    /// parameter values may legally contain a colon, which a naive
    /// <c>IndexOf(':')</c> would split on and corrupt.
    /// </summary>
    private static IcsProperty ParseLine(string line)
    {
        var quoted = false;
        var colon = -1;

        for (var i = 0; i < line.Length; i += 1)
        {
            var c = line[i];
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (c == ':' && !quoted)
            {
                colon = i;
                break;
            }
        }

        if (colon < 0)
        {
            throw new FormatException($"Content line has no value separator: {Truncate(line)}");
        }

        var head = line[..colon];
        var value = line[(colon + 1)..];

        var segments = SplitParameters(head);
        var name = segments[0].Trim().ToLowerInvariant();
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var segment in segments.Skip(1))
        {
            var equals = segment.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            var key = segment[..equals].Trim().ToLowerInvariant();
            var raw = segment[(equals + 1)..].Trim();
            if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            {
                raw = raw[1..^1];
            }

            parameters[key] = raw;
        }

        return new IcsProperty(name, parameters, value, line);
    }

    private static List<string> SplitParameters(string head)
    {
        var parts = new List<string>();
        var quoted = false;
        var start = 0;

        for (var i = 0; i < head.Length; i += 1)
        {
            var c = head[i];
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (c == ';' && !quoted)
            {
                parts.Add(head[start..i]);
                start = i + 1;
            }
        }

        parts.Add(head[start..]);
        return parts;
    }

    private static string Truncate(string line) => line.Length > 80 ? line[..80] : line;
}
