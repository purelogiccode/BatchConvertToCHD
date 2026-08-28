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
            FileExtensions.Mds
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
                        continue;

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
            onLog?.Invoke(
                $" Skipping {Path.GetFileName(suppression.DataFile)} - {suppression.Reason}."
            );

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