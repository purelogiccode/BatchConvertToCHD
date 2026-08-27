using System.Runtime.InteropServices;

namespace CSOSharp.Models;

/// <summary>
/// Represents the header of a CSO/CISO file.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct CsoHeader
{
    /// <summary>
    /// The CISO magic bytes: 0x4F534943 ("CISO" in ASCII).
    /// </summary>
    public const uint MagicValue = 0x4F534943;

    /// <summary>
    /// The expected header size (24 bytes).
    /// </summary>
    public const int ExpectedHeaderSize = 24;

    /// <summary>
    /// The CISO magic identifier read from the file.
    /// </summary>
    public uint Magic { get; }

    /// <summary>
    /// The header size in bytes (typically 24).
    /// </summary>
    public uint HeaderSize { get; }

    /// <summary>
    /// The total uncompressed size of the ISO image in bytes.
    /// </summary>
    public ulong UncompressedSize { get; }

    /// <summary>
    /// The size of each uncompressed block in bytes (typically 2048).
    /// </summary>
    public uint BlockSize { get; }

    /// <summary>
    /// The CSO format version (1 = deflate/zlib, 2 = LZ4).
    /// </summary>
    public byte Version { get; }

    /// <summary>
    /// The number of bits to left-shift index entries to get the real file offset.
    /// </summary>
    public byte IndexOffsetShift { get; }

    /// <summary>
    /// The total number of data blocks in the ISO image.
    /// </summary>
    public uint TotalBlocks { get; }

    /// <summary>
    /// Whether the header magic is valid.
    /// </summary>
    public bool IsValid => Magic == MagicValue;

    /// <summary>
    /// Whether this is a CSO v1 file using deflate/zlib compression.
    /// </summary>
    public bool IsV1 => Version == 1;

    /// <summary>
    /// Whether this is a CSO v2/ZSO file using LZ4 compression.
    /// </summary>
    public bool IsV2 => Version == 2;

    internal CsoHeader(
        uint magic,
        uint headerSize,
        ulong uncompressedSize,
        uint blockSize,
        byte version,
        byte indexOffsetShift
    )
    {
        Magic = magic;
        HeaderSize = headerSize;
        UncompressedSize = uncompressedSize;
        BlockSize = blockSize;
        Version = version;
        IndexOffsetShift = indexOffsetShift;
        TotalBlocks = blockSize > 0 ? (uint)(uncompressedSize / blockSize) : 0;
    }
}