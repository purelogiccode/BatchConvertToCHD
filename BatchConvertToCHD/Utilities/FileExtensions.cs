namespace BatchConvertToCHD.Utilities;

/// <summary>
/// Centralized constants for file extensions used throughout the application.
/// </summary>
internal static class FileExtensions
{
    /// <summary>
    /// String comparer for ordinal case-insensitive extension comparisons.
    /// </summary>
    private static readonly StringComparer ExtensionComparer = StringComparer.OrdinalIgnoreCase;

    // Disc image formats
    internal const string Cue = ".cue";
    internal const string Iso = ".iso";
    internal const string Img = ".img";
    internal const string Gdi = ".gdi";
    internal const string Toc = ".toc";
    internal const string Raw = ".raw";
    internal const string Ccd = ".ccd";
    internal const string Sub = ".sub";
    internal const string Bin = ".bin";
    internal const string Mds = ".mds";
    internal const string Mdf = ".mdf";
    internal const string Ecm = ".ecm";

    // First volumes of split disc images. Later parts (.002, .i01, ...) are found from the first
    // one and are deliberately not listed, so a set is only ever offered as a single input.
    internal const string SplitFirstNumbered = ".001";
    internal const string SplitFirstAlcohol = ".i00";

    // Archive formats
    internal const string Zip = ".zip";
    internal const string SevenZip = ".7z";
    internal const string Rar = ".rar";

    // Compressed disc image formats
    internal const string Cso = ".cso";
    internal const string Pbp = ".pbp";
    internal const string Isz = ".isz";

    // Output format
    internal const string Chd = ".chd";

    // Extraction outputs (chdman fallback)
    internal const string Avi = ".avi";

    /// <summary>
    /// All supported input extensions for conversion.
    /// </summary>
    /// <remarks>
    /// .bin is included because a disc is often distributed as a bare raw .bin with no cue at all.
    /// A cue is generated for it at conversion time. When a sibling descriptor does cover the .bin
    /// it is dropped from the batch by <see cref="InputFileFilter"/>, so split-track sets still
    /// convert once, through their cue.
    /// </remarks>
    internal static readonly string[] AllSupportedInputExtensionsForConversion =
    [
        Cue, Iso, Img, Gdi, Toc, Raw, Ccd, Bin, Mds, Ecm, Isz, SplitFirstNumbered, SplitFirstAlcohol, Zip, SevenZip,
        Rar, Cso, Pbp
    ];

    /// <summary>
    /// HashSet of all supported input extensions for efficient case-insensitive lookups.
    /// </summary>
    internal static readonly HashSet<string> AllSupportedInputExtensionsForConversionSet =
        new(AllSupportedInputExtensionsForConversion, ExtensionComparer);

    /// <summary>
    /// Archive file extensions.
    /// </summary>
    internal static readonly string[] ArchiveExtensions =
    [
        Zip, SevenZip, Rar
    ];

    /// <summary>
    /// HashSet of archive extensions for efficient case-insensitive lookups.
    /// </summary>
    internal static readonly HashSet<string> ArchiveExtensionsSet =
        new(ArchiveExtensions, ExtensionComparer);

    /// <summary>
    /// Primary target extensions for extraction from archives.
    /// </summary>
    /// <remarks>
    /// .isz is included because an archived ISZ is decompressed in place by the conversion loop, so
    /// an archive holding one is convertible rather than reported as containing nothing supported.
    /// </remarks>
    internal static readonly string[] PrimaryTargetExtensions =
    [
        Cue, Iso, Img, Gdi, Toc, Raw, Ccd, Mds, Isz
    ];

    /// <summary>
    /// HashSet of primary target extensions for efficient case-insensitive lookups.
    /// </summary>
    internal static readonly HashSet<string> PrimaryTargetExtensionsSet =
        new(PrimaryTargetExtensions, ExtensionComparer);
}