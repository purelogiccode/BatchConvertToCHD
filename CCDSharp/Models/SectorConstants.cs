namespace CCDSharp.Models;

/// <summary>
///     Constants for CD sector sizes and layout.
/// </summary>
public static class SectorConstants
{
    /// <summary>
    ///     Raw CD sector size in bytes (including sync, header, data, EDC/ECC).
    /// </summary>
    public const int RawSectorSize = 2352;

    /// <summary>
    ///     Standard ISO 9660 user data size per sector.
    /// </summary>
    public const int UserDataSize = 2048;

    /// <summary>
    ///     Offset of the mode byte within a raw sector (after 12-byte sync + 3-byte MSF header).
    /// </summary>
    public const int ModeOffset = 15;

    /// <summary>
    ///     Offset of user data in a Mode 1 sector (12 sync + 4 header = 16 bytes).
    /// </summary>
    public const int Mode1DataOffset = 16;

    /// <summary>
    ///     Offset of user data in a Mode 2 Form 1 sector (12 sync + 4 header + 8 subheader = 24 bytes).
    /// </summary>
    public const int Mode2Form1DataOffset = 24;

    /// <summary>
    ///     Number of frames (sectors) per second in CD standard.
    /// </summary>
    public const int FramesPerSecond = 75;

    /// <summary>
    ///     Number of seconds per minute.
    /// </summary>
    public const int SecondsPerMinute = 60;

    /// <summary>
    ///     Number of frames per minute (75 * 60 = 4500).
    /// </summary>
    public const int FramesPerMinute = FramesPerSecond * SecondsPerMinute;

    /// <summary>
    ///     Standard lead-in offset in sectors (2 seconds = 150 frames).
    /// </summary>
    public const int LeadInSectors = 150;

    /// <summary>
    ///     Sync mark pattern: 12 bytes (00 FF FF FF FF FF FF FF FF FF FF 00).
    /// </summary>
    public static readonly byte[] SyncMark =
    [
        0x00,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0x00
    ];
}