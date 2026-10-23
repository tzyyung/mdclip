using System.Globalization;
using System.Text;

namespace MdViewer;

// Reading the files we show and search, in one place.
static class TextFiles
{
    static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);
    static readonly Lazy<Encoding> Legacy = new(LegacyEncoding);

    // The whole file, even while an editor has it open for writing.
    public static byte[] ReadBytes(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    public static string ReadText(string path) => Decode(ReadBytes(path));

    // UTF-8 (with or without BOM) or UTF-16 with BOM; anything else that is not valid UTF-8 was saved by an
    // older editor in the language's legacy code page (Big5 for Traditional Chinese), or in `legacy`.
    public static string Decode(byte[] bytes, Encoding? legacy = null)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes is [0xFE, 0xFF, ..]) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return (legacy ?? Legacy.Value).GetString(bytes); }
    }

    // A Windows code page such as 950 (Big5).
    public static Encoding CodePage(int page)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(page);
    }

    // From the user's language rather than the system code page, which is UTF-8 itself when Windows'
    // "use Unicode UTF-8 for worldwide language support" is on (as here). Big5 when the language has none:
    // Traditional Chinese documents are what this viewer mostly shows.
    static Encoding LegacyEncoding()
    {
        var page = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        try { return CodePage(page is 0 or 65001 ? 950 : page); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return Encoding.Latin1; }
    }
}
