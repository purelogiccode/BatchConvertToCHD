using System.Globalization;
using System.Text;

namespace Alcohol120Sharp;

/// <summary>
///     Turns an Alcohol .mds/.mdf pair into something chdman can actually convert.
///     Three shapes exist in practice:
///     - 2352-byte sectors: a cue is all that is missing, and the .mdf is referenced where it lies.
///     - 2448-byte sectors (2352 data + 96 subchannel): chdman will not read these, so the subchannel
///     tail is stripped into a new image and a cue is written for that.
///     - 2048-byte sectors: the .mdf is really an ISO, so it converts as a DVD image with no cue.
/// </summary>
public static class MdsInputPreparer
{
    /// <summary>Sectors repacked per read. A write per sector is far too slow at disc scale.</summary>
    private const int StripChunkSectors = 2048;

    private const string BinExtension = ".bin";
    private const string CueExtension = ".cue";

    /// <summary>
    ///     Prepares <paramref name="disc" /> for conversion, writing any generated files into
    ///     <paramref name="workDir" />.
    /// </summary>
    /// <param name="disc">The parsed Alcohol image.</param>
    /// <param name="workDir">Existing directory for generated files.</param>
    /// <param name="onLog">Optional logging callback.</param>
    /// <param name="token">Cancellation token.</param>
    public static async Task<Result> PrepareAsync(
        MdsDisc disc,
        string workDir,
        Action<string>? onLog,
        CancellationToken token
    )
    {
        if (disc.MdfPath is null || !File.Exists(disc.MdfPath))
            return Result.Failed("the .mdf data file was not found next to the .mds descriptor");

        // Alcohol can split the data across ".i00", ".i01" and so on. Nothing can read that until
        // the pieces are back together, so join them before looking at sector layout.
        var dataFilePath = disc.MdfPath;
        var volumeSet = SplitImageJoiner.TryGetVolumeSet(dataFilePath);
        if (volumeSet is not null)
        {
            onLog?.Invoke(
                $" {Path.GetFileName(dataFilePath)} is part 1 of a {volumeSet.Count}-part split image; joining the parts."
            );
            dataFilePath = Path.Combine(
                workDir,
                Path.GetFileNameWithoutExtension(disc.MdsPath) + BinExtension
            );
            await SplitImageJoiner.JoinAsync(volumeSet, dataFilePath, token).ConfigureAwait(false);
        }

        if (disc.IsDvdImage)
        {
            onLog?.Invoke(
                $" {Path.GetFileName(disc.MdsPath)} describes {MdsDisc.CookedSectorSize}-byte sectors, so the data file is a DVD image; converting it directly."
            );
            return Result.Dvd(dataFilePath);
        }

        if (!disc.AllTracksDescribable)
        {
            var unknown = disc
                .Tracks.Where(static t => t.CueTrackType is null)
                .Select(static t => t.Description);
            return Result.Failed(
                $"the descriptor uses track modes this build cannot express in a cue ({string.Join(", ", unknown)})"
            );
        }

        if (disc.NeedsSubchannelStrip)
        {
            var strippedPath = Path.Combine(
                workDir,
                Path.GetFileNameWithoutExtension(dataFilePath) + ".stripped" + BinExtension
            );
            onLog?.Invoke(
                $" {Path.GetFileName(dataFilePath)} stores {disc.SectorSize}-byte sectors; stripping subchannel data down to {MdsDisc.RawSectorSize} bytes so chdman can read it."
            );

            var stripped = await StripSubchannelAsync(
                    dataFilePath,
                    strippedPath,
                    disc.SectorSize,
                    token
                )
                .ConfigureAwait(false);
            if (stripped is not null) return Result.Failed(stripped);

            var cuePath = await WriteCueAsync(disc, workDir, Path.GetFileName(strippedPath), token)
                .ConfigureAwait(false);
            return Result.Cue(cuePath);
        }

        if (!disc.IsPlainRawCd)
            return Result.Failed(
                $"the descriptor reports {disc.SectorSize} bytes per sector, which is neither a raw CD ({MdsDisc.RawSectorSize}), a subchannel-bearing CD ({MdsDisc.RawPlusSubchannelSize}) nor a DVD image ({MdsDisc.CookedSectorSize})"
            );

        // Already 2352: reference the data file where it is rather than duplicating a whole disc.
        var reference = GetReferencePath(workDir, dataFilePath);
        if (reference is null)
        {
            reference = Path.GetFileName(dataFilePath);
            onLog?.Invoke(
                $" {Path.GetFileName(dataFilePath)} cannot be referenced relatively from the work directory; copying it."
            );
            await CopyAsync(dataFilePath, Path.Combine(workDir, reference), token)
                .ConfigureAwait(false);
        }

        var plainCuePath = await WriteCueAsync(disc, workDir, reference, token)
            .ConfigureAwait(false);
        return Result.Cue(plainCuePath);
    }

    /// <summary>
    ///     Copies <paramref name="sourcePath" /> to <paramref name="destinationPath" /> keeping only the
    ///     first <see cref="MdsDisc.RawSectorSize" /> bytes of every sector. Returns null on success or a
    ///     user-facing reason on failure.
    /// </summary>
    /// <param name="sourcePath">The .mdf holding oversized sectors.</param>
    /// <param name="destinationPath">Where the 2352-byte image is written.</param>
    /// <param name="sectorSize">Bytes per sector in the source.</param>
    /// <param name="token">Cancellation token.</param>
    public static async Task<string?> StripSubchannelAsync(
        string sourcePath,
        string destinationPath,
        int sectorSize,
        CancellationToken token
    )
    {
        if (sectorSize <= MdsDisc.RawSectorSize) return $"sector size {sectorSize} carries no subchannel data to strip";

        var length = new FileInfo(sourcePath).Length;
        if (length == 0 || length % sectorSize != 0)
            return
                $"{Path.GetFileName(sourcePath)} is {length:N0} bytes, which is not a whole number of {sectorSize}-byte sectors, so it is truncated or the descriptor is wrong";

        var readBuffer = new byte[sectorSize * StripChunkSectors];
        var writeBuffer = new byte[MdsDisc.RawSectorSize * StripChunkSectors];

        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            readBuffer.Length,
            true
        );
        await using var output = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            writeBuffer.Length,
            true
        );

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var read = await input
                .ReadAtLeastAsync(readBuffer, readBuffer.Length, false, token)
                .ConfigureAwait(false);
            if (read == 0) break;

            var sectors = read / sectorSize;
            for (var sector = 0; sector < sectors; sector++)
                readBuffer
                    .AsSpan(sector * sectorSize, MdsDisc.RawSectorSize)
                    .CopyTo(writeBuffer.AsSpan(sector * MdsDisc.RawSectorSize));

            await output
                .WriteAsync(writeBuffer.AsMemory(0, sectors * MdsDisc.RawSectorSize), token)
                .ConfigureAwait(false);

            if (read < readBuffer.Length) break;
        }

        return null;
    }

    /// <summary>
    ///     Writes a single-FILE cue describing every track, and returns its path.
    /// </summary>
    /// <param name="disc">The parsed Alcohol image.</param>
    /// <param name="workDir">Directory the cue is written into.</param>
    /// <param name="dataFileReference">FILE entry to record, relative to <paramref name="workDir" />.</param>
    /// <param name="token">Cancellation token.</param>
    public static async Task<string> WriteCueAsync(
        MdsDisc disc,
        string workDir,
        string dataFileReference,
        CancellationToken token
    )
    {
        var builder = new StringBuilder();
        builder.Append("FILE \"").Append(dataFileReference).Append("\" BINARY\r\n");

        foreach (var track in disc.Tracks)
        {
            builder
                .Append("  TRACK ")
                .Append(track.Number.ToString("00", CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(track.CueTrackType)
                .Append("\r\n");

            // INDEX 01 at the recorded LBA only. The descriptor carries no pregap information, and
            // this is what produced verifiable CHDs for these discs in practice.
            builder.Append("    INDEX 01 ").Append(FormatMsf(track.StartLba)).Append("\r\n");
        }

        var cuePath = Path.Combine(
            workDir,
            Path.GetFileNameWithoutExtension(disc.MdsPath) + CueExtension
        );

        // No BOM: chdman's cue parser does not skip one.
        await File.WriteAllTextAsync(cuePath, builder.ToString(), new UTF8Encoding(false), token)
            .ConfigureAwait(false);

        return cuePath;
    }

    /// <summary>Formats an absolute sector count as the MM:SS:FF a cue INDEX expects.</summary>
    /// <param name="lba">Absolute sector number.</param>
    public static string FormatMsf(long lba)
    {
        const int framesPerSecond = 75;
        const int framesPerMinute = framesPerSecond * 60;

        if (lba < 0) lba = 0;

        var minutes = lba / framesPerMinute;
        var remainder = lba % framesPerMinute;
        var seconds = remainder / framesPerSecond;
        var frames = remainder % framesPerSecond;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{minutes:00}:{seconds:00}:{frames:00}"
        );
    }

    private static string? GetReferencePath(string workDir, string dataFilePath)
    {
        try
        {
            var relative = Path.GetRelativePath(workDir, dataFilePath);
            return Path.IsPathRooted(relative) ? null : relative;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static async Task CopyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken token
    )
    {
        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            1024 * 1024,
            true
        );
        await using var output = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            true
        );
        await input.CopyToAsync(output, token).ConfigureAwait(false);
    }

    /// <summary>What chdman should be handed for an Alcohol image.</summary>
    /// <param name="CuePath">Cue to convert as a CD, or null.</param>
    /// <param name="DvdImagePath">Image to convert as a DVD, or null.</param>
    /// <param name="FailureReason">Why nothing could be prepared, or null on success.</param>
    public sealed record Result(string? CuePath, string? DvdImagePath, string? FailureReason)
    {
        public bool Success => FailureReason is null;

        public static Result Cue(string cuePath)
        {
            return new Result(cuePath, null, null);
        }

        public static Result Dvd(string imagePath)
        {
            return new Result(null, imagePath, null);
        }

        public static Result Failed(string reason)
        {
            return new Result(null, null, reason);
        }
    }
}