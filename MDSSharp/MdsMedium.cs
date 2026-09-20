namespace MDSSharp;

/// <summary>
///     The medium type recorded in the MDS header. Values are the ones Alcohol writes; anything else
///     is reported as <see cref="Unknown" /> and handled by inspecting the sector sizes instead.
/// </summary>
public enum MdsMedium
{
    /// <summary>No recognised medium type in the descriptor.</summary>
    Unknown = -1,

    /// <summary>A pressed CD-ROM.</summary>
    Cd = 0x00,

    /// <summary>A CD-R.</summary>
    CdR = 0x01,

    /// <summary>A CD-RW.</summary>
    CdRw = 0x02,

    /// <summary>A pressed DVD-ROM.</summary>
    Dvd = 0x10,

    /// <summary>A DVD-R.</summary>
    DvdMinusR = 0x12
}