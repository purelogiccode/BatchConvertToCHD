using System.Globalization;

namespace Alcohol120Sharp;

/// <summary>
///     Reassembles disc images that were split into numbered pieces.
///     Two layouts turn up in collections. Alcohol splits a .mdf into ".i00", ".i01" and so on, and
///     plain byte-splitters produce ".001", ".002" - sometimes wearing an archive extension as well
///     ("game.rar.001", or even ".rar" parts that were never archives at all). None of these are
///     readable until the pieces are put back together in order.
///     A set is only accepted when the successor actually exists, so a lone file that happens to end
///     in ".001" is left alone.
/// </summary>
public static class SplitImageJoiner
{
    /// <summary>Copy buffer for concatenation. Disc-sized files, so keep it large.</summary>
    private const int CopyBufferBytes = 4 * 1024 * 1024;

    /// <summary>Guards against runaway enumeration if a directory is pathological.</summary>
    private const int MaxVolumes = 999;

    /// <summary>
    ///     When <paramref name="firstVolumePath" /> is the first piece of a split set, returns every
    ///     piece in order. Returns null when the file is not a first volume, or when no second piece
    ///     exists.
    /// </summary>
    /// <param name="firstVolumePath">Candidate first volume.</param>
    public static List<string>? TryGetVolumeSet(string firstVolumePath)
    {
        var extension = Path.GetExtension(firstVolumePath);
        if (extension.Length == 0) return null;

        var stem = firstVolumePath[..^extension.Length];
        var successorFormat = GetSuccessorFormat(extension);
        if (successorFormat is null) return null;

        var parts = new List<string> { firstVolumePath };
        for (var index = 1; index < MaxVolumes; index++)
        {
            var candidate = stem + successorFormat(index);
            var resolved = ResolveCaseInsensitive(candidate);
            if (resolved is null) break;

            parts.Add(resolved);
        }

        // A single file is not a split set, however it is named.
        return parts.Count > 1 ? parts : null;
    }

    /// <summary>
    ///     Concatenates <paramref name="parts" /> into <paramref name="destinationPath" /> and returns the
    ///     total bytes written.
    /// </summary>
    /// <param name="parts">Volumes in order.</param>
    /// <param name="destinationPath">File to create.</param>
    /// <param name="token">Cancellation token.</param>
    public static async Task<long> JoinAsync(
        IReadOnlyList<string> parts,
        string destinationPath,
        CancellationToken token
    )
    {
        await using var output = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            CopyBufferBytes,
            true
        );

        foreach (var part in parts)
        {
            token.ThrowIfCancellationRequested();

            await using var input = new FileStream(
                part,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                CopyBufferBytes,
                true
            );
            await input.CopyToAsync(output, CopyBufferBytes, token).ConfigureAwait(false);
        }

        await output.FlushAsync(token).ConfigureAwait(false);

        return output.Length;
    }

    /// <summary>Total size of a volume set, or 0 when it cannot be measured.</summary>
    /// <param name="parts">Volumes in the set.</param>
    public static long GetTotalBytes(IEnumerable<string> parts)
    {
        long total = 0;
        foreach (var part in parts)
            try
            {
                total += new FileInfo(part).Length;
            }
            catch (Exception)
            {
                return 0;
            }

        return total;
    }

    /// <summary>
    ///     Returns a function producing the extension of volume <c>n</c> for the numbering style of
    ///     <paramref name="firstExtension" />, or null when the extension is not a first-volume marker.
    /// </summary>
    private static Func<int, string>? GetSuccessorFormat(string firstExtension)
    {
        switch (firstExtension)
        {
            // ".001" style: three decimal digits, first volume is 001.
            case ['.', _, _, _] and [_, '0', '0', '1']:
                return static index =>
                    "." + (index + 1).ToString("000", CultureInfo.InvariantCulture);
            // ".i00" style used by Alcohol, first volume is i00.
            case ['.', 'i' or 'I', '0', '0']:
            {
                var prefix = firstExtension[1];
                return index => "." + prefix + index.ToString("00", CultureInfo.InvariantCulture);
            }
            default:
                return null;
        }
    }

    /// <summary>
    ///     Returns the on-disk path matching <paramref name="candidate" />, tolerating case differences
    ///     in the file name, or null when nothing matches.
    /// </summary>
    private static string? ResolveCaseInsensitive(string candidate)
    {
        if (File.Exists(candidate)) return candidate;

        var directory = Path.GetDirectoryName(candidate);
        var fileName = Path.GetFileName(candidate);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;

        try
        {
            return Directory
                .GetFiles(directory)
                .FirstOrDefault(f =>
                    string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase)
                );
        }
        catch (Exception)
        {
            return null;
        }
    }
}