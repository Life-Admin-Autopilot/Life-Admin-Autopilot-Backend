using System.Text;
using Life_Admin_Autopilot.BLL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Kernel.Errors;

namespace Life_Admin_Autopilot.Tests.Features.DocumentScans;

/// <summary>
/// The cheap page count that gates the upload. Two of these cases were verified
/// against the live reference server, and are marked as such.
/// </summary>
public sealed class PdfPageCounterTests
{
    /// <summary>The parity harness's own <c>pdf.min</c> fixture, byte for byte.</summary>
    private const string MinimalPdf =
        "%PDF-1.4\n" +
        "1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n" +
        "2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
        "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]>>endobj\n" +
        "trailer<</Root 1 0 R>>\n" +
        "%%EOF\n";

    [Fact]
    public void counts_the_harness_fixture_as_one_page()
    {
        // Verified live: the reference server returns pageCount 1 for these bytes.
        Assert.Equal(1, PdfPageCounter.Count(Bytes(MinimalPdf)));
    }

    [Fact]
    public void does_not_mistake_the_page_tree_node_for_a_page()
    {
        // The whole trick is one letter: /Type/Pages is the tree, /Type/Page is a leaf.
        var twoPages = MinimalPdf.Replace(
            "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]>>endobj",
            "3 0 obj<</Type/Page/Parent 2 0 R>>endobj\n4 0 obj<</Type/Page/Parent 2 0 R>>endobj",
            StringComparison.Ordinal);

        Assert.Equal(2, PdfPageCounter.Count(Bytes(twoPages)));
    }

    [Fact]
    public void falls_back_to_the_page_tree_count_when_no_leaf_is_visible()
    {
        var noLeaves = "%PDF-1.7\n1 0 obj<</Type/Pages/Kids[2 0 R]/Count 7>>endobj\ntrailer<<>>\n%%EOF\n";

        Assert.Equal(7, PdfPageCounter.Count(Bytes(noLeaves)));
    }

    [Fact]
    public void rejects_bytes_with_no_pdf_header_as_invalid_pdf()
    {
        // VERIFIED LIVE: posting "this is not a pdf at all" as application/pdf gets
        // 400 invalid_pdf from the reference server, because pdf-lib throws.
        var error = Assert.Throws<AppException>(() => PdfPageCounter.Count(Bytes("this is not a pdf at all")));

        Assert.Equal(400, error.Status);
        Assert.Equal("invalid_pdf", error.Code);
        Assert.Equal("Could not read that PDF — it may be corrupt or password-protected.", error.Message);
    }

    [Fact]
    public void rejects_an_encrypted_pdf()
    {
        // pdf-lib throws EncryptedPDFError; the Node route does not set ignoreEncryption.
        var encrypted = "%PDF-1.4\n1 0 obj<</Type/Page>>endobj\ntrailer<</Encrypt 9 0 R/Root 1 0 R>>\n%%EOF\n";

        Assert.Throws<AppException>(() => PdfPageCounter.Count(Bytes(encrypted)));
    }

    [Fact]
    public void reports_one_page_rather_than_corrupt_when_the_tree_is_unreadable()
    {
        // A header-bearing file we cannot decode is NOT called corrupt: under-counting
        // only relaxes the page cap, whereas a false invalid_pdf rejects a document
        // the reference server accepts, which the user cannot work around.
        var opaque = "%PDF-1.7\n" + new string('ÿ', 400) + "\n%%EOF\n";

        Assert.Equal(1, PdfPageCounter.Count(Bytes(opaque)));
    }

    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);
}
