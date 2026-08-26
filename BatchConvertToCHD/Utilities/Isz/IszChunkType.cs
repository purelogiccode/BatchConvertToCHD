namespace BatchConvertToCHD.Utilities.Isz;

/// <summary>
/// How one ISZ chunk was stored, taken from the top two bits of its chunk table entry.
///
/// The spec names these ADI_ZERO (0x00), ADI_DATA (0x40), ADI_ZLIB (0x80) and ADI_BZ2 (0xC0),
/// which are the flag byte values before the two bits are shifted down.
/// </summary>
internal enum IszChunkType
{
    /// <summary>ADI_ZERO: the chunk is all zero bytes and nothing is stored for it.</summary>
    Zero = 0,

    /// <summary>ADI_DATA: stored verbatim.</summary>
    Stored = 1,

    /// <summary>ADI_ZLIB: deflate inside a zlib wrapper.</summary>
    ZLib = 2,

    /// <summary>ADI_BZ2: bzip2.</summary>
    BZip2 = 3,
}