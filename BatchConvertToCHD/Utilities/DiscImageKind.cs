namespace BatchConvertToCHD.Utilities;

/// <summary>What a file turned out to be once its leading bytes were read.</summary>
internal enum DiscImageKind
{
    /// <summary>Nothing recognisable.</summary>
    Unknown,

    /// <summary>Raw CD sectors: the 12-byte sync mark is present.</summary>
    RawCd,

    /// <summary>Alcohol 120% .mds descriptor.</summary>
    AlcoholDescriptor,

    /// <summary>A real RAR archive.</summary>
    Rar,

    /// <summary>A real ZIP archive.</summary>
    Zip,

    /// <summary>A real 7-Zip archive.</summary>
    SevenZip,

    /// <summary>A genuinely compressed ISZ image.</summary>
    Isz,

    /// <summary>An ECM-encoded image.</summary>
    Ecm,

    /// <summary>A CISO/ZISO compressed image.</summary>
    Cso,

    /// <summary>A PSP/PSX EBOOT.PBP.</summary>
    Pbp,

    /// <summary>An existing CHD.</summary>
    Chd
}