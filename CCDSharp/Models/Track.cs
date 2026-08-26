namespace CCDSharp.Models;

/// <summary>
/// Represents a single track within a CloneCD disc image.
/// </summary>
public sealed class Track
{
    /// <summary>
    /// The track number (1-based).
    /// </summary>
    public int Number { get; internal set; }

    /// <summary>
    /// The track mode (Audio, Mode1, Mode2).
    /// </summary>
    public TrackMode Mode { get; internal set; }

    /// <summary>
    /// The track's index points. Key is the index number (0, 1, 2...), value is the LBA sector offset.
    /// Index 0 = pregap start, Index 1 = track start.
    /// </summary>
    public IDictionary<int, int> Indexes { get; internal set; } = new Dictionary<int, int>();

    /// <summary>
    /// FLAGS from the CCD file (DCP, 4CH, PRE, SCMS). May be null if not specified.
    /// </summary>
    public string? Flags { get; internal set; }

    /// <summary>
    /// ISRC (International Standard Recording Code). May be null if not specified.
    /// </summary>
    public string? Isrc { get; internal set; }

    /// <summary>
    /// Gets the LBA offset of INDEX 01 (the track start).
    /// Returns -1 if INDEX 01 is not present.
    /// </summary>
    public int Index01Lba => Indexes.TryGetValue(1, out var lba) ? lba : -1;

    /// <summary>
    /// Gets the CUE track type string for this track.
    /// </summary>
    public string CueTrackType =>
        Mode switch
        {
            TrackMode.Audio => "AUDIO",
            TrackMode.Mode1 => "MODE1/2352",
            TrackMode.Mode2 => "MODE2/2352",
            _ => "MODE1/2352",
        };

    /// <summary>
    /// Whether this track is an audio track.
    /// </summary>
    public bool IsAudio => Mode == TrackMode.Audio;
}
