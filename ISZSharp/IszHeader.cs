using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ISZSharp;

/// <summary>
///     The header at the front of every ISZ segment file, as defined by EZB Systems' ISZ File Format
///     Specification 1.00, plus the four extra fields UltraISO writes after it.
///     The fields are written packed and little-endian with no alignment padding, which no C# struct
///     layout reproduces reliably, so each one is read at its documented offset instead. Offsets are
///     spelled out in <see cref="TryRead" /> because a single wrong one silently produces a plausible
///     but useless image.
///     UltraISO headers are 64 bytes: the 48-byte header the specification documents followed by a
///     CRC32 of the restored image, the stored data size, a reserved value and a CRC32 of the stored
///     data. Those fields are exposed when the header declares 64 bytes and are absent (null) for a
///     plain 48-byte header, whose trailing bytes belong to the segment or chunk table instead.
/// </summary>
/// <param name="HeaderSize">Header length in bytes, 48 for the specification's header or 64 with UltraISO's checksum fields.</param>
/// <param name="Version">Format version, 1 for every known writer.</param>
/// <param name="VolumeSerialNumber">Identifies segments as belonging to the same image.</param>
/// <param name="SectorSize">Bytes per sector of the stored image, 2048 for an ISO.</param>
/// <param name="TotalSectors">Sectors in the stored image.</param>
/// <param name="PasswordMode">Encryption in use: 0 none, 1 password, 2-4 AES 128/192/256.</param>
/// <param name="SegmentSize">Segment size in bytes the image was split at.</param>
/// <param name="ChunkCount">Number of chunks, the number of chunk table entries; 0 when the header also says there is no chunk table.</param>
/// <param name="ChunkSize">Uncompressed bytes per chunk.</param>
/// <param name="PointerLength">Bytes per chunk table entry.</param>
/// <param name="SegmentNumber">Which segment this file is; segment 1 carries 0.</param>
/// <param name="ChunkTableOffset">Offset of the chunk table, or 0 when there is none and the data is one uncompressed run.</param>
/// <param name="SegmentTableOffset">Offset of the segment table, or 0 when the image is whole.</param>
/// <param name="DataOffset">Offset of the first chunk's data in this file.</param>
/// <param name="UncompressedCrc">CRC32 of the restored image as UltraISO stores it, or null when the header has no checksum fields.</param>
/// <param name="DataSize">Total data size field UltraISO writes beside the checksums, or null.</param>
/// <param name="StoredCrc">CRC32 of the stored chunk data as UltraISO stores it, or null.</param>
public sealed record IszHeader(
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
    uint DataOffset,
    uint? UncompressedCrc = null,
    uint? DataSize = null,
    uint? StoredCrc = null
)
{
    /// <summary>The four bytes every ISZ file opens with.</summary>
    public const string Signature = "IsZ!";

    /// <summary>Header length the specification defines, and the number of bytes <see cref="TryRead" /> needs.</summary>
    public const int Length = 48;

    /// <summary>Header length UltraISO's checksum fields extend the specification's header to.</summary>
    public const int ExtendedLength = 64;

    /// <summary>Largest chunk table entry width that can be read into a 32-bit value.</summary>
    private const int MaxPointerLength = 4;

    /// <summary>
    ///     A chunk table entry cannot describe more than a chunk's worth of data, and the app has to
    ///     allocate a buffer of this size, so an implausible value is rejected rather than trusted.
    /// </summary>
    private const uint MaxChunkSize = 64 * 1024 * 1024;

    /// <summary>Uncompressed size of the image this header describes.</summary>
    public long ImageSizeBytes => TotalSectors * SectorSize;

    /// <summary>True when chunk data is encrypted and cannot be read without the password.</summary>
    public bool IsEncrypted => PasswordMode != 0;

    /// <summary>True when the image was split across several files.</summary>
    public bool IsSegmented => SegmentTableOffset != 0;

    /// <summary>True when the header carries UltraISO's checksum fields and the restored image can be verified against one.</summary>
    public bool HasChecksums => UncompressedCrc is not null || StoredCrc is not null;

    /// <summary>How the encryption in use should be described to the user.</summary>
    public string EncryptionDescription =>
        PasswordMode switch
        {
            0 => "none",
            1 => "password",
            2 => "AES-128",
            3 => "AES-192",
            4 => "AES-256",
            _ => "an unrecognised method ("
                 + PasswordMode.ToString(CultureInfo.InvariantCulture)
                 + ")"
        };

    /// <summary>A one-line summary for the log.</summary>
    public string Summary =>
        $"version {Version.ToString(CultureInfo.InvariantCulture)}, {TotalSectors.ToString("N0", CultureInfo.InvariantCulture)} x {SectorSize.ToString(CultureInfo.InvariantCulture)}-byte sectors = {ImageSizeBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes, {ChunkCount.ToString("N0", CultureInfo.InvariantCulture)} chunks of {ChunkSize.ToString("N0", CultureInfo.InvariantCulture)} bytes"
        + (HasChecksums ? ", with an UltraISO checksum" : string.Empty);

    /// <summary>True when <paramref name="header" /> opens with the ISZ signature.</summary>
    /// <param name="header">Leading bytes of a file.</param>
    public static bool HasSignature(ReadOnlySpan<byte> header)
    {
        return header.Length >= Signature.Length
               && Encoding
                   .ASCII.GetString(header[..Signature.Length])
                   .Equals(Signature, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Parses a header from <paramref name="header" />, or returns null when the bytes are not an
    ///     ISZ header at all.
    /// </summary>
    /// <param name="header">At least <see cref="Length" /> bytes from the front of the file; the checksum fields are read when <see cref="ExtendedLength" /> bytes are given and the header declares 64.</param>
    public static IszHeader? TryRead(ReadOnlySpan<byte> header)
    {
        if (header.Length < Length || !HasSignature(header)) return null;

        uint? uncompressedCrc = null;
        uint? dataSize = null;
        uint? storedCrc = null;

        // The four extra fields exist only when the header itself says it is longer. For a 48-byte
        // header these bytes are the start of the segment or chunk table, and reading them as
        // checksums would invent values.
        if (header[4] >= ExtendedLength && header.Length >= ExtendedLength)
        {
            uncompressedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[48..]);
            dataSize = BinaryPrimitives.ReadUInt32LittleEndian(header[52..]);

            // Bytes 56-59 are a reserved field between the size and the stored-data checksum.
            storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[60..]);
        }

        return new IszHeader(
            header[4],
            header[5],
            BinaryPrimitives.ReadUInt32LittleEndian(header[6..]),
            BinaryPrimitives.ReadUInt16LittleEndian(header[10..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[12..]),
            header[16],
            BinaryPrimitives.ReadInt64LittleEndian(header[17..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[25..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[29..]),
            header[33],
            header[34],
            BinaryPrimitives.ReadUInt32LittleEndian(header[35..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[39..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[43..]),
            uncompressedCrc,
            dataSize,
            storedCrc
        );
    }

    /// <summary>
    ///     Returns why this header cannot be decompressed, or null when it can. The reason is written
    ///     for the log, so it says what the user has to do about it.
    /// </summary>
    public string? GetUnusableReason()
    {
        if (IsEncrypted)
        {
            return
                $"the ISZ image is encrypted ({EncryptionDescription}) and this tool cannot decrypt it. Open it in UltraISO with the password and save it as an ISO first.";
        }

        if (Version != 1)
        {
            return
                $"the ISZ header declares format version {Version.ToString(CultureInfo.InvariantCulture)}, and only version 1 is understood. The file header is damaged.";
        }

        if (SegmentNumber != 0)
        {
            return
                $"this is segment {SegmentNumber.ToString(CultureInfo.InvariantCulture)} of a split image, not the first one. Open the .isz first segment and the rest will be found beside it.";
        }

        if (SectorSize <= 0 || TotalSectors == 0)
        {
            return
                $"the ISZ header declares {TotalSectors.ToString("N0", CultureInfo.InvariantCulture)} sectors of {SectorSize.ToString(CultureInfo.InvariantCulture)} bytes, which describes no image. The file header is damaged.";
        }

        if (ChunkSize == 0 || ChunkSize > MaxChunkSize)
        {
            return
                $"the ISZ header declares a chunk size of {ChunkSize.ToString("N0", CultureInfo.InvariantCulture)} bytes, which is not a size any real image uses. The file header is damaged.";
        }

        // A missing chunk table is legal - the specification says a zero pointer offset means none,
        // and the image is then a single uncompressed run. Its fields are only checked when present.
        if (ChunkTableOffset != 0)
        {
            if (ChunkCount == 0)
            {
                return
                    "the ISZ header declares a chunk table but no chunks, so there is nothing to decompress. The file header is damaged.";
            }

            if (PointerLength is < 1 or > MaxPointerLength)
            {
                return
                    $"the ISZ chunk table uses {PointerLength.ToString(CultureInfo.InvariantCulture)}-byte entries, which this build cannot read.";
            }
        }

        return null;
    }
}