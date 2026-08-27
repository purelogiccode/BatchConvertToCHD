namespace CCDSharp.Models;

/// <summary>
/// The mode of a track as defined in the CloneCD .ccd file.
/// Maps to the MODE field in [TRACK N] sections.
/// </summary>
public enum TrackMode
{
    /// <summary>
    /// Audio track (Red Book CD-DA). 2352 bytes per sector of PCM audio.
    /// </summary>
    Audio = 0,

    /// <summary>
    /// Mode 1 data track (CD-ROM). 2352 bytes raw, 2048 bytes user data.
    /// </summary>
    Mode1 = 1,

    /// <summary>
    /// Mode 2 data track (CD-XA / CDI). 2352 bytes raw, variable user data.
    /// </summary>
    Mode2 = 2,
}