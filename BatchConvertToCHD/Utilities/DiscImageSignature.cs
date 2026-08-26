using System.IO;
using System.Text;

namespace BatchConvertToCHD.Utilities;

/// <summary>
/// Identifies files by their leading bytes instead of trusting the extension.
///
/// Collections are full of files whose name does not match their content: raw CD dumps saved as
/// .iso, disc images renamed .rar, byte-splits given archive extensions, and images called .isz
/// that were never compressed. Reading the magic bytes settles what a file really is, which both
/// routes it correctly and lets the log say something true when it cannot be converted.
/// </summary>
internal static class DiscImageSignature
{
    /// <summary>Bytes read from the front of a file; enough for every signature below.</summary>
    private const int HeaderLength = 32;

    /// <summary>The 12-byte sync pattern that opens every raw CD sector.</summary>
    private static readonly byte[] CdSyncMark =
        [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    /// <summary>
    /// Reads <paramref name="path"/> and reports what it appears to be.
    /// </summary>
    /// <param name="path">File to inspect.</param>
    internal static DiscImageKind Detect(string path)
    {
        try
        {
            var header = new byte[HeaderLength];
            int read;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            }

            return Classify(header.AsSpan(0, read));
        }
        catch (Exception)
        {
            return DiscImageKind.Unknown;
        }
    }

    /// <summary>
    /// Classifies an already-read header. Separated from <see cref="Detect"/> so it can be tested
    /// without touching the filesystem.
    /// </summary>
    /// <param name="header">Leading bytes of the file.</param>
    internal static DiscImageKind Classify(ReadOnlySpan<byte> header)
    {
        if (header.Length >= CdSyncMark.Length && header[..CdSyncMark.Length].SequenceEqual(CdSyncMark))
        {
            return DiscImageKind.RawCd;
        }

        if (StartsWithAscii(header, "MEDIA DESCRIPTOR"))
        {
            return DiscImageKind.AlcoholDescriptor;
        }

        if (StartsWithAscii(header, "Rar!"))
        {
            return DiscImageKind.Rar;
        }

        switch (header.Length)
        {
            // Local file header, central directory, or a spanned/empty archive marker.
            case >= 4 when header[0] == 0x50 && header[1] == 0x4B &&
                           (header[2] is 0x03 or 0x05 or 0x07):
                return DiscImageKind.Zip;
            case >= 6 when header[0] == 0x37 && header[1] == 0x7A &&
                           header[2] == 0xBC && header[3] == 0xAF && header[4] == 0x27 && header[5] == 0x1C:
                return DiscImageKind.SevenZip;
        }

        if (StartsWithAscii(header, "IsZ!"))
        {
            return DiscImageKind.Isz;
        }

        if (header.Length >= 4 && header[0] == 0x45 && header[1] == 0x43 && header[2] == 0x4D && header[3] == 0x00)
        {
            return DiscImageKind.Ecm;
        }

        if (StartsWithAscii(header, "CISO") || StartsWithAscii(header, "ZISO"))
        {
            return DiscImageKind.Cso;
        }

        if (header.Length >= 4 && header[0] == 0x00 && header[1] == 0x50 && header[2] == 0x42 && header[3] == 0x50)
        {
            return DiscImageKind.Pbp;
        }

        if (StartsWithAscii(header, "MComprHD"))
        {
            return DiscImageKind.Chd;
        }

        return DiscImageKind.Unknown;
    }

    /// <summary>True when the kind is an archive container the app knows how to extract.</summary>
    /// <param name="kind">Detected kind.</param>
    internal static bool IsArchive(DiscImageKind kind)
    {
        return kind is DiscImageKind.Rar or DiscImageKind.Zip or DiscImageKind.SevenZip;
    }

    /// <summary>
    /// A human-readable description used when a file's content contradicts its extension.
    /// </summary>
    /// <param name="kind">Detected kind.</param>
    internal static string Describe(DiscImageKind kind)
    {
        return kind switch
        {
            DiscImageKind.RawCd => "a raw CD disc image",
            DiscImageKind.AlcoholDescriptor => "an Alcohol 120% .mds descriptor",
            DiscImageKind.Rar => "a RAR archive",
            DiscImageKind.Zip => "a ZIP archive",
            DiscImageKind.SevenZip => "a 7-Zip archive",
            DiscImageKind.Isz => "a compressed ISZ image",
            DiscImageKind.Ecm => "an ECM-encoded image",
            DiscImageKind.Cso => "a CISO/ZISO compressed image",
            DiscImageKind.Pbp => "a PlayStation EBOOT.PBP",
            DiscImageKind.Chd => "an existing CHD",
            _ => "an unrecognised format"
        };
    }

    private static bool StartsWithAscii(ReadOnlySpan<byte> header, string signature)
    {
        if (header.Length < signature.Length)
        {
            return false;
        }

        return Encoding.ASCII.GetString(header[..signature.Length]).Equals(signature, StringComparison.Ordinal);
    }
}