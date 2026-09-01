namespace Alcohol120Sharp;

/// <summary>
///     An Alcohol 120% image described by a .mds descriptor and backed by a .mdf data file.
/// </summary>
/// <param name="SessionCount">Sessions recorded in the descriptor.</param>
/// <param name="Tracks">Tracks in descriptor order, lead-in and lead-out entries removed.</param>
/// <param name="MdsPath">Path of the .mds descriptor.</param>
/// <param name="MdfPath">Path of the .mdf data file, or null when it could not be found.</param>
public sealed record MdsDisc(
    int SessionCount,
    IReadOnlyList<MdsTrack> Tracks,
    string MdsPath,
    string? MdfPath
)
{
    /// <summary>Raw CD sector size.</summary>
    public const int RawSectorSize = 2352;

    /// <summary>Raw CD sector plus 96 bytes of subchannel data, as Alcohol commonly rips.</summary>
    public const int RawPlusSubchannelSize = 2448;

    /// <summary>Raw CD sector plus 16 bytes of subchannel data.</summary>
    public const int RawPlusShortSubchannelSize = 2368;

    /// <summary>Cooked 2048-byte sectors, meaning the .mdf is really a DVD/ISO image.</summary>
    public const int CookedSectorSize = 2048;

    /// <summary>
    ///     Sector size for the image. Taken from the first track: a mixed-size disc is not something
    ///     Alcohol produces, and a single figure is what the stripping and cue writing need.
    /// </summary>
    public int SectorSize => Tracks.Count > 0 ? Tracks[0].SectorSize : 0;

    /// <summary>True when the .mdf holds 2048-byte sectors and should be converted as a DVD image.</summary>
    public bool IsDvdImage => SectorSize == CookedSectorSize;

    /// <summary>True when every sector carries trailing subchannel bytes that chdman will not read.</summary>
    public bool NeedsSubchannelStrip =>
        SectorSize is RawPlusSubchannelSize or RawPlusShortSubchannelSize;

    /// <summary>True when the sectors are already the plain 2352 bytes a cue can describe.</summary>
    public bool IsPlainRawCd => SectorSize == RawSectorSize;

    /// <summary>True when every track's mode maps to something a cue can express.</summary>
    public bool AllTracksDescribable =>
        Tracks.Count > 0 && Tracks.All(static t => t.CueTrackType is not null);

    /// <summary>A one-line summary for the log.</summary>
    public string Summary =>
        $"{Tracks.Count} track(s), {SectorSize} bytes/sector: {string.Join(" ", Tracks.Select(static t => t.Description))}";
}