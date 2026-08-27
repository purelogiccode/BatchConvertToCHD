namespace CCDSharp.Models;

/// <summary>
/// Represents a parsed CloneCD disc image (.ccd file) with its associated tracks.
/// </summary>
public sealed class DiscImage
{
    /// <summary>
    /// The CloneCD version from the .ccd file header.
    /// </summary>
    public int Version { get; internal set; }

    /// <summary>
    /// Number of TOC entries in the disc.
    /// </summary>
    public int TocEntries { get; internal set; }

    /// <summary>
    /// Number of sessions in the disc.
    /// </summary>
    public int Sessions { get; internal set; }

    /// <summary>
    /// Whether data tracks are scrambled.
    /// </summary>
    public bool DataTracksScrambled { get; internal set; }

    /// <summary>
    /// CD-TEXT data length.
    /// </summary>
    public int CdTextLength { get; internal set; }

    /// <summary>
    /// Media Catalog Number (MCN / barcode). May be null.
    /// </summary>
    public string? Catalog { get; internal set; }

    /// <summary>
    /// The tracks parsed from the .ccd file, ordered by track number.
    /// </summary>
    public IList<Track> Tracks { get; internal set; } = new List<Track>();

    /// <summary>
    /// The path to the .ccd file that was parsed.
    /// </summary>
    public string? FilePath { get; internal set; }

    /// <summary>
    /// The path to the associated .img data file.
    /// </summary>
    public string? ImgFilePath { get; internal set; }

    /// <summary>
    /// The path to the associated .sub subchannel file. May be null if not present.
    /// </summary>
    public string? SubFilePath { get; internal set; }
}