using System.Buffers.Binary;

namespace MDSSharp;

/// <summary>
///     Daemon Tools MDS v2 / MDX descriptor parsing.
///     A v2 file carries the same session and track tables as v1, but behind an encrypted and
///     compressed descriptor and with wider blocks: 32-byte session blocks, 80-byte track blocks
///     whose mode byte encodes the sector type in its low three bits, and 32-byte footer blocks
///     with UTF-16 file names. The layout was taken from libmirage's <c>image-mdx</c> parser and
///     validated against the mdsx test images.
/// </summary>
public static partial class MdsParser
{
    /// <summary>Offset of the data-encryption header pointer in a v2 descriptor header.</summary>
    private const int V2DataEncryptionHeaderOffset = 0x58;

    /// <summary>Size of one v2 session block.</summary>
    private const int V2SessionBlockSize = 32;

    /// <summary>Offset of the track-block count inside a v2 session block.</summary>
    private const int V2SessionTrackCountOffset = 0x0A;

    /// <summary>Offset of the track-block table pointer inside a v2 session block.</summary>
    private const int V2SessionTrackOffsetOffset = 0x14;

    /// <summary>Size of one v2 track block.</summary>
    private const int V2TrackBlockSize = 80;

    /// <summary>Offset of the mode byte inside a v2 track block.</summary>
    private const int V2TrackModeOffset = 0x00;

    /// <summary>Offset of the subchannel byte inside a v2 track block.</summary>
    private const int V2TrackSubchannelOffset = 0x01;

    /// <summary>Offset of the ADR/CTL byte inside a v2 track block.</summary>
    private const int V2TrackAdrCtlOffset = 0x02;

    /// <summary>Offset of the POINT (track number) byte inside a v2 track block.</summary>
    private const int V2TrackPointOffset = 0x04;

    /// <summary>Offset of the extra-block pointer inside a v2 track block.</summary>
    private const int V2TrackExtraOffsetOffset = 0x0C;

    /// <summary>Offset of the sector size inside a v2 track block.</summary>
    private const int V2TrackSectorSizeOffset = 0x10;

    /// <summary>Offset of the start sector inside a v2 track block.</summary>
    private const int V2TrackStartSectorOffset = 0x24;

    /// <summary>Offset of the footer count inside a v2 track block.</summary>
    private const int V2TrackFooterCountOffset = 0x30;

    /// <summary>Offset of the footer table pointer inside a v2 track block.</summary>
    private const int V2TrackFooterOffsetOffset = 0x34;

    /// <summary>Offset of the 64-bit track length inside a v2 track block.</summary>
    private const int V2TrackLength64Offset = 0x40;

    /// <summary>Size of one v2 footer block.</summary>
    private const int V2FooterBlockSize = 32;

    /// <summary>Offset of the file-name pointer inside a v2 footer block.</summary>
    private const int V2FooterFilenameOffset = 0x00;

    /// <summary>Offset of the flags byte inside a v2 footer block.</summary>
    private const int V2FooterFlagsOffset = 0x04;

    /// <summary>Offset of the compression-table pointer inside a v2 footer block.</summary>
    private const int V2FooterCompressionTableOffset = 0x18;

    /// <summary>Low three bits of the v2 mode byte select the sector type.</summary>
    private const byte V2SectorTypeMask = 0x07;

    /// <summary>Footer flag bit 0 marks the track data as compressed.</summary>
    private const byte V2FooterCompressedFlag = 0x01;

    /// <summary>
    ///     Parses a decrypted v2 descriptor. <paramref name="fileBytes" /> is the raw file; the
    ///     descriptor is decrypted and decompressed internally.
    /// </summary>
    /// <param name="mdsPath">Path of the .mds descriptor.</param>
    /// <param name="fileBytes">Whole file contents.</param>
    /// <returns>The parsed image.</returns>
    /// <exception cref="InvalidDataException">The descriptor is not a readable MDS v2 image.</exception>
    private static MdsDisc ParseV2(string mdsPath, byte[] fileBytes)
    {
        var (bytes, isMdx) = MdxCrypto.DecryptDescriptor(fileBytes);

        var medium = ReadV2MediumType(bytes);
        var sessionCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(SessionCountOffset));
        if (sessionCount is 0 or > MaxPlausibleSessions)
        {
            throw new InvalidDataException(
                $"Descriptor reports {sessionCount} sessions, so it is corrupt or truncated."
            );
        }

        var sessionBlockOffset = (long)
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(SessionBlockOffsetOffset));
        var dataEncryptionHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(V2DataEncryptionHeaderOffset)
        );

        var tracks = new List<MdsTrack>();
        var declaredFiles = new List<string>();
        var compressed = false;

        for (var session = 0; session < sessionCount; session++)
        {
            var sessionBase = sessionBlockOffset + ((long)session * V2SessionBlockSize);
            if (sessionBase < 0 || sessionBase + V2SessionBlockSize > bytes.Length) break;

            var trackCount = bytes[sessionBase + V2SessionTrackCountOffset];
            var trackBlockOffset = (long)
                BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan((int)(sessionBase + V2SessionTrackOffsetOffset))
                );

            for (var track = 0; track < trackCount; track++)
            {
                var trackBase = trackBlockOffset + ((long)track * V2TrackBlockSize);
                if (trackBase < 0 || trackBase + V2TrackBlockSize > bytes.Length) break;

                var sectorType = (byte)(
                    bytes[trackBase + V2TrackModeOffset] & V2SectorTypeMask
                );

                // Sector type 0 is a maintenance entry; POINT outside 1-99 is lead-in/lead-out.
                if (sectorType == 0) continue;

                var point = bytes[trackBase + V2TrackPointOffset];
                if (point is < 1 or > 99) continue;

                var sectorSize = BinaryPrimitives.ReadUInt16LittleEndian(
                    bytes.AsSpan((int)(trackBase + V2TrackSectorSizeOffset))
                );
                var startSector = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan((int)(trackBase + V2TrackStartSectorOffset))
                );
                var extraOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan((int)(trackBase + V2TrackExtraOffsetOffset))
                );
                var trackLength64 = BinaryPrimitives.ReadUInt64LittleEndian(
                    bytes.AsSpan((int)(trackBase + V2TrackLength64Offset))
                );

                var (pregap, length) = medium == MdsMedium.Cd
                    ? ReadExtraBlock(bytes, medium, extraOffset)
                    : (0, (long)trackLength64);

                tracks.Add(
                    new MdsTrack(point, sectorType, sectorSize, startSector)
                    {
                        PregapSectors = pregap,
                        LengthSectors = length,
                        SubchannelMode = bytes[trackBase + V2TrackSubchannelOffset],
                        AdrCtl = bytes[trackBase + V2TrackAdrCtlOffset],
                    }
                );

                var footerOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan((int)(trackBase + V2TrackFooterOffsetOffset))
                );
                var footerCount = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan((int)(trackBase + V2TrackFooterCountOffset))
                );

                foreach (var name in ReadV2FooterFileNames(bytes, footerOffset, footerCount))
                {
                    AddDistinct(declaredFiles, [name]);
                }

                if (HasCompressedFooter(bytes, footerOffset, footerCount)) compressed = true;
            }
        }

        if (tracks.Count == 0)
            throw new InvalidDataException("Descriptor contains no readable tracks.");

        var dataFiles =
            ResolveDeclaredFiles(mdsPath, declaredFiles) ?? ResolveFallbackDataFile(mdsPath);
        if (isMdx) dataFiles = [mdsPath];

        return new MdsDisc(
            sessionCount,
            tracks,
            mdsPath,
            dataFiles.Count > 0 ? dataFiles[0] : null
        )
        {
            MediumType = medium,
            DataFilePaths = dataFiles,
            HasEncryptedTrackData = dataEncryptionHeaderOffset != 0,
            HasCompressedTrackData = compressed,
            IsMdxContainer = isMdx,
        };
    }

    /// <summary>
    ///     Maps the v2 medium type to the public enum: 0-2 are the CD family and 3 is DVD. The v1
    ///     values are accepted too, because some producers write them in v2 descriptors.
    /// </summary>
    /// <param name="bytes">Decrypted descriptor.</param>
    private static MdsMedium ReadV2MediumType(byte[] bytes)
    {
        if (bytes.Length < MediumTypeOffset + sizeof(ushort)) return MdsMedium.Unknown;

        return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(MediumTypeOffset)) switch
        {
            0x00 => MdsMedium.Cd,
            0x01 => MdsMedium.CdR,
            0x02 => MdsMedium.CdRw,
            0x03 or 0x10 => MdsMedium.Dvd,
            0x12 => MdsMedium.DvdMinusR,
            _ => MdsMedium.Unknown,
        };
    }

    /// <summary>
    ///     Reads the UTF-16 file names out of a v2 track's 32-byte footer blocks. V2 footers always
    ///     store wide characters, so the v1 widechar flag is not consulted.
    /// </summary>
    /// <param name="bytes">Decrypted descriptor.</param>
    /// <param name="footerOffset">Offset of the track's first footer block, or 0.</param>
    /// <param name="fileCount">Number of footer blocks the track declares.</param>
    private static List<string> ReadV2FooterFileNames(
        byte[] bytes,
        uint footerOffset,
        uint fileCount
    )
    {
        var names = new List<string>();
        if (footerOffset == 0) return names;

        var count = (int)Math.Min(fileCount, MaxFooterFiles);
        for (var index = 0; index < count; index++)
        {
            var footerBase = (long)footerOffset + ((long)index * V2FooterBlockSize);
            if (footerBase < 0 || footerBase + V2FooterBlockSize > bytes.Length) break;

            var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan((int)footerBase + V2FooterFilenameOffset)
            );
            if (nameOffset == 0 || nameOffset >= bytes.Length) continue;

            var name = ReadWideName(bytes, nameOffset);
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
        }

        return names;
    }

    /// <summary>
    ///     True when any of a track's footer blocks marks the track data as compressed, either via
    ///     the flag byte or by pointing at a compression table.
    /// </summary>
    /// <param name="bytes">Decrypted descriptor.</param>
    /// <param name="footerOffset">Offset of the track's first footer block, or 0.</param>
    /// <param name="fileCount">Number of footer blocks the track declares.</param>
    private static bool HasCompressedFooter(byte[] bytes, uint footerOffset, uint fileCount)
    {
        if (footerOffset == 0) return false;

        var count = (int)Math.Min(fileCount, MaxFooterFiles);
        for (var index = 0; index < count; index++)
        {
            var footerBase = (long)footerOffset + ((long)index * V2FooterBlockSize);
            if (footerBase < 0 || footerBase + V2FooterBlockSize > bytes.Length) break;

            if ((bytes[footerBase + V2FooterFlagsOffset] & V2FooterCompressedFlag) != 0)
                return true;

            if (
                BinaryPrimitives.ReadUInt64LittleEndian(
                    bytes.AsSpan((int)footerBase + V2FooterCompressionTableOffset)
                ) != 0
            )
                return true;
        }

        return false;
    }
}
