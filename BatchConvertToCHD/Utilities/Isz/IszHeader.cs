using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace BatchConvertToCHD.Utilities.Isz;

/// <summary>
/// The header at the front of every ISZ segment file, as defined by EZB Systems' ISZ File Format
/// Specification 1.00.
///
/// The fields are written packed and little-endian with no alignment padding, which no C# struct
/// layout reproduces reliably, so each one is read at its documented offset instead. Offsets are
/// spelled out in <see cref="TryRead"/> because a single wrong one silently produces a plausible
/// but useless image.
/// </summary>
/// <param name="HeaderSize">Header length in bytes, 48 for version 1.</param>
/// <param name="Version">Format version.</param>
/// <param name="VolumeSerialNumber">Identifies segments as belonging to the same image.</param>
/// <param name="SectorSize">Bytes per sector of the stored image, 2048 for an ISO.</param>
/// <param name="TotalSectors">Sectors in the stored image.</param>
/// <param name="PasswordMode">Encryption in use: 0 none, 1 password, 2-4 AES 128/192/256.</param>
/// <param name="SegmentSize">Segment size in bytes the image was split at.</param>
/// <param name="ChunkCount">Number of chunks, and so the number of chunk table entries.</param>
/// <param name="ChunkSize">Uncompressed bytes per chunk.</param>
/// <param name="PointerLength">Bytes per chunk table entry.</param>
/// <param name="SegmentNumber">Which segment this file is.</param>
/// <param name="ChunkTableOffset">Offset of the chunk table, or 0 when there is none.</param>
/// <param name="SegmentTableOffset">Offset of the segment table, or 0 when the image is whole.</param>
/// <param name="DataOffset">Offset of the first chunk's data in this file.</param>
internal sealed record IszHeader(
    int HeaderSize,
    int Version,
    uint VolumeSerialNumber,
    int SectorSize,
    uint TotalSectors,
    int PasswordMode,
    long SegmentSize,
    uint ChunkCount,
    uint ChunkSize,
    int PointerLength,
    int SegmentNumber,
    uint ChunkTableOffset,
    uint SegmentTableOffset,
    uint DataOffset)
{
    /// <summary>The four bytes every ISZ file opens with.</summary>
    internal const string Signature = "IsZ!";

    /// <summary>Header length for version 1, and the number of bytes <see cref="TryRead"/> needs.</summary>
    internal const int Length = 48;

    /// <summary>Largest chunk table entry width that can be read into a 32-bit value.</summary>
    private const int MaxPointerLength = 4;

    /// <summary>
    /// A chunk table entry cannot describe more than a chunk's worth of data, and the app has to
    /// allocate a buffer of this size, so an implausible value is rejected rather than trusted.
    /// </summary>
    private const uint MaxChunkSize = 64 * 1024 * 1024;

    /// <summary>Uncompressed size of the image this header describes.</summary>
    internal long ImageSizeBytes => TotalSectors * SectorSize;

    /// <summary>True when chunk data is encrypted and cannot be read without the password.</summary>
    internal bool IsEncrypted => PasswordMode != 0;

    /// <summary>True when the image was split across several files.</summary>
    internal bool IsSegmented => SegmentTableOffset != 0;

    /// <summary>How the encryption in use should be described to the user.</summary>
    internal string EncryptionDescription => PasswordMode switch
    {
        0 => "none",
        1 => "password",
        2 => "AES-128",
        3 => "AES-192",
        4 => "AES-256",
        _ => "an unrecognised method (" + PasswordMode.ToString(CultureInfo.InvariantCulture) + ")"
    };

    /// <summary>A one-line summary for the log.</summary>
    internal string Summary =>
        $"version {Version.ToString(CultureInfo.InvariantCulture)}, {TotalSectors.ToString("N0", CultureInfo.InvariantCulture)} x {SectorSize.ToString(CultureInfo.InvariantCulture)}-byte sectors = {ImageSizeBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes, {ChunkCount.ToString("N0", CultureInfo.InvariantCulture)} chunks of {ChunkSize.ToString("N0", CultureInfo.InvariantCulture)} bytes";

    /// <summary>True when <paramref name="header"/> opens with the ISZ signature.</summary>
    /// <param name="header">Leading bytes of a file.</param>
    internal static bool HasSignature(ReadOnlySpan<byte> header)
    {
        return header.Length >= Signature.Length &&
               Encoding.ASCII.GetString(header[..Signature.Length]).Equals(Signature, StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses a header from <paramref name="header"/>, or returns null when the bytes are not an
    /// ISZ header at all.
    /// </summary>
    /// <param name="header">At least <see cref="Length"/> bytes from the front of the file.</param>
    internal static IszHeader? TryRead(ReadOnlySpan<byte> header)
    {
        if (header.Length < Length || !HasSignature(header))
        {
            return null;
        }

        return new IszHeader(
            HeaderSize: header[4],
            Version: header[5],
            VolumeSerialNumber: BinaryPrimitives.ReadUInt32LittleEndian(header[6..]),
            SectorSize: BinaryPrimitives.ReadUInt16LittleEndian(header[10..]),
            TotalSectors: BinaryPrimitives.ReadUInt32LittleEndian(header[12..]),
            PasswordMode: header[16],
            SegmentSize: BinaryPrimitives.ReadInt64LittleEndian(header[17..]),
            ChunkCount: BinaryPrimitives.ReadUInt32LittleEndian(header[25..]),
            ChunkSize: BinaryPrimitives.ReadUInt32LittleEndian(header[29..]),
            PointerLength: header[33],
            SegmentNumber: header[34],
            ChunkTableOffset: BinaryPrimitives.ReadUInt32LittleEndian(header[35..]),
            SegmentTableOffset: BinaryPrimitives.ReadUInt32LittleEndian(header[39..]),
            DataOffset: BinaryPrimitives.ReadUInt32LittleEndian(header[43..]));
    }

    /// <summary>
    /// Returns why this header cannot be decompressed, or null when it can. The reason is written
    /// for the log, so it says what the user has to do about it.
    /// </summary>
    internal string? GetUnusableReason()
    {
        if (IsEncrypted)
        {
            return
                $"the ISZ image is encrypted ({EncryptionDescription}) and this tool cannot decrypt it. Open it in UltraISO with the password and save it as an ISO first.";
        }

        if (SectorSize <= 0 || TotalSectors == 0)
        {
            return
                $"the ISZ header declares {TotalSectors.ToString("N0", CultureInfo.InvariantCulture)} sectors of {SectorSize.ToString(CultureInfo.InvariantCulture)} bytes, which describes no image. The file header is damaged.";
        }

        if (ChunkCount == 0 || ChunkSize == 0)
        {
            return "the ISZ header declares no chunks, so there is nothing to decompress. The file header is damaged.";
        }

        if (ChunkSize > MaxChunkSize)
        {
            return
                $"the ISZ header declares a chunk size of {ChunkSize.ToString("N0", CultureInfo.InvariantCulture)} bytes, which is far larger than any real image uses. The file header is damaged.";
        }

        if (PointerLength is < 1 or > MaxPointerLength)
        {
            return
                $"the ISZ chunk table uses {PointerLength.ToString(CultureInfo.InvariantCulture)}-byte entries, which this build cannot read.";
        }

        if (ChunkTableOffset == 0)
        {
            return "the ISZ file has no chunk table, so its data cannot be located. The file header is damaged.";
        }

        return null;
    }
}