namespace MDSSharp;

/// <summary>
///     An Alcohol 120% image described by a .mds descriptor and backed by a .mdf data file.
/// </summary>
/// <param name="SessionCount">Sessions recorded in the descriptor.</param>
/// <param name="Tracks">Tracks in descriptor order, lead-in and lead-out entries removed.</param>
/// <param name="MdsPath">Path of the .mds descriptor.</param>
/// <param name="MdfPath">
///     Path of the first data file (or the .i00 first volume of a split set), or null when none could be found.
/// </param>
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

    /// <summary>Cooked 2048-byte sectors, as stored for a DVD or a cooked CD data track.</summary>
    public const int CookedSectorSize = 2048;

    /// <summary>Cooked Mode 2 sector without its sync, header and EDC fields.</summary>
    public const int Mode2XaSectorSize = 2336;

    /// <summary>The medium type the descriptor declares, or <see cref="MdsMedium.Unknown" />.</summary>
    public MdsMedium MediumType { get; init; } = MdsMedium.Unknown;

    /// <summary>
    ///     Data files the descriptor references, in track order. Usually one file; a descriptor may
    ///     split the data across several files, which concatenate in this order to form the image.
    /// </summary>
    public IReadOnlyList<string> DataFilePaths { get; init; } = [];

    /// <summary>True when the descriptor declares DVD media.</summary>
    public bool IsDvdMedia => MediumType is MdsMedium.Dvd or MdsMedium.DvdMinusR;

    /// <summary>True when the descriptor declares CD media.</summary>
    public bool IsCdMedia => MediumType is MdsMedium.Cd or MdsMedium.CdR or MdsMedium.CdRw;

    /// <summary>
    ///     Sector size for the image. Taken from the first track: a mixed-size disc is not something
    ///     Alcohol produces, and a single figure is what the stripping and cue writing need.
    /// </summary>
    public int SectorSize => Tracks.Count > 0 ? Tracks[0].SectorSize : 0;

    /// <summary>
    ///     True when the data file is a DVD image: the descriptor says so, or it is an older descriptor
    ///     with no medium type whose sectors are the cooked 2048 bytes a DVD uses.
    /// </summary>
    public bool IsDvdImage =>
        IsDvdMedia || (MediumType == MdsMedium.Unknown && SectorSize == CookedSectorSize);

    /// <summary>True when a CD image stores cooked 2048-byte sectors and needs a MODE1/2048 cue.</summary>
    public bool IsCookedCd => IsCdMedia && SectorSize == CookedSectorSize;

    /// <summary>True when every sector carries trailing subchannel bytes that chdman will not read.</summary>
    public bool NeedsSubchannelStrip =>
        SectorSize is RawPlusSubchannelSize or RawPlusShortSubchannelSize;

    /// <summary>True when the sectors are already the plain 2352 bytes a cue can describe.</summary>
    public bool IsPlainRawCd => SectorSize == RawSectorSize;

    /// <summary>True when every track's mode maps to something a cue can express.</summary>
    public bool AllTracksDescribable =>
        Tracks.Count > 0 && Tracks.All(static t => t.CueTrackType is not null);

    /// <summary>
    ///     True when the descriptor records a length for every track and at least one track has a
    ///     pregap, which is what the pregap handling needs to place INDEX 00.
    /// </summary>
    public bool HasPregapInfo =>
        Tracks.Count > 0
        && Tracks.All(static t => t.LengthSectors > 0)
        && Tracks.Any(static t => t.PregapSectors > 0);

    /// <summary>A one-line summary for the log.</summary>
    public string Summary =>
        $"{Tracks.Count} track(s), {SectorSize} bytes/sector: {string.Join(" ", Tracks.Select(static t => t.Description))}";
}