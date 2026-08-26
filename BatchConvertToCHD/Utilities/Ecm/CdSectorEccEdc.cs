using System.Buffers.Binary;

namespace BatchConvertToCHD.Utilities.Ecm;

/// <summary>
/// Regenerates the error detection and correction fields of a raw 2352-byte CD sector.
///
/// This is what ECM strips out: the EDC checksum and the Reed-Solomon P/Q parity are wholly
/// derivable from the sector's user data, so an encoder discards them and a decoder computes them
/// again. Getting the parity subtly wrong is the dangerous failure here, because the game data
/// still reads correctly while the image's hash never matches a known-good dump - so this is
/// verified byte for byte against Neill Corlett's original tool rather than by inspection.
///
/// The layout of a raw sector: 12 bytes of sync, a 4-byte address and mode header, then the mode's
/// own arrangement of user data, EDC and parity inside the remaining 2336 bytes.
/// </summary>
internal static class CdSectorEccEdc
{
    /// <summary>Bytes in a raw CD sector.</summary>
    internal const int SectorSize = 2352;

    /// <summary>Bytes in a CD sector without its sync and header, which is how Mode 2 is stored.</summary>
    internal const int Mode2DataSize = 2336;

    // Offsets within a raw sector.
    private const int SyncLength = 12;
    private const int AddressOffset = 0x0C;
    private const int UserDataOffset = 0x10;
    private const int Mode1EdcOffset = 0x810;
    private const int Mode1IntermediateOffset = 0x814;
    private const int Mode1IntermediateLength = 8;
    private const int Mode2Form1EdcOffset = 0x818;
    private const int Mode2Form2EdcOffset = 0x92C;
    private const int EccPOffset = 0x81C;
    private const int EccQOffset = 0x8C8;

    /// <summary>GF(2^8) multiply-by-two table, with the CD-ROM field polynomial 0x11D.</summary>
    private static readonly byte[] EccForwardLut = new byte[256];

    /// <summary>Inverse of <see cref="EccForwardLut"/>, used to finish a parity byte.</summary>
    private static readonly byte[] EccBackwardLut = new byte[256];

    /// <summary>Byte-wise table for the EDC CRC-32 variant, polynomial 0xD8018001, reflected.</summary>
    private static readonly uint[] EdcLut = new uint[256];

    static CdSectorEccEdc()
    {
        for (uint i = 0; i < 256; i++)
        {
            var doubled = (i << 1) ^ ((i & 0x80) != 0 ? 0x11Du : 0u);
            EccForwardLut[i] = (byte)doubled;
            EccBackwardLut[(i ^ doubled) & 0xFF] = (byte)i;

            var edc = i;
            for (var bit = 0; bit < 8; bit++)
            {
                edc = (edc >> 1) ^ ((edc & 1) != 0 ? 0xD8018001u : 0u);
            }

            EdcLut[i] = edc;
        }
    }

    /// <summary>
    /// Continues an EDC checksum over <paramref name="data"/>. Callers accumulate across the whole
    /// image, which is how an ECM file's trailing checksum is validated.
    /// </summary>
    /// <param name="edc">Checksum so far, 0 to start.</param>
    /// <param name="data">Bytes to fold in.</param>
    internal static uint ComputeEdc(uint edc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            edc = (edc >> 8) ^ EdcLut[(edc ^ value) & 0xFF];
        }

        return edc;
    }

    /// <summary>Writes the 12-byte sync pattern and mode byte that open every sector.</summary>
    /// <param name="sector">A full 2352-byte sector buffer.</param>
    /// <param name="mode">Sector mode, 1 or 2.</param>
    internal static void WriteSyncAndMode(Span<byte> sector, byte mode)
    {
        sector.Clear();
        sector[0] = 0x00;
        sector.Slice(1, SyncLength - 2).Fill(0xFF);
        sector[SyncLength - 1] = 0x00;
        sector[AddressOffset + 3] = mode;
    }

    /// <summary>
    /// Fills in the EDC, the intermediate zero field and the P/Q parity of a Mode 1 sector, whose
    /// user data occupies 0x010 to 0x80F.
    /// </summary>
    /// <param name="sector">A full 2352-byte sector with sync, header and user data already set.</param>
    internal static void GenerateMode1(Span<byte> sector)
    {
        WriteEdc(sector, 0x000, 0x810, Mode1EdcOffset);
        sector.Slice(Mode1IntermediateOffset, Mode1IntermediateLength).Clear();
        GenerateEcc(sector, zeroAddress: false);
    }

    /// <summary>
    /// Fills in the EDC and P/Q parity of a Mode 2 Form 1 sector, whose 8-byte subheader and user
    /// data occupy 0x010 to 0x817.
    /// </summary>
    /// <param name="sector">A full 2352-byte sector with sync, header, subheader and data set.</param>
    internal static void GenerateMode2Form1(Span<byte> sector)
    {
        WriteEdc(sector, UserDataOffset, 0x808, Mode2Form1EdcOffset);

        // Mode 2 parity is computed over a zeroed address, because a Form 1 sector's parity has to
        // stay valid when the sector is read without its header.
        GenerateEcc(sector, zeroAddress: true);
    }

    /// <summary>
    /// Fills in the EDC of a Mode 2 Form 2 sector. Form 2 carries no parity: the extra 276 bytes are
    /// user data, which is why it is used for streamed audio and video.
    /// </summary>
    /// <param name="sector">A full 2352-byte sector with sync, header, subheader and data set.</param>
    internal static void GenerateMode2Form2(Span<byte> sector)
    {
        WriteEdc(sector, UserDataOffset, 0x91C, Mode2Form2EdcOffset);
    }

    private static void WriteEdc(
        Span<byte> sector,
        int sourceOffset,
        int length,
        int destinationOffset
    )
    {
        var edc = ComputeEdc(0, sector.Slice(sourceOffset, length));
        BinaryPrimitives.WriteUInt32LittleEndian(sector[destinationOffset..], edc);
    }

    private static void GenerateEcc(Span<byte> sector, bool zeroAddress)
    {
        Span<byte> savedAddress = stackalloc byte[4];
        if (zeroAddress)
        {
            sector.Slice(AddressOffset, 4).CopyTo(savedAddress);
            sector.Slice(AddressOffset, 4).Clear();
        }

        // P parity spans the data column-wise, Q parity diagonally; the magic numbers are the
        // interleave the CD-ROM standard defines and are only meaningful as a set.
        ComputeEccBlock(
            sector,
            AddressOffset,
            majorCount: 86,
            minorCount: 24,
            majorMult: 2,
            minorInc: 86,
            EccPOffset
        );
        ComputeEccBlock(
            sector,
            AddressOffset,
            majorCount: 52,
            minorCount: 43,
            majorMult: 86,
            minorInc: 88,
            EccQOffset
        );

        if (zeroAddress)
        {
            savedAddress.CopyTo(sector.Slice(AddressOffset, 4));
        }
    }

    private static void ComputeEccBlock(
        Span<byte> sector,
        int sourceOffset,
        int majorCount,
        int minorCount,
        int majorMult,
        int minorInc,
        int destinationOffset
    )
    {
        var size = majorCount * minorCount;

        for (var major = 0; major < majorCount; major++)
        {
            var index = (major >> 1) * majorMult + (major & 1);
            byte eccA = 0;
            byte eccB = 0;

            for (var minor = 0; minor < minorCount; minor++)
            {
                var value = sector[sourceOffset + index];
                index += minorInc;
                if (index >= size)
                {
                    index -= size;
                }

                eccA ^= value;
                eccB ^= value;
                eccA = EccForwardLut[eccA];
            }

            eccA = EccBackwardLut[EccForwardLut[eccA] ^ eccB];
            sector[destinationOffset + major] = eccA;
            sector[destinationOffset + major + majorCount] = (byte)(eccA ^ eccB);
        }
    }
}