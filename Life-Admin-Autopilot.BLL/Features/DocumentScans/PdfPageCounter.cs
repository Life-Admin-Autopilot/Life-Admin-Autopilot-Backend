using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Life_Admin_Autopilot.DAL.Kernel.Errors;

namespace Life_Admin_Autopilot.BLL.Features.DocumentScans;

/// <summary>
/// Port of <c>server/src/lib/pdfPageCount.ts</c> — a CHEAP, non-rendering page
/// count, used only to reject an over-long PDF before it costs a storage write
/// and a vision call.
///
/// <para>
/// <b>Why this is hand-rolled.</b> Node uses <c>pdf-lib</c>; .NET has no
/// equivalent in the dependency set, and adding a PDF library to a shared
/// <c>.csproj</c> while six other slices are in flight buys a merge conflict for
/// one integer. Counting page objects needs a fraction of a parser, so that is
/// what this is.
/// </para>
///
/// <para>
/// <b>Where it deliberately differs from pdf-lib, and why that is the safe
/// direction.</b> Both agree on the two REJECTIONS — a missing <c>%PDF-</c>
/// header and an encrypted file are <c>invalid_pdf</c> on either server, matching
/// pdf-lib's <c>Expected PDF header</c> and <c>EncryptedPDFError</c>. Where they
/// can differ is the COUNT: a PDF whose page tree lives entirely inside a
/// non-Flate compressed object stream may scan as zero here, and this returns
/// <b>1</b> rather than raising <c>invalid_pdf</c>. Under-counting only relaxes
/// the page cap; guessing "corrupt" would reject a document the reference server
/// accepts, which is the failure the user cannot work around.
/// </para>
/// </summary>
public static class PdfPageCounter
{
    /// <summary>
    /// <c>/Type /Page</c> but NOT <c>/Type /Pages</c> — the negative lookahead is
    /// the whole trick, since the tree node and the leaf differ by one letter.
    /// </summary>
    private static readonly Regex PageObject =
        new(@"/Type\s*/Page(?![A-Za-z0-9])", RegexOptions.Compiled);

    /// <summary>The page-tree node's own tally, used only when no leaf is visible.</summary>
    private static readonly Regex PageTreeCount =
        new(@"/Count\s+(\d+)", RegexOptions.Compiled);

    private static readonly Regex FlateStream =
        new(@"/FlateDecode[^>]*>>\s*stream\r?\n", RegexOptions.Compiled);

    /// <summary>How far into the file the header may start. pdf-lib scans a small prefix too.</summary>
    private const int HeaderSearchWindow = 1024;

    public static int Count(byte[] bytes)
    {
        // Latin-1 keeps a byte-per-char mapping, so binary stream payloads cannot
        // merge two bytes into one char and shift the offsets a match reports.
        var text = Latin1(bytes, bytes.Length);

        if (!HasHeader(text))
        {
            throw InvalidPdf();
        }

        if (text.Contains("/Encrypt", StringComparison.Ordinal))
        {
            // pdf-lib throws EncryptedPDFError unless ignoreEncryption is set, and
            // the Node route does not set it.
            throw InvalidPdf();
        }

        var direct = CountIn(text);
        if (direct > 0)
        {
            return direct;
        }

        var inflated = CountInFlateStreams(bytes, text);
        return inflated > 0 ? inflated : 1;
    }

    /// <summary>The message is part of the contract — em dash included.</summary>
    public static AppException InvalidPdf() => AppException.BadRequest(
        "invalid_pdf",
        "Could not read that PDF — it may be corrupt or password-protected.");

    private static bool HasHeader(string text) =>
        text.AsSpan(0, Math.Min(HeaderSearchWindow, text.Length)).IndexOf("%PDF-") >= 0;

    private static int CountIn(string text)
    {
        var leaves = PageObject.Matches(text).Count;
        if (leaves > 0)
        {
            return leaves;
        }

        // No leaf visible: fall back to the largest /Count, which on a well-formed
        // tree is the root node's total.
        var best = 0;
        foreach (Match match in PageTreeCount.Matches(text))
        {
            if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                best = Math.Max(best, n);
            }
        }

        return best;
    }

    /// <summary>
    /// Modern PDFs pack the catalog and page tree into Flate-compressed object
    /// streams, where a raw scan sees nothing. Inflate those and scan again.
    /// </summary>
    private static int CountInFlateStreams(byte[] bytes, string text)
    {
        var best = 0;

        foreach (Match match in FlateStream.Matches(text))
        {
            var start = match.Index + match.Length;
            var end = text.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end <= start)
            {
                continue;
            }

            try
            {
                using var source = new MemoryStream(bytes, start, end - start, writable: false);
                using var inflate = new ZLibStream(source, CompressionMode.Decompress);
                using var buffer = new MemoryStream();
                inflate.CopyTo(buffer, 16 * 1024);
                best = Math.Max(best, CountIn(Latin1(buffer.GetBuffer(), (int)buffer.Length)));
            }
            catch (InvalidDataException)
            {
                // Not actually zlib, or truncated. Nothing to learn from it.
            }
        }

        return best;
    }

    private static string Latin1(byte[] bytes, int length) =>
        Encoding.Latin1.GetString(bytes, 0, length);
}
