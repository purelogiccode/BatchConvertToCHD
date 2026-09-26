using System.IO;

namespace BatchConvertToCHD.Utilities;

/// <summary>
///     Removes redundant inputs from a conversion batch before any work starts.
///     A disc is often present in a folder twice: once as a descriptor (.cue/.ccd/.gdi/.toc) and once
///     as the raw data file it points at (.bin/.img/.iso/.raw). Both resolve to the same output CHD
///     name, so converting both means the second attempt overwrites - and on failure deletes - the
///     output of the first. The raw image also cannot be converted correctly on its own: chdman is
///     handed no track layout and picks a verb from the extension, which produces
///     "Data size ... is not divisible by sector size 512" for a CloneCD .img.
///     Suppressing the covered data file is the safe direction. A descriptor that covers an image is
///     always the better input, and every suppression is reported so nothing disappears silently.
/// </summary>
internal static class InputFileFilter
{
    /// <summary>Descriptor extensions that describe a track layout and reference data files.</summary>
    private static readonly HashSet<string> DescriptorExtensions = new(
        [
            FileExtensions.Cue,
            FileExtensions.Ccd,
            FileExtensions.Gdi,
            FileExtensions.Toc,
            FileExtensions.Mds,
            FileExtensions.Mdx
        ],
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>Raw data extensions that a descriptor can cover.</summary>
    private static readonly HashSet<string> DataExtensions = new(
        [FileExtensions.Bin, FileExtensions.Img, FileExtensions.Iso, FileExtensions.Raw],
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>
    ///     Returns the raw data files in <paramref name="files" /> that are already covered by a
    ///     descriptor in the same directory, and so must not be converted separately.
    /// </summary>
    /// <param name="files">Candidate input paths. Only files in the same directory are compared.</param>
    /// <param name="token">Cancellation token.</param>
    internal static async Task<List<Suppression>> FindCompanionSuppressionsAsync(
        IEnumerable<string> files,
        CancellationToken token
    )
    {
        var suppressions = new List<Suppression>();

        foreach (
            var group in files.GroupBy(
                static f => Path.GetDirectoryName(f) ?? string.Empty,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            token.ThrowIfCancellationRequested();

            var descriptors = group
                .Where(static f => DescriptorExtensions.Contains(Path.GetExtension(f)))
                .ToList();
            if (descriptors.Count == 0) continue;

            var dataFiles = group
                .Where(static f => DataExtensions.Contains(Path.GetExtension(f)))
                .ToList();
            if (dataFiles.Count == 0) continue;

            // Descriptor text is only read when a base-name match did not already settle it.
            var descriptorText = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dataFile in dataFiles)
            {
                token.ThrowIfCancellationRequested();

                var dataBaseName = Path.GetFileNameWithoutExtension(dataFile);
                var byName = descriptors.FirstOrDefault(d =>
                    string.Equals(
                        Path.GetFileNameWithoutExtension(d),
                        dataBaseName,
                        StringComparison.OrdinalIgnoreCase
                    )
                );
                if (byName is not null)
                {
                    suppressions.Add(new Suppression(dataFile, byName, true));
                    continue;
                }

                // Split-track sets name their bins differently from the cue ("Game (Track 02).bin"),
                // so fall back to looking the data file up inside each descriptor's text. A plain
                // case-insensitive contains covers cue FILE lines, gdi track lines and toc DATAFILE
                // lines without needing a parser per format, and still cannot match across folders.
                var dataFileName = Path.GetFileName(dataFile);
                foreach (var descriptor in descriptors)
                {
                    // A .ccd never names its .img, so there is nothing to look up.
                    if (
                        Path.GetExtension(descriptor)
                        .Equals(FileExtensions.Ccd, StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        continue;
                    }

                    if (!descriptorText.TryGetValue(descriptor, out var text))
                    {
                        text = await ReadDescriptorTextAsync(descriptor, token)
                            .ConfigureAwait(false);
                        descriptorText[descriptor] = text;
                    }

                    if (text.Contains(dataFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        suppressions.Add(new Suppression(dataFile, descriptor, false));
                        break;
                    }
                }
            }
        }

        return suppressions;
    }

    /// <summary>
    ///     Applies <see cref="FindCompanionSuppressionsAsync" /> and returns the inputs that remain,
    ///     reporting every drop through <paramref name="onLog" />. Input order is preserved.
    /// </summary>
    /// <param name="files">Candidate input paths.</param>
    /// <param name="onLog">Callback used to report each suppressed file.</param>
    /// <param name="token">Cancellation token.</param>
    internal static async Task<List<string>> RemoveCompanionDataFilesAsync(
        IEnumerable<string> files,
        Action<string>? onLog,
        CancellationToken token
    )
    {
        var ordered = files.ToList();
        var suppressions = await FindCompanionSuppressionsAsync(ordered, token)
            .ConfigureAwait(false);
        if (suppressions.Count == 0) return ordered;

        var suppressed = new HashSet<string>(
            suppressions.Select(static s => s.DataFile),
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var suppression in suppressions)
        {
            onLog?.Invoke(
                $" Skipping {Path.GetFileName(suppression.DataFile)} - {suppression.Reason}."
            );
        }

        return [.. ordered.Where(f => !suppressed.Contains(f))];
    }

    /// <summary>
    ///     Drops every <c>.partNN.rar</c> volume that is not the first one of its set. A multi-part
    ///     RAR is decoded from its first volume, so offering each part as its own input would extract
    ///     the same image once per volume, and a part that starts mid-data fails outright. When the
    ///     first volume is not in the batch the lowest-numbered part is kept, so extraction can still
    ///     report the set as incomplete. Input order is preserved.
    /// </summary>
    /// <param name="files">Candidate input paths.</param>
    /// <param name="onLog">Callback used to report each dropped volume.</param>
    internal static List<string> RemoveRarVolumeParts(IEnumerable<string> files, Action<string>? onLog)
    {
        var ordered = files.ToList();
        var suppressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (
            var directoryGroup in ordered
                .Where(static f => RarVolumeSet.TryGetPartInfo(f, out _, out _))
                .GroupBy(
                    static f => Path.GetDirectoryName(f) ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase
                )
        )
        {
            foreach (
                var setGroup in directoryGroup.GroupBy(
                    static f =>
                    {
                        RarVolumeSet.TryGetPartInfo(f, out var setBaseName, out _);
                        return setBaseName;
                    },
                    StringComparer.OrdinalIgnoreCase
                )
            )
            {
                var parts = setGroup
                    .Select(static f =>
                    {
                        RarVolumeSet.TryGetPartInfo(f, out _, out var partNumber);
                        return (File: f, partNumber);
                    })
                    .OrderBy(static p => p.partNumber)
                    .ToList();

                // ".part1" beside ".part01" parses as the same part number; the spelling whose
                // continuation is on disk is the one that extracts the set.
                var firsts = parts.Where(static p => p.partNumber == 1).ToList();
                var keeper =
                    firsts.FirstOrDefault(static p => RarVolumeSet.HasContinuation(p.File)).File
                    ?? (firsts.Count > 0 ? firsts[0].File : parts[0].File);

                foreach (var part in parts)
                {
                    if (string.Equals(part.File, keeper, StringComparison.OrdinalIgnoreCase))
                        continue;

                    suppressed.Add(part.File);
                    onLog?.Invoke(
                        $" Skipping {Path.GetFileName(part.File)} - part {part.partNumber} of a multi-part RAR set; {RarVolumeSet.GetFirstVolumeName(part.File)} extracts the whole set."
                    );
                }
            }
        }

        return [.. ordered.Where(f => !suppressed.Contains(f))];
    }

    /// <summary>
    ///     Groups inputs that would all be written to the same output CHD path. Any group with more
    ///     than one member is a collision: whichever input runs last wins, and a failure on it would
    ///     discard the output of the others.
    /// </summary>
    /// <param name="files">Candidate input paths.</param>
    /// <param name="outputPathSelector">Maps an input path to the CHD path it would produce.</param>
    internal static List<IGrouping<string, string>> FindOutputCollisions(
        IEnumerable<string> files,
        Func<string, string> outputPathSelector
    )
    {
        return
        [
            .. files
                .GroupBy(outputPathSelector, StringComparer.OrdinalIgnoreCase)
                .Where(static g => g.Count() > 1)
        ];
    }

    /// <summary>
    ///     Removes inputs whose output CHD path is already produced by another input in the same
    ///     batch. Converting both would only overwrite one product with the other, and the archive
    ///     copy costs an extraction on top, so each collision is resolved by keeping the first
    ///     non-archive input (the original image the user kept next to its archived copy) or, when
    ///     every member is an archive, the first input. The input order is preserved otherwise.
    /// </summary>
    /// <param name="files">Candidate input paths.</param>
    /// <param name="outputPathSelector">Maps an input path to the CHD path it would produce.</param>
    /// <returns>The inputs to convert, and one <see cref="SkippedDuplicate" /> per dropped input.</returns>
    internal static (string[] Kept, List<SkippedDuplicate> Skipped) ResolveOutputCollisions(
        IEnumerable<string> files,
        Func<string, string> outputPathSelector
    )
    {
        var all = files.ToArray();
        var collisions = FindOutputCollisions(all, outputPathSelector);
        if (collisions.Count == 0)
            return (all, []);

        var skipped = new List<SkippedDuplicate>();
        var skippedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var collision in collisions)
        {
            var keeper =
                collision.FirstOrDefault(f =>
                    !FileExtensions.ArchiveExtensionsSet.Contains(Path.GetExtension(f))
                ) ?? collision.First();

            foreach (var file in collision)
            {
                if (string.Equals(file, keeper, StringComparison.Ordinal))
                    continue;

                skippedFiles.Add(file);
                skipped.Add(new SkippedDuplicate(file, keeper, collision.Key));
            }
        }

        return ([.. all.Where(f => !skippedFiles.Contains(f))], skipped);
    }

    private static async Task<string> ReadDescriptorTextAsync(
        string descriptorPath,
        CancellationToken token
    )
    {
        try
        {
            var (lines, _, _) = await GameFileParser
                .ReadLinesWithDetectedEncodingAsync(descriptorPath, token)
                .ConfigureAwait(false);
            return string.Join('\n', lines);
        }
        catch (Exception)
        {
            // An unreadable descriptor simply covers nothing; the data file stays in the batch.
            return string.Empty;
        }
    }

    /// <summary>One input dropped from the batch because another input already targets its output.</summary>
    /// <param name="SkippedFile">Full path of the input that will not be converted.</param>
    /// <param name="KeptFile">Full path of the input that will produce the shared output.</param>
    /// <param name="OutputPath">The CHD path both inputs would have written.</param>
    internal sealed record SkippedDuplicate(string SkippedFile, string KeptFile, string OutputPath);

    /// <summary>One input dropped from the batch, with the descriptor that covers it.</summary>
    /// <param name="DataFile">Full path of the raw data file being suppressed.</param>
    /// <param name="Descriptor">Full path of the descriptor that covers it.</param>
    /// <param name="MatchedByName">
    ///     True when the two share a base name; false when the descriptor's text references the data
    ///     file.
    /// </param>
    internal sealed record Suppression(string DataFile, string Descriptor, bool MatchedByName)
    {
        /// <summary>A log-ready explanation of why the data file was dropped.</summary>
        internal string Reason
        {
            get
            {
                var descriptorName = Path.GetFileName(Descriptor);
                return MatchedByName
                    ? $"covered by {descriptorName} (same base name)"
                    : $"referenced by {descriptorName}";
            }
        }
    }
}