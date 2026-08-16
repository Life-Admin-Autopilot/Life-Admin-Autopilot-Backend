using System.Text;

namespace Life_Admin_Autopilot.BLL.Features.VoiceNotes;

/// <summary>
/// What container a stored voice note actually is, read from the bytes rather than
/// believed from the header.
///
/// <para>
/// <b>The declared type is not usable here, and that is by design upstream.</b>
/// <c>POST /me/voice-notes</c> accepts exactly four content types —
/// <c>audio/m4a</c>, <c>audio/mp4</c>, <c>audio/aac</c> and
/// <c>application/octet-stream</c> — because that is the <c>express.raw()</c> filter
/// the reference server runs, and widening it would change which bodies the API
/// takes at all. The web client records WAV, which is not on that list, so it
/// uploads as <c>application/octet-stream</c> and that string is what lands in
/// <c>mimeType</c>. Branching on it would hand the ASR provider a data URI claiming
/// a generic binary blob for what is in fact a perfectly ordinary WAV.
/// </para>
///
/// <para>
/// So the container is sniffed. Four magic numbers cover everything the two clients
/// produce, and an unrecognised header falls back to the declared type — a caller
/// that told the truth is still believed, this only overrules the one case where it
/// could not.
/// </para>
/// </summary>
public static class VoiceAudioFormat
{
    /// <summary>
    /// What the ASR provider is told when nothing matched and the note declared
    /// nothing useful either. WAV because that is what the recorder ships today, and
    /// a wrong guess costs one provider round trip rather than a silent misread.
    /// </summary>
    public const string Fallback = "audio/wav";

    /// <summary>
    /// The generic type the upload route stores for anything the client could not
    /// name. Never forwarded to the provider — this is exactly the value the sniff
    /// exists to replace.
    /// </summary>
    public const string Unknown = "application/octet-stream";

    /// <summary>
    /// The provider's content type for <paramref name="bytes"/>.
    /// </summary>
    /// <param name="declared">
    /// The note's stored <c>mimeType</c>. Used only when the bytes are unrecognised
    /// and it says something more specific than <see cref="Unknown"/>.
    /// </param>
    public static string Sniff(ReadOnlySpan<byte> bytes, string? declared)
    {
        var sniffed = FromMagic(bytes);
        if (sniffed is not null)
        {
            return sniffed;
        }

        var trimmed = (declared ?? string.Empty).Split(';')[0].Trim();

        return trimmed.Length > 0 && !trimmed.Equals(Unknown, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : Fallback;
    }

    /// <summary>
    /// The container, or null when the header matches nothing known.
    ///
    /// <para>
    /// RIFF is checked in two halves because the four bytes between them are the
    /// file length — <c>"RIFF"</c> alone also opens AVI and WebP, and only the
    /// <c>"WAVE"</c> at offset 8 says which. <c>ftyp</c> at offset 4 is the ISO base
    /// media box that opens both <c>.m4a</c> and <c>.mp4</c>; they are the same
    /// container and the provider is told so.
    /// </para>
    /// </summary>
    private static string? FromMagic(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 12 && Matches(bytes, 0, "RIFF") && Matches(bytes, 8, "WAVE"))
        {
            return "audio/wav";
        }

        if (bytes.Length >= 8 && Matches(bytes, 4, "ftyp"))
        {
            return "audio/mp4";
        }

        if (bytes.Length >= 3 && Matches(bytes, 0, "ID3"))
        {
            return "audio/mpeg";
        }

        // A bare MPEG frame sync: eleven set bits. Only worth reading when there is no
        // ID3 tag in front of the audio, which is the case for a raw encoder dump.
        if (bytes.Length >= 2 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0)
        {
            return "audio/mpeg";
        }

        return null;
    }

    private static bool Matches(ReadOnlySpan<byte> bytes, int offset, string ascii) =>
        bytes.Length >= offset + ascii.Length
        && bytes.Slice(offset, ascii.Length).SequenceEqual(Encoding.ASCII.GetBytes(ascii));
}
