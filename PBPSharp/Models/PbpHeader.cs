using System.Runtime.InteropServices;

namespace PBPSharp.Models;

/// <summary>
///     Represents the header of a PBP file containing offsets to embedded resources.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct PbpHeader
{
    /// <summary>
    ///     The PBP magic bytes: 0x50425000 ("\0PBP" in ASCII, read as little-endian uint32 from bytes 00 50 42 50).
    /// </summary>
    public const uint MagicValue = 0x50425000;

    /// <summary>
    ///     The PBP header size (36 bytes: 9 uint32 fields).
    /// </summary>
    public const int HeaderSize = 0x28;

    /// <summary>
    ///     Offset of PARAM.SFO in the PBP file (should always be 0x28).
    /// </summary>
    public int SfoOffset { get; }

    /// <summary>
    ///     Offset of ICON0.PNG in the PBP file.
    /// </summary>
    public int Icon0Offset { get; }

    /// <summary>
    ///     Offset of ICON1.PMF or ICON1.PNG in the PBP file.
    /// </summary>
    public int Icon1Offset { get; }

    /// <summary>
    ///     Offset of PIC0.PNG in the PBP file.
    /// </summary>
    public int Pic0Offset { get; }

    /// <summary>
    ///     Offset of PIC1.PNG in the PBP file.
    /// </summary>
    public int Pic1Offset { get; }

    /// <summary>
    ///     Offset of SND0.AT3 in the PBP file.
    /// </summary>
    public int Snd0Offset { get; }

    /// <summary>
    ///     Offset of DATA.PSP in the PBP file.
    /// </summary>
    public int DataPspOffset { get; }

    /// <summary>
    ///     Offset of DATA.PSAR in the PBP file.
    /// </summary>
    public int DataPsarOffset { get; }

    /// <summary>
    ///     The version number read from the header.
    /// </summary>
    public uint Version { get; }

    /// <summary>
    ///     Whether the PBP magic is valid.
    /// </summary>
    public bool IsValid { get; }

    /// <summary>
    ///     Initializes a new instance of the <see cref="PbpHeader" /> struct from the raw PBP
    ///     header fields. Instances are created by <see cref="PbpFile.Open(string, out PbpFile?)" />
    ///     and <see cref="PbpFile.Open(Stream, bool, out PbpFile?)" />.
    /// </summary>
    /// <param name="version">The PBP format version from the header.</param>
    /// <param name="sfoOffset">Offset of PARAM.SFO.</param>
    /// <param name="icon0Offset">Offset of ICON0.PNG.</param>
    /// <param name="icon1Offset">Offset of ICON1.PMF or ICON1.PNG.</param>
    /// <param name="pic0Offset">Offset of PIC0.PNG.</param>
    /// <param name="pic1Offset">Offset of PIC1.PNG.</param>
    /// <param name="snd0Offset">Offset of SND0.AT3.</param>
    /// <param name="dataPspOffset">Offset of DATA.PSP.</param>
    /// <param name="dataPsarOffset">Offset of DATA.PSAR.</param>
    internal PbpHeader(
        uint version,
        int sfoOffset,
        int icon0Offset,
        int icon1Offset,
        int pic0Offset,
        int pic1Offset,
        int snd0Offset,
        int dataPspOffset,
        int dataPsarOffset
    )
    {
        Version = version;
        SfoOffset = sfoOffset;
        Icon0Offset = icon0Offset;
        Icon1Offset = icon1Offset;
        Pic0Offset = pic0Offset;
        Pic1Offset = pic1Offset;
        Snd0Offset = snd0Offset;
        DataPspOffset = dataPspOffset;
        DataPsarOffset = dataPsarOffset;
        IsValid = true;
    }
}