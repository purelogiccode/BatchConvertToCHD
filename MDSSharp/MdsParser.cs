using System.Buffers.Binary;
using System.Text;

namespace MDSSharp;

/// <summary>
///     Reads the track table out of an Alcohol 120% .mds descriptor.
///     The layout below was recovered by inspecting real descriptors and cross-checked against
///     libmirage's reverse engineering; Alcohol's format is not published.
///     header    0x00  16 bytes  "MEDIA DESCRIPTOR" signature
///     0x10  u16       format version (2 bytes)
///     0x12  u16       medium type: 0 CD, 1 CD-R, 2 CD-RW, 0x10 DVD, 0x12 DVD-R
///     0x14  u16       session count
///     0x50  u32       offset of the first session block
///     session   24 bytes each
///     0x0A  u8        number of track blocks in this session
///     0x14  u32       offset of the first track block
///     track     80 bytes each
///     0x00  u8        mode (only the low nibble selects the mode)
///     0x01  u8        subchannel mode (0x08 is 96-byte interleaved P-W)
///     0x02  u8        ADR/CTL
///     0x04  u8        POINT - the track number, or a lead-in/lead-out marker
///     0x0C  u32       offset of the track's extra block (pregap and length)
///     0x10  u16       sector size
///     0x24  u32       start LBA
///     0x30  u32       number of data files
///     0x34  u32       offset of the track's footer blocks (data file names)
///     extra      8 bytes each
///     0x00  u32       pregap sectors
///     0x04  u32       track length in sectors
///     footer    16 bytes each
///     0x00  u32       offset of the data file name
///     0x04  u32       non-zero when the name is stored as UTF-16
/// </summary>
public static class MdsParser
{
    private const string Signature = "MEDIA DESCRIPTOR";
    private const int SignatureLength = 16;
    private const int MediumTypeOffset = 0x12;
    private const int SessionCountOffset = 0x14;
    private const int SessionBlockOffsetOffset = 0x50;
    private const int SessionBlockSize = 24;
    private const int SessionTrackCountOffset = 0x0A;
    private const int SessionTrackOffsetOffset = 0x14;
    private const int TrackBlockSize = 80;
    private const int TrackModeOffset = 0x00;
    private const int TrackSubchannelOffset = 0x01;
    private const int TrackAdrCtlOffset = 0x02;
    private const int TrackPointOffset = 0x04;
    private const int TrackExtraOffsetOffset = 0x0C;
    private const int TrackSectorSizeOffset = 0x10;
    private const int TrackStartLbaOffset = 0x24;
    private const int TrackFooterCountOffset = 0x30;
    private const int TrackFooterOffsetOffset = 0x34;
    private const int ExtraBlockSize = 8;
    private const int FooterBlockSize = 16;
    private const int FooterFilenameOffset = 0x00;
    private const int FooterWidecharOffset = 0x04;
    private const int MaxFooterFiles = 64;
    private const int MaxFileNameChars = 512;

    /// <summary>An .mds is small; anything larger is not a descriptor.</summary>
    private const long MaxDescriptorBytes = 1024 * 1024;

    /// <summary>Sessions beyond this mean the bytes are not a real descriptor.</summary>
    private const int MaxPlausibleSessions = 99;

    private const string MdfExtension = ".mdf";
    private const string SplitFirstAlcoholExtension = ".i00";

    /// <summary>True when <paramref name="path" /> starts with the Alcohol descriptor signature.</summary>
    /// <param name="path">File to test.</param>
    public static bool IsMdsFile(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            var header = new byte[SignatureLength];
            if (
                stream.ReadAtLeast(header, header.Length, false) < header.Length
            )
                return false;

            return Encoding.ASCII.GetString(header).Equals(Signature, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    ///     Parses <paramref name="mdsPath" /> and locates its data file(s).
    /// </summary>
    /// <param name="mdsPath">Path of the .mds descriptor.</param>
    /// <exception cref="InvalidDataException">The file is not a usable descriptor.</exception>
    public static MdsDisc Parse(string mdsPath)
    {
        var info = new FileInfo(mdsPath);
        if (!info.Exists) throw new FileNotFoundException("MDS descriptor not found.", mdsPath);

        if (info.Length > MaxDescriptorBytes)
        {
            throw new InvalidDataException(
                $"{info.Length:N0} bytes is too large to be an MDS descriptor."
            );
        }

        var bytes = File.ReadAllBytes(mdsPath);
        if (
            bytes.Length < SessionBlockOffsetOffset + sizeof(uint)
            || !Encoding
                .ASCII.GetString(bytes, 0, SignatureLength)
                .Equals(Signature, StringComparison.Ordinal)
        )
        {
            throw new InvalidDataException(
                "Not an Alcohol MDS descriptor (missing \"MEDIA DESCRIPTOR\" signature)."
            );
        }

        var medium = ReadMediumType(bytes);
        var sessionCount = BinaryPrimitives.ReadUInt16LittleEndian(
            bytes.AsSpan(SessionCountOffset)
        );

        // A corrupt or truncated descriptor produces nonsense here - one real example reported 8233
        // sessions - and walking that many offsets would just read garbage.
        if (sessionCount is 0 or > MaxPlausibleSessions)
        {
            throw new InvalidDataException(
                $"Descriptor reports {sessionCount} sessions, so it is corrupt or truncated."
            );
        }

        var sessionBlockOffset = (long)
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(SessionBlockOffsetOffset));
        var tracks = new List<MdsTrack>();
        var declaredFiles = new List<string>();

        for (var session = 0; session < sessionCount; session++)
        {
            var sessionBase = sessionBlockOffset + ((long)session * SessionBlockSize);
            if (sessionBase < 0 || sessionBase + SessionBlockSize > bytes.Length) break;

            var trackCount = bytes[sessionBase + SessionTrackCountOffset];
            var trackBlockOffset = (long)
                BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan((int)(sessionBase + SessionTrackOffsetOffset))
                );

            for (var track = 0; track < trackCount; track++)
            {
                var trackBase = trackBlockOffset + ((long)track * TrackBlockSize);
                if (trackBase < 0 || trackBase + TrackBlockSize > bytes.Length) break;

                var point = bytes[trackBase + TrackPointOffset];

                // POINT outside 1-99 is a lead-in or lead-out descriptor, not a playable track.
                if (point is < 1 or > 99) continue;

                var extraOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan((int)trackBase + TrackExtraOffsetOffset)
                );
                var (pregap, length) = ReadExtraBlock(bytes, medium, extraOffset);

                tracks.Add(
                    new MdsTrack(
                        point,
                        bytes[trackBase + TrackModeOffset],
                        BinaryPrimitives.ReadUInt16LittleEndian(
                            bytes.AsSpan((int)(trackBase + TrackSectorSizeOffset))
                        ),
                        BinaryPrimitives.ReadUInt32LittleEndian(
                            bytes.AsSpan((int)(trackBase + TrackStartLbaOffset))
                        )
                    )
                    {
                        PregapSectors = pregap,
                        LengthSectors = length,
                        SubchannelMode = bytes[trackBase + TrackSubchannelOffset],
                        AdrCtl = bytes[trackBase + TrackAdrCtlOffset]
                    }
                );

                var footerOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan((int)trackBase + TrackFooterOffsetOffset)
                );
                var footerCount = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan((int)trackBase + TrackFooterCountOffset)
                );
                AddDistinct(
                    declaredFiles,
                    ReadFooterFileNames(bytes, footerOffset, footerCount)
                );
            }
        }

        if (tracks.Count == 0) throw new InvalidDataException("Descriptor contains no readable tracks.");

        var dataFiles = ResolveDeclaredFiles(mdsPath, declaredFiles) ?? ResolveFallbackDataFile(mdsPath);

        return new MdsDisc(
            sessionCount,
            tracks,
            mdsPath,
            dataFiles.Count > 0 ? dataFiles[0] : null
        )
        {
            MediumType = medium,
            DataFilePaths = dataFiles
        };
    }

    /// <summary>Reads the medium type, returning <see cref="MdsMedium.Unknown" /> for values outside the known set.</summary>
    /// <param name="bytes">Whole descriptor.</param>
    private static MdsMedium ReadMediumType(byte[] bytes)
    {
        if (bytes.Length < MediumTypeOffset + sizeof(ushort)) return MdsMedium.Unknown;

        var value = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(MediumTypeOffset));
        var medium = (MdsMedium)value;

        return Enum.IsDefined(medium) ? medium : MdsMedium.Unknown;
    }

    /// <summary>
    ///     Reads a track's extra block, which records the pregap and length for CD media. DVD
    ///     descriptors do not use it for that purpose, so nothing is read for them.
    /// </summary>
    /// <param name="bytes">Whole descriptor.</param>
    /// <param name="medium">Medium type read from the header.</param>
    /// <param name="extraOffset">Offset of the extra block, or 0.</param>
    private static (long Pregap, long Length) ReadExtraBlock(
        byte[] bytes,
        MdsMedium medium,
        uint extraOffset
    )
    {
        if (medium is MdsMedium.Dvd or MdsMedium.DvdMinusR) return (0, 0);
        if (extraOffset == 0 || extraOffset + ExtraBlockSize > bytes.Length) return (0, 0);

        var pregap = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)extraOffset));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan((int)extraOffset + sizeof(uint))
        );

        return (pregap, length);
    }

    /// <summary>
    ///     Reads the data file names out of a track's footer blocks. A name is either terminated
    ///     UTF-16 or single-byte text; anything that runs past the descriptor is ignored.
    /// </summary>
    /// <param name="bytes">Whole descriptor.</param>
    /// <param name="footerOffset">Offset of the track's first footer block, or 0.</param>
    /// <param name="fileCount">Number of footer blocks the track declares.</param>
    private static List<string> ReadFooterFileNames(
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
            var footerBase = (long)footerOffset + ((long)index * FooterBlockSize);
            var name = ReadFooterFileName(bytes, footerBase);
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
        }

        return names;
    }

    /// <summary>Reads one footer block's file name, or null when it is absent or malformed.</summary>
    /// <param name="bytes">Whole descriptor.</param>
    /// <param name="footerBase">Offset of the footer block.</param>
    private static string? ReadFooterFileName(byte[] bytes, long footerBase)
    {
        if (footerBase < 0 || footerBase + FooterBlockSize > bytes.Length) return null;

        var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan((int)footerBase + FooterFilenameOffset)
        );
        if (nameOffset == 0 || nameOffset >= bytes.Length) return null;

        var wide = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan((int)footerBase + FooterWidecharOffset)
        ) != 0;

        return wide ? ReadWideName(bytes, nameOffset) : ReadNarrowName(bytes, nameOffset);
    }

    /// <summary>Reads a null-terminated UTF-16LE file name.</summary>
    /// <param name="bytes">Whole descriptor.</param>
    /// <param name="nameOffset">Offset of the first character.</param>
    private static string? ReadWideName(byte[] bytes, uint nameOffset)
    {
        var maxBytes = (int)Math.Min(MaxFileNameChars * 2L, bytes.Length - (long)nameOffset);
        var length = 0;
        while (length + 1 < maxBytes)
        {
            if (bytes[nameOffset + length] == 0 && bytes[nameOffset + length + 1] == 0) break;

            length += 2;
        }

        return length == 0 ? null : Encoding.Unicode.GetString(bytes, (int)nameOffset, length);
    }

    /// <summary>Reads a null-terminated single-byte file name, falling back to Latin-1 for invalid UTF-8.</summary>
    /// <param name="bytes">Whole descriptor.</param>
    /// <param name="nameOffset">Offset of the first character.</param>
    private static string? ReadNarrowName(byte[] bytes, uint nameOffset)
    {
        var maxBytes = (int)Math.Min(MaxFileNameChars, bytes.Length - (long)nameOffset);
        var length = 0;
        while (length < maxBytes && bytes[nameOffset + length] != 0) length++;

        if (length == 0) return null;

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes, (int)nameOffset, length);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes, (int)nameOffset, length);
        }
    }

    /// <summary>Adds <paramref name="names" /> to <paramref name="target" />, ignoring duplicates.</summary>
    /// <param name="target">Accumulated names.</param>
    /// <param name="names">Names to add.</param>
    private static void AddDistinct(List<string> target, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (!target.Contains(name, StringComparer.OrdinalIgnoreCase)) target.Add(name);
        }
    }

    /// <summary>
    ///     Resolves every data file name the descriptor declares, in order, or null when the
    ///     descriptor names no files or any of them cannot be found (the caller then falls back to
    ///     locating the data file by name, which also covers split volumes).
    /// </summary>
    /// <param name="mdsPath">Path of the .mds descriptor.</param>
    /// <param name="declaredNames">File names read from the track footers.</param>
    private static List<string>? ResolveDeclaredFiles(string mdsPath, List<string> declaredNames)
    {
        if (declaredNames.Count == 0) return null;

        var resolved = new List<string>(declaredNames.Count);
        foreach (var declaredName in declaredNames)
        {
            var path = ResolveDeclaredFile(mdsPath, declaredName);
            if (path is null) return null;

            if (!resolved.Contains(path, StringComparer.OrdinalIgnoreCase)) resolved.Add(path);
        }

        return resolved;
    }

    /// <summary>
    ///     Resolves one declared name in the descriptor's folder. A name of the form "*.mdf" is
    ///     understood as "the .mds's own name with that extension", which is how Alcohol writes the
    ///     common case.
    /// </summary>
    /// <param name="mdsPath">Path of the .mds descriptor.</param>
    /// <param name="declaredName">Name recorded in a track footer.</param>
    private static string? ResolveDeclaredFile(string mdsPath, string declaredName)
    {
        var directory = Path.GetDirectoryName(mdsPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;

        var name = Path.GetFileName(declaredName.Trim());
        if (string.IsNullOrWhiteSpace(name)) return null;

        if (name.Contains('*'))
        {
            var dot = name.IndexOf('.');
            var extension = dot >= 0 ? name[dot..] : string.Empty;
            name = Path.GetFileNameWithoutExtension(mdsPath) + extension;
        }

        var candidate = Path.Combine(directory, name);
        if (File.Exists(candidate)) return candidate;

        try
        {
            return Directory
                .GetFiles(directory)
                .FirstOrDefault(f =>
                    string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)
                );
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    ///     Finds the data file when the descriptor names none, or names files that cannot be found:
    ///     the matching base name first, then a decorated base name ("Game" beside "Game (USA).mdf"),
    ///     then the only .mdf in the folder, then an unambiguous match one folder down.
    /// </summary>
    /// <param name="mdsPath">Path of the .mds descriptor.</param>
    private static List<string> ResolveFallbackDataFile(string mdsPath)
    {
        var dataFile = FindDataFile(mdsPath);

        return dataFile is null ? [] : [dataFile];
    }

    /// <summary>
    ///     Finds the data file beside <paramref name="mdsPath" />: the matching base name first, then a
    ///     decorated base name ("Game" beside "Game (USA).mdf"), then the only .mdf in the folder, then
    ///     an unambiguous match one folder down.
    /// </summary>
    private static string? FindDataFile(string mdsPath)
    {
        var directory = Path.GetDirectoryName(mdsPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;

        var baseName = Path.GetFileNameWithoutExtension(mdsPath);

        var dataFile = FindCompanion(directory, baseName, MdfExtension);
        if (dataFile is not null) return dataFile;

        // Alcohol also splits the data across ".i00", ".i01" and so on with no .mdf at all. The
        // first volume stands in for the data file; the preparer joins the set before reading it.
        dataFile = FindCompanion(directory, baseName, SplitFirstAlcoholExtension);
        if (dataFile is not null) return dataFile;

        // Some collections leave the data file one folder down. Only an unambiguous exact base
        // name match is accepted, so a sibling game's image is never picked up.
        return FindCompanionInSubdirectories(directory, baseName, MdfExtension);
    }

    /// <summary>
    ///     Picks the file with the requested extension in <paramref name="directory" />: the exact base
    ///     name first, then a unique decorated variant, then a lone candidate whatever its name.
    /// </summary>
    private static string? FindCompanion(string directory, string baseName, string extension)
    {
        string[] candidates;
        try
        {
            candidates =
            [
                .. Directory
                    .GetFiles(directory)
                    .Where(f =>
                        Path.GetExtension(f).Equals(extension, StringComparison.OrdinalIgnoreCase)
                    )
            ];
        }
        catch (Exception)
        {
            return null;
        }

        var byName = candidates.FirstOrDefault(f =>
            NamesMatch(Path.GetFileNameWithoutExtension(f), baseName)
        );
        if (byName is not null) return byName;

        if (candidates.Length == 1) return candidates[0];

        var decorated =
            candidates
                .Where(f => IsDecoratedMatch(Path.GetFileNameWithoutExtension(f), baseName))
                .ToArray();
        return decorated.Length == 1 ? decorated[0] : null;
    }

    /// <summary>
    ///     Finds the exact base name in the immediate subfolders of <paramref name="directory" />, or
    ///     null when nothing matches or more than one folder holds a match.
    /// </summary>
    private static string? FindCompanionInSubdirectories(
        string directory,
        string baseName,
        string extension
    )
    {
        string[] subdirectories;
        try
        {
            subdirectories = Directory.GetDirectories(directory);
        }
        catch (Exception)
        {
            return null;
        }

        var matches = new List<string>();
        foreach (var subdirectory in subdirectories)
        {
            string[] candidates;
            try
            {
                candidates =
                [
                    .. Directory
                        .GetFiles(subdirectory)
                        .Where(f =>
                            Path.GetExtension(f)
                                .Equals(extension, StringComparison.OrdinalIgnoreCase)
                        )
                ];
            }
            catch (Exception)
            {
                continue;
            }

            matches.AddRange(
                candidates.Where(f => NamesMatch(Path.GetFileNameWithoutExtension(f), baseName))
            );
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>True when two base names match once case and Unicode composition are ignored.</summary>
    private static bool NamesMatch(string candidate, string baseName)
    {
        return string.Equals(
            candidate.Normalize(NormalizationForm.FormC),
            baseName.Normalize(NormalizationForm.FormC),
            StringComparison.OrdinalIgnoreCase
        );
    }

    /// <summary>
    ///     True when one name is the other plus a separator-led decoration, so "Game" pairs with
    ///     "Game (USA)" or "Game - Disc 1" but not with names whose next character is alphanumeric.
    /// </summary>
    private static bool IsDecoratedMatch(string candidate, string baseName)
    {
        var normalizedCandidate = candidate.Normalize(NormalizationForm.FormC);
        var normalizedBase = baseName.Normalize(NormalizationForm.FormC);

        var (longer, shorter) =
            normalizedCandidate.Length > normalizedBase.Length
                ? (normalizedCandidate, normalizedBase)
                : (normalizedBase, normalizedCandidate);

        return shorter.Length > 0
               && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase)
               && !char.IsLetterOrDigit(longer[shorter.Length]);
    }
}
