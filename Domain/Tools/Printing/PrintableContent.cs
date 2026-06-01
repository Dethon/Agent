using System.Text;

namespace Domain.Tools.Printing;

// Detects a document's format from its leading magic bytes so the print queue can reject content
// the printer cannot render (which would otherwise print as gibberish). Returns a lowercase token.
public static class PrintableContent
{
    public static string DetectFormat(ReadOnlySpan<byte> bytes)
    {
        if (HasPrefix(bytes, [0xFF, 0xD8, 0xFF]))
        {
            return "jpeg";
        }

        if (HasPrefix(bytes, [0x89, 0x50, 0x4E, 0x47]))
        {
            return "png";
        }

        if (HasPrefix(bytes, "%PDF"u8))
        {
            return "pdf";
        }

        if (HasPrefix(bytes, "GIF8"u8))
        {
            return "gif";
        }

        if (HasPrefix(bytes, "BM"u8))
        {
            return "bmp";
        }

        if (HasPrefix(bytes, [0x49, 0x49, 0x2A, 0x00]) || HasPrefix(bytes, [0x4D, 0x4D, 0x00, 0x2A]))
        {
            return "tiff";
        }

        if (HasPrefix(bytes, "RaS2"u8) || HasPrefix(bytes, "RaS3"u8) || HasPrefix(bytes, "2SaR"u8) || HasPrefix(bytes, "3SaR"u8))
        {
            return "pwg-raster";
        }

        if (HasPrefix(bytes, "UNIRAST"u8))
        {
            return "urf";
        }

        if (bytes.Length > 0 && bytes[0] == 0x1B)
        {
            return "pcl";
        }

        return IsUtf8Text(bytes) ? "text" : "unknown";
    }

    public static bool IsSupported(string format, string supportedCsv) =>
        (supportedCsv ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(s => s.Equals(format, StringComparison.OrdinalIgnoreCase));

    private static bool HasPrefix(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> prefix) =>
        bytes.Length >= prefix.Length && bytes[..prefix.Length].SequenceEqual(prefix);

    private static bool IsUtf8Text(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IndexOf((byte)0) >= 0)
        {
            return false;
        }

        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}